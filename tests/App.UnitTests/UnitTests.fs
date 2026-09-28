namespace App.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open App
open App.Auth
open App.Database
open App.Domain
open App.Views
open ByzantineSystems.Automata.Core
open Expecto
open Microsoft.AspNetCore.Identity
open Microsoft.FSharp.Reflection
open Oxpecker.ViewEngine
open Serilog
open Serilog.Core
open Serilog.Events

module UnitTests =
    let ``development login password hash is valid`` () =
        let hash =
            "AQAAAAEAAYagAAAAEMoYqa1WkeGpOvW4HJDH1tdTStebCy7kT1nExUm64FaEi8kJttez9rCwS/lgnNHW8w=="

        let result =
            PasswordHasher<ApplicationUser>().VerifyHashedPassword(ApplicationUser(), hash, "Test-Operator-42!")

        Assert.NotEqual(PasswordVerificationResult.Failed, result)

    let ``persisted feature names round trip explicitly`` () =
        for flag in FeatureFlag.all do
            let name = FeatureFlag.persistedName flag
            Assert.Equal(Some flag, FeatureFlag.tryParse name)

        Assert.Equal(None, FeatureFlag.tryParse "new-dashboard")

    let ``scheduling rejects past and malformed timestamps`` () =
        let now = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)

        Assert.Equal(Error EffectiveTimeIsInPast, Scheduling.parseEffectiveTime now (Some "2026-03-01T11:59:59Z"))

        Assert.Equal(
            Ok(ScheduledAt(DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero))),
            Scheduling.parseEffectiveTime now (Some "2026-03-01T08:30:00-04:00")
        )

        match Scheduling.parseEffectiveTime now (Some "2026-03-01T12:30") with
        | Error(InvalidEffectiveTime _) -> ()
        | actual -> Assert.Fail $"Expected a timestamp without an offset to be invalid, got {actual}."

        match Scheduling.parseEffectiveTime now (Some "not-a-date") with
        | Error(InvalidEffectiveTime _) -> ()
        | actual -> Assert.Fail $"Expected invalid effective time, got {actual}."

        Assert.Equal(Ok Immediate, Scheduling.parseEffectiveTime now None)

    let ``feature definitions use AlwaysOn only for enabled rows`` () =
        let store = FakeFeatureFlagStore()
        let flagStore = store :> IFeatureFlagStore

        let enabled =
            flagStore.GetCurrent(NewDashboard, CancellationToken.None).Result.Value

        let disabled =
            flagStore.GetCurrent(BetaCheckout, CancellationToken.None).Result.Value

        let enabledDefinition = FeatureDefinitions.fromCurrent enabled
        let disabledDefinition = FeatureDefinitions.fromCurrent disabled

        Assert.Equal("NewDashboard", enabledDefinition.Name)
        Assert.Equal("AlwaysOn", enabledDefinition.EnabledFor |> Seq.exactlyOne |> _.Name)
        Assert.Empty disabledDefinition.EnabledFor

    let ``workflow maps schedule conflicts`` () =
        task {
            let store = FakeFeatureFlagStore(BoundaryExists)

            let change =
                { Flag = NewDashboard
                  State = Disabled
                  EffectiveTime = Immediate }

            let! result = Workflow.schedule store DateTimeOffset.UtcNow change CancellationToken.None
            Assert.Equal(Error Conflict, result)
        }

    let ``scheduling an existing value is unchanged`` () =
        task {
            let store = FakeFeatureFlagStore()
            let flagStore = store :> IFeatureFlagStore

            let change =
                { Flag = NewDashboard
                  State = Enabled
                  EffectiveTime = Immediate }

            let! result = Workflow.schedule store DateTimeOffset.UtcNow change CancellationToken.None
            Assert.Equal(Ok Unchanged, result)

            let! schedule = flagStore.GetSchedule(NewDashboard, CancellationToken.None)
            Assert.Equal(1, schedule.Value.History.Length)
        }

    let ``demo view selects variants and distinguishes event and button swaps`` () =
        let enabled = DemoViews.fragment NewDashboard true |> Render.toString
        let disabled = DemoViews.fragment NewDashboard false |> Render.toString

        Assert.Contains("New dashboard", enabled)
        Assert.Contains("Classic dashboard", disabled)
        Assert.Contains("hx-swap=\"outerMorph\"", enabled)
        Assert.Contains("hx-swap=\"outerHTML\"", enabled)

    let private userId =
        UserId.create (Guid.Parse "11111111-2222-3333-4444-555555555555")
        |> Result.defaultWith Assert.Fail

    let private completedAt = DateTimeOffset.FromUnixTimeMilliseconds 1_800_000_000_123L

    let private expectResolution state event =
        match Chart.resolve AccountFlow.chartValue state event with
        | Ok resolution -> resolution
        | Error error -> Assert.Fail $"Expected the flow event to resolve, got %A{error}."

    let ``authoritative completion survives callback reordering`` () =
        let completion = CompletionSucceeded(EmailVerification, userId, completedAt)
        let fromFresh = expectResolution Fresh completion

        Assert.Equal(
            Completed
                { Kind = EmailVerification
                  UserId = userId
                  CompletedAt = completedAt },
            fromFresh.Next
        )

        Assert.Empty fromFresh.Actions

        let pending =
            NotificationPending
                { Kind = EmailVerification
                  UserId = userId
                  Generation = 1
                  ResendCount = 0 }

        let fromPending = expectResolution pending completion
        Assert.Equal(fromFresh.Next, fromPending.Next)
        Assert.Empty fromPending.Actions

    let ``completion metadata must match an active flow`` () =
        let pending =
            NotificationPending
                { Kind = PasswordReset
                  UserId = userId
                  Generation = 1
                  ResendCount = 0 }

        let mismatched = CompletionSucceeded(EmailChange, userId, completedAt)
        let resolution = expectResolution pending mismatched
        Assert.Equal(pending, resolution.Next)
        Assert.Empty resolution.Actions

    let ``flow codecs have stable golden JSON and reject malformed payloads`` () =
        let completion = CompletionSucceeded(EmailVerification, userId, completedAt)

        Assert.Equal(
            Ok
                "{\"tag\":\"completion-succeeded-v1\",\"kind\":\"email-verification\",\"userId\":\"11111111-2222-3333-4444-555555555555\",\"completedAt\":1800000000123}",
            AccountFlowCodec.event.Encode completion
        )

        Assert.Equal(
            Ok completion,
            AccountFlowCodec.event.Decode
                "{\"tag\":\"completion-succeeded-v1\",\"kind\":\"email-verification\",\"userId\":\"11111111-2222-3333-4444-555555555555\",\"completedAt\":1800000000123}"
        )

        match AccountFlowCodec.event.Decode "{\"tag\":\"completion-succeeded-v1\",\"completedAt\":1800000000123}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"Missing completion identity decoded as %A{value}."

        match
            AccountFlowCodec.event.Decode
                "{\"tag\":\"notification-queued-v2\",\"generation\":1,\"token\":\"must-not-persist\"}"
        with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"An unknown secret-bearing property decoded as %A{value}."

        match
            AccountFlowCodec.state.Decode
                "{\"tag\":\"awaiting-completion-v1\",\"kind\":\"password-reset\",\"userId\":\"11111111-2222-3333-4444-555555555555\",\"generation\":2,\"resendCount\":0}"
        with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"Inconsistent active counters decoded as %A{value}."

    let ``start and resend emit notifications for their generations`` () =
        let started = expectResolution Fresh (StartRequested(EmailVerification, userId))
        Assert.Equal([ SendNotification 1 ], started.Actions)

        let pending =
            match started.Next with
            | NotificationPending flow -> flow
            | other -> Assert.Fail $"Expected notification-pending, got %A{other}."

        Assert.Equal(1, pending.Generation)
        Assert.Equal(0, pending.ResendCount)

        let awaiting = expectResolution started.Next (NotificationQueued 1)
        let resent = expectResolution awaiting.Next ResendRequested
        Assert.Equal([ SendNotification 2 ], resent.Actions)

        match resent.Next with
        | NotificationPending flow ->
            Assert.Equal(2, flow.Generation)
            Assert.Equal(1, flow.ResendCount)
        | other -> Assert.Fail $"Expected notification-pending after resend, got %A{other}."

    let ``delivery callbacks apply only to the current generation`` () =
        let active generation resends =
            { Kind = EmailVerification
              UserId = userId
              Generation = generation
              ResendCount = resends }

        // SMTP acceptance may overtake the queued callback; both advance a pending flow.
        let pending = NotificationPending(active 1 0)
        let viaQueued = expectResolution pending (NotificationQueued 1)
        let viaSent = expectResolution pending (NotificationSent 1)

        Assert.Equal(AwaitingCompletion(active 1 0), viaQueued.Next)
        Assert.Equal(AwaitingCompletion(active 1 0), viaSent.Next)

        // A permanent rejection after the queued callback moves the flow to delivery-failed.
        let awaiting = AwaitingCompletion(active 1 0)
        let failed = expectResolution awaiting (NotificationSendFailed 1)
        Assert.Equal(DeliveryFailed(active 1 0), failed.Next)

        // Stale callbacks from a superseded generation are absorbed everywhere.
        let resentPending = NotificationPending(active 2 1)
        let resentAwaiting = AwaitingCompletion(active 2 1)

        for stale in [ NotificationQueued 1; NotificationSent 1; NotificationSendFailed 1 ] do
            Assert.Equal(resentPending, (expectResolution resentPending stale).Next)
            Assert.Equal(resentAwaiting, (expectResolution resentAwaiting stale).Next)

    let ``flow codecs version notification callbacks with their generation`` () =
        let golden = "{\"tag\":\"notification-sent-v2\",\"generation\":2}"

        Assert.Equal(Ok golden, AccountFlowCodec.event.Encode(NotificationSent 2))
        Assert.Equal(Ok(NotificationSent 2), AccountFlowCodec.event.Decode golden)

        Assert.Equal(
            Ok "{\"tag\":\"notification-queued-v2\",\"generation\":1}",
            AccountFlowCodec.event.Encode(NotificationQueued 1)
        )

        Assert.Equal(
            Ok "{\"tag\":\"notification-send-failed-v2\",\"generation\":3}",
            AccountFlowCodec.event.Encode(NotificationSendFailed 3)
        )

        match AccountFlowCodec.event.Decode "{\"tag\":\"notification-sent-v2\",\"generation\":0}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A generation below one decoded as %A{value}."

        match AccountFlowCodec.event.Decode "{\"tag\":\"notification-sent-v1\"}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A superseded generation-less tag decoded as %A{value}."

    let ``flow action codec carries a validated generation`` () =
        Assert.Equal(
            Ok "{\"tag\":\"send-notification-v2\",\"generation\":2}",
            AccountFlowCodec.action.Encode(SendNotification 2)
        )

        Assert.Equal(
            Ok(SendNotification 2),
            AccountFlowCodec.action.Decode "{\"tag\":\"send-notification-v2\",\"generation\":2}"
        )

        match AccountFlowCodec.action.Decode "{\"tag\":\"send-notification-v2\",\"generation\":0}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A generation below one decoded as %A{value}."

        match AccountFlowCodec.action.Decode "{\"tag\":\"send-notification-v1\"}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A superseded action tag decoded as %A{value}."

    let ``capabilities are 256-bit scoped keyed hashes`` () =
        let key =
            CapabilityHashKey.create "primary-2026" (Array.init 32 byte)
            |> Result.defaultWith Assert.Fail

        let rotatedKey =
            CapabilityHashKey.create "rotated-2027" (Array.init 32 (fun index -> byte (index + 1)))
            |> Result.defaultWith Assert.Fail

        let purpose =
            CapabilityPurpose.create "guest-cart" |> Result.defaultWith Assert.Fail

        let otherPurpose =
            CapabilityPurpose.create "order-tracking" |> Result.defaultWith Assert.Fail

        let entity = CapabilityEntity.create "cart-123" |> Result.defaultWith Assert.Fail

        let otherEntity =
            CapabilityEntity.create "cart-456" |> Result.defaultWith Assert.Fail

        let raw = Capability.generate ()
        let another = Capability.generate ()
        let encoded = Capability.encode raw
        let digest = Capability.digest key purpose entity raw

        Assert.True(encoded.StartsWith("cap_v1_", StringComparison.Ordinal))
        Assert.Equal(50, encoded.Length)
        Assert.NotEqual(encoded, Capability.encode another)
        Assert.Equal("[REDACTED CAPABILITY]", string raw)
        Assert.DoesNotContain(encoded, Convert.ToHexString digest.Digest)

        match Capability.tryParse encoded with
        | None -> Assert.Fail "A generated capability did not parse."
        | Some parsed ->
            Assert.True(Capability.verify key purpose entity parsed digest)
            Assert.False(Capability.verify key otherPurpose entity parsed digest)
            Assert.False(Capability.verify key purpose otherEntity parsed digest)
            Assert.False(Capability.verify rotatedKey purpose entity parsed digest)

    let private unionCaseCount<'value> () =
        FSharpType.GetUnionCases(typeof<'value>) |> Array.length

    let private assertSafeJson (canary: string) (json: string) =
        Assert.DoesNotContain(canary, json)
        use document = JsonDocument.Parse json

        let rec inspect (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                for property in element.EnumerateObject() do
                    let name = property.Name.ToLowerInvariant()
                    Assert.False(SafeDiagnostics.forbiddenPropertyNames.Contains name)
                    inspect property.Value
            | JsonValueKind.Array ->
                for item in element.EnumerateArray() do
                    inspect item
            | _ -> ()

        inspect document.RootElement

    let ``durable account codecs cover every case without secret fields`` () =
        let canary = "CANARY-secret-credential-42"

        let active =
            { Kind = PasswordReset
              UserId = userId
              Generation = 2
              ResendCount = 1 }

        let states =
            [ Fresh
              NotificationPending active
              AwaitingCompletion active
              Completed
                  { Kind = PasswordReset
                    UserId = userId
                    CompletedAt = completedAt }
              Expired active
              DeliveryFailed active
              ManualReview
                  { Kind = PasswordReset
                    UserId = userId
                    Reason = ManualReviewReason.ReconciliationExhausted } ]

        let events =
            [ StartRequested(PasswordReset, userId)
              NotificationQueued 1
              NotificationSent 1
              NotificationSendFailed 1
              CompletionSucceeded(PasswordReset, userId, completedAt)
              ExpiryTimerFired(1, completedAt)
              ResendRequested
              MarkedForManualReview ManualReviewReason.OperatorRequested ]

        let actions = [ SendNotification 1 ]

        let errors =
            [ FlowActionError.InvalidFlowEntityId
              FlowActionError.FlowRequestNotActive
              FlowActionError.FlowUserNotFound
              FlowActionError.IdentityTokenEmpty
              FlowActionError.CallbackEncodingFailed
              FlowActionError.ActionReceiptMismatch ]

        Assert.Equal(unionCaseCount<FlowState> (), states.Length)
        Assert.Equal(unionCaseCount<FlowEvent> (), events.Length)
        Assert.Equal(unionCaseCount<FlowAction> (), actions.Length)
        Assert.Equal(unionCaseCount<FlowActionError> (), errors.Length)

        states
        |> List.iter (fun value ->
            AccountFlowCodec.state.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

        events
        |> List.iter (fun value ->
            AccountFlowCodec.event.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

        actions
        |> List.iter (fun value ->
            AccountFlowCodec.action.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

        errors
        |> List.iter (fun value ->
            AccountFlowCodec.error.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

        let probeErrors =
            [ ProbeActionError.CallbackEncodingFailed
              ProbeActionError.ActionReceiptMismatch ]

        Assert.Equal(unionCaseCount<ProbeActionError> (), probeErrors.Length)

        probeErrors
        |> List.iter (fun value ->
            Probe.errorCodec.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

    let ``closed error codecs use stable tags and reject unknown data`` () =
        let golden = "{\"tag\":\"action-receipt-mismatch-v1\"}"
        Assert.Equal(Ok golden, AccountFlowCodec.error.Encode FlowActionError.ActionReceiptMismatch)
        Assert.Equal(Ok FlowActionError.ActionReceiptMismatch, AccountFlowCodec.error.Decode golden)

        Assert.Equal(
            Ok ProbeActionError.CallbackEncodingFailed,
            Probe.errorCodec.Decode "{\"tag\":\"callback-encoding-failed-v1\"}"
        )

        match AccountFlowCodec.error.Decode "{\"tag\":\"provider-said-secret\"}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"An unknown account-flow error decoded as %A{value}."

        match Probe.errorCodec.Decode "{\"tag\":\"callback-encoding-failed-v1\",\"secret\":\"x\"}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A secret-bearing probe error decoded as %A{value}."

    type private CaptureSink() =
        let events = ResizeArray<LogEvent>()
        member _.Events = events :> IReadOnlyCollection<LogEvent>

        interface ILogEventSink with
            member _.Emit(logEvent) = events.Add logEvent

    let ``safe diagnostics do not log exception messages`` () =
        let canary = "CANARY-password-token@example.test"
        let sink = CaptureSink()
        use logger = LoggerConfiguration().WriteTo.Sink(sink).CreateLogger()
        let error = InvalidOperationException canary
        logger.Error("worker pass failed ({ExceptionType})", SafeDiagnostics.exceptionType error)
        let rendered = sink.Events |> Seq.map _.RenderMessage() |> String.concat "\n"
        Assert.DoesNotContain(canary, rendered)
        Assert.Contains("InvalidOperationException", rendered)

    let ``runtime readiness requires fresh successful components`` () =
        let health = RuntimeHealth(TimeProvider.System)
        Assert.False(health.Snapshot() |> RuntimeHealth.startupReady)

        health.Succeeded RuntimeComponent.Boot
        health.Succeeded RuntimeComponent.ProbeMachine
        health.Succeeded RuntimeComponent.AccountFlowMachine
        Assert.True(health.Snapshot() |> RuntimeHealth.startupReady)

        health.Succeeded RuntimeComponent.IntegrationOutboxRelay
        health.Succeeded RuntimeComponent.EmailDeliveryRelay
        health.Succeeded RuntimeComponent.FlowDeadlineScanner
        let snapshot = health.Snapshot()
        Assert.True(RuntimeHealth.workersReady (DateTimeOffset.UtcNow) (TimeSpan.FromMinutes 1.) snapshot)

        Assert.False(
            RuntimeHealth.workersReady (DateTimeOffset.UtcNow.AddMinutes 2.) (TimeSpan.FromMinutes 1.) snapshot
        )

        health.Failed(RuntimeComponent.EmailDeliveryRelay, RuntimeFailure.PassFailed)
        Assert.False(RuntimeHealth.workersReady (DateTimeOffset.UtcNow) (TimeSpan.FromMinutes 1.) (health.Snapshot()))
        health.Succeeded RuntimeComponent.EmailDeliveryRelay
        Assert.True(RuntimeHealth.workersReady (DateTimeOffset.UtcNow) (TimeSpan.FromMinutes 1.) (health.Snapshot()))

    let tests =
        testList
            "unit"
            [ testCase "development login password hash is valid" ``development login password hash is valid``
              testCase "persisted feature names round trip explicitly" ``persisted feature names round trip explicitly``
              testCase
                  "scheduling rejects past and malformed timestamps"
                  ``scheduling rejects past and malformed timestamps``
              testCase "feature definitions project state" ``feature definitions use AlwaysOn only for enabled rows``
              testCaseTask "workflow maps schedule conflicts" ``workflow maps schedule conflicts``
              testCaseTask "scheduling existing values is unchanged" ``scheduling an existing value is unchanged``
              testCase
                  "demo views use deliberate swaps"
                  ``demo view selects variants and distinguishes event and button swaps``
              testCase
                  "authoritative completion survives callback reordering"
                  ``authoritative completion survives callback reordering``
              testCase "completion metadata matches active flow" ``completion metadata must match an active flow``
              testCase
                  "notification actions carry their generation"
                  ``start and resend emit notifications for their generations``
              testCase
                  "delivery callbacks match the current generation"
                  ``delivery callbacks apply only to the current generation``
              testCase "flow action codec validates generation" ``flow action codec carries a validated generation``
              testCase
                  "notification callback codecs are versioned and strict"
                  ``flow codecs version notification callbacks with their generation``
              testCase
                  "flow codecs are stable and strict"
                  ``flow codecs have stable golden JSON and reject malformed payloads``
              testCase "capabilities use scoped keyed hashes" ``capabilities are 256-bit scoped keyed hashes``
              testCase
                  "durable codecs contain no secret fields"
                  ``durable account codecs cover every case without secret fields``
              testCase "closed error codecs are strict" ``closed error codecs use stable tags and reject unknown data``
              testCase "safe diagnostics redact exception messages" ``safe diagnostics do not log exception messages``
              testCase
                  "runtime readiness requires fresh components"
                  ``runtime readiness requires fresh successful components`` ]
