namespace App

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open App.Database
open App.Domain

/// <summary>
/// Deterministic in-process sandbox provider. One operation id names exactly one outcome, so
/// retries and queries always converge, and a call log records every provider call for
/// idempotency assertions. Cardholder data is not representable in its inputs: the only
/// method inputs are the opaque sandbox references.
/// </summary>
type SimulatedPaymentGateway() =
    let authorizeCalls = ConcurrentDictionary<string, int>()
    let captureCalls = ConcurrentDictionary<string, int>()
    let refundCalls = ConcurrentDictionary<string, int>()
    let voidCalls = ConcurrentDictionary<string, int>()

    let seed (operationId: PaymentOperationId) =
        SHA256.HashData(Encoding.UTF8.GetBytes(PaymentOperationId.value operationId))

    let reference (operationId: PaymentOperationId) =
        let bytes = seed operationId
        $"sim-{Convert.ToHexString(bytes, 0, 6).ToLowerInvariant()}"

    let authorizationExpiry (operationId: PaymentOperationId) =
        let bytes = seed operationId
        DateTimeOffset.UtcNow.AddDays(5. + float (bytes[6] % 3uy))

    let resolve (operationId: PaymentOperationId) (method: PaymentMethodReference) =
        match PaymentMethodReference.value method with
        | "sandbox://decline" -> GatewayDeclined "do-not-honor"
        | "sandbox://unknown" ->
            let bytes = seed operationId

            if bytes[7] % 2uy = 0uy then
                GatewayAuthorized(reference operationId, authorizationExpiry operationId)
            else
                GatewayDeclined "expired-card"
        | _ -> GatewayAuthorized(reference operationId, authorizationExpiry operationId)

    let record (store: ConcurrentDictionary<string, int>) key =
        store.AddOrUpdate(key, 1, fun _ current -> current + 1) |> ignore

    let lookup (store: ConcurrentDictionary<string, int>) (operationId: PaymentOperationId) =
        match store.TryGetValue(PaymentOperationId.value operationId) with
        | true, calls -> calls
        | false, _ -> 0

    interface IPaymentGateway with
        member _.Authorize(operationId, _, method, _) =
            record authorizeCalls (PaymentOperationId.value operationId)

            match PaymentMethodReference.value method with
            | "sandbox://decline" -> Task.FromResult(GatewayDeclined "do-not-honor")
            | "sandbox://unknown" -> Task.FromResult GatewayOutcomeUnknown
            | _ -> Task.FromResult(GatewayAuthorized(reference operationId, authorizationExpiry operationId))

        member _.Void(operationId, _, _) =
            record voidCalls (PaymentOperationId.value operationId)
            Task.FromResult GatewayVoided

        member _.Capture(operationId, _, _, method, _) =
            record captureCalls (PaymentOperationId.value operationId)

            match PaymentMethodReference.value method with
            | "sandbox://decline" -> Task.FromResult(GatewayCaptureDeclined "do-not-honor")
            | "sandbox://unknown" -> Task.FromResult GatewayCaptureUnknown
            | _ -> Task.FromResult(GatewayCaptured(reference operationId))

        member _.QueryCapture(operationId, _, method, _) =
            match PaymentMethodReference.value method with
            | "sandbox://decline" -> Task.FromResult(Some(GatewayCaptureDeclined "do-not-honor"))
            | _ -> Task.FromResult(Some(GatewayCaptured(reference operationId)))

        member _.QueryAuthorization(operationId, method, _) =
            Task.FromResult(Some(resolve operationId method))

        member _.Refund(operationId, _, _, _) =
            record refundCalls (PaymentOperationId.value operationId)

            if PaymentOperationId.value operationId |> _.Contains(":unknown:") then
                Task.FromResult GatewayRefundUnknown
            else
                Task.FromResult(GatewayRefunded(reference operationId))

        member _.QueryRefund(operationId, _, _) =
            Task.FromResult(Some(GatewayRefunded(reference operationId)))

    /// <summary>How many times the provider was asked to authorize this operation.</summary>
    member _.AuthorizeCalls(operationId: PaymentOperationId) = lookup authorizeCalls operationId

    /// <summary>How many times the provider was asked to capture this operation.</summary>
    member _.CaptureCalls(operationId: PaymentOperationId) = lookup captureCalls operationId

    member _.RefundCalls(operationId: PaymentOperationId) = lookup refundCalls operationId

    /// <summary>How many times the provider was asked to void this operation.</summary>
    member _.VoidCalls(operationId: PaymentOperationId) = lookup voidCalls operationId
