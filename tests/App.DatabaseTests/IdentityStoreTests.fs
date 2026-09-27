namespace App.Tests

open System
open System.IO
open System.Threading
open App
open App.Database
open App.Domain
open Expecto
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Npgsql

type IdentityStoreTests(fixture: PostgreSqlFixture) =
    let reset () =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("TRUNCATE user_tokens, users CASCADE", connection)
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let applicationUser suffix =
        let user = ApplicationUser()
        user.UserName <- $"operator-{suffix}"
        user.NormalizedUserName <- user.UserName.ToUpperInvariant()
        user.Email <- $"operator-{suffix}@example.test"
        user.NormalizedEmail <- user.Email.ToUpperInvariant()
        user.EmailConfirmed <- true
        user.PasswordHash <- "hashed-password"
        user.SecurityStamp <- Guid.NewGuid().ToString("N")
        user.ConcurrencyStamp <- Guid.NewGuid().ToString("N")
        user

    member _.``store creates finds updates and detects stale writes``() =
        task {
            do! reset ()
            use dataSource = NpgsqlDataSource.Create fixture.ConnectionString

            let concrete =
                new PostgresUserStore(dataSource, AccountOperationContext(), EphemeralDataProtectionProvider())

            let store = concrete :> IUserStore<ApplicationUser>
            let emailStore = concrete :> IUserEmailStore<ApplicationUser>
            let user = applicationUser "crud"

            let! created = store.CreateAsync(user, CancellationToken.None)
            Assert.True created.Succeeded

            let! byName = store.FindByNameAsync(user.NormalizedUserName, CancellationToken.None)
            let! byEmail = emailStore.FindByEmailAsync(user.NormalizedEmail, CancellationToken.None)
            Assert.Equal(user.Id, byName.Id)
            Assert.Equal(user.Id, byEmail.Id)

            let! firstCopy = store.FindByIdAsync(user.Id.ToString(), CancellationToken.None)
            let! staleCopy = store.FindByIdAsync(user.Id.ToString(), CancellationToken.None)
            firstCopy.UserName <- "renamed"
            firstCopy.NormalizedUserName <- "RENAMED"
            let! firstUpdate = store.UpdateAsync(firstCopy, CancellationToken.None)
            Assert.True firstUpdate.Succeeded

            staleCopy.UserName <- "stale"
            staleCopy.NormalizedUserName <- "STALE"
            let! staleUpdate = store.UpdateAsync(staleCopy, CancellationToken.None)
            Assert.False staleUpdate.Succeeded
            Assert.Contains(staleUpdate.Errors, fun error -> error.Code = "ConcurrencyFailure")
        }

    member _.``store maps duplicate normalized email and username``() =
        task {
            do! reset ()
            use dataSource = NpgsqlDataSource.Create fixture.ConnectionString

            let store =
                new PostgresUserStore(dataSource, AccountOperationContext(), EphemeralDataProtectionProvider())
                :> IUserStore<ApplicationUser>

            let first = applicationUser "first"
            let! firstResult = store.CreateAsync(first, CancellationToken.None)
            Assert.True firstResult.Succeeded

            let duplicateName = applicationUser "second"
            duplicateName.NormalizedUserName <- first.NormalizedUserName
            let! nameResult = store.CreateAsync(duplicateName, CancellationToken.None)
            Assert.Contains(nameResult.Errors, fun error -> error.Code = "DuplicateUserName")

            let duplicateEmail = applicationUser "third"
            duplicateEmail.NormalizedEmail <- first.NormalizedEmail
            let! emailResult = store.CreateAsync(duplicateEmail, CancellationToken.None)
            Assert.Contains(emailResult.Errors, fun error -> error.Code = "DuplicateEmail")
        }

    member _.``authenticator and hashed recovery codes round trip and redeem once``() =
        task {
            do! reset ()
            use dataSource = NpgsqlDataSource.Create fixture.ConnectionString

            let concrete =
                new PostgresUserStore(dataSource, AccountOperationContext(), EphemeralDataProtectionProvider())

            let users = concrete :> IUserStore<ApplicationUser>
            let keys = concrete :> IUserAuthenticatorKeyStore<ApplicationUser>
            let recovery = concrete :> IUserTwoFactorRecoveryCodeStore<ApplicationUser>
            let user = applicationUser "tokens"
            let! created = users.CreateAsync(user, CancellationToken.None)
            Assert.True created.Succeeded

            do! keys.SetAuthenticatorKeyAsync(user, "shared-secret", CancellationToken.None)
            let! key = keys.GetAuthenticatorKeyAsync(user, CancellationToken.None)
            Assert.Equal("shared-secret", key)

            do! recovery.ReplaceCodesAsync(user, [ "alpha-code"; "beta-code" ], CancellationToken.None)
            let! initialCount = recovery.CountCodesAsync(user, CancellationToken.None)
            Assert.Equal(2, initialCount)

            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()

            use command =
                new NpgsqlCommand(
                    "SELECT value FROM user_tokens WHERE user_id = @user_id AND name = 'RecoveryCodes'",
                    connection
                )

            command.Parameters.AddWithValue("user_id", user.Id) |> ignore
            let! stored = command.ExecuteScalarAsync()
            Assert.StartsWith("p1:", string stored)
            Assert.DoesNotContain("alpha-code", string stored)
            Assert.DoesNotContain("beta-code", string stored)

            let attempts =
                [| recovery.RedeemCodeAsync(user, "alpha-code", CancellationToken.None)
                   recovery.RedeemCodeAsync(user, "alpha-code", CancellationToken.None) |]

            let! redeemed = System.Threading.Tasks.Task.WhenAll attempts
            Assert.Equal(1, redeemed |> Array.filter id |> Array.length)
            let! remaining = recovery.CountCodesAsync(user, CancellationToken.None)
            Assert.Equal(1, remaining)
        }

    member _.``account handoff commits atomically and a mismatched completion rolls back``() =
        task {
            do! reset ()
            use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
            let operations = AccountOperationContext()

            let concrete =
                new PostgresUserStore(dataSource, operations, EphemeralDataProtectionProvider())

            let store = concrete :> IUserStore<ApplicationUser>
            let user = applicationUser "flow"
            user.Id <- Guid.NewGuid()
            let destination = "flow-recipient@example.test"

            let registration =
                AccountOperation.forRegistration
                    EmailVerification
                    user.Id
                    destination
                    (DateTimeOffset.UtcNow.AddHours 24.)
                |> Result.defaultWith Assert.Fail

            match operations.Attach registration with
            | Ok() -> ()
            | Error message -> Assert.Fail message

            let! created = store.CreateAsync(user, CancellationToken.None)
            Assert.True created.Succeeded

            let flowId =
                match registration with
                | RegisterFlow operation -> operation.FlowId
                | CompleteFlow _ -> Assert.Fail "Expected a registration operation."

            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync()

            use verifyCreate =
                new NpgsqlCommand(
                    """SELECT
                           (SELECT count(*) FROM account_flow_requests),
                           (SELECT count(*) FROM flow_deadlines),
                           (SELECT count(*) FROM account_operation_markers),
                           (SELECT count(*) FROM integration_outbox),
                           (SELECT event::text FROM integration_outbox LIMIT 1)""",
                    connection
                )

            let! createdRows = verifyCreate.ExecuteReaderAsync()
            Assert.True(createdRows.Read())
            Assert.Equal(1L, createdRows.GetInt64 0)
            Assert.Equal(1L, createdRows.GetInt64 1)
            Assert.Equal(1L, createdRows.GetInt64 2)
            Assert.Equal(1L, createdRows.GetInt64 3)
            Assert.DoesNotContain(destination, createdRows.GetString 4)
            createdRows.Dispose()

            let! persisted = store.FindByIdAsync(user.Id.ToString(), CancellationToken.None)
            let originalEmail = persisted.Email
            persisted.Email <- "must-rollback@example.test"

            let mismatchedCompletion =
                AccountOperation.forCompletion flowId user.Id PasswordReset DateTimeOffset.UtcNow
                |> Result.defaultWith Assert.Fail

            match operations.Attach mismatchedCompletion with
            | Ok() -> ()
            | Error message -> Assert.Fail message

            let mutable rejected = false

            try
                let! _ = store.UpdateAsync(persisted, CancellationToken.None)
                ()
            with :? InvalidOperationException ->
                rejected <- true

            Assert.True(rejected, "A completion for the wrong flow kind must be rejected.")

            use verifyRollback =
                new NpgsqlCommand(
                    """SELECT u.email, r.status,
                              (SELECT count(*) FROM account_operation_markers),
                              (SELECT count(*) FROM integration_outbox)
                       FROM users u
                       JOIN account_flow_requests r ON r.user_id = u.id
                       WHERE u.id = @user_id""",
                    connection
                )

            verifyRollback.Parameters.AddWithValue("user_id", user.Id) |> ignore
            let! rolledBack = verifyRollback.ExecuteReaderAsync()
            Assert.True(rolledBack.Read())
            Assert.Equal(originalEmail, rolledBack.GetString 0)
            Assert.Equal("requested", rolledBack.GetString 1)
            Assert.Equal(1L, rolledBack.GetInt64 2)
            Assert.Equal(1L, rolledBack.GetInt64 3)
        }

    member _.``purpose-specific identity tokens survive an application restart``() =
        task {
            do! reset ()

            let keyRingPath =
                Path.Combine(Path.GetTempPath(), $"fsnix-token-test-{Guid.NewGuid():N}")

            Directory.CreateDirectory keyRingPath |> ignore

            let createApp () =
                let builder = Application.createBuilder [||]
                builder.Configuration["ConnectionStrings:App"] <- fixture.ConnectionString
                builder.Configuration["DataProtection:KeyRingPath"] <- keyRingPath
                Application.configureServices builder
                builder.Build()

            try
                let! tokenMaterial =
                    task {
                        use firstApp = createApp ()
                        use scope = firstApp.Services.CreateScope()

                        let manager =
                            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()

                        let options =
                            scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value

                        Assert.Equal(
                            AccountTokenProviders.EmailConfirmation,
                            options.Tokens.EmailConfirmationTokenProvider
                        )

                        Assert.Equal(AccountTokenProviders.PasswordReset, options.Tokens.PasswordResetTokenProvider)
                        Assert.Equal(AccountTokenProviders.ChangeEmail, options.Tokens.ChangeEmailTokenProvider)

                        let user = applicationUser "restart-token"
                        user.EmailConfirmed <- false
                        let! created = manager.CreateAsync(user, "Strong-Restart-Password-42!")
                        Assert.True created.Succeeded
                        let! email = manager.GenerateEmailConfirmationTokenAsync(user)
                        let! reset = manager.GeneratePasswordResetTokenAsync(user)
                        return user.Id, email, reset
                    }

                let userId, emailToken, resetToken = tokenMaterial

                use secondApp = createApp ()
                use secondScope = secondApp.Services.CreateScope()

                let restarted =
                    secondScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()

                let! user = restarted.FindByIdAsync(userId.ToString())

                let! resetWithEmail = restarted.ResetPasswordAsync(user, emailToken, "Another-Strong-Password-42!")
                Assert.False resetWithEmail.Succeeded

                let! confirmWithReset = restarted.ConfirmEmailAsync(user, resetToken)
                Assert.False confirmWithReset.Succeeded

                let! confirmed = restarted.ConfirmEmailAsync(user, emailToken)
                Assert.True confirmed.Succeeded

                let! reset = restarted.ResetPasswordAsync(user, resetToken, "Another-Strong-Password-42!")
                Assert.True reset.Succeeded
            finally
                if Directory.Exists keyRingPath then
                    Directory.Delete(keyRingPath, true)
        }
