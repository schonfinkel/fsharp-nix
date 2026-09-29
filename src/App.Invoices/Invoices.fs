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
    | RenderPending of IssuedInvoice
    /// <summary>The PDF is stored in <c>fsnix.invoice_documents</c> under this digest.</summary>
    | Rendered of IssuedInvoice * DocumentDigest
    | RenderFailed of IssuedInvoice * reasonCode: ReasonCode
    | ManualReview of InvoiceRequest * reasonCode: ReasonCode
    | Closed of IssuedInvoice

type InvoiceEvent =
    | InvoiceRequested of InvoiceRequest
    | SnapshotIssued of InvoiceId * InvoiceNumber
    | IssuanceFailed of InvoiceId * reasonCode: ReasonCode
    /// <summary>Operator recovery from manual review: re-runs issuance, which resolves by the
    /// invoice business key, so a snapshot that did commit is reported rather than renumbered.</summary>
    | IssuanceRetryRequested
    | DocumentRendered of InvoiceId * DocumentDigest
    | DocumentRenderFailed of InvoiceId * reasonCode: ReasonCode
    /// <summary>Operator recovery from a failed render. Rendering is deterministic, so a retry
    /// that succeeds stores the same content address as any earlier success would have.</summary>
    | RenderRetryRequested
    /// <summary>The render scanner found no stored document by the deadline and re-requests the
    /// render; <c>check</c> numbers the request so each one is a distinct command.</summary>
    | RenderReconcileRequested of check: int
    /// <summary>The render scanner gave up; the operator retry applies from <c>RenderFailed</c>.</summary>
    | RenderReconcileExhausted
    | CloseRequested

type InvoiceAction =
    | IssueSnapshot of InvoiceRequest
    | RenderDocument of IssuedInvoice

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
        | RenderPending _ -> stateId "render-pending"
        | Rendered _ -> stateId "rendered"
        | RenderFailed _ -> stateId "render-failed"
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

    let private renderResult state event =
        match state, event with
        | RenderPending issued, DocumentRendered(id, _)
        | RenderPending issued, DocumentRenderFailed(id, _) -> id = issued.Request.InvoiceId
        | _ -> false

    let private issuanceRetry state event =
        match state, event with
        | ManualReview _, IssuanceRetryRequested -> true
        | _ -> false

    let private renderRetry state event =
        match state, event with
        | RenderFailed _, RenderRetryRequested -> true
        | _ -> false

    let private renderReconcile state event =
        match state, event with
        | RenderPending _, (RenderReconcileRequested _ | RenderReconcileExhausted) -> true
        | _ -> false

    let private close state event =
        match state, event with
        | Rendered _, CloseRequested -> true
        | _ -> false

    /// <summary>Duplicate requests and late or stale issuance/render callbacks are no-ops.</summary>
    let private absorb _ =
        function
        | InvoiceRequested _
        | SnapshotIssued _
        | IssuanceFailed _
        | DocumentRendered _
        | DocumentRenderFailed _
        | RenderReconcileRequested _
        | RenderReconcileExhausted -> true
        | IssuanceRetryRequested
        | RenderRetryRequested
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
                        let issued = { Request = request; Number = number }

                        [ RenderDocument issued ], RenderPending issued
                    | SnapshotPending request, IssuanceFailed(_, reason) -> [], ManualReview(request, reason)
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "render-pending" {
                on renderReconcile (fun state event ->
                    match state, event with
                    | RenderPending issued, RenderReconcileRequested _ -> [ RenderDocument issued ], state
                    | RenderPending issued, RenderReconcileExhausted ->
                        [], RenderFailed(issued, ReasonCode.ofLiteral "render-timeout")
                    | _ -> [], state)

                on renderResult (fun state event ->
                    match state, event with
                    | RenderPending issued, DocumentRendered(_, digest) -> [], Rendered(issued, digest)
                    | RenderPending issued, DocumentRenderFailed(_, reason) -> [], RenderFailed(issued, reason)
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "rendered" {
                on close (fun state _ ->
                    match state with
                    | Rendered(issued, _) -> [], Closed issued
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "render-failed" {
                on renderRetry (fun state _ ->
                    match state with
                    | RenderFailed(issued, _) -> [ RenderDocument issued ], RenderPending issued
                    | _ -> [], state)

                internalOn absorb (fun _ _ -> [])
            }

            state "manual-review" {
                on issuanceRetry (fun state _ ->
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
