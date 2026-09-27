namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

[<RequireQualifiedAccess>]
module CartChange =
    [<Literal>]
    let Channel = "fsnix_cart_changed"

type CartChange =
    | RefreshRequired
    | KeepAlive

type ICartChangeSubscription =
    inherit IAsyncDisposable
    abstract member Next: CancellationToken -> Task<CartChange>

type ICartChangeSource =
    abstract member Subscribe: CancellationToken -> Task<ICartChangeSubscription>

type private PostgresCartChangeSubscription(connection: NpgsqlConnection) =
    let heartbeat = TimeSpan.FromSeconds 15.

    interface ICartChangeSubscription with
        member _.Next(cancellationToken) =
            task {
                let! notified = connection.WaitAsync(heartbeat, cancellationToken)
                return if notified then RefreshRequired else KeepAlive
            }

        member _.DisposeAsync() = connection.DisposeAsync()

[<Sealed>]
type PostgresCartChangeSource(dataSource: NpgsqlDataSource) =
    interface ICartChangeSource with
        member _.Subscribe(cancellationToken) =
            task {
                let connection = dataSource.CreateConnection()
                do! connection.OpenAsync(cancellationToken)

                try
                    use command = new NpgsqlCommand($"LISTEN {CartChange.Channel}", connection)
                    let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                    return PostgresCartChangeSubscription(connection) :> ICartChangeSubscription
                with error ->
                    do! connection.DisposeAsync()
                    return raise error
            }
