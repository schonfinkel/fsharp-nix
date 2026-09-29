namespace App.DatabaseTests

open System
open System.Threading
open App.Database
open App.Tests
open Expecto
open Npgsql

type EmailOutboxTests(fixture: PostgreSqlFixture) =
    let dataSource = AutomataStore.createDataSource fixture.ConnectionString
    let relayOptions = EmailOutbox.RelayOptions.defaults "email-test-relay"

    let insertPendingEmail (maxAttempts: int) =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()
            let userId = Guid.NewGuid()
            let flowId = Guid.NewGuid()

            use user =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.users (id, username, normalized_username, email, normalized_email, security_stamp, concurrency_stamp)
                       VALUES (@id, @username, @normalized, @email, @normalized_email, 'security', 'concurrency')""",
                    connection
                )

            let username = $"email-test-{userId:N}"
            user.Parameters.AddWithValue("id", userId) |> ignore
            user.Parameters.AddWithValue("username", username) |> ignore

            user.Parameters.AddWithValue("normalized", username.ToUpperInvariant())
            |> ignore

            user.Parameters.AddWithValue("email", $"{username}@example.test") |> ignore

            user.Parameters.AddWithValue("normalized_email", $"{username.ToUpperInvariant()}@EXAMPLE.TEST")
            |> ignore

            let! _ = user.ExecuteNonQueryAsync()

            use request =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.account_flow_requests (flow_id, flow_kind, user_id, destination_email, expires_at)
                       VALUES (@flow_id, 'email-verification', @user_id, @destination, statement_timestamp() + interval '1 hour')""",
                    connection
                )

            request.Parameters.AddWithValue("flow_id", flowId) |> ignore
            request.Parameters.AddWithValue("user_id", userId) |> ignore

            request.Parameters.AddWithValue("destination", $"{username}@example.test")
            |> ignore

            let! _ = request.ExecuteNonQueryAsync()

            use email =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.account_email_outbox (flow_id, generation, protected_payload, encryption_version, max_attempts)
                       VALUES (@flow_id, 1, '\\x6b1a7f'::bytea, 1, @max_attempts)
                       RETURNING email_id""",
                    connection
                )

            email.Parameters.AddWithValue("flow_id", flowId) |> ignore
            email.Parameters.AddWithValue("max_attempts", maxAttempts) |> ignore
            let! emailId = email.ExecuteScalarAsync()
            return flowId, (emailId :?> int64)
        }

    let claimOne () =
        task {
            let! claimed = EmailOutbox.claim dataSource relayOptions CancellationToken.None

            match claimed with
            | [ email ] -> return email
            | other -> return Assert.Fail $"Expected exactly one claimed email, got %d{other.Length}."
        }

    let emailRow (emailId: int64) =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT status, attempts, protected_payload IS NULL, last_error
                       FROM fsnix.account_email_outbox WHERE email_id = @email_id""",
                    connection
                )

            command.Parameters.AddWithValue("email_id", emailId) |> ignore
            let! reader = command.ExecuteReaderAsync()

            if reader.Read() then
                let row =
                    (reader.GetString 0,
                     reader.GetInt32 1,
                     reader.GetBoolean 2,
                     (if reader.IsDBNull 3 then None else Some(reader.GetString 3)))

                reader.Dispose()
                return row
            else
                reader.Dispose()
                return Assert.Fail $"Expected the email row %d{emailId}."
        }

    let outboxCallback (callbackKey: string) =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT event->>'tag', (event->>'generation')::int
                       FROM fsnix.integration_outbox WHERE callback_key = @callback_key""",
                    connection
                )

            command.Parameters.AddWithValue("callback_key", callbackKey) |> ignore
            let! reader = command.ExecuteReaderAsync()

            if reader.Read() then
                let row = (reader.GetString 0, reader.GetInt32 1)
                reader.Dispose()
                return Some row
            else
                reader.Dispose()
                return None
        }

    member _.``claim leases a batch and hides it from competing owners``() =
        task {
            let! _, emailId = insertPendingEmail 3
            let! email = claimOne ()
            Assert.Equal(emailId, email.EmailId)
            Assert.Equal(1, email.Attempts)

            let competing = EmailOutbox.RelayOptions.defaults "email-test-relay-competitor"

            let! visible = EmailOutbox.claim dataSource competing CancellationToken.None

            let stillLeased = visible |> List.filter (fun row -> row.EmailId = emailId)

            Assert.Empty stillLeased

            let! status, attempts, payloadErased, _ = emailRow emailId
            Assert.Equal("pending", status)
            Assert.Equal(1, attempts)
            Assert.False payloadErased
        }

    member _.``settle sent erases the payload and enqueues the generation callback``() =
        task {
            let! flowId, emailId = insertPendingEmail 3
            let! email = claimOne ()

            match! EmailOutbox.settleSent dataSource relayOptions email CancellationToken.None with
            | Ok true -> ()
            | other -> Assert.Fail $"Expected the sent settlement, got %A{other}."

            let! status, _, payloadErased, lastError = emailRow emailId
            Assert.Equal("sent", status)
            Assert.True(payloadErased)
            Assert.Equal(None, lastError)

            let! callback = outboxCallback (EmailOutbox.sentCallbackKey flowId 1)
            Assert.Equal(Some("notification-sent-v1", 1), callback)
        }

    member _.``settlement is fenced by the claiming lease owner``() =
        task {
            let! flowId, emailId = insertPendingEmail 3
            let! email = claimOne ()

            let foreign = EmailOutbox.RelayOptions.defaults "email-test-relay-foreign"

            match! EmailOutbox.settleSent dataSource foreign email CancellationToken.None with
            | Ok false -> ()
            | other -> Assert.Fail $"Expected a fenced settlement to be refused, got %A{other}."

            let! status, _, payloadErased, _ = emailRow emailId
            Assert.Equal("pending", status)
            Assert.False payloadErased

            let! callback = outboxCallback (EmailOutbox.sentCallbackKey flowId 1)
            Assert.Equal(None, callback)
        }

    member _.``retryable failure releases with backoff and keeps the payload``() =
        task {
            let! flowId, emailId = insertPendingEmail 3
            let! email = claimOne ()

            match! EmailOutbox.fail dataSource relayOptions email "smtp-status-451" false CancellationToken.None with
            | Ok() -> ()
            | Error message -> Assert.Fail message

            let! status, attempts, payloadErased, lastError = emailRow emailId
            Assert.Equal("pending", status)
            Assert.Equal(1, attempts)
            Assert.False payloadErased
            Assert.Equal(Some "smtp-status-451", lastError)

            let! callback = outboxCallback (EmailOutbox.failedCallbackKey flowId 1)
            Assert.Equal(None, callback)
        }

    member _.``permanent failure dead-letters erases the payload and notifies the flow``() =
        task {
            let! flowId, emailId = insertPendingEmail 3
            let! email = claimOne ()

            match! EmailOutbox.fail dataSource relayOptions email "smtp-status-550" true CancellationToken.None with
            | Ok() -> ()
            | Error message -> Assert.Fail message

            let! status, _, payloadErased, lastError = emailRow emailId
            Assert.Equal("dead", status)
            Assert.True(payloadErased)
            Assert.Equal(Some "smtp-status-550", lastError)

            let! callback = outboxCallback (EmailOutbox.failedCallbackKey flowId 1)
            Assert.Equal(Some("notification-send-failed-v1", 1), callback)
        }

    member _.``exhausted attempts dead-letter even a retryable classification``() =
        task {
            let! _, emailId = insertPendingEmail 1
            let! email = claimOne ()
            Assert.Equal(1, email.Attempts)
            Assert.Equal(1, email.MaxAttempts)

            match! EmailOutbox.fail dataSource relayOptions email "smtp-status-451" false CancellationToken.None with
            | Ok() -> ()
            | Error message -> Assert.Fail message

            let! status, _, payloadErased, _ = emailRow emailId
            Assert.Equal("dead", status)
            Assert.True(payloadErased)
        }

    member _.``the database rejects terminal rows that retain a payload``() =
        task {
            let! flowId, _ = insertPendingEmail 3
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use insert =
                new NpgsqlCommand(
                    """INSERT INTO fsnix.account_email_outbox (flow_id, generation, protected_payload, encryption_version, status, sent_at)
                       VALUES (@flow_id, 2, '\\x6b1a7f'::bytea, 1, 'sent', statement_timestamp())""",
                    connection
                )

            insert.Parameters.AddWithValue("flow_id", flowId) |> ignore

            try
                let! _ = insert.ExecuteNonQueryAsync()
                Assert.Fail "A sent row retaining its protected payload was accepted."
            with :? PostgresException ->
                ()
        }
