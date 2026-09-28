namespace App.DatabaseTests

open System
open System.Threading.Tasks
open System.Threading
open App.Database
open App.Domain
open App.Orders
open App.Tests
open Npgsql

type OrderReservationTests(fixture: PostgreSqlFixture) =
    let dataSource = NpgsqlDataSource.Create fixture.ConnectionString

    let line productId orderLineId =
        { LineId = OrderLineId.create orderLineId |> Result.defaultWith Assert.Fail
          ProductId = ProductId.create productId |> Result.defaultWith Assert.Fail
          Sku = Sku.create "RACE-ITEM" |> Result.defaultWith Assert.Fail
          Name = NonEmptyString.create 100 "Race item" |> Result.defaultWith Assert.Fail
          UnitPrice = Money.create 1m "USD" |> Result.defaultWith Assert.Fail
          Quantity = Quantity.create 1 |> Result.defaultWith Assert.Fail
          PriceVersion = PriceVersion.create 1L |> Result.defaultWith Assert.Fail }

    let reserve orderId orderLineId productId =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()
            use! transaction = connection.BeginTransactionAsync()

            let! result =
                StockReservations.reserve
                    connection
                    transaction
                    orderId
                    1L
                    (DateTimeOffset.UtcNow.AddMinutes 5.)
                    "orders"
                    (int64 (Random.Shared.Next(1, Int32.MaxValue)))
                    0
                    [ line productId orderLineId ]
                    CancellationToken.None

            do! transaction.CommitAsync()
            return result
        }

    member _.``concurrent last-unit reservations allow exactly one order``() =
        task {
            let productId = Guid.Parse "00000000-0000-0000-0000-000000000001"
            use setup = dataSource.CreateConnection()
            do! setup.OpenAsync()

            use reset =
                new NpgsqlCommand("UPDATE fsnix.product_stock SET on_hand=1,reserved=0 WHERE product_id=@id", setup)

            reset.Parameters.AddWithValue("id", productId) |> ignore
            let! _ = reset.ExecuteNonQueryAsync()

            let! outcomes =
                [| reserve $"order:{Guid.NewGuid():D}" (Guid.NewGuid()) productId
                   reserve $"order:{Guid.NewGuid():D}" (Guid.NewGuid()) productId |]
                |> Task.WhenAll

            Assert.Equal(
                1,
                outcomes
                |> Array.sumBy (function
                    | Ok _ -> 1
                    | Error _ -> 0)
            )

            Assert.Equal(
                1,
                outcomes
                |> Array.sumBy (function
                    | Error ReservationFailure.InsufficientStock -> 1
                    | _ -> 0)
            )

            use verify =
                new NpgsqlCommand("SELECT reserved FROM fsnix.product_stock WHERE product_id=@id", setup)

            verify.Parameters.AddWithValue("id", productId) |> ignore
            let! reserved = verify.ExecuteScalarAsync()
            Assert.Equal(1, Convert.ToInt32 reserved)
        }

    member _.``release fences a delayed reserve action``() =
        task {
            let orderId = $"order:{Guid.NewGuid():D}"
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()
            use! transaction = connection.BeginTransactionAsync()
            do! StockReservations.release connection transaction orderId CancellationToken.None
            do! transaction.CommitAsync()

            use! delayed = connection.BeginTransactionAsync()
            let productId = Guid.Parse "00000000-0000-0000-0000-000000000002"

            let! result =
                StockReservations.reserve
                    connection
                    delayed
                    orderId
                    1L
                    (DateTimeOffset.UtcNow.AddMinutes 5.)
                    "orders"
                    1L
                    0
                    [ line productId (Guid.NewGuid()) ]
                    CancellationToken.None

            do! delayed.CommitAsync()
            Assert.Equal(Error ReservationFailure.InvalidReservation, result)
        }

    member _.``opposite order multi SKU reservations do not deadlock``() =
        task {
            use setup = dataSource.CreateConnection()
            do! setup.OpenAsync()

            use stock =
                new NpgsqlCommand(
                    "UPDATE fsnix.product_stock SET on_hand=10,reserved=0 WHERE product_id IN ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002')",
                    setup
                )

            let! _ = stock.ExecuteNonQueryAsync()
            let first = Guid.Parse "00000000-0000-0000-0000-000000000001"
            let second = Guid.Parse "00000000-0000-0000-0000-000000000002"

            let doMulti orderId products =
                task {
                    use connection = dataSource.CreateConnection()
                    do! connection.OpenAsync()
                    use! tx = connection.BeginTransactionAsync()

                    let! result =
                        StockReservations.reserve
                            connection
                            tx
                            orderId
                            1L
                            (DateTimeOffset.UtcNow.AddMinutes 5.)
                            "orders"
                            (int64 (Random.Shared.Next(1, Int32.MaxValue)))
                            0
                            (products |> List.map (fun product -> line product (Guid.NewGuid())))
                            CancellationToken.None

                    do! tx.CommitAsync()
                    return result
                }

            let! results =
                Task.WhenAll
                    [| doMulti $"order:{Guid.NewGuid():D}" [ first; second ]
                       doMulti $"order:{Guid.NewGuid():D}" [ second; first ] |]

            Assert.All(
                results,
                fun result ->
                    match result with
                    | Ok ids -> Assert.Equal(2, ids.Length)
                    | Error failure -> Assert.Fail $"Reservation failed: %A{failure}"
            )
        }

    member _.``order snapshots are immutable and idempotently captured``() =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()
            let id = Guid.NewGuid()

            let snapshot =
                { SnapshotId = id
                  OrderId = $"order:{id:D}"
                  CustomerId = Guid.NewGuid()
                  CustomerEmail = "buyer@example.test"
                  AddressJson = "{\"countryCode\":\"US\"}"
                  LinesJson = "[]"
                  Subtotal = 1m
                  Shipping = 0m
                  Tax = 0m
                  Total = 1m
                  Currency = "USD" }

            use! transaction = connection.BeginTransactionAsync()
            let! first = OrderSnapshots.insert connection transaction snapshot CancellationToken.None
            let! replay = OrderSnapshots.insert connection transaction snapshot CancellationToken.None
            Assert.Equal(Ok(), first)
            Assert.Equal(Ok(), replay)
            do! transaction.CommitAsync()

            use update =
                new NpgsqlCommand("UPDATE fsnix.order_snapshots SET total_amount=2 WHERE snapshot_id=@id", connection)

            update.Parameters.AddWithValue("id", id) |> ignore
            let! mutation = Assert.ThrowsAsync<PostgresException>(fun () -> update.ExecuteNonQueryAsync() :> Task)
            Assert.Equal("order snapshots are immutable", mutation.MessageText)
        }

    member _.``stock commit is idempotent and cannot be released``() =
        task {
            let productId = Guid.Parse "00000000-0000-0000-0000-000000000001"
            let orderId = $"order:{Guid.NewGuid():D}"

            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()

            use reset =
                new NpgsqlCommand(
                    "UPDATE fsnix.product_stock SET on_hand=5,reserved=0 WHERE product_id=@id",
                    connection
                )

            reset.Parameters.AddWithValue("id", productId) |> ignore
            let! _ = reset.ExecuteNonQueryAsync()

            use! reserveTx = connection.BeginTransactionAsync()

            let! reserved =
                StockReservations.reserve
                    connection
                    reserveTx
                    orderId
                    1L
                    (DateTimeOffset.UtcNow.AddMinutes 5.)
                    "orders"
                    9001L
                    0
                    [ line productId (Guid.NewGuid()) ]
                    CancellationToken.None

            Assert.True(Result.isOk reserved)
            do! reserveTx.CommitAsync()

            use! commitTx = connection.BeginTransactionAsync()
            let! committed = StockReservations.commit connection commitTx orderId CancellationToken.None
            do! commitTx.CommitAsync()
            Assert.Equal(1, committed)

            use! replayTx = connection.BeginTransactionAsync()
            let! replayed = StockReservations.commit connection replayTx orderId CancellationToken.None
            do! replayTx.CommitAsync()
            Assert.Equal(0, replayed)

            use! releaseTx = connection.BeginTransactionAsync()
            do! StockReservations.release connection releaseTx orderId CancellationToken.None
            do! releaseTx.CommitAsync()

            use verify =
                new NpgsqlCommand(
                    "SELECT s.on_hand,s.reserved,r.status FROM fsnix.product_stock s JOIN fsnix.stock_reservations r USING(product_id) WHERE r.order_id=@order",
                    connection
                )

            verify.Parameters.AddWithValue("order", orderId) |> ignore
            let! reader = verify.ExecuteReaderAsync()
            Assert.True(reader.Read())
            Assert.Equal(4, reader.GetInt32 0)
            Assert.Equal(0, reader.GetInt32 1)
            Assert.Equal("committed", reader.GetString 2)
        }
