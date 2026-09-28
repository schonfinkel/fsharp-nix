namespace App

open System.Threading
open App.Database
open App.Refunds
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Npgsql

type RefundEffectHandler(dataSource: NpgsqlDataSource, gateway: IPaymentGateway) =
    interface IActionHandler<RefundEntityId, RefundAction, RefundActionError> with
        member _.HandleAsync(action: LeasedAction<RefundEntityId, RefundAction>, ct: CancellationToken) =
            match action.Work.Action with
            | CallGatewayRefund _
            | QueryGatewayRefund _ -> RefundEffects.applyGateway dataSource gateway action.Work ct
            | RequestAllocation _
            | SettleAllocation _
            | ReleaseAllocation _
            | NotifyOriginSucceeded _
            | NotifyOriginFailed _ -> RefundEffects.applyLocal dataSource action.Work ct
