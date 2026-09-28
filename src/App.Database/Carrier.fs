namespace App.Database

open System.Threading
open System.Threading.Tasks
open App.Domain

/// <summary>Sanitized carrier label outcome. Carrier references are opaque tokens; reason codes
/// are bounded internal codes, never raw provider error text.</summary>
type CarrierLabelResult =
    | CarrierLabelCreated of CarrierReference
    | CarrierLabelFailed of reasonCode: string

/// <summary>
/// The shipping-carrier boundary. Implementations must be deterministic per shipment id: label
/// creation for one shipment resolves to one reference. Calls run outside any database
/// transaction. Tracking events are delivered to the shipments machine through the integration
/// outbox by a carrier webhook relay or, in the sandbox, an operator action.
/// </summary>
type ICarrier =
    abstract member CreateLabel: shipmentId: ShipmentId * ct: CancellationToken -> Task<CarrierLabelResult>
