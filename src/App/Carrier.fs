namespace App

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Database
open App.Domain

/// <summary>
/// Deterministic in-process sandbox carrier. Label creation always succeeds and derives a stable
/// opaque reference from the shipment id, so retries converge.
/// </summary>
type SimulatedCarrier() =
    let reference (shipmentId: ShipmentId) =
        let bytes =
            SHA256.HashData(Encoding.UTF8.GetBytes(ShipmentId.wireString shipmentId))

        $"sim-{Convert.ToHexString(bytes, 0, 6).ToLowerInvariant()}"

    interface ICarrier with
        member _.CreateLabel(shipmentId, _) =
            let reference = reference shipmentId

            CarrierReference.create reference
            |> Result.map CarrierLabelCreated
            |> Result.defaultWith (fun _ -> CarrierLabelFailed "invalid-carrier-reference")
            |> Task.FromResult
