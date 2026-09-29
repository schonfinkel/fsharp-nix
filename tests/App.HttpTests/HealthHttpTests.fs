namespace App.Tests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open App
open Npgsql

type HealthHttpTests(fixture: PostgreSqlFixture) =
    let waitForReady (client: HttpClient) =
        task {
            let deadline = DateTimeOffset.UtcNow.AddSeconds 10.
            let mutable response: HttpResponseMessage option = None

            let notReady () =
                response
                |> Option.exists (fun item -> item.StatusCode = HttpStatusCode.OK)
                |> not

            while DateTimeOffset.UtcNow < deadline && notReady () do
                let! current = client.GetAsync "/health/ready"
                response <- Some current

                if current.StatusCode <> HttpStatusCode.OK then
                    current.Dispose()
                    do! Task.Delay(TimeSpan.FromMilliseconds 100.)

            return
                response
                |> Option.defaultWith (fun () -> Assert.Fail "Readiness returned no response.")
        }

    member _.``public health probes are minimal and no-store``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use client = factory.CreateClient()

            for (path: string), (expected: string) in [ "/health/live", "live"; "/health/startup", "started" ] do
                use! response = client.GetAsync path
                let! body = response.Content.ReadAsStringAsync()
                Assert.Equal(HttpStatusCode.OK, response.StatusCode)
                Assert.Equal(expected, body)
                Assert.Contains("no-store", response.Headers.CacheControl.ToString())
                Assert.Equal("text/plain", response.Content.Headers.ContentType.MediaType)

            use! ready = waitForReady client
            let! readyBody = ready.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode)
            Assert.Equal("ready", readyBody)
            Assert.Contains("no-store", ready.Headers.CacheControl.ToString())
            Assert.DoesNotContain(fixture.ConnectionString, readyBody)
        }

    member _.``operational health requires MFA and renders only aggregates``() =
        task {
            let canary = "CANARY-token-password@example.test"
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use insert =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.integration_outbox
                           (callback_key, machine_id, entity_id, event, status, attempts, max_attempts,
                            last_error, failed_at)
                       VALUES (@canary, 'secret-machine', @canary, jsonb_build_object('token', @canary),
                               'dead', 10, 10, @canary, statement_timestamp())""",
                    connection
                )

            insert.Parameters.AddWithValue("canary", canary) |> ignore
            let! _ = insert.ExecuteNonQueryAsync()

            use anonymousFactory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use anonymous =
                anonymousFactory.CreateClient(
                    Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions(AllowAutoRedirect = false)
                )

            use! denied = anonymous.GetAsync "/admin/operations"
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode)

            use factory = new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString)
            use client = factory.CreateClient()
            use! response = client.GetAsync "/admin/operations"
            let! html = response.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Contains("Runtime and delivery health", html)
            Assert.Contains("dead rows requiring reconciliation", html)
            Assert.Contains("Gateway reconciliation", html)
            Assert.Contains("gateway-reconciliation-scanner", html)
            Assert.Contains("shipment-lost-scanner", html)
            Assert.DoesNotContain(canary, html)
            Assert.DoesNotContain("last_error", html)
            Assert.Contains("no-store", response.Headers.CacheControl.ToString())
        }
