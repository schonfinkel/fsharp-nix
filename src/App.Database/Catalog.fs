namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open NodaMoney
open Npgsql

/// <summary>A product's current catalog and stock view. The unit price is a Money value built
/// from the persisted exact amount and ISO 4217 code; the description is optional.</summary>
type ProductSnapshot =
    { ProductId: ProductId
      Sku: Sku
      Name: NonEmptyString
      Description: string option
      UnitPrice: Money
      PriceVersion: PriceVersion
      Active: bool
      OnHand: int }

[<RequireQualifiedAccess>]
type CatalogFailure =
    | SkuAlreadyExists
    | ProductNotFound
    | InsufficientStock

/// <summary>Relational catalog and stock store. Products are not a machine; every operation is
/// a plain SQL statement or transaction. Raw Npgsql (not SqlHydra) keeps the tsvector search
/// column and numeric money columns outside the generated-schema surface.</summary>
[<Sealed>]
type CatalogStore(dataSource: NpgsqlDataSource) =

    let readSnapshot (reader: NpgsqlDataReader) : ProductSnapshot =
        let description = if reader.IsDBNull 3 then None else Some(reader.GetString 3)

        let price =
            match Money.create (reader.GetDecimal 4) (reader.GetString 5) with
            | Ok money -> money
            | Error message -> invalidOp $"A catalog row carried invalid money: {message}"

        let priceVersion =
            match PriceVersion.create (reader.GetInt64 6) with
            | Ok version -> version
            | Error message -> invalidOp $"A catalog row carried an invalid price version: {message}"

        { ProductId =
            match ProductId.create (reader.GetGuid 0) with
            | Ok id -> id
            | Error message -> invalidOp $"A catalog row carried an invalid product id: {message}"
          Sku =
            match Sku.create (reader.GetString 1) with
            | Ok sku -> sku
            | Error message -> invalidOp $"A catalog row carried an invalid SKU: {message}"
          Name =
            match NonEmptyString.create 200 (reader.GetString 2) with
            | Ok name -> name
            | Error message -> invalidOp $"A catalog row carried an invalid name: {message}"
          Description = description
          UnitPrice = price
          PriceVersion = priceVersion
          Active = reader.GetBoolean 7
          OnHand = reader.GetInt32 8 }

    let selectPrefix =
        """SELECT p.product_id, p.sku, p.name, p.description, p.price_amount, p.price_currency,
                  p.price_version, p.active, COALESCE(s.on_hand, 0)
           FROM fsnix.products p
           LEFT JOIN fsnix.product_stock s ON s.product_id = p.product_id"""

    member _.Browse(activeOnly: bool, ct: CancellationToken) : Task<ProductSnapshot list> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    $"{selectPrefix}
                      WHERE p.active = @active
                      ORDER BY p.name",
                    connection
                )

            command.Parameters.AddWithValue("active", activeOnly) |> ignore
            let! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ProductSnapshot>()

            while! reader.ReadAsync(ct) do
                rows.Add(readSnapshot reader)

            reader.Dispose()
            return List.ofSeq rows
        }

    member this.Search(query: string, ct: CancellationToken) : Task<ProductSnapshot list> =
        task {
            let query = query.Trim()

            if String.IsNullOrWhiteSpace query then
                return! this.Browse(true, ct)
            else
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync(ct)

                use command =
                    new NpgsqlCommand(
                        $"{selectPrefix}
                          WHERE p.active
                            AND p.search @@ websearch_to_tsquery('english', @query)
                          ORDER BY p.name",
                        connection
                    )

                command.Parameters.AddWithValue("query", query) |> ignore
                let! reader = command.ExecuteReaderAsync(ct)
                let rows = ResizeArray<ProductSnapshot>()

                while! reader.ReadAsync(ct) do
                    rows.Add(readSnapshot reader)

                reader.Dispose()
                return List.ofSeq rows
        }

    member _.Get(productId: Guid, ct: CancellationToken) : Task<ProductSnapshot option> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    $"{selectPrefix}
                      WHERE p.product_id = @product_id",
                    connection
                )

            command.Parameters.AddWithValue("product_id", productId) |> ignore
            let! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            let snapshot = if found then Some(readSnapshot reader) else None
            reader.Dispose()
            return snapshot
        }

    member _.Create
        (
            productId: Guid,
            sku: Sku,
            name: NonEmptyString,
            description: string option,
            unitPrice: Money,
            onHand: int,
            ct: CancellationToken
        ) : Task<Result<unit, CatalogFailure>> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)
            use! transaction = connection.BeginTransactionAsync(ct)

            try
                use product =
                    new NpgsqlCommand(
                        """INSERT INTO fsnix.products
                               (product_id, sku, name, description, price_amount, price_currency, price_version, active)
                           VALUES (@product_id, @sku, @name, @description, @price_amount, @price_currency, 1, TRUE)""",
                        connection,
                        transaction
                    )

                product.Parameters.AddWithValue("product_id", productId) |> ignore
                product.Parameters.AddWithValue("sku", Sku.value sku) |> ignore
                product.Parameters.AddWithValue("name", NonEmptyString.value name) |> ignore

                match description with
                | Some value -> product.Parameters.AddWithValue("description", value) |> ignore
                | None -> product.Parameters.AddWithValue("description", DBNull.Value) |> ignore

                product.Parameters.AddWithValue("price_amount", Money.amount unitPrice)
                |> ignore

                product.Parameters.AddWithValue("price_currency", Money.currencyCode unitPrice)
                |> ignore

                let! _ = product.ExecuteNonQueryAsync(ct)

                use stock =
                    new NpgsqlCommand(
                        """INSERT INTO fsnix.product_stock (product_id, on_hand, reserved)
                           VALUES (@product_id, @on_hand, 0)""",
                        connection,
                        transaction
                    )

                stock.Parameters.AddWithValue("product_id", productId) |> ignore
                stock.Parameters.AddWithValue("on_hand", onHand) |> ignore
                let! _ = stock.ExecuteNonQueryAsync(ct)

                do! transaction.CommitAsync(ct)
                return Ok()
            with :? PostgresException as error when error.SqlState = PostgresErrorCodes.UniqueViolation ->
                do! transaction.RollbackAsync(ct)
                return Error CatalogFailure.SkuAlreadyExists
        }

    member _.Update
        (productId: Guid, name: NonEmptyString, description: string option, unitPrice: Money, ct: CancellationToken)
        : Task<Result<unit, CatalogFailure>> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.products
                       SET name = @name,
                           description = @description,
                           price_amount = @price_amount,
                           price_currency = @price_currency,
                           price_version = price_version + 1,
                           updated_at = statement_timestamp()
                       WHERE product_id = @product_id""",
                    connection
                )

            command.Parameters.AddWithValue("product_id", productId) |> ignore
            command.Parameters.AddWithValue("name", NonEmptyString.value name) |> ignore

            match description with
            | Some value -> command.Parameters.AddWithValue("description", value) |> ignore
            | None -> command.Parameters.AddWithValue("description", DBNull.Value) |> ignore

            command.Parameters.AddWithValue("price_amount", Money.amount unitPrice)
            |> ignore

            command.Parameters.AddWithValue("price_currency", Money.currencyCode unitPrice)
            |> ignore

            let! affected = command.ExecuteNonQueryAsync(ct)

            return
                if affected = 1 then
                    Ok()
                else
                    Error CatalogFailure.ProductNotFound
        }

    member _.Retire(productId: Guid, ct: CancellationToken) : Task<Result<unit, CatalogFailure>> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.products
                       SET active = FALSE, updated_at = statement_timestamp()
                       WHERE product_id = @product_id""",
                    connection
                )

            command.Parameters.AddWithValue("product_id", productId) |> ignore
            let! affected = command.ExecuteNonQueryAsync(ct)

            return
                if affected = 1 then
                    Ok()
                else
                    Error CatalogFailure.ProductNotFound
        }

    member this.AdjustStock(productId: Guid, delta: int, ct: CancellationToken) : Task<Result<unit, CatalogFailure>> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """UPDATE fsnix.product_stock
                       SET on_hand = on_hand + @delta, updated_at = statement_timestamp()
                       WHERE product_id = @product_id
                         AND on_hand + @delta >= 0""",
                    connection
                )

            command.Parameters.AddWithValue("product_id", productId) |> ignore
            command.Parameters.AddWithValue("delta", delta) |> ignore
            let! affected = command.ExecuteNonQueryAsync(ct)

            if affected = 1 then
                return Ok()
            else
                let! exists = this.Get(productId, ct)

                return
                    match exists with
                    | None -> Error CatalogFailure.ProductNotFound
                    | Some _ -> Error CatalogFailure.InsufficientStock
        }
