namespace App.Database

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.Logging
open Npgsql

type Probe = class end

type ProbeId = EntityId<Probe>

type ProbePhase =
    | Idle
    | EffectPending
    | Done

type ProbeState = { Phase: ProbePhase; Runs: int }

type ProbeEvent =
    | RunProbe
    | EffectCompleted

type ProbeAction = PerformProbeEffect

[<RequireQualifiedAccess>]
type ProbeActionError =
    | CallbackEncodingFailed
    | ActionReceiptMismatch

[<RequireQualifiedAccess>]
module Probe =

    type private ErrorDto =
        { [<JsonPropertyName("tag")>]
          Tag: string }

    [<Literal>]
    let MachineKey = "probes"

    [<Literal>]
    let ActionQueue = "probe_actions"

    /// Bump on every semantic chart change (guards, transitions, codecs), even when the
    /// structure is unchanged; Automata's fingerprint cannot see inside functions.
    [<Literal>]
    let ChartVersion = 1

    let initialProbeState = { Phase = Idle; Runs = 0 }

    let private codecOptions =
        let options =
            JsonSerializerOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)

        options.RespectRequiredConstructorParameters <- true
        options

    let private encodeError error =
        try
            let tag =
                match error with
                | ProbeActionError.CallbackEncodingFailed -> "callback-encoding-failed-v1"
                | ProbeActionError.ActionReceiptMismatch -> "action-receipt-mismatch-v1"

            Ok(JsonSerializer.Serialize({ Tag = tag }, codecOptions))
        with error ->
            Error(CodecError.EncodeError("ProbeActionError", error))

    let private decodeError (json: string) =
        try
            use document = JsonDocument.Parse json
            let properties = document.RootElement.EnumerateObject() |> Seq.toList

            match properties with
            | [ property ] when property.Name = "tag" ->
                match property.Value.GetString() with
                | "callback-encoding-failed-v1" -> Ok ProbeActionError.CallbackEncodingFailed
                | "action-receipt-mismatch-v1" -> Ok ProbeActionError.ActionReceiptMismatch
                | tag -> Error(CodecError.DecodeError("ProbeActionError", FormatException $"Unknown tag '{tag}'."))
            | _ ->
                Error(
                    CodecError.DecodeError("ProbeActionError", FormatException "Expected exactly one 'tag' property.")
                )
        with error ->
            Error(CodecError.DecodeError("ProbeActionError", error))

    let errorCodec: Codec<ProbeActionError> = Codec.create encodeError decodeError

    let private chartResult =
        statechart<ProbeState, ProbeEvent, ProbeAction, ProbeActionError> {
            root "probe"

            classify (fun state ->
                match state.Phase with
                | Idle -> stateId "idle"
                | EffectPending -> stateId "effect-pending"
                | Done -> stateId "done")

            state "idle" {
                on (fun _ event -> event = RunProbe) (fun state _ ->
                    [ PerformProbeEffect ], { state with Phase = EffectPending })
            }

            state "effect-pending" {
                on (fun _ event -> event = EffectCompleted) (fun state _ ->
                    [],
                    { state with
                        Phase = Done
                        Runs = state.Runs + 1 })

                internalOn (fun _ event -> event = RunProbe) (fun _ _ -> [])
            }

            state "done" {
                on (fun _ event -> event = RunProbe) (fun state _ ->
                    [ PerformProbeEffect ], { state with Phase = EffectPending })
            }
        }

    let chartValue =
        match chartResult with
        | Ok built -> built
        | Error errors -> invalidOp $"probe chart is invalid: %A{errors}"

    let private options (context: PostgresContext) =
        { MachineStoreOptions.forEntityId<Probe, ProbeState, ProbeEvent, ProbeAction, ProbeActionError>
              context
              ActionQueue with
            ErrorCodec = errorCodec }

    let workerStore (context: PostgresContext) = options context |> PostgresMachineStore

    let clientStore (context: PostgresContext) =
        { options context with
            Listener = ListenerConnection.Off }
        |> PostgresMachineStore

    let private build (log: ILogger) storeArg =
        machine<ProbeId, ProbeState, ProbeEvent, ProbeAction, ProbeActionError> (machineId MachineKey) {
            chart chartValue
            chartVersion ChartVersion
            initialState initialProbeState
            store storeArg
            logger log
        }

    let buildWorker (log: ILogger) (context: PostgresContext) =
        build log (workerStore context :> IMachineStore<ProbeId, ProbeState, ProbeEvent, ProbeAction, ProbeActionError>)

    let buildClient (log: ILogger) (context: PostgresContext) =
        build log (clientStore context :> IMachineStore<ProbeId, ProbeState, ProbeEvent, ProbeAction, ProbeActionError>)

[<RequireQualifiedAccess>]
module ProbeEffects =

    let callbackKey (record: ActionRecord<ProbeId, ProbeAction>) : string =
        $"xmsg:v1:%s{MachineId.value record.MachineId}:%d{CommandId.value record.CommandId}:%d{record.Ordinal}:probe-effect"

    let private encodeEvent (event: ProbeEvent) : Result<string, ProbeActionError> =
        Serialization.systemTextJson<ProbeEvent>().Encode event
        |> Result.mapError (fun _ -> ProbeActionError.CallbackEncodingFailed)

    let apply
        (dataSource: NpgsqlDataSource)
        (record: ActionRecord<ProbeId, ProbeAction>)
        (ct: CancellationToken)
        : Task<Result<unit, ProbeActionError>> =
        task {
            match encodeEvent EffectCompleted with
            | Error error -> return Error error
            | Ok eventJson ->
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync(ct)
                use transaction = connection.BeginTransaction()

                let kind = string record.Action

                match! WorkflowEffects.receipt connection transaction record kind kind ct with
                | Error ReceiptFailure.EncodingFailed -> return Error ProbeActionError.CallbackEncodingFailed
                | Error ReceiptFailure.Mismatch ->
                    do! transaction.CommitAsync(ct)
                    return Error ProbeActionError.ActionReceiptMismatch
                | Ok ReceiptStatus.Duplicate ->
                    do! transaction.CommitAsync(ct)
                    return Ok()
                | Ok ReceiptStatus.FirstRun ->
                    do!
                        WorkflowEffects.callback
                            connection
                            transaction
                            (callbackKey record)
                            Probe.MachineKey
                            (EntityId.value record.EntityId)
                            eventJson
                            ct

                    do! transaction.CommitAsync(ct)
                    return Ok()
        }
