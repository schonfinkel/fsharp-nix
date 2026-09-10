namespace App

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Identity
open Npgsql
open NpgsqlTypes

[<AllowNullLiteral>]
type ApplicationUser() =
    member val Id = Guid.Empty with get, set
    member val UserName: string = null with get, set
    member val NormalizedUserName: string = null with get, set
    member val Email: string = null with get, set
    member val NormalizedEmail: string = null with get, set
    member val EmailConfirmed = false with get, set
    member val PasswordHash: string = null with get, set
    member val TwoFactorEnabled = false with get, set
    member val SecurityStamp: string = null with get, set
    member val ConcurrencyStamp: string = null with get, set
    member val LockoutEnd = Nullable<DateTimeOffset>() with get, set
    member val LockoutEnabled = true with get, set
    member val AccessFailedCount = 0 with get, set

[<Sealed>]
type PostgresUserStore(dataSource: NpgsqlDataSource) =
    let errors = IdentityErrorDescriber()

    let columns =
        "id, username, normalized_username, email, normalized_email, email_confirmed, password_hash, two_factor_enabled, security_stamp, concurrency_stamp, lockout_end, lockout_enabled, access_failed_count"

    let userFromReader (reader: NpgsqlDataReader) =
        let user = ApplicationUser()
        user.Id <- reader.GetGuid 0
        user.UserName <- reader.GetString 1
        user.NormalizedUserName <- reader.GetString 2
        user.Email <- reader.GetString 3
        user.NormalizedEmail <- reader.GetString 4
        user.EmailConfirmed <- reader.GetBoolean 5
        user.PasswordHash <- reader.GetString 6
        user.TwoFactorEnabled <- reader.GetBoolean 7
        user.SecurityStamp <- reader.GetString 8
        user.ConcurrencyStamp <- reader.GetString 9

        let lockoutEnd =
            reader.GetDateTime 10
            |> fun value -> DateTime.SpecifyKind(value, DateTimeKind.Utc)
            |> DateTimeOffset

        user.LockoutEnd <-
            if lockoutEnd = DateTimeOffset.MinValue then
                Nullable()
            else
                Nullable lockoutEnd

        user.LockoutEnabled <- reader.GetBoolean 11
        user.AccessFailedCount <- reader.GetInt32 12
        user

    let checkUser (user: ApplicationUser) (cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()
        ArgumentNullException.ThrowIfNull user

    let addTextOrEmpty (command: NpgsqlCommand) (name: string) (value: string) =
        command.Parameters.Add(name, NpgsqlDbType.Text).Value <- if isNull value then "" else value

    let addLockoutEnd (command: NpgsqlCommand) (value: Nullable<DateTimeOffset>) =
        command.Parameters.Add("lockout_end", NpgsqlDbType.TimestampTz).Value <-
            if value.HasValue then
                value.Value.UtcDateTime
            else
                DateTimeOffset.MinValue.UtcDateTime

    let addUserParameters (command: NpgsqlCommand) (user: ApplicationUser) =
        command.Parameters.AddWithValue("id", user.Id) |> ignore
        command.Parameters.AddWithValue("username", user.UserName) |> ignore

        command.Parameters.AddWithValue("normalized_username", user.NormalizedUserName)
        |> ignore

        command.Parameters.AddWithValue("email", user.Email) |> ignore

        command.Parameters.AddWithValue("normalized_email", user.NormalizedEmail)
        |> ignore

        command.Parameters.AddWithValue("email_confirmed", user.EmailConfirmed)
        |> ignore

        addTextOrEmpty command "password_hash" user.PasswordHash

        command.Parameters.AddWithValue("two_factor_enabled", user.TwoFactorEnabled)
        |> ignore

        command.Parameters.AddWithValue("security_stamp", user.SecurityStamp) |> ignore

        command.Parameters.AddWithValue("concurrency_stamp", user.ConcurrencyStamp)
        |> ignore

        addLockoutEnd command user.LockoutEnd

        command.Parameters.AddWithValue("lockout_enabled", user.LockoutEnabled)
        |> ignore

        command.Parameters.AddWithValue("access_failed_count", user.AccessFailedCount)
        |> ignore

    let duplicateResult (exceptionValue: PostgresException) (user: ApplicationUser) =
        match exceptionValue.ConstraintName with
        | "uq_users_normalized_username" -> IdentityResult.Failed(errors.DuplicateUserName user.UserName)
        | "uq_users_normalized_email" -> IdentityResult.Failed(errors.DuplicateEmail user.Email)
        | _ -> IdentityResult.Failed(errors.DefaultError())

    let queryOne (sql: string) (parameterName: string) (parameterValue: obj) cancellationToken =
        task {
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use command = new NpgsqlCommand(sql, connection)
            command.Parameters.AddWithValue(parameterName, parameterValue) |> ignore
            use! reader = command.ExecuteReaderAsync cancellationToken
            let! found = reader.ReadAsync cancellationToken
            return if found then userFromReader reader else null
        }

    let setToken (user: ApplicationUser) provider name (value: string) cancellationToken =
        task {
            checkUser user cancellationToken
            use! connection = dataSource.OpenConnectionAsync cancellationToken

            if isNull value then
                use command =
                    new NpgsqlCommand(
                        "DELETE FROM fsnix.user_tokens WHERE user_id = @user_id AND login_provider = @provider AND name = @name",
                        connection
                    )

                command.Parameters.AddWithValue("user_id", user.Id) |> ignore
                command.Parameters.AddWithValue("provider", provider) |> ignore
                command.Parameters.AddWithValue("name", name) |> ignore
                let! _ = command.ExecuteNonQueryAsync cancellationToken
                return ()
            else
                use command =
                    new NpgsqlCommand(
                        """
                        INSERT INTO fsnix.user_tokens (user_id, login_provider, name, value)
                        VALUES (@user_id, @provider, @name, @value)
                        ON CONFLICT (user_id, login_provider, name)
                        DO UPDATE SET value = EXCLUDED.value
                        """,
                        connection
                    )

                command.Parameters.AddWithValue("user_id", user.Id) |> ignore
                command.Parameters.AddWithValue("provider", provider) |> ignore
                command.Parameters.AddWithValue("name", name) |> ignore
                command.Parameters.AddWithValue("value", value) |> ignore
                let! _ = command.ExecuteNonQueryAsync cancellationToken
                return ()
        }

    let getToken (user: ApplicationUser) provider name cancellationToken =
        task {
            checkUser user cancellationToken
            use! connection = dataSource.OpenConnectionAsync cancellationToken

            use command =
                new NpgsqlCommand(
                    "SELECT value FROM fsnix.user_tokens WHERE user_id = @user_id AND login_provider = @provider AND name = @name",
                    connection
                )

            command.Parameters.AddWithValue("user_id", user.Id) |> ignore
            command.Parameters.AddWithValue("provider", provider) |> ignore
            command.Parameters.AddWithValue("name", name) |> ignore
            let! value = command.ExecuteScalarAsync cancellationToken

            return
                if isNull value || value = DBNull.Value then
                    null
                else
                    string value
        }

    let hashRecoveryCode (code: string) =
        code |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToBase64String

    let fixedTimeEqual (left: string) (right: string) =
        let leftBytes = Convert.FromBase64String left
        let rightBytes = Convert.FromBase64String right
        CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes)

    let authenticationProvider = "[AspNetUserStore]"
    let authenticatorKeyName = "AuthenticatorKey"
    let recoveryCodesName = "RecoveryCodes"

    interface IUserStore<ApplicationUser> with
        member _.CreateAsync(user, cancellationToken) =
            task {
                checkUser user cancellationToken

                if user.Id = Guid.Empty then
                    user.Id <- Guid.NewGuid()

                if String.IsNullOrWhiteSpace user.SecurityStamp then
                    user.SecurityStamp <- Guid.NewGuid().ToString("N")

                if String.IsNullOrWhiteSpace user.ConcurrencyStamp then
                    user.ConcurrencyStamp <- Guid.NewGuid().ToString("N")

                use! connection = dataSource.OpenConnectionAsync cancellationToken

                use command =
                    new NpgsqlCommand(
                        """
                        INSERT INTO fsnix.users
                            (id, username, normalized_username, email, normalized_email, email_confirmed,
                             password_hash, two_factor_enabled, security_stamp, concurrency_stamp,
                             lockout_end, lockout_enabled, access_failed_count)
                        VALUES
                            (@id, @username, @normalized_username, @email, @normalized_email, @email_confirmed,
                             @password_hash, @two_factor_enabled, @security_stamp, @concurrency_stamp,
                             @lockout_end, @lockout_enabled, @access_failed_count)
                        """,
                        connection
                    )

                addUserParameters command user

                try
                    let! _ = command.ExecuteNonQueryAsync cancellationToken
                    return IdentityResult.Success
                with :? PostgresException as exceptionValue when
                    exceptionValue.SqlState = PostgresErrorCodes.UniqueViolation ->
                    return duplicateResult exceptionValue user
            }

        member _.UpdateAsync(user, cancellationToken) =
            task {
                checkUser user cancellationToken
                let previousStamp = user.ConcurrencyStamp
                let nextStamp = Guid.NewGuid().ToString("N")
                use! connection = dataSource.OpenConnectionAsync cancellationToken

                use command =
                    new NpgsqlCommand(
                        """
                        UPDATE fsnix.users
                        SET username = @username,
                            normalized_username = @normalized_username,
                            email = @email,
                            normalized_email = @normalized_email,
                            email_confirmed = @email_confirmed,
                            password_hash = @password_hash,
                            two_factor_enabled = @two_factor_enabled,
                            security_stamp = @security_stamp,
                            concurrency_stamp = @next_concurrency_stamp,
                            lockout_end = @lockout_end,
                            lockout_enabled = @lockout_enabled,
                            access_failed_count = @access_failed_count
                        WHERE id = @id AND concurrency_stamp = @concurrency_stamp
                        """,
                        connection
                    )

                addUserParameters command user
                command.Parameters.AddWithValue("next_concurrency_stamp", nextStamp) |> ignore

                try
                    let! affected = command.ExecuteNonQueryAsync cancellationToken

                    if affected = 1 then
                        user.ConcurrencyStamp <- nextStamp
                        return IdentityResult.Success
                    else
                        return IdentityResult.Failed(errors.ConcurrencyFailure())
                with :? PostgresException as exceptionValue when
                    exceptionValue.SqlState = PostgresErrorCodes.UniqueViolation ->
                    user.ConcurrencyStamp <- previousStamp
                    return duplicateResult exceptionValue user
            }

        member _.DeleteAsync(user, cancellationToken) =
            task {
                checkUser user cancellationToken
                use! connection = dataSource.OpenConnectionAsync cancellationToken

                use command =
                    new NpgsqlCommand(
                        "DELETE FROM fsnix.users WHERE id = @id AND concurrency_stamp = @concurrency_stamp",
                        connection
                    )

                command.Parameters.AddWithValue("id", user.Id) |> ignore

                command.Parameters.AddWithValue("concurrency_stamp", user.ConcurrencyStamp)
                |> ignore

                let! affected = command.ExecuteNonQueryAsync cancellationToken

                return
                    if affected = 1 then
                        IdentityResult.Success
                    else
                        IdentityResult.Failed(errors.ConcurrencyFailure())
            }

        member _.FindByIdAsync(userId, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            match Guid.TryParse userId with
            | true, id -> queryOne $"SELECT {columns} FROM fsnix.users WHERE id = @id" "id" (box id) cancellationToken
            | false, _ -> Task.FromResult null

        member _.FindByNameAsync(normalizedUserName, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            queryOne
                $"SELECT {columns} FROM fsnix.users WHERE normalized_username = @normalized_username"
                "normalized_username"
                (box normalizedUserName)
                cancellationToken

        member _.GetUserIdAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult(user.Id.ToString())

        member _.GetUserNameAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.UserName

        member _.SetUserNameAsync(user, userName, cancellationToken) =
            checkUser user cancellationToken
            user.UserName <- userName
            Task.CompletedTask

        member _.GetNormalizedUserNameAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.NormalizedUserName

        member _.SetNormalizedUserNameAsync(user, normalizedName, cancellationToken) =
            checkUser user cancellationToken
            user.NormalizedUserName <- normalizedName
            Task.CompletedTask

        member _.Dispose() = ()

    interface IUserPasswordStore<ApplicationUser> with
        member _.SetPasswordHashAsync(user, passwordHash, cancellationToken) =
            checkUser user cancellationToken
            user.PasswordHash <- passwordHash
            Task.CompletedTask

        member _.GetPasswordHashAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.PasswordHash

        member _.HasPasswordAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult(not (String.IsNullOrEmpty user.PasswordHash))

    interface IUserEmailStore<ApplicationUser> with
        member _.SetEmailAsync(user, email, cancellationToken) =
            checkUser user cancellationToken
            user.Email <- email
            Task.CompletedTask

        member _.GetEmailAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.Email

        member _.GetEmailConfirmedAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.EmailConfirmed

        member _.SetEmailConfirmedAsync(user, confirmed, cancellationToken) =
            checkUser user cancellationToken
            user.EmailConfirmed <- confirmed
            Task.CompletedTask

        member _.FindByEmailAsync(normalizedEmail, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            queryOne
                $"SELECT {columns} FROM fsnix.users WHERE normalized_email = @normalized_email"
                "normalized_email"
                (box normalizedEmail)
                cancellationToken

        member _.GetNormalizedEmailAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.NormalizedEmail

        member _.SetNormalizedEmailAsync(user, normalizedEmail, cancellationToken) =
            checkUser user cancellationToken
            user.NormalizedEmail <- normalizedEmail
            Task.CompletedTask

    interface IUserSecurityStampStore<ApplicationUser> with
        member _.SetSecurityStampAsync(user, stamp, cancellationToken) =
            checkUser user cancellationToken
            user.SecurityStamp <- stamp
            Task.CompletedTask

        member _.GetSecurityStampAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.SecurityStamp

    interface IUserTwoFactorStore<ApplicationUser> with
        member _.SetTwoFactorEnabledAsync(user, enabled, cancellationToken) =
            checkUser user cancellationToken
            user.TwoFactorEnabled <- enabled
            Task.CompletedTask

        member _.GetTwoFactorEnabledAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.TwoFactorEnabled

    interface IUserAuthenticatorKeyStore<ApplicationUser> with
        member _.SetAuthenticatorKeyAsync(user, key, cancellationToken) =
            setToken user authenticationProvider authenticatorKeyName key cancellationToken

        member _.GetAuthenticatorKeyAsync(user, cancellationToken) =
            getToken user authenticationProvider authenticatorKeyName cancellationToken

    interface IUserTwoFactorRecoveryCodeStore<ApplicationUser> with
        member _.ReplaceCodesAsync(user, recoveryCodes, cancellationToken) =
            recoveryCodes
            |> Seq.map hashRecoveryCode
            |> String.concat ";"
            |> fun value -> setToken user authenticationProvider recoveryCodesName value cancellationToken

        member _.CountCodesAsync(user, cancellationToken) =
            task {
                let! value = getToken user authenticationProvider recoveryCodesName cancellationToken

                return
                    if String.IsNullOrEmpty value then
                        0
                    else
                        value.Split(';', StringSplitOptions.RemoveEmptyEntries).Length
            }

        member _.RedeemCodeAsync(user, code, cancellationToken) =
            task {
                checkUser user cancellationToken
                let submittedHash = hashRecoveryCode code
                use! connection = dataSource.OpenConnectionAsync cancellationToken
                use! transaction = connection.BeginTransactionAsync cancellationToken

                use selectCommand =
                    new NpgsqlCommand(
                        """
                        SELECT value
                        FROM fsnix.user_tokens
                        WHERE user_id = @user_id AND login_provider = @provider AND name = @name
                        FOR UPDATE
                        """,
                        connection,
                        transaction
                    )

                selectCommand.Parameters.AddWithValue("user_id", user.Id) |> ignore

                selectCommand.Parameters.AddWithValue("provider", authenticationProvider)
                |> ignore

                selectCommand.Parameters.AddWithValue("name", recoveryCodesName) |> ignore
                let! storedValue = selectCommand.ExecuteScalarAsync cancellationToken

                let codes =
                    if isNull storedValue || storedValue = DBNull.Value then
                        Array.empty
                    else
                        string storedValue |> _.Split(';', StringSplitOptions.RemoveEmptyEntries)

                match codes |> Array.tryFindIndex (fixedTimeEqual submittedHash) with
                | None ->
                    do! transaction.CommitAsync cancellationToken
                    return false
                | Some index ->
                    let remaining = codes |> Array.removeAt index |> String.concat ";"

                    use updateCommand =
                        new NpgsqlCommand(
                            """
                            UPDATE fsnix.user_tokens
                            SET value = @value
                            WHERE user_id = @user_id AND login_provider = @provider AND name = @name
                            """,
                            connection,
                            transaction
                        )

                    updateCommand.Parameters.AddWithValue("value", remaining) |> ignore
                    updateCommand.Parameters.AddWithValue("user_id", user.Id) |> ignore

                    updateCommand.Parameters.AddWithValue("provider", authenticationProvider)
                    |> ignore

                    updateCommand.Parameters.AddWithValue("name", recoveryCodesName) |> ignore
                    let! _ = updateCommand.ExecuteNonQueryAsync cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return true
            }

    interface IUserLockoutStore<ApplicationUser> with
        member _.GetLockoutEndDateAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.LockoutEnd

        member _.SetLockoutEndDateAsync(user, lockoutEnd, cancellationToken) =
            checkUser user cancellationToken
            user.LockoutEnd <- lockoutEnd
            Task.CompletedTask

        member _.IncrementAccessFailedCountAsync(user, cancellationToken) =
            checkUser user cancellationToken
            user.AccessFailedCount <- user.AccessFailedCount + 1
            Task.FromResult user.AccessFailedCount

        member _.ResetAccessFailedCountAsync(user, cancellationToken) =
            checkUser user cancellationToken
            user.AccessFailedCount <- 0
            Task.CompletedTask

        member _.GetAccessFailedCountAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.AccessFailedCount

        member _.GetLockoutEnabledAsync(user, cancellationToken) =
            checkUser user cancellationToken
            Task.FromResult user.LockoutEnabled

        member _.SetLockoutEnabledAsync(user, enabled, cancellationToken) =
            checkUser user cancellationToken
            user.LockoutEnabled <- enabled
            Task.CompletedTask
