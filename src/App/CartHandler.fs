namespace App

open System
open System.Threading
open System.Threading.Tasks
open App.Cart
open App.Database
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Npgsql

[<RequireQualifiedAccess>]
module CartTiming =
    let AbandonmentWindow = TimeSpan.FromMinutes 30.

/// <summary>
/// Resolves each cart action to its durable effect. RecordCartTouch arms or cancels the
/// abandonment deadline; the merge actions persist the snapshot, submit to the target cart,
/// notify the guest, and revoke the guest capability — each idempotent through its receipt.
/// </summary>
type CartEffectHandler(dataSource: NpgsqlDataSource, timeProvider: TimeProvider) =
    interface IActionHandler<CartId, CartAction, CartActionError> with
        member _.HandleAsync(action: LeasedAction<CartId, CartAction>, ct: CancellationToken) =
            task {
                let record = action.Work

                match record.Action with
                | RecordCartTouch _ ->
                    return!
                        CartEffects.applyRecordCartTouch
                            dataSource
                            record
                            (timeProvider.GetUtcNow() + CartTiming.AbandonmentWindow)
                            ct
                | CaptureMergeSnapshot _ -> return! CartEffects.applyCaptureMergeSnapshot dataSource record ct
                | SubmitMergeSnapshot _ -> return! CartEffects.applySubmitMergeSnapshot dataSource record ct
                | NotifyMergeApplied _ -> return! CartEffects.applyNotifyMergeApplied dataSource record ct
                | RevokeGuestCapability _ -> return! CartEffects.applyRevokeGuestCapability dataSource record ct
            }
