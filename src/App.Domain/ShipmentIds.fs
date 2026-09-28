namespace App.Domain

open System

[<Struct>]
type ShipmentId = private ShipmentId of value: Guid

module ShipmentId =
    let create (value: Guid) : Result<ShipmentId, string> =
        if value = Guid.Empty then
            Error "A shipment id must not be empty."
        else
            Ok(ShipmentId value)

    let value (ShipmentId value) = value

    let tryParse (value: string) : Result<ShipmentId, string> =
        match Guid.TryParseExact(value, "D") with
        | true, id -> create id
        | false, _ -> Error "A shipment id must be a GUID in \"D\" format."

    let wireString id = (value id).ToString("D")

[<Struct>]
type ShipmentAllocationId = private ShipmentAllocationId of value: Guid

module ShipmentAllocationId =
    let create (value: Guid) : Result<ShipmentAllocationId, string> =
        if value = Guid.Empty then
            Error "A shipment allocation id must not be empty."
        else
            Ok(ShipmentAllocationId value)

    let value (ShipmentAllocationId value) = value

    let tryParse (value: string) : Result<ShipmentAllocationId, string> =
        match Guid.TryParseExact(value, "D") with
        | true, id -> create id
        | false, _ -> Error "A shipment allocation id must be a GUID in \"D\" format."

    let wireString id = (value id).ToString("D")

[<Struct>]
type CaptureId = private CaptureId of value: Guid

module CaptureId =
    let create (value: Guid) : Result<CaptureId, string> =
        if value = Guid.Empty then
            Error "A capture id must not be empty."
        else
            Ok(CaptureId value)

    let value (CaptureId value) = value

    let tryParse (value: string) : Result<CaptureId, string> =
        match Guid.TryParseExact(value, "D") with
        | true, id -> create id
        | false, _ -> Error "A capture id must be a GUID in \"D\" format."

    let wireString id = (value id).ToString("D")

type TrackingEventId = private TrackingEventId of value: string

module TrackingEventId =
    let maxValueLength = 128

    let create (value: string) : Result<TrackingEventId, string> =
        if String.IsNullOrWhiteSpace value then
            Error "A tracking event id is required."
        else
            let trimmed = value.Trim()

            if trimmed.Length > maxValueLength then
                Error $"A tracking event id must be at most %d{maxValueLength} characters."
            elif trimmed |> Seq.exists Char.IsControl then
                Error "A tracking event id must not contain control characters."
            else
                Ok(TrackingEventId trimmed)

    let value (TrackingEventId value) = value

    let tryParse (value: string) = create value

type CarrierReference = private CarrierReference of value: string

module CarrierReference =
    let maxValueLength = 128

    let create (value: string) : Result<CarrierReference, string> =
        if String.IsNullOrWhiteSpace value then
            Error "A carrier reference is required."
        else
            let trimmed = value.Trim()

            if trimmed.Length > maxValueLength then
                Error $"A carrier reference must be at most %d{maxValueLength} characters."
            elif trimmed |> Seq.exists Char.IsControl then
                Error "A carrier reference must not contain control characters."
            else
                Ok(CarrierReference trimmed)

    let value (CarrierReference value) = value

    let tryParse (value: string) = create value
