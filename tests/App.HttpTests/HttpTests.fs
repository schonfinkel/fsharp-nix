namespace App.Tests

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Security.Claims
open System.Text.Encodings.Web
open System.Text.RegularExpressions
open App
open App.Domain
open Expecto
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.FeatureManagement
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Npgsql
open Oxpecker.Htmx

type TestAuthenticationState(authenticated: bool) =
    member _.Authenticated = authenticated

type TestAuthenticationHandler
    (
        options: IOptionsMonitor<AuthenticationSchemeOptions>,
        logger: ILoggerFactory,
        encoder: UrlEncoder,
        state: TestAuthenticationState
    ) =
    inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)

    override _.HandleAuthenticateAsync() =
        if state.Authenticated then
            let claims =
                [ Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString())
                  Claim(ClaimTypes.Name, "test-admin")
                  Claim("amr", "mfa") ]

            let identity = ClaimsIdentity(claims, "Test")
            let ticket = AuthenticationTicket(ClaimsPrincipal(identity), "Test")
            AuthenticateResult.Success ticket |> System.Threading.Tasks.Task.FromResult
        else
            AuthenticateResult.NoResult() |> System.Threading.Tasks.Task.FromResult

type AppFactory(store: FakeFeatureFlagStore, connectionString: string, ?authenticated: bool) =
    inherit WebApplicationFactory<AppMarker>()

    override _.ConfigureWebHost(builder: IWebHostBuilder) =
        builder
            .UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/App")))
            .ConfigureServices(fun services ->
                services.RemoveAll<NpgsqlDataSource>() |> ignore

                services.AddSingleton<NpgsqlDataSource>(fun _ -> NpgsqlDataSource.Create connectionString)
                |> ignore

                services.AddSingleton(TestAuthenticationState(defaultArg authenticated true))
                |> ignore

                services
                    .AddAuthentication(fun options ->
                        options.DefaultAuthenticateScheme <- "Test"
                        options.DefaultChallengeScheme <- "Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", ignore)
                |> ignore

                services.AddSingleton<IFeatureFlagStore>(store :> IFeatureFlagStore) |> ignore

                services.AddSingleton<IFeatureDefinitionProvider, DatabaseFeatureDefinitionProvider>()
                |> ignore)
        |> ignore

type HttpTests(fixture: PostgreSqlFixture) =
    let csrf (client: HttpClient) =
        task {
            let! html = client.GetStringAsync "/admin/features"

            let matched =
                Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")

            Assert.True(matched.Success, "Expected an antiforgery token in the admin form.")
            return matched.Groups[1].Value
        }

    let postSchedule (client: HttpClient) name fields =
        let request =
            new HttpRequestMessage(HttpMethod.Post, $"/admin/features/{name}/schedule")

        request.Headers.Add(HxRequestHeader.Request, "true")
        request.Content <- new FormUrlEncodedContent(fields)
        client.SendAsync request

    member _.``anonymous users are redirected away from feature administration``() =
        task {
            use factory =
                new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString, false)

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            let! response = client.GetAsync "/admin/features"
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode)
            Assert.StartsWith("/account/login", response.Headers.Location.OriginalString)
        }

    member _.``normal navigation returns a layout and HTMX returns a fragment``() =
        task {
            use factory = new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString)
            use client = factory.CreateClient()
            let! fullResponse = client.GetAsync "/admin/features"
            let! full = fullResponse.Content.ReadAsStringAsync()
            Assert.Contains("<html", full)
            Assert.Contains("src=\"/assets/hx-sse.min.js\"", full)
            Assert.Contains("hx-sse:connect=\"/events/features\"", full)
            Assert.Contains("hx-trigger=\"feature-change from:body\"", full)
            Assert.Contains("hx-swap=\"outerMorph\"", full)
            Assert.Contains("hx-disable=\"find button\"", full)
            Assert.Contains("hx-indicator=\"#schedule-indicator-NewDashboard\"", full)
            Assert.Contains("src=\"/js/scheduling.js\"", full)
            Assert.Contains("name=\"effectiveAtLocal\"", full)
            Assert.Contains("name=\"effectiveAt\"", full)
            Assert.Contains("Effective at (your local time; blank means now)", full)

            let policy =
                fullResponse.Headers.GetValues("Content-Security-Policy") |> Seq.exactlyOne

            Assert.Contains("default-src 'self'", policy)

            use request = new HttpRequestMessage(HttpMethod.Get, "/admin/features")
            request.Headers.Add(HxRequestHeader.Request, "true")
            let! response = client.SendAsync request
            let! fragment = response.Content.ReadAsStringAsync()
            Assert.DoesNotContain("<html", fragment)
            Assert.Contains("feature-NewDashboard", fragment)
            Assert.True(response.Headers.Vary |> Seq.contains HxRequestHeader.Request)
        }

    member _.``demo endpoints expose both feature variants``() =
        task {
            let store = FakeFeatureFlagStore()
            use factory = new AppFactory(store, fixture.ConnectionString)
            use client = factory.CreateClient()

            let! enabled = client.GetStringAsync "/demo/dashboard"
            let! disabled = client.GetStringAsync "/demo/checkout"
            Assert.Contains("New dashboard", enabled)
            Assert.Contains("Standard checkout", disabled)
        }

    member _.``feature event stream starts with a refresh event``() =
        task {
            use factory = new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString)
            use client = factory.CreateClient()
            use request = new HttpRequestMessage(HttpMethod.Get, "/events/features")

            use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType.MediaType)
            use! stream = response.Content.ReadAsStreamAsync()
            use reader = new StreamReader(stream)
            let! eventLine = reader.ReadLineAsync()
            let! dataLine = reader.ReadLineAsync()
            Assert.Equal("event: feature-change", eventLine)
            Assert.Equal("data: refresh", dataLine)
        }

    member _.``admin scheduling requires antiforgery``() =
        task {
            use factory = new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString)
            use client = factory.CreateClient()
            let fields = Dictionary<string, string>()
            fields["enabled"] <- "true"
            let! response = postSchedule client "NewDashboard" fields
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)
        }

    member _.``schedule success replaces card and emits feature event``() =
        task {
            use factory = new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString)
            use client = factory.CreateClient()
            let! token = csrf client
            let fields = Dictionary<string, string>()
            fields["enabled"] <- "true"
            fields["__RequestVerificationToken"] <- token
            let! response = postSchedule client "BetaCheckout" fields
            let! html = response.Content.ReadAsStringAsync()

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("feature-change", response.Headers.GetValues(HxResponseHeader.Trigger) |> Seq.exactlyOne)
            Assert.Contains("feature-BetaCheckout", html)
            Assert.Contains("Enabled", html)
        }

    member _.``no-op schedule replaces card without emitting a feature event``() =
        task {
            use factory = new AppFactory(FakeFeatureFlagStore(), fixture.ConnectionString)
            use client = factory.CreateClient()
            let! token = csrf client
            let fields = Dictionary<string, string>()
            fields["enabled"] <- "true"
            fields["__RequestVerificationToken"] <- token
            let! response = postSchedule client "NewDashboard" fields
            let! html = response.Content.ReadAsStringAsync()

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.False(response.Headers.Contains HxResponseHeader.Trigger)
            Assert.Contains("feature-NewDashboard", html)
            Assert.Contains("Enabled", html)
        }

    member _.``validation missing flags and conflicts have deliberate statuses``() =
        task {
            let store = FakeFeatureFlagStore()
            use factory = new AppFactory(store, fixture.ConnectionString)
            use client = factory.CreateClient()
            let! token = csrf client

            let invalid = Dictionary<string, string>()
            invalid["effectiveAt"] <- "2020-01-01T00:00:00Z"
            invalid["__RequestVerificationToken"] <- token
            let! invalidResponse = postSchedule client "NewDashboard" invalid
            let! invalidHtml = invalidResponse.Content.ReadAsStringAsync()
            Assert.Equal(enum<HttpStatusCode> 422, invalidResponse.StatusCode)
            Assert.Contains("value=\"2020-01-01T00:00:00Z\"", invalidHtml)
            Assert.Contains("aria-invalid=\"true\"", invalidHtml)

            let unconverted = Dictionary<string, string>()
            unconverted["effectiveAtLocal"] <- "2099-01-01T12:00"
            unconverted["__RequestVerificationToken"] <- token
            let! unconvertedResponse = postSchedule client "NewDashboard" unconverted
            let! unconvertedHtml = unconvertedResponse.Content.ReadAsStringAsync()
            Assert.Equal(enum<HttpStatusCode> 422, unconvertedResponse.StatusCode)
            Assert.Contains("value=\"2099-01-01T12:00\"", unconvertedHtml)
            Assert.Contains("Enter a valid local date and time.", unconvertedHtml)

            let missing = Dictionary<string, string>()
            missing["__RequestVerificationToken"] <- token
            let! missingResponse = postSchedule client "UnknownFlag" missing
            Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode)

            store.SetScheduleFailure(Some BoundaryExists)
            let conflict = Dictionary<string, string>()
            conflict["__RequestVerificationToken"] <- token
            let! conflictResponse = postSchedule client "NewDashboard" conflict
            Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode)
        }
