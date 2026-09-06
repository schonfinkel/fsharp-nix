namespace App.Tests

open System
open System.Threading
open App
open Microsoft.AspNetCore.Identity
open Npgsql
open Xunit

[<Collection("postgres")>]
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

    [<Fact>]
    member _.``store creates finds updates and detects stale writes``() =
        task {
            do! reset ()
            use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
            let concrete = new PostgresUserStore(dataSource)
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

    [<Fact>]
    member _.``store maps duplicate normalized email and username``() =
        task {
            do! reset ()
            use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
            let store = new PostgresUserStore(dataSource) :> IUserStore<ApplicationUser>
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

    [<Fact>]
    member _.``authenticator and hashed recovery codes round trip and redeem once``() =
        task {
            do! reset ()
            use dataSource = NpgsqlDataSource.Create fixture.ConnectionString
            let concrete = new PostgresUserStore(dataSource)
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
