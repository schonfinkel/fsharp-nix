namespace App.Invoices

open App.Domain
open ByzantineSystems.Automata.Core

/// <summary>An invoice whose immutable relational snapshot has committed under
/// <see cref="Number"/>. The belief carries only the identity and number; the legal content
/// lives in <c>fsnix.invoices</c>/<c>fsnix.invoice_lines</c>.</summary>
type IssuedInvoice =
    { Request: InvoiceRequest
      Number: InvoiceNumber }

type InvoiceState =
    | Initial
    | SnapshotPending of InvoiceRequest
    | Issued of IssuedInvoice
    | ManualReview of InvoiceRequest * reasonCode: ReasonCode
    | Closed of IssuedInvoice

type InvoiceEvent =
    | InvoiceRequested of InvoiceRequest
    | SnapshotIssued of InvoiceId * InvoiceNumber
    | IssuanceFailed of InvoiceId * reasonCode: ReasonCode
    /// <summary>Operator recovery from manual review: re-runs issuance, which resolves by the
    /// invoice business key, so a snapshot that did commit is reported rather than renumbered.</summary>
    | IssuanceRetryRequested
    | CloseRequested

type InvoiceAction = IssueSnapshot of InvoiceRequest

[<RequireQualifiedAccess>]
type InvoiceActionError =
    | CallbackEncodingFailed
    | ActionReceiptMismatch
    | InvalidAction

type Invoice = class end
type InvoiceEntityId = EntityId<Invoice>

[<RequireQualifiedAccess>]
module Invoices =
    [<Literal>]
    let MachineKey = "invoices"

    [<Literal>]
    let ActionQueue = "invoice_actions"

    /// Bump on every semantic chart change (guards, transitions, codecs), even when the
    /// structure is unchanged; Automata's fingerprint cannot see inside functions.
    [<Literal>]
    let ChartVersion = 1

    let initialState = Initial

    let invoiceEntityId (id: InvoiceId) : InvoiceEntityId =
        entityId $"invoice:{InvoiceId.wireString id}"

    let classifyState =
        function
        | Initial -> stateId "initial"
        | SnapshotPending _ -> stateId "snapshot-pending"
        | Issued _ -> stateId "issued"
        | ManualReview _ -> stateId "manual-review"
        | Closed _ -> stateId "closed"

    let private requested state event =
        match state, event with
        | Initial, InvoiceRequested request -> Result.isOk (InvoiceRequest.validate request)
        | _ -> false

    let private issuanceResult state event =
        match state, event with
        | SnapshotPending request, SnapshotIssued(id, _)
        | SnapshotPending request, IssuanceFailed(id, _) -> id = request.InvoiceId
        | _ -> false

    let private retry state event =
        match state, event with
        | ManualReview _, IssuanceRetryRequested -> true
        | _ -> false

    let private close state event =
        match state, event with
        | Issued _, CloseRequested -> true
        | _ -> false

    /// <summary>Duplicate requests and late or stale issuance callbacks are no-ops.</summary>
    let private absorb _ =
        function
        | InvoiceRequested _
        | SnapshotIssued _
        | IssuanceFailed _ -> true
        | IssuanceRetryRequested
        | CloseRequested -> false

    let chartResult =
        statechart<InvoiceState, InvoiceEvent, InvoiceAction, InvoiceActionError> {
            root MachineKey
            classify classifyState

            state "initial" {
                on requested (fun state event ->
                    match event with
                    | InvoiceRequested request -> [ IssueSnapshot request ], SnapshotPending request
                    | _ -> [], state)
            }

            state "snapshot-pending" {
                on issuanceResult (fun state event ->
                    match state, event with
                    | SnapshotPending request, SnapshotIssued(_, number) ->
                        [], Issued { Request = request; Number = number }
                    | SnapshotPending request, IssuanceFailed(_, reason) -> [], ManualReview(request, reason)
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "issued" {
                on close (fun state _ ->
                    match state with
                    | Issued issued -> [], Closed issued
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "manual-review" {
                on retry (fun state _ ->
                    match state with
                    | ManualReview(request, _) -> [ IssueSnapshot request ], SnapshotPending request
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "closed" { internalOn absorb (fun _ _ -> []) }
        }

    let chartValue =
        match chartResult with
        | Ok chart -> chart
        | Error errors -> invalidOp $"invoices chart is invalid: %A{errors}"
