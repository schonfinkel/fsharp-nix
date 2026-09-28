namespace App

open System
open System.Threading
open App.Database
open App.Orders
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Npgsql

type OrderEffectHandler(dataSource: NpgsqlDataSource, timeProvider: TimeProvider) =
    interface IActionHandler<OrderId, OrderAction, OrderActionError> with
        member _.HandleAsync(action: LeasedAction<OrderId, OrderAction>, ct: CancellationToken) =
            let record = action.Work

            match record.Action with
            | ReserveStock _ ->
                OrderEffects.applyReserveStock
                    dataSource
                    record
                    (timeProvider.GetUtcNow() + TimeSpan.FromMinutes 15.)
                    ct
            | ReleaseReservations _ -> OrderEffects.applyRelease dataSource record ct
            | NotifyCartConverted _ -> OrderEffects.applyConvertCart dataSource record ct
            | RequestAuthorization _ -> OrderEffects.applyRequestAuthorization dataSource record ct
            | RequestPaymentCancellation _ -> OrderEffects.applyRequestPaymentCancellation dataSource record ct
            | CommitStock _ -> OrderEffects.applyCommitStock dataSource record ct
            | CreateShipment _ -> OrderEffects.applyCreateShipment dataSource record ct
            | RequestCapture _ -> OrderEffects.applyRequestCapture dataSource record ct
            | StartReturn _ -> OrderEffects.applyStartReturn dataSource record ct
            | StartRefund _ -> OrderEffects.applyStartRefund dataSource record ct
