namespace App

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open App.Auth
open App.Cart
open App.Database
open App.Domain
open App.Orders
open App.Payments
open App.Refunds
open App.Invoices
open App.Returns
open App.Shipments
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open App.Migrations
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Diagnostics
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.FeatureManagement
open Npgsql
open Oxpecker
open Serilog
open Serilog.Events

type AppMarker = class end

[<RequireQualifiedAccess>]
module BootSql =
    let appliedRepeatables = Sql.load "Boot/applied-repeatables"
    let appliedScripts = Sql.load "Boot/applied-scripts"
    let fsmSchemaExists = Sql.load "Boot/fsm-schema-exists"

[<RequireQualifiedAccess>]
module BootChecks =

    /// <summary>
    /// Read-only verification that the deployed schema is current before serving traffic.
    /// Only <c>--migrate</c> (and the deployment job running it) mutates schemas; a web pod
    /// that boots against a stale or missing schema fails loudly instead of creating it.
    /// </summary>
    let verify
        (logger: Microsoft.Extensions.Logging.ILogger)
        (connectionString: string)
        (environmentName: string)
        : Task<Result<unit, string>> =
        task {
            let expected = Migrator.expectedScriptNames environmentName

            try
                use connection = new NpgsqlConnection(connectionString)
                do! connection.OpenAsync()

                use fsmExists = new NpgsqlCommand(BootSql.fsmSchemaExists, connection)

                let! fsm = fsmExists.ExecuteScalarAsync()

                if not (fsm :?> bool) then
                    return Error "The fsm schema is missing; run the deployment migration job (--migrate) first."
                else
                    use journal = new NpgsqlCommand(BootSql.appliedScripts, connection)

                    let! reader = journal.ExecuteReaderAsync()
                    let applied = HashSet<string>()

                    while reader.Read() do
                        applied.Add(reader.GetString 0) |> ignore

                    reader.Dispose()

                    let missing =
                        expected
                        |> List.filter (fun name ->
                            not (applied.Contains name)
                            && not (name.Contains(".repeatable.", StringComparison.Ordinal)))
                        |> List.distinct

                    let repeatableExpected =
                        expected
                        |> List.filter (fun name -> name.Contains(".repeatable.", StringComparison.Ordinal))

                    use repeatable = new NpgsqlCommand(BootSql.appliedRepeatables, connection)

                    let! repeatableReader = repeatable.ExecuteReaderAsync()
                    let repeatableApplied = HashSet<string>()

                    while repeatableReader.Read() do
                        repeatableApplied.Add(repeatableReader.GetString 0) |> ignore

                    repeatableReader.Dispose()

                    let missingRepeatables =
                        repeatableExpected
                        |> List.filter (fun name -> not (repeatableApplied.Contains name))

                    match missing, missingRepeatables with
                    | [], [] ->
                        logger.LogInformation("Boot checks passed: schema and journals are current.")
                        return Ok()
                    | _ ->
                        let absent = missing @ missingRepeatables |> String.concat ", "
                        return Error $"Pending migrations detected ({absent}); run --migrate before starting."
            with error ->
                return Error $"Boot checks failed ({SafeDiagnostics.exceptionType error})."
        }

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
        let builder =
            WebApplication.CreateBuilder(WebApplicationOptions(Args = args, EnvironmentName = environmentName args))

        builder.Host.UseSerilog(fun context services logger ->
            logger.ReadFrom
                .Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .WriteTo.Console(
                    outputTemplate =
                        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
                )
            |> ignore)
        |> ignore

        builder

    let connectionString (configuration: IConfiguration) =
        configuration.GetConnectionString "App"
        |> Option.ofObj
        |> Option.defaultValue "Host=127.0.0.1;Port=5432;Database=fsnix;Username=fsnix;Password=fsnix"

    /// <summary>The HMAC key for guest-cart (and later order-tracking) capability hashes.
    /// Development falls back to a fixed non-secret key; production refuses to boot without an
    /// explicitly configured key.</summary>
    let capabilityHashKey (configuration: IConfiguration) (environmentName: string) : CapabilityHashKey =
        let keyId =
            configuration["Capabilities:HashKeyId"]
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue "dev-primary"

        let keyBase64 =
            configuration["Capabilities:HashKey"]
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.orElseWith (fun () ->
                if String.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase) then
                    invalidOp "Production requires Capabilities:HashKey (base64, at least 256 bits)."
                else
                    Some "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")

        let bytes =
            try
                Convert.FromBase64String keyBase64.Value
            with _ ->
                invalidOp "Capabilities:HashKey must be base64-encoded."

        CapabilityHashKey.create keyId bytes
        |> Result.defaultWith (fun message -> invalidOp message)

    /// <summary>Supervised worker registration for one machine: the worker store owns LISTEN and
    /// processing, the chart registry is shared, and action delivery resolves the scoped
    /// <c>IActionHandler</c>.</summary>
    let private worker<'Id, 'State, 'Event, 'Action, 'Error when 'Id: equality>
        machineKey
        loggerName
        (buildWorker:
            Microsoft.Extensions.Logging.ILogger
                -> PostgresContext
                -> Result<
                    ByzantineSystems.Automata.Runtime.Machine<'Id, 'State, 'Event, 'Action, 'Error>,
                    ByzantineSystems.Automata.Runtime.MachineConfigError list
                 >)
        =
        { MachineKey = machineKey
          Supervisor = AutomataSupervisorOptions.defaults machineKey
          Actions = ActionDelivery.registered<'Id, 'Action, 'Error>
          MachineFactory =
            fun (provider: IServiceProvider) ->
                buildWorker
                    (provider.GetRequiredService<ILoggerFactory>().CreateLogger(loggerName: string))
                    (provider.GetRequiredService<PostgresContext>())
          ChartRegistry =
            fun provider -> PostgresChartRegistry { Context = provider.GetRequiredService<PostgresContext>() }
          TimeProvider = TimeProvider.System }

    let configureServices (builder: WebApplicationBuilder) =
        builder.Services.AddRouting() |> ignore
        builder.Services.AddAntiforgery() |> ignore
        builder.Services.AddOxpecker() |> ignore
        builder.Services.AddSingleton(TimeProvider.System) |> ignore
        builder.Services.AddSingleton<RuntimeHealth>() |> ignore
        builder.Services.AddSingleton<CatalogStore>() |> ignore

        builder.Services.AddSingleton(CheckoutPricing.load builder.Configuration)
        |> ignore

        builder.Services.AddSingleton(InvoiceIssuerConfig.load builder.Configuration)
        |> ignore

        builder.Services.AddSingleton(ReconciliationPolicy.load builder.Configuration)
        |> ignore

        builder.Services.AddSingleton(InvoiceRenderPolicyConfig.load builder.Configuration)
        |> ignore

        builder.Services.AddSingleton(ShipmentTrackingPolicy.load builder.Configuration)
        |> ignore

        builder.Services.AddSingleton<ICartChangeSource, PostgresCartChangeSource>()
        |> ignore

        builder.Services.AddSingleton<CapabilityHashKey>(fun _ ->
            capabilityHashKey builder.Configuration builder.Environment.EnvironmentName)
        |> ignore

        // Identity tokens and protected token values depend on Data Protection; a durable,
        // shared key ring with a stable discriminator is mandatory in production so restarts
        // and multiple instances validate each other's tokens.
        let dataProtection: IDataProtectionBuilder = builder.Services.AddDataProtection()
        dataProtection.SetApplicationName("fsnix") |> ignore

        match builder.Configuration["DataProtection:KeyRingPath"] with
        | path when not (String.IsNullOrWhiteSpace path) ->
            dataProtection.PersistKeysToFileSystem(DirectoryInfo(path)) |> ignore
        | _ ->
            if builder.Environment.IsProduction() then
                invalidOp
                    "Production requires DataProtection:KeyRingPath so token keys survive restarts and are shared across instances."

        // Email delivery is an explicit deployment decision: production must configure an SMTP
        // provider (host, TLS, sender, public origin); development defaults to devenv Mailpit.
        let emailOptions = EmailOptions.load builder.Configuration

        match EmailOptions.validate builder.Environment.EnvironmentName emailOptions with
        | Ok() -> ()
        | Error message -> invalidOp message

        builder.Services.AddSingleton(emailOptions) |> ignore

        builder.Services.AddSingleton<IEmailTransport>(fun _ -> SmtpEmailTransport(emailOptions) :> IEmailTransport)
        |> ignore

        DatabaseServices.addPostgres (connectionString builder.Configuration) builder.Services
        |> ignore

        builder.Services
            .AddIdentityCore<ApplicationUser>(fun options ->
                options.User.RequireUniqueEmail <- true
                options.SignIn.RequireConfirmedEmail <- true
                options.Password.RequiredLength <- 12
                options.Password.RequireDigit <- true
                options.Password.RequireLowercase <- true
                options.Password.RequireUppercase <- true
                options.Password.RequireNonAlphanumeric <- true
                options.Lockout.AllowedForNewUsers <- true
                options.Lockout.MaxFailedAccessAttempts <- 5
                options.Lockout.DefaultLockoutTimeSpan <- TimeSpan.FromMinutes 15.
                options.Tokens.EmailConfirmationTokenProvider <- AccountTokenProviders.EmailConfirmation
                options.Tokens.PasswordResetTokenProvider <- AccountTokenProviders.PasswordReset
                options.Tokens.ChangeEmailTokenProvider <- AccountTokenProviders.ChangeEmail)
            .AddUserStore<PostgresUserStore>()
            .AddSignInManager()
            .AddTokenProvider<AuthenticatorTokenProvider<ApplicationUser>>(TokenOptions.DefaultAuthenticatorProvider)
            .AddTokenProvider<EmailConfirmationTokenProvider>(AccountTokenProviders.EmailConfirmation)
            .AddTokenProvider<PasswordResetTokenProvider>(AccountTokenProviders.PasswordReset)
            .AddTokenProvider<ChangeEmailTokenProvider>(AccountTokenProviders.ChangeEmail)
        |> ignore

        builder.Services.Configure<EmailConfirmationTokenOptions>(fun (options: EmailConfirmationTokenOptions) ->
            options.Name <- AccountTokenProviders.EmailConfirmation
            options.TokenLifespan <- AccountTokenLifespans.EmailConfirmation)
        |> ignore

        builder.Services.Configure<PasswordResetTokenOptions>(fun (options: PasswordResetTokenOptions) ->
            options.Name <- AccountTokenProviders.PasswordReset
            options.TokenLifespan <- AccountTokenLifespans.PasswordReset)
        |> ignore

        builder.Services.Configure<ChangeEmailTokenOptions>(fun (options: ChangeEmailTokenOptions) ->
            options.Name <- AccountTokenProviders.ChangeEmail
            options.TokenLifespan <- AccountTokenLifespans.ChangeEmail)
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

        builder.Services
            .AddSingleton<ISupervisionEventStore>(fun provider ->
                PostgresSupervisionStore(provider.GetRequiredService<PostgresContext>()) :> ISupervisionEventStore)
            .AddScoped<IActionHandler<ProbeId, ProbeAction, ProbeActionError>, ProbeEffectHandler>()
            .AddScoped<IActionHandler<FlowId, FlowAction, FlowActionError>, AccountFlowEffectHandler>()
            .AddScoped<IActionHandler<CartId, CartAction, CartActionError>, CartEffectHandler>()
            .AddScoped<IActionHandler<OrderId, OrderAction, OrderActionError>, OrderEffectHandler>()
            .AddScoped<IActionHandler<PaymentId, PaymentAction, PaymentActionError>, PaymentEffectHandler>()
            .AddScoped<IActionHandler<ShipmentEntityId, ShipmentAction, ShipmentActionError>, ShipmentEffectHandler>()
            .AddScoped<IActionHandler<RefundEntityId, RefundAction, RefundActionError>, RefundEffectHandler>()
            .AddScoped<IActionHandler<ReturnEntityId, ReturnAction, ReturnActionError>, ReturnEffectHandler>()
            .AddScoped<IActionHandler<InvoiceEntityId, InvoiceAction, InvoiceActionError>, InvoiceEffectHandler>()
            .AddSingleton<SimulatedPaymentGateway>()
            .AddSingleton<IPaymentGateway>(fun provider ->
                provider.GetRequiredService<SimulatedPaymentGateway>() :> IPaymentGateway)
            .AddSingleton<SimulatedCarrier>()
            .AddSingleton<ICarrier>(fun provider -> provider.GetRequiredService<SimulatedCarrier>() :> ICarrier)
            .AddSingleton<ProbeMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<ProbeMachineClient>())
            .AddSingleton<AccountFlowMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<AccountFlowMachineClient>())
            .AddSingleton<CartMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<CartMachineClient>())
            .AddSingleton<OrderMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<OrderMachineClient>())
            .AddSingleton<PaymentMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<PaymentMachineClient>())
            .AddSingleton<ShipmentMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<ShipmentMachineClient>())
            .AddSingleton<RefundMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<RefundMachineClient>())
            .AddSingleton<ReturnMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<ReturnMachineClient>())
            .AddSingleton<InvoiceMachineClient>()
            .AddHostedService(fun provider -> provider.GetRequiredService<InvoiceMachineClient>())
            .AddHostedService<ReservationExpiryScanner>()
            .AddSingleton<OutboxDestination list>(fun provider ->
                let probeClient = provider.GetRequiredService<ProbeMachineClient>()
                let flowsClient = provider.GetRequiredService<AccountFlowMachineClient>()
                let cartsClient = provider.GetRequiredService<CartMachineClient>()
                let ordersClient = provider.GetRequiredService<OrderMachineClient>()
                let paymentsClient = provider.GetRequiredService<PaymentMachineClient>()
                let shipmentsClient = provider.GetRequiredService<ShipmentMachineClient>()
                let refundsClient = provider.GetRequiredService<RefundMachineClient>()
                let returnsClient = provider.GetRequiredService<ReturnMachineClient>()
                let invoicesClient = provider.GetRequiredService<InvoiceMachineClient>()

                [ OutboxDestination.forMachineProvider
                      Probe.MachineKey
                      EntityId.create
                      (Serialization.systemTextJson<ProbeEvent> ())
                      (fun () -> probeClient.Probe)
                  OutboxDestination.forMachineProvider
                      AccountFlow.MachineKey
                      EntityId.create
                      AccountFlowCodec.event
                      (fun () -> flowsClient.Flows)
                  OutboxDestination.forMachineProvider Cart.MachineKey EntityId.create CartCodec.event (fun () ->
                      cartsClient.Carts)
                  OutboxDestination.forMachineProvider Orders.MachineKey EntityId.create OrderCodec.event (fun () ->
                      ordersClient.Orders)
                  OutboxDestination.forMachineProvider
                      Payments.MachineKey
                      EntityId.create
                      PaymentCodec.event
                      (fun () -> paymentsClient.Payments)
                  OutboxDestination.forMachineProvider
                      Shipments.MachineKey
                      EntityId.create
                      ShipmentCodec.event
                      (fun () -> shipmentsClient.Shipments)
                  OutboxDestination.forMachineProvider Refunds.MachineKey EntityId.create RefundCodec.event (fun () ->
                      refundsClient.Refunds)
                  OutboxDestination.forMachineProvider Returns.MachineKey EntityId.create ReturnCodec.event (fun () ->
                      returnsClient.Returns)
                  OutboxDestination.forMachineProvider
                      Invoices.MachineKey
                      EntityId.create
                      InvoiceCodec.event
                      (fun () -> invoicesClient.Invoices) ])
            .AddHostedService<IntegrationOutboxRelay>()
            .AddHostedService<EmailDeliveryRelay>()
            .AddHostedService<FlowDeadlineScanner>()
            .AddHostedService<CartAbandonmentScanner>()
            .AddHostedService<CartMergeScanner>()
            .AddHostedService<ReturnWindowScanner>()
            .AddHostedService<GatewayReconciliationScanner>()
            .AddHostedService<AuthorizationExpiryScanner>()
            .AddHostedService<InvoiceRenderScanner>()
            .AddHostedService<ShipmentLostScanner>()
            .AddAutomata(worker Probe.MachineKey "probes" Probe.buildWorker)
            .AddAutomata(worker AccountFlow.MachineKey "flows" AccountFlowCodec.buildWorker)
            .AddAutomata(worker Cart.MachineKey "carts" CartCodec.buildWorker)
            .AddAutomata(worker Orders.MachineKey "orders" OrderCodec.buildWorker)
            .AddAutomata(worker Payments.MachineKey "payments" PaymentCodec.buildWorker)
            .AddAutomata(worker Shipments.MachineKey "shipments" ShipmentCodec.buildWorker)
            .AddAutomata(worker Refunds.MachineKey "refunds" RefundCodec.buildWorker)
            .AddAutomata(worker Returns.MachineKey "returns" ReturnCodec.buildWorker)
            .AddAutomata(worker Invoices.MachineKey "invoices" InvoiceCodec.buildWorker)
            .AddAutomataMaintenance(
                { MaintenanceOptions.defaults (fun provider ->
                      PostgresMaintenance(provider.GetRequiredService<PostgresContext>())) with
                    Scheduler = MaintenanceScheduler.InProcess }
            )
        |> ignore

    let applyBootChecks (app: WebApplication) =
        let connection = connectionString app.Configuration
        let health = app.Services.GetRequiredService<RuntimeHealth>()

        task {
            health.Starting RuntimeComponent.Boot
            let! verified = BootChecks.verify app.Logger connection app.Environment.EnvironmentName

            match verified with
            | Ok() -> health.Succeeded RuntimeComponent.Boot
            | Error message ->
                health.Failed(RuntimeComponent.Boot, RuntimeFailure.StartupFailed)
                return raise (InvalidOperationException message)
        }

    let endpoints =
        [ GET
              [ route "/health/live" Health.live
                route "/health/startup" Health.startup
                route "/health/ready" Health.ready
                route "/" Demo.index
                route "/events/features" FeatureEvents.stream
                route "/demo/dashboard" (Demo.fragment NewDashboard)
                route "/demo/checkout" (Demo.fragment BetaCheckout)
                route "/catalog" CatalogEndpoints.search
                route "/cart" CartEndpoints.page
                route "/checkout" (Account.requireAuthenticated OrderEndpoints.page)
                route "/orders/{orderId}" (Account.requireAuthenticated OrderEndpoints.show)
                route "/returns/{returnId}" (Account.requireAuthenticated ReturnEndpoints.show)
                route "/cart/events" CartEvents.stream
                route "/account/login" Account.loginPage
                route "/account/login/2fa" Account.twoFactorPage
                route "/account/login/recovery" Account.recoveryLoginPage
                route "/account/register" AccountFlowEndpoints.registerPage
                route "/account/forgot-password" AccountFlowEndpoints.forgotPasswordPage
                route "/account/resend" AccountFlowEndpoints.resendPage
                route "/account/confirm-email" AccountFlowEndpoints.confirmEmailPage
                route "/account/reset-password" AccountFlowEndpoints.resetPasswordPage
                route "/account/confirm-email-change" AccountFlowEndpoints.confirmEmailChangePage
                route "/account/email" (Account.requireAuthenticated AccountFlowEndpoints.changeEmailPage)
                route "/account/2fa" (Account.requireAuthenticated Account.enrollmentPage)
                route "/admin/features" (Account.requireMfa Admin.index)
                route "/admin/features/{name}" (Account.requireMfa Admin.featureCard)
                route "/admin/operations" (Account.requireMfa OperationalHealthEndpoints.index)
                route "/admin/catalog" (Account.requireMfa CatalogAdminEndpoints.index)
                route "/admin/returns/{returnId}" (Account.requireMfa ReturnEndpoints.adminPage)
                route "/invoices" (Account.requireAuthenticated InvoiceEndpoints.list)
                route "/invoices/{invoiceId}/pdf" (Account.requireAuthenticated InvoiceEndpoints.customerPdf)
                route "/admin/invoices" (Account.requireMfa InvoiceEndpoints.adminList)
                route "/admin/invoices/{invoiceId}/pdf" (Account.requireMfa InvoiceEndpoints.adminPdf)
                route "/admin/probe" (Account.requireMfa ProbeAdmin.index) ]
          POST
              [ route "/account/login" (Admin.requireValidAntiforgery Account.login)
                route "/account/login/2fa" (Admin.requireValidAntiforgery Account.twoFactor)
                route "/account/login/recovery" (Admin.requireValidAntiforgery Account.recoveryLogin)
                route "/account/register" (Admin.requireValidAntiforgery AccountFlowEndpoints.register)
                route "/account/forgot-password" (Admin.requireValidAntiforgery AccountFlowEndpoints.forgotPassword)
                route "/account/resend" (Admin.requireValidAntiforgery AccountFlowEndpoints.resend)
                route "/account/confirm-email" (Admin.requireValidAntiforgery AccountFlowEndpoints.confirmEmail)
                route "/account/reset-password" (Admin.requireValidAntiforgery AccountFlowEndpoints.resetPassword)
                route
                    "/account/confirm-email-change"
                    (Admin.requireValidAntiforgery AccountFlowEndpoints.confirmEmailChange)
                route
                    "/account/email"
                    (Account.requireAuthenticated (Admin.requireValidAntiforgery AccountFlowEndpoints.changeEmail))
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
                route "/cart/items" (Admin.requireValidAntiforgery CartEndpoints.addItem)
                route "/cart/items/{productId}" (Admin.requireValidAntiforgery CartEndpoints.updateItem)
                route "/cart/items/{productId}/remove" (Admin.requireValidAntiforgery CartEndpoints.removeItem)
                route "/cart/clear" (Admin.requireValidAntiforgery CartEndpoints.clear)
                route "/checkout" (Account.requireAuthenticated (Admin.requireValidAntiforgery OrderEndpoints.submit))
                route
                    "/orders/{orderId}/authorize"
                    (Account.requireAuthenticated (Admin.requireValidAntiforgery OrderEndpoints.authorize))
                route
                    "/orders/{orderId}/cancel"
                    (Account.requireAuthenticated (Admin.requireValidAntiforgery OrderEndpoints.cancel))
                route
                    "/orders/{orderId}/returns"
                    (Account.requireAuthenticated (Admin.requireValidAntiforgery OrderEndpoints.requestReturn))
                route
                    "/admin/returns/{returnId}/{action}"
                    (Account.requireMfa (Admin.requireValidAntiforgery ReturnEndpoints.adminAction))
                route "/cart/merge" (Account.requireAuthenticated (Admin.requireValidAntiforgery CartEndpoints.merge))
                route
                    "/admin/catalog/products"
                    (Account.requireMfa (Admin.requireValidAntiforgery CatalogAdminEndpoints.save))
                route
                    "/admin/catalog/products/{productId}/retire"
                    (Account.requireMfa (Admin.requireValidAntiforgery CatalogAdminEndpoints.retire))
                route
                    "/admin/catalog/products/{productId}/adjust-stock"
                    (Account.requireMfa (Admin.requireValidAntiforgery CatalogAdminEndpoints.adjustStock))
                route
                    "/admin/features/{name}/schedule"
                    (Account.requireMfa (Admin.requireValidAntiforgery Admin.schedule))
                route "/admin/probe/run" (Account.requireMfa (Admin.requireValidAntiforgery ProbeAdmin.run))
                route
                    "/admin/invoices/{invoiceId}/retry-render"
                    (Account.requireMfa (Admin.requireValidAntiforgery InvoiceEndpoints.retryRender))
                route
                    "/admin/orders/{orderId}/ship"
                    (Account.requireMfa (Admin.requireValidAntiforgery AdminFulfilment.ship))
                route
                    "/admin/orders/{orderId}/prepare"
                    (Account.requireMfa (Admin.requireValidAntiforgery AdminFulfilment.prepare))
                route
                    "/admin/orders/{orderId}/dispatch"
                    (Account.requireMfa (Admin.requireValidAntiforgery AdminFulfilment.dispatch))
                route
                    "/admin/orders/{orderId}/deliver"
                    (Account.requireMfa (Admin.requireValidAntiforgery AdminFulfilment.deliver))
                route
                    "/admin/orders/{orderId}/hold"
                    (Account.requireMfa (Admin.requireValidAntiforgery AdminFulfilment.hold))
                route
                    "/admin/orders/{orderId}/release"
                    (Account.requireMfa (Admin.requireValidAntiforgery AdminFulfilment.release)) ] ]

    let create (args: string array) =
        Pdf.configure ()
        let builder = createBuilder args
        configureServices builder
        let app = builder.Build()
        applyBootChecks app |> (fun t -> t.GetAwaiter().GetResult())

        app.UseSerilogRequestLogging() |> ignore

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
                            logger.LogError(
                                "Unhandled request failure ({ExceptionType})",
                                SafeDiagnostics.exceptionType exceptionValue
                            ))

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
            let renderCheckOnly = args |> Array.contains "--render-check"

            if renderCheckOnly then
                // Needs no database or configuration: proves the QuestPDF native library and the
                // shipped fonts load in this environment (the flake's pdf-render check and the OCI
                // image run exactly this).
                try
                    let pdf = Pdf.renderSample ()

                    if Pdf.isPdf pdf then
                        printfn "PDF render check succeeded (%d bytes)." pdf.Length
                        return 0
                    else
                        eprintfn "PDF render check produced invalid output."
                        return 1
                with error ->
                    eprintfn "PDF render check failed: %s" (error.GetType().FullName)
                    eprintfn "%O" error
                    return 1
            elif migrateOnly then
                let builder = Application.createBuilder args
                use app = builder.Build()
                let connection = Application.connectionString app.Configuration

                match DatabaseMigrations.runAll app.Logger connection app.Environment.EnvironmentName with
                | Ok() ->
                    printfn "Database migrations applied successfully."
                    return 0
                | Error message ->
                    eprintfn "%s" message
                    return 1
            elif bootstrapOnly then
                let builder = Application.createBuilder args
                Application.configureServices builder
                use app = builder.Build()
                do! Application.applyBootChecks app

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
