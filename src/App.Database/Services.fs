namespace App.Database

open App.Domain
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.AspNetCore.DataProtection
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Npgsql

[<RequireQualifiedAccess>]
module DatabaseServices =
    let addPostgres (connectionString: string) (services: IServiceCollection) =
        services.AddSingleton<NpgsqlDataSource>(fun _ -> AutomataStore.createDataSource connectionString)
        |> ignore

        services.AddSingleton<PostgresContext>(fun provider ->
            let logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger "Automata"

            AutomataStore.createContext (provider.GetRequiredService<NpgsqlDataSource>()) (fun event ->
                logger.LogInformation("Automata resilience event: {EventType}", event.GetType().Name)))
        |> ignore

        services.AddScoped<AccountOperationContext>() |> ignore

        services.AddScoped<PostgresUserStore>(fun provider ->
            new PostgresUserStore(
                provider.GetRequiredService<NpgsqlDataSource>(),
                provider.GetRequiredService<AccountOperationContext>(),
                provider.GetRequiredService<IDataProtectionProvider>()
            ))
        |> ignore

        services.AddSingleton<PostgresFeatureFlagStore>() |> ignore

        services.AddSingleton<IFeatureFlagStore>(fun provider ->
            provider.GetRequiredService<PostgresFeatureFlagStore>() :> IFeatureFlagStore)
        |> ignore

        services.AddSingleton<PostgresFeatureChangeSource>() |> ignore

        services.AddSingleton<IFeatureChangeSource>(fun provider ->
            provider.GetRequiredService<PostgresFeatureChangeSource>() :> IFeatureChangeSource)
        |> ignore

        services
