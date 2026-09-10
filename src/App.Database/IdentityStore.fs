namespace App.Database

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Database.Schema
open App.Database.Schema.fsnix
open Microsoft.AspNetCore.Identity
open Npgsql
open SqlHydra.Query
open SqlHydra.Query.NpgsqlExtensions

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
    let db = QueryContextFactory.Create dataSource

    let userFromRow (row: users) =
        let user = ApplicationUser()
        user.Id <- row.id
        user.UserName <- row.username
        user.NormalizedUserName <- row.normalized_username
        user.Email <- row.email
        user.NormalizedEmail <- row.normalized_email
        user.EmailConfirmed <- row.email_confirmed
        user.PasswordHash <- Option.toObj row.password_hash
        user.TwoFactorEnabled <- row.two_factor_enabled
        user.SecurityStamp <- row.security_stamp
        user.ConcurrencyStamp <- row.concurrency_stamp

        user.LockoutEnd <-
            row.lockout_end
            |> Option.map (fun value -> DateTime.SpecifyKind(value, DateTimeKind.Utc) |> DateTimeOffset)
            |> Option.toNullable

        user.LockoutEnabled <- row.lockout_enabled
        user.AccessFailedCount <- row.access_failed_count
        user

    let rowFromUser (user: ApplicationUser) : users =
        { id = user.Id
          username = user.UserName
          normalized_username = user.NormalizedUserName
          email = user.Email
          normalized_email = user.NormalizedEmail
          email_confirmed = user.EmailConfirmed
          password_hash = Option.ofObj user.PasswordHash
          two_factor_enabled = user.TwoFactorEnabled
          security_stamp = user.SecurityStamp
          concurrency_stamp = user.ConcurrencyStamp
          lockout_end =
            if user.LockoutEnd.HasValue then
                Some user.LockoutEnd.Value.UtcDateTime
            else
                None
          lockout_enabled = user.LockoutEnabled
          access_failed_count = user.AccessFailedCount }

    let checkUser (user: ApplicationUser) (cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()
        ArgumentNullException.ThrowIfNull user

    let duplicateResult (exceptionValue: PostgresException) (user: ApplicationUser) =
        match exceptionValue.ConstraintName with
        | "uq_users_normalized_username" -> IdentityResult.Failed(errors.DuplicateUserName user.UserName)
        | "uq_users_normalized_email" -> IdentityResult.Failed(errors.DuplicateEmail user.Email)
        | _ -> IdentityResult.Failed(errors.DefaultError())

    let setToken (user: ApplicationUser) provider name (value: string) cancellationToken =
        task {
            checkUser user cancellationToken

            if isNull value then
                let! _ =
                    deleteTask db {
                        for token in fsnix.user_tokens do
                            where (token.user_id = user.Id && token.login_provider = provider && token.name = name)
                            cancel cancellationToken
                    }

                return ()
            else
                let row: user_tokens =
                    { user_id = user.Id
                      login_provider = provider
                      name = name
                      value = value }

                let! _ =
                    insertTask db {
                        for token in fsnix.user_tokens do
                            entity row
                            onConflict (token.user_id, token.login_provider, token.name)
                            doUpdate token.value
                            cancel cancellationToken
                    }

                return ()
        }

    let getToken (user: ApplicationUser) provider name cancellationToken =
        task {
            checkUser user cancellationToken

            let! value =
                selectTask db {
                    for token in fsnix.user_tokens do
                        where (token.user_id = user.Id && token.login_provider = provider && token.name = name)
                        select token.value
                        tryHead
                        cancel cancellationToken
                }

            return Option.toObj value
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

                try
                    let! _ =
                        insertTask db {
                            into fsnix.users
                            entity (rowFromUser user)
                            cancel cancellationToken
                        }

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
                let row = rowFromUser user

                try
                    let! affected =
                        updateTask db {
                            for persisted in fsnix.users do
                                set persisted.username row.username
                                set persisted.normalized_username row.normalized_username
                                set persisted.email row.email
                                set persisted.normalized_email row.normalized_email
                                set persisted.email_confirmed row.email_confirmed
                                set persisted.password_hash row.password_hash
                                set persisted.two_factor_enabled row.two_factor_enabled
                                set persisted.security_stamp row.security_stamp
                                set persisted.concurrency_stamp nextStamp
                                set persisted.lockout_end row.lockout_end
                                set persisted.lockout_enabled row.lockout_enabled
                                set persisted.access_failed_count row.access_failed_count
                                where (persisted.id = user.Id && persisted.concurrency_stamp = previousStamp)
                                cancel cancellationToken
                        }

                    if affected = 1 then
                        user.ConcurrencyStamp <- nextStamp
                        return IdentityResult.Success
                    else
                        return IdentityResult.Failed(errors.ConcurrencyFailure())
                with :? PostgresException as exceptionValue when
                    exceptionValue.SqlState = PostgresErrorCodes.UniqueViolation ->
                    return duplicateResult exceptionValue user
            }

        member _.DeleteAsync(user, cancellationToken) =
            task {
                checkUser user cancellationToken

                let! affected =
                    deleteTask db {
                        for persisted in fsnix.users do
                            where (persisted.id = user.Id && persisted.concurrency_stamp = user.ConcurrencyStamp)
                            cancel cancellationToken
                    }

                return
                    if affected = 1 then
                        IdentityResult.Success
                    else
                        IdentityResult.Failed(errors.ConcurrencyFailure())
            }

        member _.FindByIdAsync(userId, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            match Guid.TryParse userId with
            | true, id ->
                task {
                    let! row =
                        selectTask db {
                            for persisted in fsnix.users do
                                where (persisted.id = id)
                                select persisted
                                tryHead
                                cancel cancellationToken
                        }

                    return row |> Option.map userFromRow |> Option.defaultValue null
                }
            | false, _ -> Task.FromResult null

        member _.FindByNameAsync(normalizedUserName, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            task {
                let! row =
                    selectTask db {
                        for persisted in fsnix.users do
                            where (persisted.normalized_username = normalizedUserName)
                            select persisted
                            tryHead
                            cancel cancellationToken
                    }

                return row |> Option.map userFromRow |> Option.defaultValue null
            }

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

            task {
                let! row =
                    selectTask db {
                        for persisted in fsnix.users do
                            where (persisted.normalized_email = normalizedEmail)
                            select persisted
                            tryHead
                            cancel cancellationToken
                    }

                return row |> Option.map userFromRow |> Option.defaultValue null
            }

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
                use! context = db.OpenContextAsync()
                use! transaction = context.Connection.BeginTransactionAsync cancellationToken
                context.Transaction <- Some transaction

                let lockQuery =
                    select {
                        for token in fsnix.user_tokens do
                            where (
                                token.user_id = user.Id
                                && token.login_provider = authenticationProvider
                                && token.name = recoveryCodesName
                            )

                            select token.value
                    }

                use selectCommand = context.BuildCommand(lockQuery.IR)
                selectCommand.CommandText <- selectCommand.CommandText.TrimEnd(';') + " FOR UPDATE"
                let! storedValue = selectCommand.ExecuteScalarAsync cancellationToken

                let codes =
                    if isNull storedValue || storedValue = DBNull.Value then
                        Array.empty
                    else
                        string storedValue |> _.Split(';', StringSplitOptions.RemoveEmptyEntries)

                match codes |> Array.tryFindIndex (fixedTimeEqual submittedHash) with
                | None ->
                    do! transaction.CommitAsync cancellationToken
                    context.Transaction <- None
                    return false
                | Some index ->
                    let remaining = codes |> Array.removeAt index |> String.concat ";"

                    let! _ =
                        updateTask context {
                            for token in fsnix.user_tokens do
                                set token.value remaining

                                where (
                                    token.user_id = user.Id
                                    && token.login_provider = authenticationProvider
                                    && token.name = recoveryCodesName
                                )

                                cancel cancellationToken
                        }

                    do! transaction.CommitAsync cancellationToken
                    context.Transaction <- None
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
