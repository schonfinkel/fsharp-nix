namespace App.Database

open System
open System.Threading
open App.Database.Schema
open App.Database.Schema.fsnix
open App.Domain
open Npgsql
open NpgsqlTypes
open SqlHydra.Query

[<AutoOpen>]
module private FeatureFlagData =
    let utcOffset (value: DateTime) =
        DateTime.SpecifyKind(value, DateTimeKind.Utc) |> DateTimeOffset

    let postgresTimestamp (value: DateTime) =
        let microsecondTicks = TimeSpan.TicksPerMillisecond / 1000L
        DateTime(value.Ticks - value.Ticks % microsecondTicks, DateTimeKind.Utc)

    let required columnName =
        Option.defaultWith (fun () -> invalidOp $"The database view returned NULL for '{columnName}'.")

    let intervalFromValues name enabled validFrom validTo recordedAt : FeatureInterval =
        let flagName = name |> required "name"

        let flag =
            flagName
            |> FeatureFlag.tryParse
            |> Option.defaultWith (fun () -> invalidOp $"Unknown persisted feature flag '{flagName}'.")

        { Flag = flag
          State = enabled |> required "enabled" |> FeatureState.ofBool
          ValidFrom = validFrom |> required "valid_from" |> utcOffset
          ValidTo = validTo |> Option.map utcOffset
          RecordedAt = recordedAt |> required "recorded_at" |> utcOffset }

    let currentInterval (row: current_feature_flag_definitions) =
        intervalFromValues row.name row.enabled row.valid_from row.valid_to row.recorded_at

    let historyInterval (row: feature_flag_history) =
        intervalFromValues row.name row.enabled row.valid_from row.valid_to row.recorded_at

    let contains timestamp (range: NpgsqlRange<DateTime>) =
        let afterLower =
            range.LowerBoundInfinite
            || timestamp > range.LowerBound
            || (timestamp = range.LowerBound && range.LowerBoundIsInclusive)

        let beforeUpper =
            range.UpperBoundInfinite
            || timestamp < range.UpperBound
            || (timestamp = range.UpperBound && range.UpperBoundIsInclusive)

        afterLower && beforeUpper

    let addText (command: NpgsqlCommand) name value =
        command.Parameters.Add(name, NpgsqlDbType.Text).Value <- value

[<Sealed>]
type PostgresFeatureFlagStore(dataSource: NpgsqlDataSource) =
    let db = QueryContextFactory.Create dataSource

    interface IFeatureFlagStore with
        member _.GetCurrent(flag, cancellationToken) =
            task {
                let name = FeatureFlag.persistedName flag

                let! row =
                    selectTask db {
                        for definition in fsnix.current_feature_flag_definitions do
                            where (definition.name = Some name)
                            select definition
                            tryHead
                            cancel cancellationToken
                    }

                return row |> Option.map currentInterval
            }

        member _.GetAllCurrent(cancellationToken) =
            task {
                let! rows =
                    selectTask db {
                        for definition in fsnix.current_feature_flag_definitions do
                            orderBy definition.name
                            select definition
                            toList
                            cancel cancellationToken
                    }

                return rows |> List.map currentInterval
            }

        member _.GetSchedule(flag, cancellationToken) =
            task {
                let name = FeatureFlag.persistedName flag

                use! context = db.OpenContextAsync()

                let! current =
                    selectTask context {
                        for definition in fsnix.current_feature_flag_definitions do
                            where (definition.name = Some name)
                            select definition
                            tryHead
                            cancel cancellationToken
                    }

                let! history =
                    selectTask context {
                        for definition in fsnix.feature_flag_history do
                            where (definition.name = Some name)
                            orderByDescending definition.valid_from
                            select definition
                            toList
                            cancel cancellationToken
                    }

                let current = current |> Option.map currentInterval
                let history = history |> List.map historyInterval

                return
                    match current, history with
                    | Some current, _ -> Some { Current = current; History = history }
                    | None, [] -> None
                    | None, _ -> invalidOp $"Feature '{FeatureFlag.persistedName flag}' has no current interval."
            }

        member _.Schedule(change, now, cancellationToken) =
            task {
                let effectiveAt = Scheduling.effectiveTimestamp now change.EffectiveTime
                let effectiveUtc = effectiveAt.UtcDateTime |> postgresTimestamp
                let name = FeatureFlag.persistedName change.Flag
                use! context = db.OpenContextAsync()
                use! transaction = context.Connection.BeginTransactionAsync cancellationToken
                context.Transaction <- Some transaction

                let lockQuery =
                    select {
                        for persisted in fsnix.feature_flags do
                            where (persisted.name = name)
                            select persisted.name
                    }

                use lockFeature = context.BuildCommand(lockQuery.IR)
                lockFeature.CommandText <- lockFeature.CommandText.TrimEnd(';') + " FOR UPDATE"
                let! lockedName = lockFeature.ExecuteScalarAsync cancellationToken

                if isNull lockedName || lockedName = DBNull.Value then
                    do! transaction.RollbackAsync cancellationToken
                    context.Transaction <- None
                    return Error MissingFeature
                else
                    let desiredState = FeatureState.toBool change.State

                    let! intervals =
                        selectTask context {
                            for interval in fsnix.feature_flag_intervals do
                                where (interval.feature_name = name)
                                select interval
                                toList
                                cancel cancellationToken
                        }

                    match
                        intervals
                        |> List.tryFind (fun interval -> contains effectiveUtc interval.valid_during)
                    with
                    | None ->
                        do! transaction.RollbackAsync cancellationToken
                        context.Transaction <- None
                        return Error TemporalGap
                    | Some current when current.enabled = desiredState ->
                        do! transaction.RollbackAsync cancellationToken
                        context.Transaction <- None
                        return Ok Unchanged
                    | Some current when current.valid_during.LowerBound = effectiveUtc ->
                        do! transaction.RollbackAsync cancellationToken
                        context.Transaction <- None
                        return Error BoundaryExists
                    | Some current ->
                        let shortened =
                            NpgsqlRange<DateTime>(
                                current.valid_during.LowerBound,
                                current.valid_during.LowerBoundIsInclusive,
                                effectiveUtc,
                                false
                            )

                        let! updated =
                            updateTask context {
                                for interval in fsnix.feature_flag_intervals do
                                    set interval.valid_during shortened
                                    where (interval.feature_name = name && interval.valid_during = current.valid_during)
                                    cancel cancellationToken
                            }

                        if updated <> 1 then
                            invalidOp $"The locked interval for feature '{name}' could not be updated."

                        let nextRange =
                            if current.valid_during.UpperBoundInfinite then
                                NpgsqlRange<DateTime>(
                                    effectiveUtc,
                                    true,
                                    false,
                                    Unchecked.defaultof<DateTime>,
                                    false,
                                    true
                                )
                            else
                                NpgsqlRange<DateTime>(
                                    effectiveUtc,
                                    true,
                                    current.valid_during.UpperBound,
                                    current.valid_during.UpperBoundIsInclusive
                                )

                        let nextInterval: feature_flag_intervals =
                            { feature_name = name
                              enabled = desiredState
                              valid_during = nextRange
                              recorded_at = DateTime.UtcNow }

                        let! _ =
                            insertTask context {
                                for interval in fsnix.feature_flag_intervals do
                                    entity nextInterval
                                    excludeColumn interval.recorded_at
                                    cancel cancellationToken
                            }

                        let connection = context.Connection :?> NpgsqlConnection
                        let transaction = context.Transaction.Value :?> NpgsqlTransaction

                        use notify =
                            new NpgsqlCommand(
                                "SELECT pg_notify('fsnix_feature_schedule_changed', @feature_name)",
                                connection,
                                transaction
                            )

                        addText notify "feature_name" name
                        let! _ = notify.ExecuteNonQueryAsync cancellationToken
                        do! transaction.CommitAsync cancellationToken
                        context.Transaction <- None
                        return Ok Changed
            }
