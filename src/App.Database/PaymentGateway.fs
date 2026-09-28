namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open App.Domain
open NodaMoney

/// <summary>Sanitized gateway authorization outcome. Provider references are opaque tokens;
/// reason codes are bounded internal codes, never raw provider error text.</summary>
type GatewayAuthorization =
    | GatewayAuthorized of providerReference: string * expiresAt: DateTimeOffset
    | GatewayDeclined of reasonCode: string
    | GatewayOutcomeUnknown

type GatewayVoid =
    | GatewayVoided
    | GatewayVoidUnknown

/// <summary>
/// The payment provider boundary. Implementations must honor the operation id as the
/// idempotency key: one operation id names at most one real provider effect, and
/// <see cref="QueryAuthorization" /> must resolve an earlier unknown outcome deterministically
/// whenever the provider can. Calls run outside any database transaction.
/// </summary>
type IPaymentGateway =
    abstract member Authorize:
        operationId: PaymentOperationId * amount: Money * method: PaymentMethodReference * ct: CancellationToken ->
            Task<GatewayAuthorization>

    abstract member Void:
        operationId: PaymentOperationId * providerReference: string * ct: CancellationToken -> Task<GatewayVoid>

    abstract member QueryAuthorization:
        operationId: PaymentOperationId * method: PaymentMethodReference * ct: CancellationToken ->
            Task<GatewayAuthorization option>
