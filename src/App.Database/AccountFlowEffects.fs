namespace App.Database

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open App.Auth
open App.Domain
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.DataProtection
open Npgsql

/// <summary>The restricted relational facts a notification effect needs to resolve its email.
/// The destination address lives only in the request row, never in FSM data.</summary>
type AccountFlowRequestRow =
    { FlowId: Guid
      Kind: FlowKind
      UserId: Guid
      DestinationEmail: string
      Generation: int }

/// <summary>The protected notification payload the email outbox carries. The plaintext —
/// including the raw Identity token — exists only in this protected <c>bytea</c>; the flows
/// machine and its history never see it.</summary>
[<RequireQualifiedAccess>]
module AccountEmail =

    [<Literal>]
    let ProtectorPurpose = "fsnix.account-email:v1"

    [<Literal>]
    let EncryptionVersion = 1

    type PayloadDto =
        { [<JsonPropertyName("v")>]
          Version: int
          [<JsonPropertyName("flowId")>]
          FlowId: string
          [<JsonPropertyName("kind")>]
          Kind: string
          [<JsonPropertyName("token")>]
          Token: string }

    /// <summary>The strictly validated plaintext of a notification payload after unprotection.
    /// Decoding rejects unknown versions, unknown flow kinds, empty tokens, and any
    /// unmapped property, so a secret-bearing field can never ride along unnoticed.</summary>
    type DecodedPayload =
        { FlowId: Guid
          Kind: FlowKind
          Token: string }

    let private options =
        JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

    let buildPayload (flowId: Guid) (kind: FlowKind) (token: string) : string =
        let dto: PayloadDto =
            { Version = 1
              FlowId = flowId.ToString("D")
              Kind = FlowKind.wireName kind
              Token = token }

        JsonSerializer.Serialize(dto, options)

    let protect (dataProtection: IDataProtectionProvider) (payload: string) : byte[] =
        let protector = dataProtection.CreateProtector ProtectorPurpose
        protector.Protect(Encoding.UTF8.GetBytes payload)

    let tryUnprotect (dataProtection: IDataProtectionProvider) (bytes: byte[]) : string option =
        try
            let protector = dataProtection.CreateProtector ProtectorPurpose
            Some(Encoding.UTF8.GetString(protector.Unprotect bytes))
        with :? CryptographicException ->
            None

    let tryDecodePayload (json: string) : Result<DecodedPayload, string> =
        try
            let dto = JsonSerializer.Deserialize<PayloadDto>(json, options)

            if isNull (box dto) then
                Error "the payload deserialized to null"
            elif dto.Version <> 1 then
                Error $"unsupported payload version %d{dto.Version}"
            elif String.IsNullOrWhiteSpace dto.Token then
                Error "the payload token was empty"
            else
                match Guid.TryParse dto.FlowId with
                | false, _ -> Error "the payload flow id was not a GUID"
                | true, flowId ->
                    match FlowKind.tryParseWireName dto.Kind with
                    | None -> Error "the payload flow kind was unknown"
                    | Some kind ->
                        Ok
                            { FlowId = flowId
                              Kind = kind
                              Token = dto.Token }
        with error ->
            Error $"the payload did not decode: {error.GetType().Name}"

/// <summary>The durable work of the <c>SendNotification</c> action: an idempotent receipt, the
/// protected email row, and the sanitized notification callback.</summary>
[<RequireQualifiedAccess>]
module AccountFlowEffects =

    let tryLoadRequest
        (dataSource: NpgsqlDataSource)
        (flowId: Guid)
        (ct: CancellationToken)
        : Task<AccountFlowRequestRow option> =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    """SELECT flow_id, flow_kind, user_id, destination_email, generation
                       FROM fsnix.account_flow_requests
                       WHERE flow_id = @flow_id AND status = 'requested'""",
                    connection
                )

            command.Parameters.AddWithValue("flow_id", flowId) |> ignore
            let! reader = command.ExecuteReaderAsync(ct)
            let! couldRead = reader.ReadAsync(ct)

            if not couldRead then
                reader.Dispose()
                return None
            else
                let rowId = reader.GetGuid 0
                let kindWire = reader.GetString 1
                let userId = reader.GetGuid 2
                let destinationEmail = reader.GetString 3
                let generation = reader.GetInt32 4
                reader.Dispose()

                return
                    FlowKind.tryParseWireName kindWire
                    |> Option.map (fun kind ->
                        { FlowId = rowId
                          Kind = kind
                          UserId = userId
                          DestinationEmail = destinationEmail
                          Generation = generation })
        }

    let private encodeEvent (event: FlowEvent) : Result<string, FlowActionError> =
        AccountFlowCodec.event.Encode event
        |> Result.mapError (fun _ -> FlowActionError.CallbackEncodingFailed)

    let private actionKind (action: FlowAction) = string action

    let private callbackKey (record: ActionRecord<FlowId, FlowAction>) : string =
        $"xmsg:v1:%s{MachineId.value record.MachineId}:%d{CommandId.value record.CommandId}:%d{record.Ordinal}:flow-effect"

    /// <summary>Applies one <c>SendNotification</c> delivery: writes the receipt, the protected
    /// email row when a payload was produced, and the notification callback, all-or-nothing.
    /// The request row advances to the action's generation, superseded deadlines are cancelled,
    /// and a new expiry deadline gated on this action's callback is written.</summary>
    let apply
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<FlowId, FlowAction>)
        (request: AccountFlowRequestRow)
        (expiresAt: DateTimeOffset)
        (event: FlowEvent)
        (protectedPayload: byte[] option)
        (ct: CancellationToken)
        : Task<Result<unit, FlowActionError>> =
        task {
            match encodeEvent event with
            | Error error -> return Error error
            | Ok eventJson ->
                let (SendNotification generation) = record.Action
                let callback = callbackKey record
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync(ct)
                use! transaction = connection.BeginTransactionAsync(ct)

                // The receipt payload is the action kind (a generation-free digest), unchanged
                // from the original account-flow protocol.
                let kind = actionKind record.Action

                match! WorkflowEffects.receipt connection transaction record kind kind ct with
                | Error ReceiptFailure.EncodingFailed -> return Error FlowActionError.CallbackEncodingFailed
                | Error ReceiptFailure.Mismatch ->
                    do! transaction.CommitAsync(ct)
                    return Error FlowActionError.ActionReceiptMismatch
                | Ok ReceiptStatus.Duplicate ->
                    do! transaction.CommitAsync(ct)
                    return Ok()
                | Ok ReceiptStatus.FirstRun ->
                    use advance =
                        new NpgsqlCommand(
                            """UPDATE fsnix.account_flow_requests
                               SET generation = @generation,
                                   resend_count = @generation - 1,
                                   expires_at = @expires_at,
                                   updated_at = statement_timestamp()
                               WHERE flow_id = @flow_id AND status = 'requested'""",
                            connection,
                            transaction
                        )

                    advance.Parameters.AddWithValue("flow_id", request.FlowId) |> ignore
                    advance.Parameters.AddWithValue("generation", generation) |> ignore
                    advance.Parameters.AddWithValue("expires_at", expiresAt) |> ignore
                    let! _ = advance.ExecuteNonQueryAsync(ct)

                    use cancelSuperseded =
                        new NpgsqlCommand(
                            """UPDATE fsnix.flow_deadlines
                               SET status = 'cancelled'
                               WHERE flow_id = @flow_id
                                 AND status = 'pending'
                                 AND generation < @generation""",
                            connection,
                            transaction
                        )

                    cancelSuperseded.Parameters.AddWithValue("flow_id", request.FlowId) |> ignore
                    cancelSuperseded.Parameters.AddWithValue("generation", generation) |> ignore
                    let! _ = cancelSuperseded.ExecuteNonQueryAsync(ct)

                    match protectedPayload with
                    | Some payload ->
                        use email =
                            new NpgsqlCommand(
                                """INSERT INTO fsnix.account_email_outbox (flow_id, generation, protected_payload, encryption_version)
                                   VALUES (@flow_id, @generation, @protected_payload, @encryption_version)
                                   ON CONFLICT (flow_id, generation) DO NOTHING""",
                                connection,
                                transaction
                            )

                        email.Parameters.AddWithValue("flow_id", request.FlowId) |> ignore
                        email.Parameters.AddWithValue("generation", generation) |> ignore
                        email.Parameters.AddWithValue("protected_payload", payload) |> ignore

                        email.Parameters.AddWithValue("encryption_version", AccountEmail.EncryptionVersion)
                        |> ignore

                        let! _ = email.ExecuteNonQueryAsync(ct)
                        ()
                    | None -> ()

                    use deadline =
                        new NpgsqlCommand(
                            """INSERT INTO fsnix.flow_deadlines (flow_id, timer_kind, generation, deadline, gate_callback_key)
                               VALUES (@flow_id, 'flow-expiry', @generation, @deadline, @gate)
                               ON CONFLICT (flow_id, timer_kind, generation) DO NOTHING""",
                            connection,
                            transaction
                        )

                    deadline.Parameters.AddWithValue("flow_id", request.FlowId) |> ignore
                    deadline.Parameters.AddWithValue("generation", generation) |> ignore
                    deadline.Parameters.AddWithValue("deadline", expiresAt) |> ignore
                    deadline.Parameters.AddWithValue("gate", callback) |> ignore
                    let! _ = deadline.ExecuteNonQueryAsync(ct)

                    do!
                        WorkflowEffects.callback
                            connection
                            transaction
                            callback
                            AccountFlow.MachineKey
                            (EntityId.value record.EntityId)
                            eventJson
                            ct

                    do! transaction.CommitAsync(ct)
                    return Ok()
        }
