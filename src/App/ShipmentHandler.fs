namespace App

open System
open System.Threading
open App.Database
open App.Shipments
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Microsoft.Extensions.Configuration
open Npgsql

/// <summary>How long a shipment may go without a carrier scan before it is marked lost.</summary>
type ShipmentTrackingPolicy = { LostAfter: TimeSpan }

[<RequireQualifiedAccess>]
module ShipmentTrackingPolicy =
    /// <summary><c>Shipments:LostAfterDays</c>, default 7.</summary>
    let load (configuration: IConfiguration) =
        match configuration["Shipments:LostAfterDays"] with
        | text when String.IsNullOrWhiteSpace text -> { LostAfter = TimeSpan.FromDays 7. }
        | text ->
            match
                Double.TryParse(text, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture)
            with
            | true, days when days > 0. -> { LostAfter = TimeSpan.FromDays days }
            | _ -> invalidOp "Shipments:LostAfterDays must be a positive number."

type ShipmentEffectHandler(dataSource: NpgsqlDataSource, carrier: ICarrier, tracking: ShipmentTrackingPolicy) =
    interface IActionHandler<ShipmentEntityId, ShipmentAction, ShipmentActionError> with
        member _.HandleAsync(action: LeasedAction<ShipmentEntityId, ShipmentAction>, ct: CancellationToken) =
            let record = action.Work

            match record.Action with
            | ConfirmAllocation _ -> ShipmentEffects.applyConfirmAllocation dataSource record ct
            | CreateCarrierLabel _ -> ShipmentEffects.applyCreateLabel dataSource carrier record ct
            | NotifyOrderDispatched _
            | NotifyOrderDelivered _ -> ShipmentEffects.applyNotifyOrder dataSource record ct
            | RequestDeliveryRetry _ -> ShipmentEffects.applyDeliveryRetry dataSource record ct
            | RecordTrackingCheckpoint _
            | StopTrackingCheck _ -> ShipmentEffects.applyTrackingCheck dataSource tracking.LostAfter record ct
