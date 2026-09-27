namespace App.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.RegularExpressions
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open App
open App.Auth
open App.Database
open App.Domain
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Expecto
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Identity
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.DependencyInjection
open Npgsql

type AccountFlowHttpTests(fixture: PostgreSqlFixture) =
    let antiForgeryToken (html: string) =
        let matched =
            Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")

        Assert.True(matched.Success, "Expected an antiforgery token in the account form.")
        matched.Groups[1].Value

    let formValue (name: string) (html: string) =
        let pattern = $"name=\"{Regex.Escape name}\"[^>]*value=\"([^\"]*)\""
        let matched = Regex.Match(html, pattern)
        Assert.True(matched.Success, $"Expected hidden form field '{name}'.")
        WebUtility.HtmlDecode matched.Groups[1].Value

    let postForm (client: HttpClient) (path: string) (fields: (string * string) list) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, path)
            let values = fields |> List.map (fun (key, value) -> KeyValuePair(key, value))
            request.Content <- new FormUrlEncodedContent(values)
            return! client.SendAsync request
        }

    let createIdentityUser (factory: IdentityAppFactory) (email: string) (password: string) =
        task {
            use scope = factory.Services.CreateScope()
            let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            let user = ApplicationUser()
            user.Id <- Guid.NewGuid()
            user.UserName <- email
            user.NormalizedUserName <- email.ToUpperInvariant()
            user.Email <- email
            user.NormalizedEmail <- email.ToUpperInvariant()
            user.EmailConfirmed <- true
            let! result = users.CreateAsync(user, password)
            Assert.True(result.Succeeded, String.concat " " (result.Errors |> Seq.map _.Description))
            return user.Id
        }

    let requestForEmail (factory: IdentityAppFactory) (email: string) (kind: FlowKind) =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT flow_id FROM fsnix.account_flow_requests
                       WHERE lower(destination_email) = lower(@email) AND flow_kind = @kind
                         AND status = 'requested' ORDER BY created_at DESC LIMIT 1""",
                    connection
                )

            command.Parameters.AddWithValue("email", email) |> ignore
            command.Parameters.AddWithValue("kind", FlowKind.wireName kind) |> ignore
            let! result = command.ExecuteScalarAsync()
            return if isNull result then None else Some(result :?> Guid)
        }

    let protectedToken (factory: IdentityAppFactory) (flowId: Guid) =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    "SELECT protected_payload FROM fsnix.account_email_outbox WHERE flow_id = @flow_id AND generation = 1",
                    connection
                )

            command.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! bytes = command.ExecuteScalarAsync()
            Assert.True(not (isNull bytes), "Expected the protected account email payload.")

            let dataProtection = factory.Services.GetRequiredService<IDataProtectionProvider>()

            let plaintext =
                AccountEmail.tryUnprotect dataProtection (bytes :?> byte[])
                |> Option.defaultWith (fun () -> Assert.Fail "Expected the protected payload to unprotect.")

            use document = JsonDocument.Parse plaintext
            return document.RootElement.GetProperty("token").GetString()
        }

    let reset () =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("TRUNCATE user_tokens, users CASCADE", connection)
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let createFlow (factory: WebApplicationFactory<AppMarker>) (destination: string) (expiresAt: DateTimeOffset) =
        task {
            use scope = factory.Services.CreateScope()
            let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            let operations = scope.ServiceProvider.GetRequiredService<AccountOperationContext>()

            let user = ApplicationUser()
            user.Id <- Guid.NewGuid()
            user.UserName <- $"flow-{Guid.NewGuid():N}"
            user.NormalizedUserName <- user.UserName.ToUpperInvariant()
            user.Email <- destination
            user.NormalizedEmail <- destination.ToUpperInvariant()
            user.EmailConfirmed <- false

            let registration =
                AccountOperation.forRegistration EmailVerification user.Id destination expiresAt
                |> Result.defaultWith Assert.Fail

            let flowId =
                match registration with
                | RegisterFlow operation -> operation.FlowId
                | CompleteFlow _ -> Assert.Fail "Expected a registration operation."

            match operations.Attach registration with
            | Ok() -> ()
            | Error message -> Assert.Fail message

            let! created = users.CreateAsync(user, "Correct-Horse-42!")
            Assert.True(created.Succeeded, String.concat " " (created.Errors |> Seq.map _.Description))
            return flowId
        }

    let waitForState (client: AccountFlowMachineClient) (flowId: Guid) (matches: FlowState -> bool) (seconds: float) =
        task {
            let entity = entityId (flowId.ToString("D"))
            let deadline = DateTime.UtcNow.AddSeconds seconds
            let mutable reached = false

            while not reached && DateTime.UtcNow < deadline do
                let! current = Machine.state client.Flows entity CancellationToken.None

                reached <-
                    match current with
                    | Ok(Some snapshot) -> matches snapshot.State
                    | _ -> false

                if not reached then
                    do! Task.Delay(TimeSpan.FromMilliseconds 100.)

            return reached
        }

    let waitForAwaitingCompletion (client: AccountFlowMachineClient) (flowId: Guid) (generation: int) =
        waitForState
            client
            flowId
            (function
            | AwaitingCompletion flow -> flow.Generation = generation
            | _ -> false)
            15.

    let runEmailPass (factory: IdentityAppFactory) =
        task {
            let dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>()
            let dataProtection = factory.Services.GetRequiredService<IDataProtectionProvider>()
            let options = factory.Services.GetRequiredService<EmailOptions>()

            let relayOptions =
                EmailOutbox.RelayOptions.defaults $"test-email-relay-{Guid.NewGuid():N}"

            return!
                EmailDelivery.deliverPending
                    dataSource
                    dataProtection
                    (factory.EmailTransport :> IEmailTransport)
                    options
                    relayOptions
                    CancellationToken.None
        }

    let emailRow (factory: IdentityAppFactory) (flowId: Guid) (generation: int) =
        task {
            use connection =
                new NpgsqlConnection(factory.Services.GetRequiredService<NpgsqlDataSource>().ConnectionString)

            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    """SELECT status, attempts, protected_payload IS NULL, last_error
                       FROM fsnix.account_email_outbox
                       WHERE flow_id = @flow_id AND generation = @generation""",
                    connection
                )

            command.Parameters.AddWithValue("flow_id", flowId) |> ignore
            command.Parameters.AddWithValue("generation", generation) |> ignore
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
                return Assert.Fail $"Expected the email row for generation %d{generation}."
        }

    member _.``registration flow reaches awaiting-completion with a protected email``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let destination = "flow-recipient@example.test"
            let! flowId = createFlow factory destination (DateTimeOffset.UtcNow.AddHours 24.)

            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! reached = waitForAwaitingCompletion flows flowId 1
            Assert.True(reached, "The account flow did not reach awaiting-completion.")

            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use email =
                new NpgsqlCommand(
                    "SELECT protected_payload, encryption_version FROM fsnix.account_email_outbox WHERE flow_id = @flow_id",
                    connection
                )

            email.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! emailReader = email.ExecuteReaderAsync()
            Assert.True(emailReader.Read(), "Expected one protected email row.")
            let payload = emailReader.GetValue 0 :?> byte[]
            let encryptionVersion = emailReader.GetInt32 1
            emailReader.Dispose()
            Assert.Equal(1, encryptionVersion)
            Assert.True(payload.Length > 0)

            let dataProtection = factory.Services.GetRequiredService<IDataProtectionProvider>()

            let unprotected =
                match AccountEmail.tryUnprotect dataProtection payload with
                | Some value -> value
                | None -> Assert.Fail "The protected payload did not unprotect with the shared key ring."

            Assert.Contains(flowId.ToString("D"), unprotected)
            Assert.Contains(FlowKind.wireName EmailVerification, unprotected)

            use document = JsonDocument.Parse unprotected
            let token = document.RootElement.GetProperty("token").GetString()
            Assert.False(String.IsNullOrWhiteSpace token)

            let ciphertext = Encoding.UTF8.GetString payload
            Assert.DoesNotContain(token, ciphertext)
            Assert.DoesNotContain(destination, ciphertext)

            use fsm =
                new NpgsqlCommand(
                    "SELECT string_agg(event::text, ' ') FROM fsnix.integration_outbox WHERE machine_id = @machine_id",
                    connection
                )

            fsm.Parameters.AddWithValue("machine_id", AccountFlow.MachineKey) |> ignore
            let! fsmText = fsm.ExecuteScalarAsync()
            let history = string fsmText
            Assert.DoesNotContain(token, history)
            Assert.DoesNotContain(destination, history)

            // Delivery (driven explicitly; the hosted relay is replaced by the fake transport).
            let! delivered = runEmailPass factory
            Assert.Equal(1, delivered)

            let sent =
                factory.EmailTransport.Sent
                |> List.filter (fun message -> message.To = destination)
                |> List.exactlyOne

            let escapedToken = Uri.EscapeDataString token
            Assert.Equal("Confirm your email address", sent.Subject)
            Assert.Equal($"{flowId:N}.1@fsnix.local", sent.MessageId)
            Assert.Contains($"/account/confirm-email?flowId={flowId:D}&token={escapedToken}", sent.TextBody)
            Assert.Contains($"&amp;token={escapedToken}", sent.HtmlBody)

            let! status, _, payloadErased, _ = emailRow factory flowId 1
            Assert.Equal("sent", status)
            Assert.True(payloadErased)
        }

    member _.``resend advances the generation and produces a second protected email``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let destination = "resend-recipient@example.test"
            let! flowId = createFlow factory destination (DateTimeOffset.UtcNow.AddHours 24.)

            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! reached = waitForAwaitingCompletion flows flowId 1
            Assert.True(reached, "The account flow did not reach awaiting-completion.")

            let entity = entityId (flowId.ToString("D"))

            let! resent =
                Machine.enqueue
                    flows.Flows
                    entity
                    (EventEnvelope.create "test-resend-1" ResendRequested)
                    CancellationToken.None

            match resent with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"Resend enqueue failed: %A{error}."

            let! advanced = waitForAwaitingCompletion flows flowId 2
            Assert.True(advanced, "The resend did not advance the flow to generation two.")

            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use emails =
                new NpgsqlCommand(
                    "SELECT generation FROM fsnix.account_email_outbox WHERE flow_id = @flow_id ORDER BY generation",
                    connection
                )

            emails.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! emailReader = emails.ExecuteReaderAsync()
            let generations = ResizeArray<int>()

            while emailReader.Read() do
                generations.Add(emailReader.GetInt32 0)

            emailReader.Dispose()
            Assert.Equal([ 1; 2 ], List.ofSeq generations)

            use request =
                new NpgsqlCommand(
                    "SELECT generation, resend_count FROM fsnix.account_flow_requests WHERE flow_id = @flow_id",
                    connection
                )

            request.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! requestReader = request.ExecuteReaderAsync()
            Assert.True(requestReader.Read(), "Expected the flow request row.")
            Assert.Equal(2, requestReader.GetInt32 0)
            Assert.Equal(1, requestReader.GetInt32 1)
            requestReader.Dispose()

            use deadlines =
                new NpgsqlCommand(
                    "SELECT generation, status FROM fsnix.flow_deadlines WHERE flow_id = @flow_id ORDER BY generation",
                    connection
                )

            deadlines.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! deadlineReader = deadlines.ExecuteReaderAsync()
            let rows = ResizeArray<int * string>()

            while deadlineReader.Read() do
                rows.Add((deadlineReader.GetInt32 0, deadlineReader.GetString 1))

            deadlineReader.Dispose()
            Assert.Equal([ 1, "cancelled"; 2, "pending" ], List.ofSeq rows)
        }

    member _.``expiry fires after the deadline and expires the flow``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let destination = "expiry-recipient@example.test"
            let! flowId = createFlow factory destination (DateTimeOffset.UtcNow.AddSeconds 8.)

            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! reached = waitForAwaitingCompletion flows flowId 1
            Assert.True(reached, "The account flow did not reach awaiting-completion.")

            let! expired =
                waitForState
                    flows
                    flowId
                    (function
                    | Expired flow -> flow.Generation = 1
                    | _ -> false)
                    25.

            Assert.True(expired, "The flow did not expire after its deadline.")

            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use deadline =
                new NpgsqlCommand(
                    "SELECT status FROM fsnix.flow_deadlines WHERE flow_id = @flow_id AND generation = 1",
                    connection
                )

            deadline.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! status = deadline.ExecuteScalarAsync()
            Assert.Equal("fired", string status)
        }

    member _.``transient smtp failure keeps the payload and a later pass delivers``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let destination = "transient-recipient@example.test"
            let! flowId = createFlow factory destination (DateTimeOffset.UtcNow.AddHours 24.)

            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! reached = waitForAwaitingCompletion flows flowId 1
            Assert.True(reached, "The account flow did not reach awaiting-completion.")

            factory.EmailTransport.FailWith (EmailFailure.SmtpStatus 451) 1

            let! firstPass = runEmailPass factory
            Assert.Equal(0, firstPass)

            let! status, attempts, payloadErased, lastError = emailRow factory flowId 1
            Assert.Equal("pending", status)
            Assert.Equal(1, attempts)
            Assert.False payloadErased
            Assert.Equal(Some "smtp-status-451", lastError)

            // Skip the backoff wait: make the row claimable immediately.
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use available =
                new NpgsqlCommand(
                    "UPDATE fsnix.account_email_outbox SET available_at = statement_timestamp() WHERE status = 'pending'",
                    connection
                )

            let! _ = available.ExecuteNonQueryAsync()

            let! secondPass = runEmailPass factory
            Assert.Equal(1, secondPass)

            let! finalStatus, _, finalErased, _ = emailRow factory flowId 1
            Assert.Equal("sent", finalStatus)
            Assert.True finalErased
            Assert.Equal(1, factory.EmailTransport.Sent.Length)
        }

    member _.``permanent smtp failure dead-letters and moves the flow to delivery-failed``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let destination = "permanent-recipient@example.test"
            let! flowId = createFlow factory destination (DateTimeOffset.UtcNow.AddHours 24.)

            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! reached = waitForAwaitingCompletion flows flowId 1
            Assert.True(reached, "The account flow did not reach awaiting-completion.")

            factory.EmailTransport.FailWith (EmailFailure.SmtpStatus 550) 1

            let! delivered = runEmailPass factory
            Assert.Equal(0, delivered)

            let! status, _, payloadErased, lastError = emailRow factory flowId 1
            Assert.Equal("dead", status)
            Assert.True payloadErased
            Assert.Equal(Some "smtp-status-550", lastError)

            let! failed =
                waitForState
                    flows
                    flowId
                    (function
                    | DeliveryFailed flow -> flow.Generation = 1
                    | _ -> false)
                    15.

            Assert.True(failed, "The flow did not move to delivery-failed.")

            // Delivery-failed is not terminal: a resend starts the next generation.
            let entity = entityId (flowId.ToString("D"))

            let! resent =
                Machine.enqueue
                    flows.Flows
                    entity
                    (EventEnvelope.create "test-resend-after-failure" ResendRequested)
                    CancellationToken.None

            match resent with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"Resend after failure failed: %A{error}."

            let! advanced = waitForAwaitingCompletion flows flowId 2
            Assert.True(advanced, "The resend did not advance the failed flow to generation two.")

            let! status, _, payloadErased, _ = emailRow factory flowId 2
            Assert.Equal("pending", status)
            Assert.False payloadErased
        }

    member _.``public registration confirms email only on antiforgery post``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let email = "public-registration@example.test"
            let password = "Correct-Horse-42!"
            let! registrationHtml = client.GetStringAsync "/account/register"
            let registrationCsrf = antiForgeryToken registrationHtml
            let registrationKey = formValue "idempotencyKey" registrationHtml

            let! registration =
                postForm
                    client
                    "/account/register"
                    [ "__RequestVerificationToken", registrationCsrf
                      "idempotencyKey", registrationKey
                      "email", email
                      "password", password
                      "confirmPassword", password ]

            Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode)
            let! registrationBody = registration.Content.ReadAsStringAsync()
            Assert.Contains("If registration can proceed, check your email", registrationBody)

            let! flowId = requestForEmail factory email EmailVerification

            let flowId =
                flowId
                |> Option.defaultWith (fun () -> Assert.Fail "Expected a registration flow.")

            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! started = waitForAwaitingCompletion flows flowId 1
            Assert.True(started, "The registration flow did not reach awaiting-completion.")

            use scope = factory.Services.CreateScope()
            let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            let! beforeConfirmation = users.FindByEmailAsync email
            Assert.False(beforeConfirmation.EmailConfirmed)
            let! token = protectedToken factory flowId

            let confirmationUrl =
                $"/account/confirm-email?flowId={flowId:D}&token={Uri.EscapeDataString token}"

            let! confirmationHtml = client.GetStringAsync confirmationUrl
            let confirmationCsrf = antiForgeryToken confirmationHtml

            // Following the email link only displays the form; GET never consumes a token.
            let! afterGet = users.FindByEmailAsync email
            Assert.False(afterGet.EmailConfirmed)

            let! confirmation =
                postForm
                    client
                    "/account/confirm-email"
                    [ "__RequestVerificationToken", confirmationCsrf
                      "flowId", flowId.ToString("D")
                      "token", token ]

            Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode)
            let! afterPost = users.FindByEmailAsync email
            Assert.True(afterPost.EmailConfirmed)

            let! completed =
                waitForState
                    flows
                    flowId
                    (function
                    | Completed flow -> flow.Kind = EmailVerification
                    | _ -> false)
                    15.

            Assert.True(completed, "The confirmed flow did not become completed.")

            // A duplicate registration has the same public status/message and creates no
            // second user or flow, so normalized-email uniqueness is not disclosed.
            let! duplicateHtml = client.GetStringAsync "/account/register"
            let duplicateCsrf = antiForgeryToken duplicateHtml
            let duplicateKey = formValue "idempotencyKey" duplicateHtml

            let! duplicate =
                postForm
                    client
                    "/account/register"
                    [ "__RequestVerificationToken", duplicateCsrf
                      "idempotencyKey", duplicateKey
                      "email", email
                      "password", password
                      "confirmPassword", password ]

            Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode)
            let! duplicateBody = duplicate.Content.ReadAsStringAsync()
            Assert.Contains("If registration can proceed, check your email", duplicateBody)
        }

    member _.``password-reset request is idempotent and uniform for known and unknown email``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let knownEmail = "known-reset@example.test"
            let! _ = createIdentityUser factory knownEmail "Correct-Horse-42!"
            let! forgotHtml = client.GetStringAsync "/account/forgot-password"
            let csrf = antiForgeryToken forgotHtml
            let key = formValue "idempotencyKey" forgotHtml

            let submit email =
                postForm
                    client
                    "/account/forgot-password"
                    [ "__RequestVerificationToken", csrf; "idempotencyKey", key; "email", email ]

            let! knownResponse = submit knownEmail
            let! knownBody = knownResponse.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.Accepted, knownResponse.StatusCode)
            Assert.Contains("If the request can be completed, an email will be sent shortly.", knownBody)

            let! firstFlow = requestForEmail factory knownEmail PasswordReset

            let firstFlow =
                firstFlow
                |> Option.defaultWith (fun () -> Assert.Fail "Expected a password-reset flow.")

            let! replayResponse = submit knownEmail
            Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode)
            let! secondFlow = requestForEmail factory knownEmail PasswordReset
            Assert.Equal(Some firstFlow, secondFlow)

            let! unknownResponse = submit "unknown-reset@example.test"
            let! unknownBody = unknownResponse.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.Accepted, unknownResponse.StatusCode)
            Assert.Contains("If the request can be completed, an email will be sent shortly.", unknownBody)
        }

    member _.``password-reset link changes password only after csrf-protected post``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let email = "complete-reset@example.test"
            let oldPassword = "Correct-Horse-42!"
            let newPassword = "Another-Horse-43!"
            let! userId = createIdentityUser factory email oldPassword
            let! forgotHtml = client.GetStringAsync "/account/forgot-password"
            let csrf = antiForgeryToken forgotHtml

            let! requested =
                postForm
                    client
                    "/account/forgot-password"
                    [ "__RequestVerificationToken", csrf
                      "idempotencyKey", formValue "idempotencyKey" forgotHtml
                      "email", email ]

            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode)
            let! flowId = requestForEmail factory email PasswordReset

            let flowId =
                flowId
                |> Option.defaultWith (fun () -> Assert.Fail "Expected a password-reset flow.")

            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! started = waitForAwaitingCompletion flows flowId 1
            Assert.True(started, "The password-reset flow did not reach awaiting-completion.")

            let! token = protectedToken factory flowId

            let resetUrl =
                $"/account/reset-password?flowId={flowId:D}&token={Uri.EscapeDataString token}"

            let! resetHtml = client.GetStringAsync resetUrl
            let resetCsrf = antiForgeryToken resetHtml

            use scope = factory.Services.CreateScope()
            let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            let! before = users.FindByIdAsync(userId.ToString("D"))
            let! oldPasswordValid = users.CheckPasswordAsync(before, oldPassword)
            Assert.True oldPasswordValid

            let! response =
                postForm
                    client
                    "/account/reset-password"
                    [ "__RequestVerificationToken", resetCsrf
                      "flowId", flowId.ToString("D")
                      "token", token
                      "password", newPassword
                      "confirmPassword", newPassword ]

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! after = users.FindByIdAsync(userId.ToString("D"))
            let! newPasswordValid = users.CheckPasswordAsync(after, newPassword)
            let! oldPasswordStillValid = users.CheckPasswordAsync(after, oldPassword)
            Assert.True newPasswordValid
            Assert.False oldPasswordStillValid
        }

    member _.``authenticated email-change request confirms the new address``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let originalEmail = "change-before@example.test"
            let nextEmail = "change-after@example.test"
            let password = "Correct-Horse-42!"
            let! userId = createIdentityUser factory originalEmail password

            let! loginHtml = client.GetStringAsync "/account/login"

            let! login =
                postForm
                    client
                    "/account/login"
                    [ "__RequestVerificationToken", antiForgeryToken loginHtml
                      "email", originalEmail
                      "password", password
                      "returnUrl", "/account/email" ]

            Assert.True(
                login.StatusCode = HttpStatusCode.OK
                || login.StatusCode = HttpStatusCode.Redirect
            )

            let! changeHtml = client.GetStringAsync "/account/email"

            let! requested =
                postForm
                    client
                    "/account/email"
                    [ "__RequestVerificationToken", antiForgeryToken changeHtml
                      "idempotencyKey", formValue "idempotencyKey" changeHtml
                      "email", nextEmail ]

            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode)
            let! flowId = requestForEmail factory nextEmail EmailChange

            let flowId =
                flowId
                |> Option.defaultWith (fun () -> Assert.Fail "Expected an email-change flow.")

            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! started = waitForAwaitingCompletion flows flowId 1
            Assert.True(started, "The email-change flow did not reach awaiting-completion.")
            let! token = protectedToken factory flowId

            let changeUrl =
                $"/account/confirm-email-change?flowId={flowId:D}&token={Uri.EscapeDataString token}"

            let! confirmationHtml = client.GetStringAsync changeUrl

            // Link navigation is display-only and leaves the account's email untouched.
            use scope = factory.Services.CreateScope()
            let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            let! before = users.FindByIdAsync(userId.ToString("D"))
            Assert.Equal(originalEmail, before.Email)

            let! changed =
                postForm
                    client
                    "/account/confirm-email-change"
                    [ "__RequestVerificationToken", antiForgeryToken confirmationHtml
                      "flowId", flowId.ToString("D")
                      "token", token ]

            Assert.Equal(HttpStatusCode.OK, changed.StatusCode)
            let! after = users.FindByIdAsync(userId.ToString("D"))
            Assert.Equal(nextEmail, after.Email)
        }

    member _.``public resend is idempotent for the submitted key``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client = factory.CreateClient()
            let email = "public-resend@example.test"
            let! flowId = createFlow factory email (DateTimeOffset.UtcNow.AddHours 24.)
            let flows = factory.Services.GetRequiredService<AccountFlowMachineClient>()
            let! initial = waitForAwaitingCompletion flows flowId 1
            Assert.True(initial, "The initial registration flow did not start.")

            let! resendHtml = client.GetStringAsync "/account/resend"
            let csrf = antiForgeryToken resendHtml
            let key = formValue "idempotencyKey" resendHtml

            let submit () =
                postForm
                    client
                    "/account/resend"
                    [ "__RequestVerificationToken", csrf
                      "idempotencyKey", key
                      "email", email
                      "kind", "email-verification" ]

            let! first = submit ()
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode)

            let! resent = waitForAwaitingCompletion flows flowId 2
            Assert.True(resent, "The public resend did not advance the flow to generation two.")

            let! replay = submit ()
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode)
            do! Task.Delay(TimeSpan.FromMilliseconds 300.)
            let! stillSecond = waitForAwaitingCompletion flows flowId 2
            Assert.True(stillSecond, "Replaying one idempotency key advanced more than one generation.")

            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    "SELECT COUNT(*) FROM fsnix.account_email_outbox WHERE flow_id = @flow_id",
                    connection
                )

            command.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! count = command.ExecuteScalarAsync()
            Assert.Equal(2L, count :?> int64)
        }
