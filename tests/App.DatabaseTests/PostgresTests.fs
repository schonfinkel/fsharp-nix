namespace App.Tests

open System
open System.Threading
open System.Threading.Tasks
open App
open App.Database
open App.Domain
open App.Migrations
open Expecto
open Npgsql

type PostgresTests(fixture: PostgreSqlFixture) =
    let sameInstant (expected: DateTimeOffset) (actual: DateTimeOffset) =
        abs ((expected - actual).TotalMilliseconds) < 1.

    let execute sql =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)
            return! command.ExecuteNonQueryAsync()
        }

    let queryStrings connectionString sql =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)
            use! reader = command.ExecuteReaderAsync()
            let values = ResizeArray<string>()

            while! reader.ReadAsync() do
                values.Add(reader.GetString 0)

            return values |> Seq.toList
        }

    let queryOptionalDate connectionString sql =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand(sql, connection)
            let! value = command.ExecuteScalarAsync()

            return
                if isNull value || value = DBNull.Value then
                    None
                else
                    Some(DateTimeOffset(Convert.ToDateTime value))
        }

    let reset () =
        execute
            """
            TRUNCATE feature_flag_intervals, feature_flags CASCADE;
            WITH seed_time AS (SELECT CURRENT_TIMESTAMP AS value)
            INSERT INTO feature_flags (name)
            SELECT name
            FROM (VALUES ('NewDashboard'), ('BetaCheckout')) seed(name);

            WITH seed_time AS (SELECT CURRENT_TIMESTAMP AS value)
            INSERT INTO feature_flag_intervals (feature_name, enabled, valid_during)
            SELECT name, FALSE, tstzrange(seed_time.value, NULL, '[)')
            FROM seed_time
            CROSS JOIN (VALUES ('NewDashboard'), ('BetaCheckout')) seed(name);
            """

    let run test =
        task {
            let! _ = reset ()
            return! test ()
        }

    member _.``migration seeds current definitions and is idempotent``() =
        run (fun () ->
            task {
                let repeatableTimestampSql =
                    "SELECT applied_at FROM repeatable_migration_state WHERE script_name = 'App.Migrations.Migrations.repeatable.001_feature_flag_views.sql'"

                let! before = queryOptionalDate fixture.ConnectionString repeatableTimestampSql
                Assert.Equal(Ok(), Migrator.migrate fixture.ConnectionString "Development")
                let! after = queryOptionalDate fixture.ConnectionString repeatableTimestampSql
                Assert.Equal(before, after)

                let! journal =
                    queryStrings
                        fixture.ConnectionString
                        "SELECT scriptname FROM schemaversions ORDER BY schemaversionsid"

                Assert.Equal<string list>(
                    [ "App.Migrations.Migrations.init.001_extensions_and_repeatable_state.sql"
                      "App.Migrations.Migrations.main.001_feature_flags.sql"
                      "App.Migrations.Migrations.main.002_identity.sql"
                      "App.Migrations.Migrations.test.001_development_marker.sql" ],
                    journal
                )

                Assert.DoesNotContain(journal, fun name -> name.Contains ".repeatable.")

                let! nullableIdentityColumns =
                    queryStrings
                        fixture.ConnectionString
                        """
                        SELECT column_name
                        FROM information_schema.columns
                        WHERE table_schema = 'fsnix'
                          AND table_name = 'users'
                          AND is_nullable = 'YES'
                        ORDER BY column_name
                        """

                Assert.Equal<string list>([ "lockout_end"; "password_hash" ], nullableIdentityColumns)

                let! unboundedRanges =
                    queryStrings
                        fixture.ConnectionString
                        "SELECT COUNT(*)::TEXT FROM feature_flag_intervals WHERE upper_inf(valid_during)"

                Assert.Equal<string list>([ "2" ], unboundedRanges)
                use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
                let store = PostgresFeatureFlagStore(dataSource) :> IFeatureFlagStore
                let! definitions = store.GetAllCurrent CancellationToken.None
                Assert.Equal(2, definitions.Length)
                Assert.All(definitions, fun definition -> Assert.Equal(Disabled, definition.State))
            })

    member _.``production excludes test migrations and still applies repeatables``() =
        task {
            Assert.Equal(Ok(), Migrator.migrate fixture.ProductionConnectionString "Production")

            let! developmentMarker =
                queryStrings
                    fixture.ProductionConnectionString
                    "SELECT COALESCE(to_regclass('development_migration_marker')::text, '')"

            Assert.Equal<string list>([ "" ], developmentMarker)

            let! journal =
                queryStrings
                    fixture.ProductionConnectionString
                    "SELECT scriptname FROM schemaversions ORDER BY schemaversionsid"

            Assert.Equal<string list>(
                [ "App.Migrations.Migrations.init.001_extensions_and_repeatable_state.sql"
                  "App.Migrations.Migrations.main.001_feature_flags.sql"
                  "App.Migrations.Migrations.main.002_identity.sql" ],
                journal
            )

            let! repeatables =
                queryStrings
                    fixture.ProductionConnectionString
                    "SELECT script_name FROM repeatable_migration_state ORDER BY script_name"

            Assert.Equal<string list>(
                [ "App.Migrations.Migrations.repeatable.001_feature_flag_views.sql" ],
                repeatables
            )
        }

    member _.``immediate and future schedules split intervals``() =
        run (fun () ->
            task {
                use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
                let store = PostgresFeatureFlagStore(dataSource) :> IFeatureFlagStore
                let now = DateTimeOffset.UtcNow
                let future = now.AddHours 2.

                let! immediate =
                    store.Schedule(
                        { Flag = NewDashboard
                          State = Enabled
                          EffectiveTime = Immediate },
                        now,
                        CancellationToken.None
                    )

                let! scheduled =
                    store.Schedule(
                        { Flag = NewDashboard
                          State = Disabled
                          EffectiveTime = ScheduledAt future },
                        now,
                        CancellationToken.None
                    )

                Assert.Equal(Ok Changed, immediate)
                Assert.Equal(Ok Changed, scheduled)
                let! schedule = store.GetSchedule(NewDashboard, CancellationToken.None)
                let history = schedule.Value.History
                Assert.Equal(3, history.Length)
                Assert.Contains(history, fun entry -> sameInstant entry.ValidFrom future && entry.State = Disabled)
            })

    member _.``no-op scheduling neither writes nor publishes a notification``() =
        run (fun () ->
            task {
                use listener = new NpgsqlConnection(fixture.ConnectionString)
                do! listener.OpenAsync()
                use listen = new NpgsqlCommand("LISTEN fsnix_feature_schedule_changed", listener)
                let! _ = listen.ExecuteNonQueryAsync()
                use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
                let store = PostgresFeatureFlagStore(dataSource) :> IFeatureFlagStore
                let now = DateTimeOffset.UtcNow

                let! scheduled =
                    store.Schedule(
                        { Flag = NewDashboard
                          State = Disabled
                          EffectiveTime = ScheduledAt(now.AddMinutes 5.) },
                        now,
                        CancellationToken.None
                    )

                Assert.Equal(Ok Unchanged, scheduled)
                let! notified = listener.WaitAsync(TimeSpan.FromMilliseconds 100.)
                Assert.False notified

                let! schedule = store.GetSchedule(NewDashboard, CancellationToken.None)
                Assert.Equal(1, schedule.Value.History.Length)
            })

    member _.``earlier scheduling preserves an existing later boundary``() =
        run (fun () ->
            task {
                use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
                let store = PostgresFeatureFlagStore(dataSource) :> IFeatureFlagStore
                let now = DateTimeOffset.UtcNow
                let earlier = now.AddHours 1.
                let later = now.AddHours 2.

                let! _ =
                    store.Schedule(
                        { Flag = BetaCheckout
                          State = Enabled
                          EffectiveTime = ScheduledAt later },
                        now,
                        CancellationToken.None
                    )

                let! result =
                    store.Schedule(
                        { Flag = BetaCheckout
                          State = Enabled
                          EffectiveTime = ScheduledAt earlier },
                        now,
                        CancellationToken.None
                    )

                Assert.Equal(Ok Changed, result)
                let! schedule = store.GetSchedule(BetaCheckout, CancellationToken.None)
                let history = schedule.Value.History

                let inserted =
                    history |> List.find (fun entry -> sameInstant entry.ValidFrom earlier)

                Assert.True(inserted.ValidTo |> Option.exists (sameInstant later))
            })

    member _.``duplicate boundaries map to temporal conflict``() =
        run (fun () ->
            task {
                use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
                let store = PostgresFeatureFlagStore(dataSource) :> IFeatureFlagStore
                let now = DateTimeOffset.UtcNow
                let boundary = now.AddHours 1.

                let change enabled =
                    { Flag = NewDashboard
                      State = FeatureState.ofBool enabled
                      EffectiveTime = ScheduledAt boundary }

                let! _ = store.Schedule(change true, now, CancellationToken.None)
                let! unchanged = store.Schedule(change true, now, CancellationToken.None)
                let! duplicate = store.Schedule(change false, now, CancellationToken.None)
                Assert.Equal(Ok Unchanged, unchanged)
                Assert.Equal(Error BoundaryExists, duplicate)
            })

    member _.``concurrent duplicate schedules produce one change``() =
        run (fun () ->
            task {
                use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
                let store = PostgresFeatureFlagStore(dataSource) :> IFeatureFlagStore
                let now = DateTimeOffset.UtcNow
                let boundary = now.AddHours 1.

                let schedule () =
                    store.Schedule(
                        { Flag = NewDashboard
                          State = Enabled
                          EffectiveTime = ScheduledAt boundary },
                        now,
                        CancellationToken.None
                    )

                let! outcomes = Task.WhenAll(schedule (), schedule ())

                Assert.Equal(1, outcomes |> Array.filter ((=) (Ok Changed)) |> Array.length)
                Assert.Equal(1, outcomes |> Array.filter ((=) (Ok Unchanged)) |> Array.length)
                let! schedule = store.GetSchedule(NewDashboard, CancellationToken.None)
                let history = schedule.Value.History
                Assert.Equal(2, history.Length)
                Assert.Contains(history, fun interval -> sameInstant interval.ValidFrom boundary)
            })

    member _.``database rejects overlapping and empty ranges``() =
        run (fun () ->
            task {
                let! overlap =
                    Assert.ThrowsAsync<PostgresException>(fun () ->
                        execute
                            """
                            INSERT INTO feature_flag_intervals (feature_name, enabled, valid_during)
                            VALUES ('NewDashboard', TRUE, tstzrange(CURRENT_TIMESTAMP, NULL, '[)'));
                            """
                        :> System.Threading.Tasks.Task)

                Assert.Equal(PostgresErrorCodes.ExclusionViolation, overlap.SqlState)

                let! empty =
                    Assert.ThrowsAsync<PostgresException>(fun () ->
                        execute
                            """
                            INSERT INTO feature_flags (name) VALUES ('AnotherFlag');
                            INSERT INTO feature_flag_intervals (feature_name, enabled, valid_during)
                            VALUES ('AnotherFlag', TRUE, 'empty'::tstzrange);
                            """
                        :> System.Threading.Tasks.Task)

                Assert.Equal(PostgresErrorCodes.CheckViolation, empty.SqlState)
            })
