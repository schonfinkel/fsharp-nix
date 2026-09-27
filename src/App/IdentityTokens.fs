namespace App

open App.Database
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

/// <summary>
/// Purpose-specific Data Protection token providers. Each provider has a distinct registered
/// name, Data Protection purpose, and lifespan, so a confirmation token can never validate as
/// a reset token. Registering them in <c>IdentityOptions.Tokens</c> means
/// <c>GenerateEmailConfirmationTokenAsync</c>, <c>GeneratePasswordResetTokenAsync</c>, and
/// <c>GenerateChangeEmailTokenAsync</c> select the intended provider.
/// </summary>

[<RequireQualifiedAccess>]
module AccountTokenProviders =

    [<Literal>]
    let EmailConfirmation = "fsnix-email-confirmation-v1"

    [<Literal>]
    let PasswordReset = "fsnix-password-reset-v1"

    [<Literal>]
    let ChangeEmail = "fsnix-change-email-v1"

type EmailConfirmationTokenOptions() =
    inherit DataProtectionTokenProviderOptions()

[<RequireQualifiedAccess>]
module private TokenProviderOptions =

    let create (name: string) (tokenLifespan: System.TimeSpan) : IOptions<DataProtectionTokenProviderOptions> =
        let configured = DataProtectionTokenProviderOptions()
        configured.Name <- name
        configured.TokenLifespan <- tokenLifespan
        Options.Create configured

type EmailConfirmationTokenProvider
    (
        options: IOptions<EmailConfirmationTokenOptions>,
        dataProtection: IDataProtectionProvider,
        logger: ILogger<DataProtectorTokenProvider<ApplicationUser>>
    ) =
    inherit
        DataProtectorTokenProvider<ApplicationUser>(
            dataProtection,
            TokenProviderOptions.create options.Value.Name options.Value.TokenLifespan,
            logger
        )

type PasswordResetTokenOptions() =
    inherit DataProtectionTokenProviderOptions()

type PasswordResetTokenProvider
    (
        options: IOptions<PasswordResetTokenOptions>,
        dataProtection: IDataProtectionProvider,
        logger: ILogger<DataProtectorTokenProvider<ApplicationUser>>
    ) =
    inherit
        DataProtectorTokenProvider<ApplicationUser>(
            dataProtection,
            TokenProviderOptions.create options.Value.Name options.Value.TokenLifespan,
            logger
        )

type ChangeEmailTokenOptions() =
    inherit DataProtectionTokenProviderOptions()

type ChangeEmailTokenProvider
    (
        options: IOptions<ChangeEmailTokenOptions>,
        dataProtection: IDataProtectionProvider,
        logger: ILogger<DataProtectorTokenProvider<ApplicationUser>>
    ) =
    inherit
        DataProtectorTokenProvider<ApplicationUser>(
            dataProtection,
            TokenProviderOptions.create options.Value.Name options.Value.TokenLifespan,
            logger
        )

[<RequireQualifiedAccess>]
module AccountTokenLifespans =

    /// <summary>Confirmation links travel by email and must survive slow readers: one day.</summary>
    let EmailConfirmation = System.TimeSpan.FromHours 24.

    /// <summary>Reset links are single-purpose credentials: one hour.</summary>
    let PasswordReset = System.TimeSpan.FromHours 1.

    /// <summary>Email-change links address a new mailbox that may not exist yet: one hour.</summary>
    let ChangeEmail = System.TimeSpan.FromHours 1.
