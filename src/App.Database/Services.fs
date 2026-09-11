namespace App.Database

open App.Domain
open Microsoft.Extensions.DependencyInjection
open Npgsql

[<RequireQualifiedAccess>]
module DatabaseServices =
    let addPostgres (connectionString: string) (services: IServiceCollection) =
        services.AddSingleton<NpgsqlDataSource>(fun _ -> NpgsqlDataSource.Create connectionString)
        |> ignore

        services.AddScoped<PostgresUserStore>() |> ignore
        services.AddSingleton<PostgresFeatureFlagStore>() |> ignore

        services.AddSingleton<IFeatureFlagStore>(fun provider ->
            provider.GetRequiredService<PostgresFeatureFlagStore>() :> IFeatureFlagStore)
        |> ignore

        services.AddSingleton<PostgresFeatureChangeSource>() |> ignore

        services.AddSingleton<IFeatureChangeSource>(fun provider ->
            provider.GetRequiredService<PostgresFeatureChangeSource>() :> IFeatureChangeSource)
        |> ignore

        services
