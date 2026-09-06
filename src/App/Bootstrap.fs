namespace App

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

[<RequireQualifiedAccess>]
module Bootstrap =
    let private missingConfiguration =
        "Bootstrap__Username, Bootstrap__Email, and Bootstrap__Password are required."

    let private configuredValue (configuration: IConfiguration) key =
        configuration[key]
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private created (result: IdentityResult) =
        if result.Succeeded then
            Ok()
        else
            result.Errors |> Seq.map _.Description |> String.concat " " |> Error

    let private runResult (services: IServiceProvider) (configuration: IConfiguration) =
        taskResult {
            let! username =
                configuredValue configuration "Bootstrap:Username"
                |> Result.requireSome missingConfiguration

            and! email =
                configuredValue configuration "Bootstrap:Email"
                |> Result.requireSome missingConfiguration

            and! password =
                configuredValue configuration "Bootstrap:Password"
                |> Result.requireSome missingConfiguration

            use scope = services.CreateScope()
            let users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            let! existingUsername = users.FindByNameAsync username
            let! existingEmail = users.FindByEmailAsync email

            do!
                (isNull existingUsername && isNull existingEmail)
                |> Result.requireTrue "A user with that username or email already exists."

            let user = ApplicationUser()
            user.UserName <- username
            user.Email <- email
            user.EmailConfirmed <- true
            user.LockoutEnabled <- true

            let! result = users.CreateAsync(user, password)
            do! created result
            return user.Id
        }

    let run (services: IServiceProvider) (configuration: IConfiguration) : Task<Result<Guid, string>> =
        task { return! runResult services configuration }
