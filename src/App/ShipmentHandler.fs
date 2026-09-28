namespace App

open System.Threading
open App.Database
open App.Shipments
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Npgsql

type ShipmentEffectHandler(dataSource: NpgsqlDataSource, carrier: ICarrier) =
    interface IActionHandler<ShipmentEntityId, ShipmentAction, ShipmentActionError> with
        member _.HandleAsync(action: LeasedAction<ShipmentEntityId, ShipmentAction>, ct: CancellationToken) =
            let record = action.Work

            match record.Action with
            | ConfirmAllocation _ -> ShipmentEffects.applyConfirmAllocation dataSource record ct
            | CreateCarrierLabel _ -> ShipmentEffects.applyCreateLabel dataSource carrier record ct
            | NotifyOrderDispatched _
            | NotifyOrderDelivered _ -> ShipmentEffects.applyNotifyOrder dataSource record ct
            | RequestDeliveryRetry _ -> ShipmentEffects.applyDeliveryRetry dataSource record ct
