namespace App

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open App.Database
open App.Database.``public``
open Microsoft.Extensions.Caching.Memory
open Microsoft.FeatureManagement
open Npgsql
open NpgsqlTypes

type FeatureFlag =
    | NewDashboard
    | BetaCheckout

[<RequireQualifiedAccess>]
module FeatureFlag =
    let all = [ NewDashboard; BetaCheckout ]

    let persistedName =
        function
        | NewDashboard -> "NewDashboard"
        | BetaCheckout -> "BetaCheckout"

    let tryParse =
        function
        | "NewDashboard" -> Some NewDashboard
        | "BetaCheckout" -> Some BetaCheckout
        | _ -> None

type EffectiveTime =
    | Now
    | At of DateTimeOffset

type ScheduleChange =
    { Flag: FeatureFlag
      Enabled: bool
      EffectiveTime: EffectiveTime }

type CurrentDefinition =
    { Flag: FeatureFlag
      Enabled: bool
      FilterName: string option
      FilterParameters: string option
      ValidFrom: DateTimeOffset
      ValidTo: DateTimeOffset option
      RecordedAt: DateTimeOffset }

type HistoryEntry =
    { Flag: FeatureFlag
      Enabled: bool
      FilterName: string option
      FilterParameters: string option
      ValidFrom: DateTimeOffset
      ValidTo: DateTimeOffset option
      RecordedAt: DateTimeOffset }

type ScheduleValidationError =
    | UnknownFlag of string
    | InvalidEffectiveTime of string
    | EffectiveTimeIsInPast

type StoreFailure =
    | MissingFlag
    | TemporalGap
    | TemporalConflict
    | KnownDatabaseFailure of string

type WorkflowFailure =
    | ValidationFailure of ScheduleValidationError
    | FlagNotFound
    | Conflict
    | DatabaseFailure of string

[<RequireQualifiedAccess>]
module Scheduling =
    let parseEffectiveTime (now: DateTimeOffset) (value: string option) =
        match value |> Option.map _.Trim() with
        | None
        | Some "" -> Ok Now
        | Some value ->
            match DateTimeOffset.TryParse value with
            | true, timestamp when timestamp.ToUniversalTime() >= now.ToUniversalTime() ->
                Ok(At(timestamp.ToUniversalTime()))
            | true, _ -> Error EffectiveTimeIsInPast
            | false, _ -> Error(InvalidEffectiveTime value)

    let effectiveTimestamp now =
        function
        | Now -> now
        | At timestamp -> timestamp

    let create now name enabled effectiveTime =
        match FeatureFlag.tryParse name with
        | None -> Error(UnknownFlag name)
        | Some flag ->
            parseEffectiveTime now effectiveTime
            |> Result.map (fun parsed ->
                { Flag = flag
                  Enabled = enabled
                  EffectiveTime = parsed })

[<RequireQualifiedAccess>]
module Projections =
    let private required columnName =
        Option.defaultWith (fun () -> invalidOp $"The database view returned NULL for '{columnName}'.")

    let private requireFlag name =
        FeatureFlag.tryParse name
        |> Option.defaultWith (fun () -> invalidOp $"Unknown persisted feature flag '{name}'.")

    let private timestamp columnName value =
        value |> required columnName |> DateTimeOffset

    let current (row: current_feature_flag_definitions) : CurrentDefinition =
        { Flag = row.name |> required "name" |> requireFlag
          Enabled = row.enabled |> required "enabled"
          FilterName = row.filter_name
          FilterParameters = row.filter_parameters
          ValidFrom = row.valid_from |> timestamp "valid_from"
          ValidTo = row.valid_to |> Option.map DateTimeOffset
          RecordedAt = row.recorded_at |> timestamp "recorded_at" }

    let history (row: feature_flag_history) : HistoryEntry =
        { Flag = row.name |> required "name" |> requireFlag
          Enabled = row.enabled |> required "enabled"
          FilterName = row.filter_name
          FilterParameters = row.filter_parameters
          ValidFrom = row.valid_from |> timestamp "valid_from"
          ValidTo = row.valid_to |> Option.map DateTimeOffset
          RecordedAt = row.recorded_at |> timestamp "recorded_at" }

type IFeatureFlagStore =
    abstract member GetCurrent: FeatureFlag * CancellationToken -> Task<CurrentDefinition option>
    abstract member GetAllCurrent: CancellationToken -> Task<CurrentDefinition list>
    abstract member GetHistory: FeatureFlag * CancellationToken -> Task<HistoryEntry list>
    abstract member Schedule: ScheduleChange * DateTimeOffset * CancellationToken -> Task<Result<unit, StoreFailure>>

[<AutoOpen>]
module private DataReader =
    let dateTimeOffset (reader: NpgsqlDataReader) ordinal =
        reader.GetDateTime(ordinal)
        |> fun value -> DateTime.SpecifyKind(value, DateTimeKind.Utc)
        |> DateTimeOffset

    let optionalDateTimeOffset (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            None
        else
            Some(dateTimeOffset reader ordinal)

    let optionalString (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            None
        else
            Some(reader.GetString ordinal)

    let currentRow (reader: NpgsqlDataReader) : current_feature_flag_definitions =
        { name = Some(reader.GetString 0)
          enabled = Some(reader.GetBoolean 1)
          filter_name = optionalString reader 2
          filter_parameters = optionalString reader 3
          valid_from = Some(reader.GetDateTime 4)
          valid_to =
            if reader.IsDBNull 5 then
                None
            else
                Some(reader.GetDateTime 5)
          recorded_at = Some(reader.GetDateTime 6) }

    let historyRow (reader: NpgsqlDataReader) : feature_flag_history =
        { name = Some(reader.GetString 0)
          enabled = Some(reader.GetBoolean 1)
          filter_name = optionalString reader 2
          filter_parameters = optionalString reader 3
          valid_from = Some(reader.GetDateTime 4)
          valid_to =
            if reader.IsDBNull 5 then
                None
            else
                Some(reader.GetDateTime 5)
          recorded_at = Some(reader.GetDateTime 6) }

type PostgresFeatureFlagStore(dataSource: NpgsqlDataSource) =
    let currentColumns =
        "name, enabled, filter_name, filter_parameters, valid_from, valid_to, recorded_at"

    let addTextParameter (command: NpgsqlCommand) name value =
        command.Parameters.Add(name, NpgsqlDbType.Text).Value <- value

    let addTimestampParameter (command: NpgsqlCommand) name (value: DateTimeOffset) =
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value <- value

    interface IFeatureFlagStore with
        member _.GetCurrent(flag, cancellationToken) =
            task {
                use! connection = dataSource.OpenConnectionAsync cancellationToken

                use command =
                    new NpgsqlCommand(
                        $"SELECT {currentColumns} FROM current_feature_flag_definitions WHERE name = @name",
                        connection
                    )

                addTextParameter command "name" (FeatureFlag.persistedName flag)
                use! reader = command.ExecuteReaderAsync cancellationToken

                let! hasRow = reader.ReadAsync cancellationToken

                return
                    if hasRow then
                        currentRow reader |> Projections.current |> Some
                    else
                        None
            }

        member _.GetAllCurrent(cancellationToken) =
            task {
                use! connection = dataSource.OpenConnectionAsync cancellationToken

                use command =
                    new NpgsqlCommand(
                        $"SELECT {currentColumns} FROM current_feature_flag_definitions ORDER BY name",
                        connection
                    )

                use! reader = command.ExecuteReaderAsync cancellationToken
                let rows = ResizeArray<CurrentDefinition>()
                let mutable reading = true

                while reading do
                    let! hasRow = reader.ReadAsync cancellationToken

                    if hasRow then
                        rows.Add(currentRow reader |> Projections.current)
                    else
                        reading <- false

                return List.ofSeq rows
            }

        member _.GetHistory(flag, cancellationToken) =
            task {
                use! connection = dataSource.OpenConnectionAsync cancellationToken

                use command =
                    new NpgsqlCommand(
                        $"SELECT {currentColumns} FROM feature_flag_history WHERE name = @name ORDER BY valid_from DESC",
                        connection
                    )

                addTextParameter command "name" (FeatureFlag.persistedName flag)
                use! reader = command.ExecuteReaderAsync cancellationToken
                let rows = ResizeArray<HistoryEntry>()
                let mutable reading = true

                while reading do
                    let! hasRow = reader.ReadAsync cancellationToken

                    if hasRow then
                        rows.Add(historyRow reader |> Projections.history)
                    else
                        reading <- false

                return List.ofSeq rows
            }

        member _.Schedule(change, now, cancellationToken) =
            task {
                let effectiveAt = Scheduling.effectiveTimestamp now change.EffectiveTime
                let name = FeatureFlag.persistedName change.Flag

                try
                    use! connection = dataSource.OpenConnectionAsync cancellationToken
                    use! transaction = connection.BeginTransactionAsync cancellationToken

                    use select =
                        new NpgsqlCommand(
                            """
                            SELECT lower(valid_during), upper(valid_during)
                            FROM feature_flags
                            WHERE name = @name AND valid_during @> @effective_at
                            FOR UPDATE
                            """,
                            connection,
                            transaction
                        )

                    addTextParameter select "name" name
                    addTimestampParameter select "effective_at" effectiveAt
                    use! reader = select.ExecuteReaderAsync cancellationToken
                    let! found = reader.ReadAsync cancellationToken

                    if not found then
                        do! reader.DisposeAsync()

                        use exists =
                            new NpgsqlCommand(
                                "SELECT EXISTS (SELECT 1 FROM feature_flags WHERE name = @name)",
                                connection,
                                transaction
                            )

                        addTextParameter exists "name" name
                        let! flagExists = exists.ExecuteScalarAsync cancellationToken
                        do! transaction.RollbackAsync cancellationToken

                        return
                            if Convert.ToBoolean flagExists then
                                Error TemporalGap
                            else
                                Error MissingFlag
                    else
                        let lowerBound = dateTimeOffset reader 0
                        let upperBound = dateTimeOffset reader 1
                        do! reader.DisposeAsync()

                        if lowerBound = effectiveAt then
                            do! transaction.RollbackAsync cancellationToken
                            return Error TemporalConflict
                        else
                            use shorten =
                                new NpgsqlCommand(
                                    """
                                    UPDATE feature_flags
                                    SET valid_during = tstzrange(lower(valid_during), @effective_at, '[)')
                                    WHERE name = @name AND lower(valid_during) = @lower_bound
                                    """,
                                    connection,
                                    transaction
                                )

                            addTextParameter shorten "name" name
                            addTimestampParameter shorten "effective_at" effectiveAt
                            addTimestampParameter shorten "lower_bound" lowerBound
                            let! updated = shorten.ExecuteNonQueryAsync cancellationToken

                            if updated <> 1 then
                                do! transaction.RollbackAsync cancellationToken
                                return Error(KnownDatabaseFailure "The selected flag version changed concurrently.")
                            else
                                use insert =
                                    new NpgsqlCommand(
                                        """
                                        INSERT INTO feature_flags
                                            (name, enabled, filter_name, filter_parameters, valid_during)
                                        VALUES
                                            (@name, @enabled, '', '{}'::JSONB,
                                             tstzrange(@effective_at, @upper_bound, '[)'))
                                        """,
                                        connection,
                                        transaction
                                    )

                                addTextParameter insert "name" name
                                insert.Parameters.Add("enabled", NpgsqlDbType.Boolean).Value <- change.Enabled
                                addTimestampParameter insert "effective_at" effectiveAt

                                addTimestampParameter insert "upper_bound" upperBound

                                let! _ = insert.ExecuteNonQueryAsync cancellationToken
                                do! transaction.CommitAsync cancellationToken
                                return Ok()
                with
                | :? PostgresException as error when
                    error.SqlState = PostgresErrorCodes.ExclusionViolation
                    || error.SqlState = PostgresErrorCodes.UniqueViolation
                    || error.SqlState = PostgresErrorCodes.CheckViolation
                    || error.SqlState = PostgresErrorCodes.DataException
                    ->
                    return Error TemporalConflict
                | :? PostgresException as error when
                    error.SqlState = PostgresErrorCodes.SerializationFailure
                    || error.SqlState = PostgresErrorCodes.DeadlockDetected
                    ->
                    return Error(KnownDatabaseFailure "The database could not serialize the scheduling change.")
            }

type CachingFeatureFlagStore(inner: IFeatureFlagStore, cache: IMemoryCache, ?expiration: TimeSpan) =
    let aggregateKey = "feature-flags:all"

    let currentKey flag =
        $"feature-flags:{FeatureFlag.persistedName flag}"

    let cacheOptions =
        MemoryCacheEntryOptions(AbsoluteExpirationRelativeToNow = defaultArg expiration (TimeSpan.FromSeconds 30.))

    let cacheTask key (factory: unit -> Task<'value>) =
        cache.GetOrCreateAsync(
            key,
            fun entry ->
                entry.SetOptions cacheOptions |> ignore
                factory ()
        )

    member _.Evict(flag) =
        cache.Remove(currentKey flag)
        cache.Remove(aggregateKey)

    interface IFeatureFlagStore with
        member _.GetCurrent(flag, cancellationToken) =
            cacheTask (currentKey flag) (fun () -> inner.GetCurrent(flag, cancellationToken))

        member _.GetAllCurrent(cancellationToken) =
            cacheTask aggregateKey (fun () -> inner.GetAllCurrent cancellationToken)

        member _.GetHistory(flag, cancellationToken) =
            inner.GetHistory(flag, cancellationToken)

        member this.Schedule(change, now, cancellationToken) =
            task {
                let! result = inner.Schedule(change, now, cancellationToken)

                match result with
                | Ok() -> this.Evict change.Flag
                | Error _ -> ()

                return result
            }

[<RequireQualifiedAccess>]
module FeatureDefinitions =
    let fromCurrent (definition: CurrentDefinition) =
        let filters: IEnumerable<FeatureFilterConfiguration> =
            if definition.Enabled then
                [ FeatureFilterConfiguration(Name = "AlwaysOn") ]
            else
                []

        FeatureDefinition(Name = FeatureFlag.persistedName definition.Flag, EnabledFor = filters)

type private AsyncEnumerator<'value>(values: 'value array, cancellationToken: CancellationToken) =
    let mutable index = -1

    interface IAsyncEnumerator<'value> with
        member _.Current = values[index]

        member _.MoveNextAsync() =
            cancellationToken.ThrowIfCancellationRequested()
            index <- index + 1
            ValueTask<bool>(index < values.Length)

        member _.DisposeAsync() = ValueTask()

type private AsyncEnumerable<'value>(values: 'value seq) =
    let values = Array.ofSeq values

    interface IAsyncEnumerable<'value> with
        member _.GetAsyncEnumerator(cancellationToken) =
            AsyncEnumerator(values, cancellationToken) :> IAsyncEnumerator<'value>

type DatabaseFeatureDefinitionProvider(store: IFeatureFlagStore) =
    interface IFeatureDefinitionProvider with
        member _.GetFeatureDefinitionAsync(name: string) =
            task {
                match FeatureFlag.tryParse name with
                | None -> return null
                | Some flag ->
                    let! definition = store.GetCurrent(flag, CancellationToken.None)

                    return definition |> Option.map FeatureDefinitions.fromCurrent |> Option.toObj
            }

        member _.GetAllFeatureDefinitionsAsync() =
            { new IAsyncEnumerable<FeatureDefinition> with
                member _.GetAsyncEnumerator(cancellationToken) =
                    let definitions =
                        task {
                            let! current = store.GetAllCurrent cancellationToken
                            return current |> Seq.map FeatureDefinitions.fromCurrent |> Array.ofSeq
                        }

                    let mutable enumerator: IAsyncEnumerator<FeatureDefinition> option = None
                    let mutable index = -1

                    { new IAsyncEnumerator<FeatureDefinition> with
                        member _.Current =
                            match enumerator with
                            | Some value -> value.Current
                            | None -> invalidOp "The async feature definition enumerator has not started."

                        member _.MoveNextAsync() =
                            ValueTask<bool>(
                                task {
                                    match enumerator with
                                    | None ->
                                        let! values = definitions
                                        let created = AsyncEnumerator(values, cancellationToken) :> IAsyncEnumerator<_>
                                        enumerator <- Some created
                                        return! created.MoveNextAsync().AsTask()
                                    | Some value -> return! value.MoveNextAsync().AsTask()
                                }
                            )

                        member _.DisposeAsync() =
                            match enumerator with
                            | Some value -> value.DisposeAsync()
                            | None -> ValueTask() } }

[<RequireQualifiedAccess>]
module Workflow =
    let schedule (store: IFeatureFlagStore) now change cancellationToken =
        task {
            let! result = store.Schedule(change, now, cancellationToken)

            return
                result
                |> Result.mapError (function
                    | MissingFlag -> FlagNotFound
                    | TemporalGap
                    | TemporalConflict -> Conflict
                    | KnownDatabaseFailure message -> DatabaseFailure message)
        }
