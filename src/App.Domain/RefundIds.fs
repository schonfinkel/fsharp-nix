namespace App.Domain

open System

[<Struct>]
type RefundId = private RefundId of Guid

[<RequireQualifiedAccess>]
module RefundId =
    let create value =
        if value = Guid.Empty then
            Error "A refund id is required."
        else
            Ok(RefundId value)

    let value (RefundId value) = value
    let wireString id = (value id).ToString("D")

    let tryParse (text: string) =
        match Guid.TryParseExact(text, "D") with
        | true, value -> create value
        | _ -> Error "A refund id must be a GUID in D format."

[<Struct>]
type RefundAllocationId = private RefundAllocationId of Guid

[<RequireQualifiedAccess>]
module RefundAllocationId =
    let create value =
        if value = Guid.Empty then
            Error "A refund allocation id is required."
        else
            Ok(RefundAllocationId value)

    let value (RefundAllocationId value) = value
    let wireString id = (value id).ToString("D")

    let tryParse (text: string) =
        match Guid.TryParseExact(text, "D") with
        | true, value -> create value
        | _ -> Error "A refund allocation id must be a GUID in D format."

type RefundOrigin =
    | OrderCancellation of orderId: string
    | InspectedReturn of orderId: string * returnId: Guid

[<RequireQualifiedAccess>]
module RefundOrigin =
    let orderId =
        function
        | OrderCancellation orderId
        | InspectedReturn(orderId, _) -> orderId
