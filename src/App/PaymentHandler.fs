namespace App

open System.Threading
open App.Database
open App.Payments
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Npgsql

type PaymentEffectHandler(dataSource: NpgsqlDataSource, gateway: IPaymentGateway) =
    interface IActionHandler<PaymentId, PaymentAction, PaymentActionError> with
        member _.HandleAsync(action: LeasedAction<PaymentId, PaymentAction>, ct: CancellationToken) =
            let record = action.Work

            match record.Action with
            | CallGatewayAuthorize _ -> PaymentEffects.applyAuthorize dataSource gateway record ct
            | CallGatewayCapture _ -> PaymentEffects.applyCapture dataSource gateway record ct
            | CallGatewayVoid _ -> PaymentEffects.applyVoid dataSource gateway record ct
            | QueryGatewayAuthorization _ -> PaymentEffects.applyQueryAuthorization dataSource gateway record ct
            | QueryGatewayCapture _ -> PaymentEffects.applyQueryCapture dataSource gateway record ct
            | NotifyOrderAuthorized _
            | NotifyOrderDeclined _
            | NotifyOrderCancelled _
            | NotifyOrderVoided _
            | NotifyOrderCaptured _ -> PaymentEffects.applyNotify dataSource record ct
