namespace App.Tests

open System
open System.Threading
open System.Threading.Tasks
open App
open App.Migrations
open Npgsql
open Xunit

type PostgreSqlFixture() =
    let baseConnectionString =
        Environment.GetEnvironmentVariable "ConnectionStrings__App"
        |> Option.ofObj
        |> Option.defaultValue "Host=127.0.0.1;Port=5432;Database=fsnix;Username=fsnix;Password=fsnix"

    let connectionStringFor database =
        let builder = NpgsqlConnectionStringBuilder baseConnectionString
        builder.Database <- database
        builder.ConnectionString

    let testConnectionString = connectionStringFor "fsnix_tests"
    let productionTestConnectionString = connectionStringFor "fsnix_tests_production"

    member _.ConnectionString = testConnectionString
    member _.ProductionConnectionString = productionTestConnectionString

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let adminBuilder = NpgsqlConnectionStringBuilder baseConnectionString
                adminBuilder.Database <- "postgres"
                use connection = new NpgsqlConnection(adminBuilder.ConnectionString)
                do! connection.OpenAsync()

                NpgsqlConnection.ClearAllPools()

                for commandText in
                    [ "DROP DATABASE IF EXISTS fsnix_tests WITH (FORCE)"
                      "DROP DATABASE IF EXISTS fsnix_tests_production WITH (FORCE)"
                      "CREATE DATABASE fsnix_tests OWNER fsnix"
                      "CREATE DATABASE fsnix_tests_production OWNER fsnix" ] do
                    use command = new NpgsqlCommand(commandText, connection)
                    let! _ = command.ExecuteNonQueryAsync()
                    ()

                match Migrator.migrate testConnectionString "Development" with
                | Ok() -> ()
                | Error failure -> raise (InvalidOperationException(failure.Message, Option.toObj failure.Exception))
            }

        member _.DisposeAsync() = Task.CompletedTask

[<CollectionDefinition("postgres")>]
type PostgreSqlCollection() =
    interface ICollectionFixture<PostgreSqlFixture>

[<Collection("postgres")>]
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
            TRUNCATE feature_flags;
            WITH seed_time AS (SELECT CURRENT_TIMESTAMP AS value)
            INSERT INTO feature_flags (name, enabled, valid_during)
            SELECT name, enabled, tstzrange(seed_time.value, 'infinity'::TIMESTAMPTZ, '[)')
            FROM seed_time
            CROSS JOIN (VALUES ('NewDashboard', FALSE), ('BetaCheckout', FALSE)) seed(name, enabled);
            """

    let run test =
        task {
            let! _ = reset ()
            return! test ()
        }

    [<Fact>]
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

                let! nullableColumns =
                    queryStrings
                        fixture.ConnectionString
                        """
                        SELECT COUNT(*)::TEXT
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name IN (
                              'feature_flags',
                              'users',
                              'user_tokens',
                              'repeatable_migration_state',
                              'development_migration_marker'
                          )
                          AND is_nullable = 'YES'
                        """

                Assert.Equal<string list>([ "0" ], nullableColumns)

                let! unboundedRanges =
                    queryStrings
                        fixture.ConnectionString
                        "SELECT COUNT(*)::TEXT FROM feature_flags WHERE upper_inf(valid_during)"

                Assert.Equal<string list>([ "0" ], unboundedRanges)
                use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
                let store = PostgresFeatureFlagStore(dataSource) :> IFeatureFlagStore
                let! definitions = store.GetAllCurrent CancellationToken.None
                Assert.Equal(2, definitions.Length)
                Assert.All(definitions, fun definition -> Assert.False definition.Enabled)
            })

    [<Fact>]
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

    [<Fact>]
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
                          Enabled = true
                          EffectiveTime = Now },
                        now,
                        CancellationToken.None
                    )

                let! scheduled =
                    store.Schedule(
                        { Flag = NewDashboard
                          Enabled = false
                          EffectiveTime = At future },
                        now,
                        CancellationToken.None
                    )

                Assert.Equal(Ok(), immediate)
                Assert.Equal(Ok(), scheduled)
                let! history = store.GetHistory(NewDashboard, CancellationToken.None)
                Assert.Equal(3, history.Length)
                Assert.Contains(history, fun entry -> sameInstant entry.ValidFrom future && not entry.Enabled)
            })

    [<Fact>]
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
                          Enabled = false
                          EffectiveTime = At later },
                        now,
                        CancellationToken.None
                    )

                let! result =
                    store.Schedule(
                        { Flag = BetaCheckout
                          Enabled = true
                          EffectiveTime = At earlier },
                        now,
                        CancellationToken.None
                    )

                Assert.Equal(Ok(), result)
                let! history = store.GetHistory(BetaCheckout, CancellationToken.None)

                let inserted =
                    history |> List.find (fun entry -> sameInstant entry.ValidFrom earlier)

                Assert.True(inserted.ValidTo |> Option.exists (sameInstant later))
            })

    [<Fact>]
    member _.``duplicate boundaries map to temporal conflict``() =
        run (fun () ->
            task {
                use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
                let store = PostgresFeatureFlagStore(dataSource) :> IFeatureFlagStore
                let now = DateTimeOffset.UtcNow
                let boundary = now.AddHours 1.

                let change enabled =
                    { Flag = NewDashboard
                      Enabled = enabled
                      EffectiveTime = At boundary }

                let! _ = store.Schedule(change true, now, CancellationToken.None)
                let! duplicate = store.Schedule(change false, now, CancellationToken.None)
                Assert.Equal(Error TemporalConflict, duplicate)
            })

    [<Fact>]
    member _.``database rejects overlapping and empty ranges``() =
        run (fun () ->
            task {
                let! overlap =
                    Assert.ThrowsAsync<PostgresException>(fun () ->
                        execute
                            """
                            INSERT INTO feature_flags (name, enabled, valid_during)
                            VALUES ('NewDashboard', TRUE, tstzrange(CURRENT_TIMESTAMP, 'infinity'::TIMESTAMPTZ, '[)'));
                            """
                        :> System.Threading.Tasks.Task)

                Assert.Equal(PostgresErrorCodes.ExclusionViolation, overlap.SqlState)

                let! empty =
                    Assert.ThrowsAsync<PostgresException>(fun () ->
                        execute
                            """
                            INSERT INTO feature_flags (name, enabled, valid_during)
                            VALUES ('AnotherFlag', TRUE, 'empty'::tstzrange);
                            """
                        :> System.Threading.Tasks.Task)

                Assert.Equal(PostgresErrorCodes.CheckViolation, empty.SqlState)
            })
