namespace App

open System
open System.Threading.Tasks
open App.Database
open App.Domain
open App.Migrations
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Diagnostics
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.FeatureManagement
open Oxpecker

type AppMarker = class end

[<RequireQualifiedAccess>]
module Application =
    let private nonEmpty value =
        value |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private environmentArgument (args: string array) =
        let name = "--environment"

        args
        |> Array.tryFindIndex (fun argument -> String.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
        |> Option.bind (fun index -> args |> Array.tryItem (index + 1))
        |> Option.orElseWith (fun () ->
            let prefix = $"{name}="

            args
            |> Array.tryPick (fun argument ->
                if argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                    Some(argument[prefix.Length ..])
                else
                    None))
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let environmentName args =
        environmentArgument args
        |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable "DOTNET_ENVIRONMENT" |> nonEmpty)
        |> Option.orElseWith (fun () -> Environment.GetEnvironmentVariable "ASPNETCORE_ENVIRONMENT" |> nonEmpty)
        |> Option.defaultValue "Development"

    let createBuilder args =
        WebApplication.CreateBuilder(WebApplicationOptions(Args = args, EnvironmentName = environmentName args))

    let connectionString (configuration: IConfiguration) =
        configuration.GetConnectionString "App"
        |> Option.ofObj
        |> Option.defaultValue "Host=127.0.0.1;Port=5432;Database=fsnix;Username=fsnix;Password=fsnix"

    let configureServices (builder: WebApplicationBuilder) =
        builder.Services.AddRouting() |> ignore
        builder.Services.AddAntiforgery() |> ignore
        builder.Services.AddOxpecker() |> ignore
        builder.Services.AddSingleton(TimeProvider.System) |> ignore

        DatabaseServices.addPostgres (connectionString builder.Configuration) builder.Services
        |> ignore

        builder.Services
            .AddIdentityCore<ApplicationUser>(fun options ->
                options.User.RequireUniqueEmail <- true
                options.Password.RequiredLength <- 12
                options.Password.RequireDigit <- true
                options.Password.RequireLowercase <- true
                options.Password.RequireUppercase <- true
                options.Password.RequireNonAlphanumeric <- true
                options.Lockout.AllowedForNewUsers <- true
                options.Lockout.MaxFailedAccessAttempts <- 5
                options.Lockout.DefaultLockoutTimeSpan <- TimeSpan.FromMinutes 15.)
            .AddUserStore<PostgresUserStore>()
            .AddSignInManager()
            .AddTokenProvider<AuthenticatorTokenProvider<ApplicationUser>>(TokenOptions.DefaultAuthenticatorProvider)
        |> ignore

        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies()
        |> ignore

        builder.Services.ConfigureApplicationCookie(fun options ->
            options.Cookie.Name <- "fsnix.identity"
            options.Cookie.HttpOnly <- true
            options.Cookie.SameSite <- SameSiteMode.Lax
            options.Cookie.SecurePolicy <- CookieSecurePolicy.SameAsRequest
            options.ExpireTimeSpan <- TimeSpan.FromHours 8.
            options.SlidingExpiration <- true
            options.LoginPath <- PathString "/account/login")
        |> ignore

        builder.Services.Configure<SecurityStampValidatorOptions>(fun (options: SecurityStampValidatorOptions) ->
            options.ValidationInterval <- TimeSpan.FromMinutes 5.)
        |> ignore

        builder.Services.AddAuthorization(fun options ->
            options.AddPolicy(
                "MfaAdmin",
                fun policy -> policy.RequireAuthenticatedUser().RequireClaim("amr", "mfa") |> ignore
            ))
        |> ignore

        // The database provider must win over the configuration provider added by FeatureManagement.
        builder.Services.AddSingleton<IFeatureDefinitionProvider, DatabaseFeatureDefinitionProvider>()
        |> ignore

        builder.Services.AddFeatureManagement() |> ignore

    let applyMigrations (app: WebApplication) =
        let connection = connectionString app.Configuration

        match Migrator.migrate connection app.Environment.EnvironmentName with
        | Ok() -> ()
        | Error failure -> raise (InvalidOperationException(failure.Message, Option.toObj failure.Exception))

    let endpoints =
        [ GET
              [ route "/" Demo.index
                route "/events/features" FeatureEvents.stream
                route "/demo/dashboard" (Demo.fragment NewDashboard)
                route "/demo/checkout" (Demo.fragment BetaCheckout)
                route "/account/login" Account.loginPage
                route "/account/login/2fa" Account.twoFactorPage
                route "/account/login/recovery" Account.recoveryLoginPage
                route "/account/2fa" (Account.requireAuthenticated Account.enrollmentPage)
                route "/admin/features" (Account.requireMfa Admin.index)
                route "/admin/features/{name}" (Account.requireMfa Admin.featureCard) ]
          POST
              [ route "/account/login" (Admin.requireValidAntiforgery Account.login)
                route "/account/login/2fa" (Admin.requireValidAntiforgery Account.twoFactor)
                route "/account/login/recovery" (Admin.requireValidAntiforgery Account.recoveryLogin)
                route
                    "/account/2fa/key"
                    (Account.requireAuthenticated (Admin.requireValidAntiforgery Account.createKey))
                route
                    "/account/2fa/enable"
                    (Account.requireAuthenticated (Admin.requireValidAntiforgery Account.enableTwoFactor))
                route
                    "/account/2fa/recovery-codes"
                    (Account.requireMfa (Admin.requireValidAntiforgery Account.regenerateRecoveryCodes))
                route
                    "/account/2fa/reset"
                    (Account.requireMfa (Admin.requireValidAntiforgery Account.resetAuthenticator))
                route "/account/logout" (Account.requireAuthenticated (Admin.requireValidAntiforgery Account.logout))
                route
                    "/admin/features/{name}/schedule"
                    (Account.requireMfa (Admin.requireValidAntiforgery Admin.schedule)) ] ]

    let create (args: string array) =
        let builder = createBuilder args
        configureServices builder
        let app = builder.Build()
        applyMigrations app

        app.UseExceptionHandler(fun errorApp ->
            errorApp.Run(fun context ->
                task {
                    let error = context.Features.Get<IExceptionHandlerFeature>()
                    let logger = context.RequestServices.GetRequiredService<ILogger<AppMarker>>()

                    match Option.ofObj error |> Option.map _.Error with
                    | Some(:? AntiforgeryValidationException) ->
                        context.Response.StatusCode <- StatusCodes.Status400BadRequest
                        context.Response.ContentType <- "text/plain; charset=utf-8"
                        do! context.Response.WriteAsync "The antiforgery token is invalid or missing."
                    | exceptionOption ->
                        exceptionOption
                        |> Option.iter (fun exceptionValue ->
                            logger.LogError(exceptionValue, "Unhandled request failure"))

                        context.Response.StatusCode <- StatusCodes.Status500InternalServerError
                        context.Response.ContentType <- "text/plain; charset=utf-8"
                        do! context.Response.WriteAsync "An unexpected error occurred."
                }))
        |> ignore

        app.Use(fun (context: HttpContext) (next: RequestDelegate) ->
            context.Response.OnStarting(fun () ->
                context.Response.Headers["Content-Security-Policy"] <-
                    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'"

                context.Response.Headers["X-Content-Type-Options"] <- "nosniff"
                context.Response.Headers["Referrer-Policy"] <- "no-referrer"
                Task.CompletedTask)
            |> ignore

            next.Invoke(context))
        |> ignore

        app.UseStaticFiles() |> ignore
        app.UseRouting() |> ignore
        app.UseAuthentication() |> ignore
        app.UseAuthorization() |> ignore
        app.UseAntiforgery() |> ignore
        app.UseOxpecker endpoints |> ignore
        app

module Program =
    let run args =
        task {
            let migrateOnly = args |> Array.contains "--migrate"
            let bootstrapOnly = args |> Array.contains "--bootstrap-user"

            if migrateOnly then
                let builder = Application.createBuilder args
                let connection = Application.connectionString builder.Configuration

                match Migrator.migrate connection builder.Environment.EnvironmentName with
                | Ok() ->
                    printfn "Database migrations applied successfully."
                    return 0
                | Error failure ->
                    eprintfn "%s" failure.Message
                    failure.Exception |> Option.iter (fun error -> eprintfn "%s" error.Message)
                    return 1
            elif bootstrapOnly then
                let builder = Application.createBuilder args
                Application.configureServices builder
                use app = builder.Build()
                Application.applyMigrations app

                let! result = Bootstrap.run app.Services builder.Configuration

                match result with
                | Ok userId ->
                    printfn "Bootstrap user %O created successfully." userId
                    return 0
                | Error message ->
                    eprintfn "%s" message
                    return 1
            else
                use app = Application.create args
                do! app.RunAsync()
                return 0
        }

    [<EntryPoint>]
    let main args = run args |> _.GetAwaiter().GetResult()
