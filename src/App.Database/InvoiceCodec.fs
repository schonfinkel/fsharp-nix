namespace App.Database

open System
open System.Text.Json
open System.Text.Json.Serialization
open App.Domain
open App.Invoices
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging

module InvoiceWire =
    [<CLIMutable>]
    type WireDto =
        {
            Tag: string
            InvoiceId: string
            OrderId: string
            SnapshotId: string
            LegalEntity: string
            Series: string
            FiscalPeriod: string
            Sequence: int64
            Reason: string
            Digest: string
            /// <summary>Credit notes only: the refund, and <c>cancellation</c> or
            /// <c>return:{returnId}</c> for its origin.</summary>
            [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
            CreditRefundId: string
            [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
            CreditOrigin: string
        }

[<RequireQualifiedAccess>]
module InvoiceCodec =
    let private options =
        JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

    do options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let private empty tag : InvoiceWire.WireDto =
        { Tag = tag
          InvoiceId = ""
          OrderId = ""
          SnapshotId = ""
          LegalEntity = ""
          Series = ""
          FiscalPeriod = ""
          Sequence = 0L
          Reason = ""
          Digest = ""
          CreditRefundId = null
          CreditOrigin = null }

    let private withRequest tag (request: InvoiceRequest) =
        { empty tag with
            InvoiceId = InvoiceId.wireString request.InvoiceId
            OrderId = request.OrderId
            SnapshotId = OrderSnapshotId.wireString request.SnapshotId
            CreditRefundId =
                request.Credit
                |> Option.map (fun credit -> RefundId.wireString credit.RefundId)
                |> Option.toObj
            CreditOrigin =
                request.Credit
                |> Option.map (fun credit ->
                    match credit.Origin with
                    | OrderCancellation _ -> "cancellation"
                    | InspectedReturn(_, returnId) -> $"return:{returnId:D}")
                |> Option.toObj }

    let private withNumber (number: InvoiceNumber) (dto: InvoiceWire.WireDto) =
        { dto with
            LegalEntity = number.LegalEntity
            Series = number.Series
            FiscalPeriod = number.FiscalPeriod
            Sequence = number.Sequence }

    let private withIssued tag (issued: IssuedInvoice) =
        withRequest tag issued.Request |> withNumber issued.Number

    let private error name message = CodecSupport.decodeError name message

    let private encode name dto =
        try
            Ok(JsonSerializer.Serialize(dto, options))
        with ex ->
            Error(CodecError.EncodeError(name, ex))

    let private decode name (json: string) : Result<InvoiceWire.WireDto, CodecError> =
        try
            let dto = JsonSerializer.Deserialize<InvoiceWire.WireDto>(json, options)

            if isNull (box dto) || String.IsNullOrWhiteSpace dto.Tag then
                Error(error name "Missing invoice tag.")
            else
                Ok dto
        with ex ->
            Error(CodecError.DecodeError(name, ex))

    let private invoiceId name (dto: InvoiceWire.WireDto) =
        InvoiceId.tryParse dto.InvoiceId |> Result.mapError (error name)

    let private request name (dto: InvoiceWire.WireDto) =
        match Guid.TryParseExact(dto.SnapshotId, "D") with
        | true, value ->
            OrderSnapshotId.create value
            |> Result.bind (fun snapshotId ->
                invoiceId name dto
                |> Result.mapError (fun _ -> "Invalid invoice id.")
                |> Result.bind (fun id ->
                    let credit =
                        match dto.CreditRefundId, dto.CreditOrigin with
                        | null, null -> Ok None
                        | refund, origin when not (isNull refund) && not (isNull origin) ->
                            RefundId.tryParse refund
                            |> Result.bind (fun refundId ->
                                match origin with
                                | "cancellation" -> Ok(OrderCancellation dto.OrderId)
                                | text when text.StartsWith("return:", StringComparison.Ordinal) ->
                                    match Guid.TryParseExact(text.Substring 7, "D") with
                                    | true, returnId -> Ok(InspectedReturn(dto.OrderId, returnId))
                                    | _ -> Error "Invalid credit return id."
                                | _ -> Error "Invalid credit origin."
                                |> Result.map (fun origin -> Some { RefundId = refundId; Origin = origin }))
                        | _ -> Error "Incomplete credit source."

                    credit
                    |> Result.map (fun credit ->
                        { InvoiceId = id
                          OrderId = dto.OrderId
                          SnapshotId = snapshotId
                          Credit = credit })))
            |> Result.bind InvoiceRequest.validate
            |> Result.mapError (error name)
        | _ -> Error(error name "Invalid invoice snapshot id.")

    let private number name (dto: InvoiceWire.WireDto) =
        InvoiceNumber.create dto.LegalEntity dto.Series dto.FiscalPeriod dto.Sequence
        |> Result.mapError (error name)

    let private issued name dto =
        request name dto
        |> Result.bind (fun request ->
            number name dto
            |> Result.map (fun number -> { Request = request; Number = number }))

    let private digest name (dto: InvoiceWire.WireDto) =
        DocumentDigest.create dto.Digest |> Result.mapError (error name)

    let private reasoned name (dto: InvoiceWire.WireDto) make value =
        CodecSupport.reason dto.Reason
        |> Result.map (fun reason -> make (value, reason))

    let state: Codec<InvoiceState> =
        Codec.create
            (fun (state: InvoiceState) ->
                let dto =
                    match state with
                    | Initial -> empty "initial-v1"
                    | SnapshotPending request -> withRequest "snapshot-pending-v1" request
                    | RenderPending issued -> withIssued "render-pending-v1" issued
                    | Rendered(issued, value) ->
                        { withIssued "rendered-v1" issued with
                            Digest = DocumentDigest.value value }
                    | RenderFailed(issued, reason) ->
                        { withIssued "render-failed-v1" issued with
                            Reason = ReasonCode.value reason }
                    | ManualReview(request, reason) ->
                        { withRequest "manual-review-v1" request with
                            Reason = ReasonCode.value reason }
                    | Closed issued -> withIssued "closed-v1" issued

                encode "InvoiceState" dto)
            (fun json ->
                decode "InvoiceState" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "initial-v1" -> Ok Initial
                    | "snapshot-pending-v1" -> request "InvoiceState" dto |> Result.map SnapshotPending
                    | "render-pending-v1" -> issued "InvoiceState" dto |> Result.map RenderPending
                    | "rendered-v1" ->
                        issued "InvoiceState" dto
                        |> Result.bind (fun issued ->
                            digest "InvoiceState" dto |> Result.map (fun value -> Rendered(issued, value)))
                    | "render-failed-v1" ->
                        issued "InvoiceState" dto
                        |> Result.bind (reasoned "InvoiceState" dto RenderFailed)
                    | "manual-review-v1" ->
                        request "InvoiceState" dto
                        |> Result.bind (fun request ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> ManualReview(request, reason)))
                    | "closed-v1" -> issued "InvoiceState" dto |> Result.map Closed
                    | _ -> Error(error "InvoiceState" "Unknown invoice state.")))

    let event: Codec<InvoiceEvent> =
        Codec.create
            (fun (value: InvoiceEvent) ->
                let dto =
                    match value with
                    | InvoiceRequested request -> withRequest "requested-v1" request
                    | SnapshotIssued(id, number) ->
                        { empty "snapshot-issued-v1" with
                            InvoiceId = InvoiceId.wireString id }
                        |> withNumber number
                    | IssuanceFailed(id, reason) ->
                        { empty "issuance-failed-v1" with
                            InvoiceId = InvoiceId.wireString id
                            Reason = ReasonCode.value reason }
                    | IssuanceRetryRequested -> empty "issuance-retry-requested-v1"
                    | DocumentRendered(id, value) ->
                        { empty "document-rendered-v1" with
                            InvoiceId = InvoiceId.wireString id
                            Digest = DocumentDigest.value value }
                    | DocumentRenderFailed(id, reason) ->
                        { empty "document-render-failed-v1" with
                            InvoiceId = InvoiceId.wireString id
                            Reason = ReasonCode.value reason }
                    | RenderRetryRequested -> empty "render-retry-requested-v1"
                    | RenderReconcileRequested check ->
                        { empty "render-reconcile-requested-v1" with
                            Sequence = int64 check }
                    | RenderReconcileExhausted -> empty "render-reconcile-exhausted-v1"
                    | CloseRequested -> empty "close-requested-v1"

                encode "InvoiceEvent" dto)
            (fun json ->
                decode "InvoiceEvent" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "requested-v1" -> request "InvoiceEvent" dto |> Result.map InvoiceRequested
                    | "snapshot-issued-v1" ->
                        invoiceId "InvoiceEvent" dto
                        |> Result.bind (fun id ->
                            number "InvoiceEvent" dto
                            |> Result.map (fun number -> SnapshotIssued(id, number)))
                    | "issuance-failed-v1" ->
                        invoiceId "InvoiceEvent" dto
                        |> Result.bind (fun id ->
                            CodecSupport.reason dto.Reason
                            |> Result.map (fun reason -> IssuanceFailed(id, reason)))
                    | "issuance-retry-requested-v1" -> Ok IssuanceRetryRequested
                    | "document-rendered-v1" ->
                        invoiceId "InvoiceEvent" dto
                        |> Result.bind (fun id ->
                            digest "InvoiceEvent" dto
                            |> Result.map (fun value -> DocumentRendered(id, value)))
                    | "document-render-failed-v1" ->
                        invoiceId "InvoiceEvent" dto
                        |> Result.bind (reasoned "InvoiceEvent" dto DocumentRenderFailed)
                    | "render-retry-requested-v1" -> Ok RenderRetryRequested
                    | "render-reconcile-requested-v1" when dto.Sequence >= 0L && dto.Sequence <= int64 Int32.MaxValue ->
                        Ok(RenderReconcileRequested(int dto.Sequence))
                    | "render-reconcile-exhausted-v1" -> Ok RenderReconcileExhausted
                    | "close-requested-v1" -> Ok CloseRequested
                    | _ -> Error(error "InvoiceEvent" "Unknown invoice event.")))

    let action: Codec<InvoiceAction> =
        Codec.create
            (fun (value: InvoiceAction) ->
                match value with
                | IssueSnapshot request -> encode "InvoiceAction" (withRequest "issue-snapshot-v1" request)
                | RenderDocument issued -> encode "InvoiceAction" (withIssued "render-document-v1" issued))
            (fun json ->
                decode "InvoiceAction" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "issue-snapshot-v1" -> request "InvoiceAction" dto |> Result.map IssueSnapshot
                    | "render-document-v1" -> issued "InvoiceAction" dto |> Result.map RenderDocument
                    | _ -> Error(error "InvoiceAction" "Unknown invoice action.")))

    let actionError: Codec<InvoiceActionError> =
        Codec.create
            (fun (value: InvoiceActionError) ->
                let tag =
                    match value with
                    | InvoiceActionError.CallbackEncodingFailed -> "callback-encoding-v1"
                    | InvoiceActionError.ActionReceiptMismatch -> "receipt-mismatch-v1"
                    | InvoiceActionError.InvalidAction -> "invalid-action-v1"

                encode "InvoiceActionError" (empty tag))
            (fun json ->
                decode "InvoiceActionError" json
                |> Result.bind (fun dto ->
                    match dto.Tag with
                    | "callback-encoding-v1" -> Ok InvoiceActionError.CallbackEncodingFailed
                    | "receipt-mismatch-v1" -> Ok InvoiceActionError.ActionReceiptMismatch
                    | "invalid-action-v1" -> Ok InvoiceActionError.InvalidAction
                    | _ -> Error(error "InvoiceActionError" "Unknown invoice action error.")))

    let storeOptions
        (context: PostgresContext)
        : MachineStoreOptions<InvoiceEntityId, InvoiceState, InvoiceEvent, InvoiceAction, InvoiceActionError> =
        { MachineStoreOptions.forEntityId<Invoice, InvoiceState, InvoiceEvent, InvoiceAction, InvoiceActionError>
              context
              Invoices.ActionQueue with
            StateCodec = state
            EventCodec = event
            ActionCodec = action
            ErrorCodec = actionError }

    let private build (log: ILogger) storeArg =
        machine<InvoiceEntityId, InvoiceState, InvoiceEvent, InvoiceAction, InvoiceActionError> (
            machineId Invoices.MachineKey
        ) {
            chart Invoices.chartValue
            chartVersion Invoices.ChartVersion
            initialState Invoices.initialState
            store storeArg
            logger log
        }

    let buildWorker log context =
        build
            log
            (PostgresMachineStore(storeOptions context)
            :> IMachineStore<InvoiceEntityId, InvoiceState, InvoiceEvent, InvoiceAction, InvoiceActionError>)

    let buildClient log context =
        build
            log
            (PostgresMachineStore(
                { storeOptions context with
                    Listener = ListenerConnection.Off }
            )
            :> IMachineStore<InvoiceEntityId, InvoiceState, InvoiceEvent, InvoiceAction, InvoiceActionError>)
