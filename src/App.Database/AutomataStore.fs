namespace App.Database

open ByzantineSystems.Automata.Resilience
open ByzantineSystems.Automata.Storage.Postgres
open Npgsql

[<RequireQualifiedAccess>]
module AutomataStore =

    let createDataSource (connectionString: string) : NpgsqlDataSource = DataSource.create connectionString

    let createContext (dataSource: NpgsqlDataSource) (onEvent: PipelineEvent -> unit) : PostgresContext =
        PostgresContext.create dataSource TransientPolicy.defaults onEvent
