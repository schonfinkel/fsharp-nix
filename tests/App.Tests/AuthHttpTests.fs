namespace App.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text.RegularExpressions
open App
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Identity
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.FeatureManagement
open Npgsql
open Xunit

type IdentityAppFactory(connectionString: string, featureStore: FakeFeatureFlagStore) =
    inherit WebApplicationFactory<AppMarker>()

    override _.ConfigureWebHost(builder: IWebHostBuilder) =
        builder.ConfigureServices(fun services ->
            services.RemoveAll<NpgsqlDataSource>() |> ignore

            services.AddSingleton<NpgsqlDataSource>(fun _ -> NpgsqlDataSource.Create connectionString)
            |> ignore

            services.AddSingleton<IFeatureFlagStore>(featureStore :> IFeatureFlagStore)
            |> ignore

            services.AddSingleton<IFeatureDefinitionProvider, DatabaseFeatureDefinitionProvider>()
            |> ignore)
        |> ignore

[<Collection("postgres")>]
[<Trait("Category", "Integration")>]
type AuthHttpTests(fixture: PostgreSqlFixture) =
    let decodeBase32 (value: string) =
        let alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
        let bytes = ResizeArray<byte>()
        let mutable buffer = 0
        let mutable bits = 0

        for character in value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant() do
            let digit = alphabet.IndexOf character
            Assert.True(digit >= 0, "Authenticator keys must use Base32.")
            buffer <- (buffer <<< 5) ||| digit
            bits <- bits + 5

            if bits >= 8 then
                bits <- bits - 8
                bytes.Add(byte (buffer >>> bits))
                buffer <- if bits = 0 then 0 else buffer &&& ((1 <<< bits) - 1)

        bytes.ToArray()

    let currentTotp key =
        let counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30L
        let counterBytes = BitConverter.GetBytes counter

        if BitConverter.IsLittleEndian then
            Array.Reverse counterBytes

        use hmac = new HMACSHA1(decodeBase32 key)
        let hash = hmac.ComputeHash counterBytes
        let offset = int hash[hash.Length - 1] &&& 0x0f

        let binary =
            ((int hash[offset] &&& 0x7f) <<< 24)
            ||| ((int hash[offset + 1] &&& 0xff) <<< 16)
            ||| ((int hash[offset + 2] &&& 0xff) <<< 8)
            ||| (int hash[offset + 3] &&& 0xff)

        (binary % 1_000_000).ToString("D6")

    let reset () =
        task {
            use connection = new NpgsqlConnection(fixture.ConnectionString)
            do! connection.OpenAsync()
            use command = new NpgsqlCommand("TRUNCATE user_tokens, users CASCADE", connection)
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }

    let csrf html =
        let matched =
            Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")

        Assert.True(matched.Success, "Expected an antiforgery token in the account form.")
        matched.Groups[1].Value

    let postForm (client: HttpClient) (path: string) fields =
        let request = new HttpRequestMessage(HttpMethod.Post, path)
        request.Content <- new FormUrlEncodedContent(fields)
        client.SendAsync request

    let createUser (users: UserManager<ApplicationUser>) username email password =
        task {
            let user = ApplicationUser()
            user.UserName <- username
            user.Email <- email
            user.EmailConfirmed <- true
            let! result = users.CreateAsync(user, password)
            Assert.True(result.Succeeded, String.concat " " (result.Errors |> Seq.map _.Description))
            return user
        }

    [<Fact>]
    member _.``bootstrap creates one account and rejects missing or duplicate configuration``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            let configuration =
                ConfigurationBuilder()
                    .AddInMemoryCollection(
                        [ KeyValuePair("Bootstrap:Username", "bootstrap-operator")
                          KeyValuePair("Bootstrap:Email", "bootstrap@example.test")
                          KeyValuePair("Bootstrap:Password", "Correct-Horse-42!") ]
                    )
                    .Build()

            let! created = Bootstrap.run factory.Services configuration

            match created with
            | Error error -> Assert.Fail error
            | Ok userId ->
                use scope = factory.Services.CreateScope()
                let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
                let! user = users.FindByIdAsync(userId.ToString())
                Assert.Equal("bootstrap-operator", user.UserName)
                Assert.True user.EmailConfirmed

            let! duplicate = Bootstrap.run factory.Services configuration

            Assert.Equal(Error "A user with that username or email already exists.", duplicate)

            let missingConfiguration = ConfigurationBuilder().Build()
            let! missing = Bootstrap.run factory.Services missingConfiguration

            Assert.Equal(Error "Bootstrap__Username, Bootstrap__Email, and Bootstrap__Password are required.", missing)
        }

    [<Fact>]
    member _.``repeated password failures lock the account without revealing why``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            use scope = factory.Services.CreateScope()
            let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            let! user = createUser users "lockout-operator" "lockout@example.test" "Correct-Horse-42!"

            for _ in 1..5 do
                let! loginPage = client.GetStringAsync "/account/login"
                let fields = Dictionary<string, string>()
                fields["email"] <- user.Email
                fields["password"] <- "Wrong-Horse-42!"
                fields["returnUrl"] <- "/admin/features"
                fields["__RequestVerificationToken"] <- csrf loginPage
                let! failed = postForm client "/account/login" fields
                let! body = failed.Content.ReadAsStringAsync()
                Assert.Equal(enum<HttpStatusCode> 422, failed.StatusCode)
                Assert.Contains("Unable to sign in with those credentials.", body)

            let! lockedLoginPage = client.GetStringAsync "/account/login"
            let correctFields = Dictionary<string, string>()
            correctFields["email"] <- user.Email
            correctFields["password"] <- "Correct-Horse-42!"
            correctFields["returnUrl"] <- "/admin/features"
            correctFields["__RequestVerificationToken"] <- csrf lockedLoginPage
            let! locked = postForm client "/account/login" correctFields
            let! lockedBody = locked.Content.ReadAsStringAsync()
            Assert.Equal(enum<HttpStatusCode> 422, locked.StatusCode)
            Assert.Contains("Unable to sign in with those credentials.", lockedBody)

            let! persisted = users.FindByEmailAsync user.Email
            Assert.True(persisted.LockoutEnd.HasValue)
            Assert.True(persisted.LockoutEnd.Value > DateTimeOffset.UtcNow)
        }

    [<Fact>]
    member _.``password session must enroll TOTP before feature administration``() =
        task {
            do! reset ()

            use factory =
                new IdentityAppFactory(fixture.ConnectionString, FakeFeatureFlagStore())

            use client =
                factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

            use scope = factory.Services.CreateScope()
            let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            let! user = createUser users "operator" "operator@example.test" "Correct-Horse-42!"

            let! anonymousAdmin = client.GetAsync "/admin/features"
            Assert.Equal(HttpStatusCode.Redirect, anonymousAdmin.StatusCode)
            Assert.StartsWith("/account/login", anonymousAdmin.Headers.Location.OriginalString)

            let! loginPage = client.GetStringAsync "/account/login"
            let loginFields = Dictionary<string, string>()
            loginFields["email"] <- user.Email
            loginFields["password"] <- "Correct-Horse-42!"
            loginFields["returnUrl"] <- "/admin/features"
            loginFields["__RequestVerificationToken"] <- csrf loginPage
            let! passwordLogin = postForm client "/account/login" loginFields
            Assert.Equal(HttpStatusCode.Redirect, passwordLogin.StatusCode)
            Assert.Equal("/account/2fa", passwordLogin.Headers.Location.OriginalString)

            let! passwordOnlyAdmin = client.GetAsync "/admin/features"
            Assert.Equal(HttpStatusCode.Redirect, passwordOnlyAdmin.StatusCode)
            Assert.Equal("/account/2fa", passwordOnlyAdmin.Headers.Location.OriginalString)

            let! enrollmentPage = client.GetStringAsync "/account/2fa"
            let keyFields = Dictionary<string, string>()
            keyFields["__RequestVerificationToken"] <- csrf enrollmentPage
            let! keyResponse = postForm client "/account/2fa/key" keyFields
            let! keyPage = keyResponse.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, keyResponse.StatusCode)
            Assert.Contains("otpauth://totp/", keyPage)

            let! persisted = users.FindByEmailAsync user.Email
            let! authenticatorKey = users.GetAuthenticatorKeyAsync persisted
            let code = currentTotp authenticatorKey

            let! directlyValid =
                users.VerifyTwoFactorTokenAsync(persisted, TokenOptions.DefaultAuthenticatorProvider, code)

            Assert.True(directlyValid, "The Identity authenticator provider should accept its generated token.")
            let enableFields = Dictionary<string, string>()
            enableFields["code"] <- code
            enableFields["__RequestVerificationToken"] <- csrf keyPage
            let! enableResponse = postForm client "/account/2fa/enable" enableFields
            let! recoveryPage = enableResponse.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode)
            Assert.Contains("These codes are shown once", recoveryPage)
            Assert.Contains("no-store", enableResponse.Headers.CacheControl.ToString())

            let recoveryCodeMatch = Regex.Match(recoveryPage, "<code>([^<]+)</code>")
            Assert.True(recoveryCodeMatch.Success, "Expected generated recovery codes.")
            let recoveryCode = recoveryCodeMatch.Groups[1].Value

            let! mfaAdmin = client.GetAsync "/admin/features"
            let! adminPage = mfaAdmin.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, mfaAdmin.StatusCode)
            Assert.Contains("Feature schedule", adminPage)

            let logoutFields = Dictionary<string, string>()
            logoutFields["__RequestVerificationToken"] <- csrf adminPage
            let! logoutResponse = postForm client "/account/logout" logoutFields
            Assert.Equal(HttpStatusCode.Redirect, logoutResponse.StatusCode)

            let! secondLoginPage = client.GetStringAsync "/account/login"
            let secondLoginFields = Dictionary<string, string>()
            secondLoginFields["email"] <- user.Email
            secondLoginFields["password"] <- "Correct-Horse-42!"
            secondLoginFields["returnUrl"] <- "/admin/features"
            secondLoginFields["__RequestVerificationToken"] <- csrf secondLoginPage
            let! secondPasswordLogin = postForm client "/account/login" secondLoginFields
            Assert.Equal(HttpStatusCode.Redirect, secondPasswordLogin.StatusCode)
            Assert.Contains("/account/login/2fa", secondPasswordLogin.Headers.Location.OriginalString)

            let! recoveryLoginPage = client.GetStringAsync "/account/login/recovery?returnUrl=%2Fadmin%2Ffeatures"
            let recoveryFields = Dictionary<string, string>()
            recoveryFields["code"] <- recoveryCode
            recoveryFields["returnUrl"] <- "/admin/features"
            recoveryFields["__RequestVerificationToken"] <- csrf recoveryLoginPage
            let! recoveryLogin = postForm client "/account/login/recovery" recoveryFields
            Assert.Equal(HttpStatusCode.Redirect, recoveryLogin.StatusCode)
            Assert.Equal("/admin/features", recoveryLogin.Headers.Location.OriginalString)

            let! recoveryAdmin = client.GetAsync "/admin/features"
            Assert.Equal(HttpStatusCode.OK, recoveryAdmin.StatusCode)

            let! recoveryAdminPage = recoveryAdmin.Content.ReadAsStringAsync()
            let secondLogoutFields = Dictionary<string, string>()
            secondLogoutFields["__RequestVerificationToken"] <- csrf recoveryAdminPage
            let! _ = postForm client "/account/logout" secondLogoutFields

            let! thirdLoginPage = client.GetStringAsync "/account/login"
            let thirdLoginFields = Dictionary<string, string>()
            thirdLoginFields["email"] <- user.Email
            thirdLoginFields["password"] <- "Correct-Horse-42!"
            thirdLoginFields["returnUrl"] <- "/admin/features"
            thirdLoginFields["__RequestVerificationToken"] <- csrf thirdLoginPage
            let! _ = postForm client "/account/login" thirdLoginFields

            let! reusedPage = client.GetStringAsync "/account/login/recovery"
            let reusedFields = Dictionary<string, string>()
            reusedFields["code"] <- recoveryCode
            reusedFields["returnUrl"] <- "/admin/features"
            reusedFields["__RequestVerificationToken"] <- csrf reusedPage
            let! reused = postForm client "/account/login/recovery" reusedFields
            Assert.Equal(enum<HttpStatusCode> 422, reused.StatusCode)

            let! authenticatorLoginPage = client.GetStringAsync "/account/login/2fa"
            let authenticatorFields = Dictionary<string, string>()
            authenticatorFields["code"] <- currentTotp authenticatorKey
            authenticatorFields["returnUrl"] <- "/admin/features"
            authenticatorFields["__RequestVerificationToken"] <- csrf authenticatorLoginPage
            let! authenticatorLogin = postForm client "/account/login/2fa" authenticatorFields
            Assert.Equal(HttpStatusCode.Redirect, authenticatorLogin.StatusCode)

            let! accountPage = client.GetStringAsync "/account/2fa"
            let regenerationFields = Dictionary<string, string>()
            regenerationFields["__RequestVerificationToken"] <- csrf accountPage

            let! regeneration = postForm client "/account/2fa/recovery-codes" regenerationFields

            let! regeneratedCodesPage = regeneration.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, regeneration.StatusCode)
            Assert.Contains("These codes are shown once", regeneratedCodesPage)

            let! resetPage = client.GetStringAsync "/account/2fa"
            let resetFields = Dictionary<string, string>()
            resetFields["__RequestVerificationToken"] <- csrf resetPage
            let! resetResponse = postForm client "/account/2fa/reset" resetFields
            Assert.Equal(HttpStatusCode.Redirect, resetResponse.StatusCode)
            Assert.Equal("/account/2fa", resetResponse.Headers.Location.OriginalString)

            let! adminAfterReset = client.GetAsync "/admin/features"
            Assert.Equal(HttpStatusCode.Redirect, adminAfterReset.StatusCode)
            Assert.Equal("/account/2fa", adminAfterReset.Headers.Location.OriginalString)
        }
