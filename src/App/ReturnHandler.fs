namespace App

open System.Threading
open App.Database
open App.Returns
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Npgsql

type ReturnEffectHandler(dataSource: NpgsqlDataSource, carrier: ICarrier) =
    interface IActionHandler<ReturnEntityId, ReturnAction, ReturnActionError> with
        member _.HandleAsync(action: LeasedAction<ReturnEntityId, ReturnAction>, ct: CancellationToken) =
            match action.Work.Action with
            | IssueLabel _ -> ReturnEffects.applyLabel dataSource carrier action.Work ct
            | VerifyAuthorization _
            | RestockItems _
            | RequestRefund _
            | NotifyOrderRefunded _
            | NotifyOrderRejected _ -> ReturnEffects.applyLocal dataSource action.Work ct
