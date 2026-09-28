namespace App.DatabaseTests

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Database
open App.Domain
open App.Tests
open Npgsql

type AccountFlowRequestTests(fixture: PostgreSqlFixture) =
    let dataSource = NpgsqlDataSource.Create fixture.ConnectionString

    let createUser () =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()
            let userId = Guid.NewGuid()
            let username = $"idempotency-{userId:N}"

            use command =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.users (id, username, normalized_username, email, normalized_email, security_stamp, concurrency_stamp)
                       VALUES (@id, @username, @normalized, @email, @normalized_email, 'security', 'concurrency')""",
                    connection
                )

            command.Parameters.AddWithValue("id", userId) |> ignore
            command.Parameters.AddWithValue("username", username) |> ignore

            command.Parameters.AddWithValue("normalized", username.ToUpperInvariant())
            |> ignore

            command.Parameters.AddWithValue("email", $"{username}@example.test") |> ignore

            command.Parameters.AddWithValue("normalized_email", $"{username.ToUpperInvariant()}@EXAMPLE.TEST")
            |> ignore

            let! _ = command.ExecuteNonQueryAsync()
            return userId
        }

    let hashes (key: string) (userId: Guid) (kind: FlowKind) (email: string) =
        let keyHash = SHA256.HashData(Encoding.UTF8.GetBytes key)

        let request =
            $"{userId:N}\n{FlowKind.wireName kind}\n{email.Trim().ToUpperInvariant()}"

        keyHash, SHA256.HashData(Encoding.UTF8.GetBytes request)

    let createRequest userId email key =
        let keyHash, requestHash = hashes key userId PasswordReset email

        AccountFlowRequests.create
            dataSource
            PasswordReset
            userId
            email
            (DateTimeOffset.UtcNow.AddHours 1.)
            keyHash
            requestHash
            CancellationToken.None

    let requestRows (userId: Guid) =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT flow_id, status FROM fsnix.account_flow_requests
                       WHERE user_id = @user_id AND flow_kind = 'password-reset'
                       ORDER BY created_at, flow_id""",
                    connection
                )

            command.Parameters.AddWithValue("user_id", userId) |> ignore
            let! reader = command.ExecuteReaderAsync()
            let rows = ResizeArray<Guid * string>()

            while reader.Read() do
                rows.Add(reader.GetGuid 0, reader.GetString 1)

            reader.Dispose()
            return List.ofSeq rows
        }

    member _.``same key and payload replay one durable request``() =
        task {
            let! userId = createUser ()
            let destination = "reset-idempotency@example.test"

            let! first = createRequest userId destination "request-key-0000000001"
            let! replay = createRequest userId destination "request-key-0000000001"
            let! conflict = createRequest userId "different@example.test" "request-key-0000000001"

            match first, replay, conflict with
            | Ok(AccountFlowRequestResult.Created firstId),
              Ok(AccountFlowRequestResult.Replayed replayId),
              Ok AccountFlowRequestResult.IdempotencyConflict ->
                Assert.Equal(firstId, replayId)
                let! rows = requestRows userId
                Assert.Equal([ firstId, "requested" ], rows)
            | other -> Assert.Fail $"Unexpected idempotency outcomes: %A{other}."
        }

    member _.``new key supersedes the active flow and cancels its deadline``() =
        task {
            let! userId = createUser ()
            let destination = "reset-supersede@example.test"

            let! first = createRequest userId destination "request-key-0000000002"
            let! second = createRequest userId destination "request-key-0000000003"

            match first, second with
            | Ok(AccountFlowRequestResult.Created firstId), Ok(AccountFlowRequestResult.Created secondId) ->
                Assert.NotEqual(firstId, secondId)
                let! rows = requestRows userId
                Assert.Equal(Map.ofList [ firstId, "superseded"; secondId, "requested" ], Map.ofList rows)

                use connection = new NpgsqlConnection(fixture.ConnectionString)
                do! connection.OpenAsync()

                use deadlines =
                    new NpgsqlCommand(
                        """SELECT flow_id, status FROM fsnix.flow_deadlines
                           WHERE flow_id = ANY(@flow_ids) ORDER BY flow_id""",
                        connection
                    )

                deadlines.Parameters.AddWithValue("flow_ids", [| firstId; secondId |]) |> ignore
                let! reader = deadlines.ExecuteReaderAsync()
                let statuses = ResizeArray<Guid * string>()

                while reader.Read() do
                    statuses.Add(reader.GetGuid 0, reader.GetString 1)

                reader.Dispose()
                Assert.Equal(Map.ofList [ firstId, "cancelled"; secondId, "pending" ], Map.ofList (List.ofSeq statuses))
            | other -> Assert.Fail $"Expected two distinct created requests, got %A{other}."
        }

    member _.``concurrent identical requests serialize to one created and one replay``() =
        task {
            let! userId = createUser ()
            let destination = "reset-race@example.test"
            let firstTask = createRequest userId destination "request-key-0000000004"
            let secondTask = createRequest userId destination "request-key-0000000004"
            let! results = Task.WhenAll [| firstTask; secondTask |]

            let created =
                results
                |> Array.choose (function
                    | Ok(AccountFlowRequestResult.Created id) -> Some id
                    | _ -> None)

            let replayed =
                results
                |> Array.choose (function
                    | Ok(AccountFlowRequestResult.Replayed id) -> Some id
                    | _ -> None)

            Assert.Equal(1, created.Length)
            Assert.Equal(1, replayed.Length)
            Assert.Equal(created[0], replayed[0])
            let! rows = requestRows userId
            Assert.Equal([ created[0], "requested" ], rows)
        }
