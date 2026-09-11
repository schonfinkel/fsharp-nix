namespace App.Database

open System
open System.Threading
open App.Database.Schema
open App.Database.Schema.fsnix
open App.Domain
open Npgsql
open SqlHydra.Query

type private PostgresFeatureChangeSubscription(context: QueryContext, connection: NpgsqlConnection) =
    let heartbeat = TimeSpan.FromSeconds 15.

    let nextTransitionDelay cancellationToken =
        task {
            let! boundary =
                selectTask context {
                    for boundary in fsnix.next_feature_flag_boundary do
                        select boundary
                        tryHead
                        cancel cancellationToken
                }

            return
                boundary
                |> Option.bind (fun row ->
                    Option.map2 (fun next observed -> next - observed) row.next_boundary row.observed_at)
        }

    interface IFeatureChangeSubscription with
        member _.Next(cancellationToken) =
            task {
                let! nextTransition = nextTransitionDelay cancellationToken

                let timeout, transitionDue =
                    match nextTransition with
                    | Some delay when delay <= heartbeat -> max delay (TimeSpan.FromMilliseconds 1.), true
                    | _ -> heartbeat, false

                let! notified = connection.WaitAsync(timeout, cancellationToken)

                return
                    if notified || transitionDue then
                        RefreshRequired
                    else
                        KeepAlive
            }

        member _.DisposeAsync() = connection.DisposeAsync()

[<Sealed>]
type PostgresFeatureChangeSource(dataSource: NpgsqlDataSource) =
    let db = QueryContextFactory.Create dataSource

    interface IFeatureChangeSource with
        member _.Subscribe(cancellationToken) =
            task {
                let! context = db.OpenContextAsync()
                let connection = context.Connection :?> NpgsqlConnection

                try
                    use command = new NpgsqlCommand("LISTEN fsnix_feature_schedule_changed", connection)

                    let! _ = command.ExecuteNonQueryAsync cancellationToken
                    return PostgresFeatureChangeSubscription(context, connection) :> IFeatureChangeSubscription
                with error ->
                    do! connection.DisposeAsync()
                    return raise error
            }
