namespace App.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open App
open App.Auth
open App.Cart
open App.Database
open App.Domain
open App.Orders
open App.Payments
open App.Shipments
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
                "{\"tag\":\"notification-queued-v1\",\"generation\":1,\"token\":\"must-not-persist\"}"
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
        Assert.Equal(FlowState.DeliveryFailed(active 1 0), failed.Next)

        // Stale callbacks from a superseded generation are absorbed everywhere.
        let resentPending = NotificationPending(active 2 1)
        let resentAwaiting = AwaitingCompletion(active 2 1)

        for stale in [ NotificationQueued 1; NotificationSent 1; NotificationSendFailed 1 ] do
            Assert.Equal(resentPending, (expectResolution resentPending stale).Next)
            Assert.Equal(resentAwaiting, (expectResolution resentAwaiting stale).Next)

    let ``flow codecs version notification callbacks with their generation`` () =
        let golden = "{\"tag\":\"notification-sent-v1\",\"generation\":2}"

        Assert.Equal(Ok golden, AccountFlowCodec.event.Encode(NotificationSent 2))
        Assert.Equal(Ok(NotificationSent 2), AccountFlowCodec.event.Decode golden)

        Assert.Equal(
            Ok "{\"tag\":\"notification-queued-v1\",\"generation\":1}",
            AccountFlowCodec.event.Encode(NotificationQueued 1)
        )

        Assert.Equal(
            Ok "{\"tag\":\"notification-send-failed-v1\",\"generation\":3}",
            AccountFlowCodec.event.Encode(NotificationSendFailed 3)
        )

        match AccountFlowCodec.event.Decode "{\"tag\":\"notification-sent-v1\",\"generation\":0}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A generation below one decoded as %A{value}."

        match AccountFlowCodec.event.Decode "{\"tag\":\"notification-sent-v1\"}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A generation-less payload decoded as %A{value}."

    let ``flow action codec carries a validated generation`` () =
        Assert.Equal(
            Ok "{\"tag\":\"send-notification-v1\",\"generation\":2}",
            AccountFlowCodec.action.Encode(SendNotification 2)
        )

        Assert.Equal(
            Ok(SendNotification 2),
            AccountFlowCodec.action.Decode "{\"tag\":\"send-notification-v1\",\"generation\":2}"
        )

        match AccountFlowCodec.action.Decode "{\"tag\":\"send-notification-v1\",\"generation\":0}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A generation below one decoded as %A{value}."

        match AccountFlowCodec.action.Decode "{\"tag\":\"send-notification-v1\"}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A generation-less action decoded as %A{value}."

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
              FlowState.DeliveryFailed active
              FlowState.ManualReview
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
        health.Succeeded RuntimeComponent.CartMachine
        health.Succeeded RuntimeComponent.OrderMachine
        health.Succeeded RuntimeComponent.PaymentMachine
        health.Succeeded RuntimeComponent.ShipmentMachine
        health.Succeeded RuntimeComponent.RefundMachine
        health.Succeeded RuntimeComponent.ReturnMachine
        health.Succeeded RuntimeComponent.InvoiceMachine
        Assert.True(health.Snapshot() |> RuntimeHealth.startupReady)

        health.Succeeded RuntimeComponent.IntegrationOutboxRelay
        health.Succeeded RuntimeComponent.EmailDeliveryRelay
        health.Succeeded RuntimeComponent.FlowDeadlineScanner
        health.Succeeded RuntimeComponent.CartAbandonmentScanner
        health.Succeeded RuntimeComponent.CartMergeScanner
        health.Succeeded RuntimeComponent.ReservationExpiryScanner
        health.Succeeded RuntimeComponent.ReturnWindowScanner
        health.Succeeded RuntimeComponent.GatewayReconciliationScanner
        health.Succeeded RuntimeComponent.AuthorizationExpiryScanner
        health.Succeeded RuntimeComponent.InvoiceRenderScanner
        health.Succeeded RuntimeComponent.ShipmentLostScanner
        let snapshot = health.Snapshot()
        Assert.True(RuntimeHealth.workersReady (DateTimeOffset.UtcNow) (TimeSpan.FromMinutes 1.) snapshot)

        Assert.False(
            RuntimeHealth.workersReady (DateTimeOffset.UtcNow.AddMinutes 2.) (TimeSpan.FromMinutes 1.) snapshot
        )

        health.Failed(RuntimeComponent.EmailDeliveryRelay, RuntimeFailure.PassFailed)
        Assert.False(RuntimeHealth.workersReady (DateTimeOffset.UtcNow) (TimeSpan.FromMinutes 1.) (health.Snapshot()))
        health.Succeeded RuntimeComponent.EmailDeliveryRelay
        Assert.True(RuntimeHealth.workersReady (DateTimeOffset.UtcNow) (TimeSpan.FromMinutes 1.) (health.Snapshot()))

    let private cartProduct (suffix: byte) =
        ProductId.create (Guid.Parse $"00000000-0000-0000-0000-0000000000{suffix:X2}")
        |> Result.defaultWith Assert.Fail

    let private makeCartLine (suffix: byte) (sku: string) (name: string) (amount: decimal) (quantity: int) : CartLine =
        { ProductId = cartProduct suffix
          Sku = Sku.create sku |> Result.defaultWith Assert.Fail
          Name = NonEmptyString.create 200 name |> Result.defaultWith Assert.Fail
          UnitPrice = Money.create amount "USD" |> Result.defaultWith Assert.Fail
          Quantity = Quantity.create quantity |> Result.defaultWith Assert.Fail }

    let private expectCart state event =
        match Chart.resolve Cart.chartValue state event with
        | Ok resolution -> resolution
        | Error error -> Assert.Fail $"Expected the cart event to resolve, got %A{error}."

    let private expectCartRejected state event =
        match Chart.resolve Cart.chartValue state event with
        | Error _ -> ()
        | Ok resolution -> Assert.Fail $"Expected the cart event to be rejected, got %A{resolution}."

    let ``cart add starts an active cart`` () =
        let line = makeCartLine 1uy "COFFEE" "Coffee" 24.90m 1
        let resolution = expectCart CartState.Empty (LineAdded(line, 0L))

        match resolution.Next with
        | CartState.Active cart ->
            Assert.Equal(1L, cart.Epoch)
            Assert.Equal([ line ], cart.Lines)
            Assert.Equal([ RecordCartTouch(1L, true) ], resolution.Actions)
        | other -> Assert.Fail $"Expected active, got %A{other}."

    let ``converted cart starts a fresh cart`` () =
        let line = makeCartLine 2uy "FRESH" "Fresh item" 3m 1

        let converted =
            expectCart (CartState.Active { Epoch = 1L; Lines = [ line ] }) (CartConverted "order:abc")

        Assert.Equal(CartState.Converted, converted.Next)
        let restarted = expectCart converted.Next (LineAdded(line, Cart.InitialEpoch))

        match restarted.Next with
        | CartState.Active cart -> Assert.Equal(1L, cart.Epoch)
        | other -> Assert.Fail $"Expected a fresh active cart, got %A{other}."

    let ``order pricing includes shipping and tax`` () =
        let amount = Money.create 10m "USD" |> Result.defaultWith Assert.Fail
        let quantity = Quantity.create 2 |> Result.defaultWith Assert.Fail
        let shipping = Money.create 5m "USD" |> Result.defaultWith Assert.Fail

        let totals =
            OrderPricing.compute [ amount, quantity ] shipping 0.08m
            |> Result.defaultWith Assert.Fail

        Assert.Equal(20m, Money.amount totals.Subtotal)
        Assert.Equal(5m, Money.amount totals.Shipping)
        Assert.Equal(2m, Money.amount totals.Tax)
        Assert.Equal(27m, Money.amount totals.Total)

    let ``order chart enters reservation pending and queues next effects`` () =
        let line = makeCartLine 3uy "ORDER" "Order item" 10m 1

        let orderLine =
            { LineId = OrderLineId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              ProductId = line.ProductId
              Sku = line.Sku
              Name = line.Name
              UnitPrice = line.UnitPrice
              Quantity = line.Quantity
              PriceVersion = PriceVersion.create 1L |> Result.defaultWith Assert.Fail }

        let totals =
            OrderPricing.compute
                [ line.UnitPrice, line.Quantity ]
                (Money.create 5m "USD" |> Result.defaultWith Assert.Fail)
                0.08m
            |> Result.defaultWith Assert.Fail

        let pending =
            { SnapshotId = OrderSnapshotId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              CustomerId = Guid.NewGuid().ToString("D")
              CartId = "customer:cart"
              Lines = [ orderLine ]
              Totals = totals
              Generation = 1L }

        match Chart.resolve Orders.chartValue Orders.initialState (OrderSubmitted pending) with
        | Ok resolution ->
            Assert.Equal(OrderState.ReservationPending pending, resolution.Next)
            Assert.Equal([ ReserveStock pending; NotifyCartConverted pending.CartId ], resolution.Actions)
        | Error chartError -> Assert.Fail $"Expected order submission to resolve, got %A{chartError}."

    let ``order codecs use versioned tags and validate payloads`` () =
        let line = makeCartLine 4uy "CODEC" "Codec item" 10m 1

        let pending =
            { SnapshotId = OrderSnapshotId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              CustomerId = Guid.NewGuid().ToString("D")
              CartId = "customer:test"
              Lines =
                [ { LineId = OrderLineId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
                    ProductId = line.ProductId
                    Sku = line.Sku
                    Name = line.Name
                    UnitPrice = line.UnitPrice
                    Quantity = line.Quantity
                    PriceVersion = PriceVersion.create 1L |> Result.defaultWith Assert.Fail } ]
              Totals =
                OrderPricing.compute
                    [ line.UnitPrice, line.Quantity ]
                    (Money.create 5m "USD" |> Result.defaultWith Assert.Fail)
                    0.08m
                |> Result.defaultWith Assert.Fail
              Generation = 1L }

        let event = OrderSubmitted pending
        let json = OrderCodec.event.Encode event |> Result.defaultWith string
        Assert.Contains("\"tag\":\"order-submitted-v1\"", json)
        Assert.Equal(Ok event, OrderCodec.event.Decode json)
        let state = ReservationPending pending
        Assert.Equal(Ok state, OrderCodec.state.Encode state |> Result.bind OrderCodec.state.Decode)
        Assert.True(Result.isError (OrderCodec.event.Decode "{\"tag\":\"unknown-v1\"}"))
        Assert.True(Result.isError (OrderCodec.event.Decode "{\"tag\":\"order-submitted-v1\"}"))

    let private makeAuthorizationAttempt () : AuthorizationAttempt =
        { OperationId =
            PaymentOperationId.create $"authorize:v1:test:{Guid.NewGuid():N}"
            |> Result.defaultWith Assert.Fail
          OrderId = $"order:{Guid.NewGuid():D}"
          Amount = Money.create 27m "USD" |> Result.defaultWith Assert.Fail
          Method = PaymentMethodReference.Sandbox.Success }

    let private expectOrderResolution state event =
        match Chart.resolve Orders.chartValue state event with
        | Ok resolution -> resolution
        | Error error -> Assert.Fail $"Expected the order event to resolve, got %A{error}."

    let private expectOrderAbsorbed (state: OrderState) event =
        let resolution = expectOrderResolution state event
        Assert.Equal(state, resolution.Next)
        Assert.Empty(resolution.Actions)

    let private expectPaymentResolution state event =
        match Chart.resolve Payments.chartValue state event with
        | Ok resolution -> resolution
        | Error error -> Assert.Fail $"Expected the payment event to resolve, got %A{error}."

    let private reservedOrder () =
        let line =
            { LineId = OrderLineId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              ProductId = ProductId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              Sku = Sku.create "PAY-ITEM" |> Result.defaultWith Assert.Fail
              Name = NonEmptyString.create 200 "Payment item" |> Result.defaultWith Assert.Fail
              UnitPrice = Money.create 25m "USD" |> Result.defaultWith Assert.Fail
              Quantity = Quantity.create 1 |> Result.defaultWith Assert.Fail
              PriceVersion = PriceVersion.create 1L |> Result.defaultWith Assert.Fail }

        let totals =
            OrderPricing.compute
                [ line.UnitPrice, line.Quantity ]
                (Money.create 5m "USD" |> Result.defaultWith Assert.Fail)
                0.08m
            |> Result.defaultWith Assert.Fail

        let pending =
            { SnapshotId = OrderSnapshotId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              CustomerId = Guid.NewGuid().ToString("D")
              CartId = "customer:pay"
              Lines = [ line ]
              Totals = totals
              Generation = 1L }

        { Pending = pending
          ReservationIds = [ ReservationId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail ] }

    let ``reserved orders wait for the pay step without queueing authorization`` () =
        let reserved = reservedOrder ()

        let resolution =
            expectOrderResolution (ReservationPending reserved.Pending) (StockReserved(reserved.ReservationIds, 1L))

        Assert.Equal(AwaitingAuthorization reserved, resolution.Next)
        Assert.Empty(resolution.Actions)

    let ``pay step queues the authorization handoff`` () =
        let reserved = reservedOrder ()

        let attempt =
            PaymentOperationId.create "authorize:v1:pay" |> Result.defaultWith Assert.Fail

        let resolution =
            expectOrderResolution
                (AwaitingAuthorization reserved)
                (AuthorizePaymentRequested(PaymentMethodReference.Sandbox.Success, attempt))

        Assert.Equal(
            PaymentPending
                { Reserved = reserved
                  Attempt = attempt },
            resolution.Next
        )

        Assert.Equal(
            [ RequestAuthorization(PaymentMethodReference.Sandbox.Success, attempt, reserved.Pending.Totals.Total) ],
            resolution.Actions
        )

    let ``payment authorization commits stock and decline returns to the pay step`` () =
        let reserved = reservedOrder ()

        let attempt =
            PaymentOperationId.create "authorize:v1:commit"
            |> Result.defaultWith Assert.Fail

        let pending =
            PaymentPending
                { Reserved = reserved
                  Attempt = attempt }

        let authorized =
            expectOrderResolution pending (PaymentAuthorized(attempt, "sim-abc123"))

        Assert.Equal(
            StockCommitPending
                { Reserved = reserved
                  ProviderReference = "sim-abc123" },
            authorized.Next
        )

        Assert.Equal([ CommitStock reserved.ReservationIds ], authorized.Actions)

        let declined =
            expectOrderResolution pending (PaymentDeclined(attempt, (ReasonCode.ofLiteral "do-not-honor")))

        Assert.Equal(AwaitingAuthorization reserved, declined.Next)
        Assert.Empty(declined.Actions)

    let ``stale payment attempt results are absorbed`` () =
        let reserved = reservedOrder ()

        let pending =
            PaymentPending
                { Reserved = reserved
                  Attempt = PaymentOperationId.create "authorize:v1:one" |> Result.defaultWith Assert.Fail }

        let stale =
            PaymentOperationId.create "authorize:v1:two" |> Result.defaultWith Assert.Fail

        expectOrderAbsorbed pending (PaymentAuthorized(stale, "sim-stale"))
        expectOrderAbsorbed pending (PaymentDeclined(stale, (ReasonCode.ofLiteral "do-not-honor")))

    let ``stock commitment places the order`` () =
        let reserved = reservedOrder ()

        let resolution =
            expectOrderResolution
                (StockCommitPending
                    { Reserved = reserved
                      ProviderReference = "sim-abc123" })
                StockCommitted

        Assert.Equal(
            Placed
                { Reserved = reserved
                  ProviderReference = "sim-abc123" },
            resolution.Next
        )

        Assert.Equal([ RequestInvoice reserved.Pending.SnapshotId ], resolution.Actions)

    let ``cancellation waits for both reservations and payment to settle`` () =
        let reserved = reservedOrder ()

        let attempt =
            PaymentOperationId.create "authorize:v1:cancel"
            |> Result.defaultWith Assert.Fail

        let pending =
            PaymentPending
                { Reserved = reserved
                  Attempt = attempt }

        let cancelling = expectOrderResolution pending CancelRequested

        Assert.Equal(
            [ ReleaseReservations reserved.ReservationIds
              RequestPaymentCancellation(ReasonCode.ofLiteral "customer-cancelled") ],
            cancelling.Actions
        )

        match cancelling.Next with
        | CancellationPending order -> Assert.False(order.PaymentSettled)
        | other -> Assert.Fail $"Expected cancellation pending, got %A{other}."

        let paymentDone = expectOrderResolution cancelling.Next PaymentSettled

        match paymentDone.Next with
        | CancellationPending afterPayment ->
            Assert.True(afterPayment.PaymentSettled)
            Assert.False(afterPayment.ReservationsSettled)
        | other -> Assert.Fail $"Expected cancellation pending, got %A{other}."

        let finished = expectOrderResolution paymentDone.Next ReservationsReleased
        Assert.Equal(Cancelled, finished.Next)

    let ``late payment results during cancellation are absorbed`` () =
        let reserved = reservedOrder ()

        let attempt =
            PaymentOperationId.create "authorize:v1:late" |> Result.defaultWith Assert.Fail

        let cancelling =
            (expectOrderResolution
                (PaymentPending
                    { Reserved = reserved
                      Attempt = attempt })
                CancelRequested)
                .Next

        expectOrderAbsorbed cancelling (PaymentAuthorized(attempt, "sim-late"))
        expectOrderAbsorbed cancelling (PaymentDeclined(attempt, (ReasonCode.ofLiteral "do-not-honor")))

    let ``payment chart authorizes notifies and voids`` () =
        let attempt = makeAuthorizationAttempt ()
        let expiry = DateTimeOffset.UtcNow.AddDays 6.

        let started =
            expectPaymentResolution Payments.initialState (AuthorizeRequested attempt)

        match started.Next with
        | AuthorizationPending pending ->
            Assert.Equal(attempt, pending.Attempt)
            Assert.False(pending.CancelRequested)
        | other -> Assert.Fail $"Expected authorization pending, got %A{other}."

        Assert.Equal([ CallGatewayAuthorize attempt ], started.Actions)

        let authorized =
            expectPaymentResolution started.Next (AuthorizationSucceeded(attempt, "sim-abc123", expiry))

        Assert.Equal(
            Authorized
                { Attempt = attempt
                  ProviderReference = "sim-abc123"
                  ExpiresAt = expiry },
            authorized.Next
        )

        (match authorized.Next with
         | Authorized payment -> Assert.Equal([ NotifyOrderAuthorized payment ], authorized.Actions)
         | _ -> Assert.Fail "expected authorized")

        let voiding =
            expectPaymentResolution
                authorized.Next
                (PaymentCancellationRequested(attempt.OrderId, (ReasonCode.ofLiteral "customer-cancelled")))

        Assert.Equal(
            VoidPending
                { Attempt = attempt
                  ProviderReference = "sim-abc123"
                  ExpiresAt = expiry },
            voiding.Next
        )

        let voided =
            expectPaymentResolution
                voiding.Next
                (VoidSucceeded
                    { Attempt = attempt
                      ProviderReference = "sim-abc123"
                      ExpiresAt = expiry })

        Assert.Equal(
            Voided
                { Attempt = attempt
                  ProviderReference = "sim-abc123"
                  ExpiresAt = expiry },
            voided.Next
        )

    let ``cancel during a pending authorization unwinds instead of notifying`` () =
        let attempt = makeAuthorizationAttempt ()

        let started =
            expectPaymentResolution Payments.initialState (AuthorizeRequested attempt)

        let cancelling =
            expectPaymentResolution
                started.Next
                (PaymentCancellationRequested(attempt.OrderId, (ReasonCode.ofLiteral "customer-cancelled")))

        match cancelling.Next with
        | AuthorizationPending pending -> Assert.True(pending.CancelRequested)
        | other -> Assert.Fail $"Expected a cancel-flagged pending authorization, got %A{other}."

        Assert.Empty(cancelling.Actions)

        let expiry = DateTimeOffset.UtcNow.AddDays 6.

        let authorized =
            expectPaymentResolution cancelling.Next (AuthorizationSucceeded(attempt, "sim-abc123", expiry))

        Assert.Equal(
            VoidPending
                { Attempt = attempt
                  ProviderReference = "sim-abc123"
                  ExpiresAt = expiry },
            authorized.Next
        )

        (match authorized.Next with
         | VoidPending payment -> Assert.Equal([ CallGatewayVoid payment ], authorized.Actions)
         | _ -> Assert.Fail "expected void pending")

        let declined =
            expectPaymentResolution
                cancelling.Next
                (AuthorizationDeclined(attempt, (ReasonCode.ofLiteral "do-not-honor")))

        Assert.Equal(CancelledWithoutCharge, declined.Next)
        Assert.Equal([ NotifyOrderCancelled attempt.OrderId ], declined.Actions)

    let ``unknown outcomes park and a cancel triggers a gateway query`` () =
        let attempt = makeAuthorizationAttempt ()

        let started =
            expectPaymentResolution Payments.initialState (AuthorizeRequested attempt)

        let parked =
            expectPaymentResolution started.Next (AuthorizationOutcomeUnknown attempt)

        Assert.Equal(
            AuthorizationUnknown
                { Attempt = attempt
                  CancelRequested = false },
            parked.Next
        )

        Assert.Empty(parked.Actions)

        let cancelling =
            expectPaymentResolution
                parked.Next
                (PaymentCancellationRequested(attempt.OrderId, (ReasonCode.ofLiteral "customer-cancelled")))

        Assert.Equal(
            AuthorizationUnknown
                { Attempt = attempt
                  CancelRequested = true },
            cancelling.Next
        )

        Assert.Equal([ QueryGatewayAuthorization attempt ], cancelling.Actions)

    let ``cancel before any authorization request closes without charge`` () =
        let orderId = $"order:{Guid.NewGuid():D}"

        let closed =
            expectPaymentResolution
                Payments.initialState
                (PaymentCancellationRequested(orderId, (ReasonCode.ofLiteral "customer-cancelled")))

        Assert.Equal(CancelledWithoutCharge, closed.Next)
        Assert.Equal([ NotifyOrderCancelled orderId ], closed.Actions)

    let ``declined payments accept a fresh authorization attempt`` () =
        let attempt = makeAuthorizationAttempt ()

        let started =
            expectPaymentResolution Payments.initialState (AuthorizeRequested attempt)

        let declined =
            expectPaymentResolution started.Next (AuthorizationDeclined(attempt, (ReasonCode.ofLiteral "do-not-honor")))

        Assert.Equal(Declined(ReasonCode.ofLiteral "do-not-honor"), declined.Next)

        let retried =
            expectPaymentResolution
                declined.Next
                (AuthorizeRequested
                    { attempt with
                        OperationId = PaymentOperationId.create "authorize:v1:retry" |> Result.defaultWith Assert.Fail })

        match retried.Next with
        | AuthorizationPending pending ->
            Assert.Equal("authorize:v1:retry", PaymentOperationId.value pending.Attempt.OperationId)
        | other -> Assert.Fail $"Expected a retried authorization, got %A{other}."

    let ``payment codecs use versioned tags and validate payloads`` () =
        let attempt = makeAuthorizationAttempt ()

        let request = AuthorizeRequested attempt
        let json = PaymentCodec.event.Encode request |> Result.defaultWith string
        Assert.Contains("\"tag\":\"authorize-requested-v1\"", json)
        Assert.Equal(Ok request, PaymentCodec.event.Decode json)

        let authorized =
            Authorized
                { Attempt = attempt
                  ProviderReference = "sim-abc123"
                  ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds 1800000000000L }

        Assert.Equal(Ok authorized, PaymentCodec.state.Encode authorized |> Result.bind PaymentCodec.state.Decode)

        let cancel =
            PaymentCancellationRequested(attempt.OrderId, (ReasonCode.ofLiteral "customer-cancelled"))

        Assert.Equal(Ok cancel, PaymentCodec.event.Encode cancel |> Result.bind PaymentCodec.event.Decode)

        let notify = NotifyOrderCancelled attempt.OrderId
        Assert.Equal(Ok notify, PaymentCodec.action.Encode notify |> Result.bind PaymentCodec.action.Decode)

        Assert.True(Result.isError (PaymentCodec.event.Decode "{\"tag\":\"authorize-requested-v1\"}"))

    let ``payment payloads never contain cardholder fields`` () =
        let attempt = makeAuthorizationAttempt ()
        let expiry = DateTimeOffset.UtcNow.AddDays 6.

        let authorized =
            { Attempt = attempt
              ProviderReference = "sim-abc123"
              ExpiresAt = expiry }

        let events =
            [ AuthorizeRequested attempt
              AuthorizationSucceeded(attempt, "sim-abc123", expiry)
              AuthorizationDeclined(attempt, (ReasonCode.ofLiteral "do-not-honor"))
              AuthorizationOutcomeUnknown attempt
              PaymentCancellationRequested(attempt.OrderId, (ReasonCode.ofLiteral "customer-cancelled"))
              VoidSucceeded authorized
              VoidOutcomeUnknown authorized
              MarkManualReview(ReasonCode.ofLiteral "reconciliation-exhausted") ]

        let actions =
            [ CallGatewayAuthorize attempt
              QueryGatewayAuthorization attempt
              CallGatewayVoid authorized
              NotifyOrderAuthorized authorized
              NotifyOrderDeclined(attempt, (ReasonCode.ofLiteral "do-not-honor"))
              NotifyOrderCancelled attempt.OrderId
              NotifyOrderVoided authorized ]

        let forbidden = [ "cardNumber"; "cvv"; "cvc"; "pan"; "card"; "secret"; "password" ]

        let payloads =
            (events |> List.map PaymentCodec.event.Encode)
            @ (actions |> List.map PaymentCodec.action.Encode)

        for payload in payloads do
            match payload with
            | Error _ -> Assert.Fail "expected every payment payload to encode"
            | Ok json ->
                let lowered = json.ToLowerInvariant()

                for field in forbidden do
                    Assert.DoesNotContain(field, lowered)

        Assert.True(Result.isError (PaymentMethodReference.create "4242424242424242"))
        Assert.True(Result.isError (PaymentMethodReference.create "4242-4242-4242-4242"))
        Assert.True(Result.isError (PaymentMethodReference.create "123"))
        Assert.True(Result.isError (PaymentMethodReference.create "vault-token"))
        Assert.True(Result.isError (PaymentOperationId.create "authorize v1"))

        let maximumOperation = PaymentOperationId.create (String.replicate 123 "a")
        Assert.True(Result.isOk maximumOperation)

        maximumOperation
        |> Result.map PaymentOperationId.voidOf
        |> Result.iter (PaymentOperationId.value >> fun value -> Assert.Equal(128, value.Length))

    let ``cart add clamps quantity to maximum`` () =
        let line = makeCartLine 1uy "COFFEE" "Coffee" 24.90m 5

        let nearlyFull =
            { line with
                Quantity = Quantity.create 998 |> Result.defaultWith Assert.Fail }

        let active = CartState.Active { Epoch = 1L; Lines = [ nearlyFull ] }
        let resolution = expectCart active (LineAdded(line, 1L))

        match resolution.Next with
        | CartState.Active cart ->
            let total = cart.Lines |> List.sumBy (fun line -> Quantity.value line.Quantity)
            Assert.Equal(Quantity.maxValue, total)
        | other -> Assert.Fail $"Expected active, got %A{other}."

    let ``stale epoch mutations are rejected`` () =
        let line = makeCartLine 1uy "COFFEE" "Coffee" 24.90m 1
        let active = CartState.Active { Epoch = 1L; Lines = [ line ] }
        expectCartRejected active (LineAdded(line, 5L))
        expectCartRejected active (Cleared 0L)
        expectCartRejected active (LineRemoved(cartProduct 1uy, 3L))

    let ``abandonment timer fires only for the current generation`` () =
        let line = makeCartLine 1uy "COFFEE" "Coffee" 24.90m 1
        let active = CartState.Active { Epoch = 2L; Lines = [ line ] }

        let stale = expectCart active (AbandonmentTimerFired(1L, DateTimeOffset.UtcNow))
        Assert.Equal(active, stale.Next)
        Assert.Empty stale.Actions

        let current = expectCart active (AbandonmentTimerFired(2L, DateTimeOffset.UtcNow))

        match current.Next with
        | CartState.Abandoned cart -> Assert.Equal(2L, cart.Epoch)
        | other -> Assert.Fail $"Expected abandoned, got %A{other}."

    let ``cart merge unions lines and clamps quantities`` () =
        let coffee = makeCartLine 1uy "COFFEE" "Coffee" 24.90m 3
        let mug = makeCartLine 2uy "MUG" "Mug" 12.00m 1
        let report = Cart.merge [ coffee ] [ coffee; mug ]
        Assert.Equal(2, report.Result.Length)
        Assert.Equal(1, report.Added)
        Assert.Equal(1, report.Updated)

        let coffeeQuantity =
            report.Result
            |> List.find (fun line -> line.ProductId = coffee.ProductId)
            |> fun line -> Quantity.value line.Quantity

        Assert.Equal(6, coffeeQuantity)

    let ``apply merge fills an empty customer cart`` () =
        let coffee = makeCartLine 1uy "COFFEE" "Coffee" 24.90m 2
        let mergeId = Guid.NewGuid()

        let resolution =
            expectCart CartState.Empty (ApplyMerge(mergeId, "guest:x", [ coffee ]))

        match resolution.Next with
        | CartState.Active cart ->
            Assert.Equal(1L, cart.Epoch)
            Assert.Equal([ coffee ], cart.Lines)

            Assert.True(
                resolution.Actions
                |> List.exists (fun action -> action = NotifyMergeApplied(mergeId, "guest:x"))
            )
        | other -> Assert.Fail $"Expected active, got %A{other}."

    let ``money wire round trips exact amount and currency`` () =
        let money = Money.create 24.90m "USD" |> Result.defaultWith Assert.Fail
        Assert.Equal("24.90 USD", Money.format money)
        Assert.Equal(Ok money, Money.tryOfWire "24.90" "USD")
        Assert.True(Result.isError (Money.create 1m "NOT-A-CURRENCY"))

    let ``capability locate digest is purpose scoped and entity free`` () =
        let key =
            CapabilityHashKey.create "primary" (Array.init 32 byte)
            |> Result.defaultWith Assert.Fail

        let purpose =
            CapabilityPurpose.create "guest-cart" |> Result.defaultWith Assert.Fail

        let otherPurpose =
            CapabilityPurpose.create "order-tracking" |> Result.defaultWith Assert.Fail

        let raw = Capability.generate ()
        let located = Capability.locateDigest key purpose raw
        Assert.Equal(32, located.Length)
        Assert.Equal(located, Capability.locateDigest key purpose raw)
        Assert.NotEqual(located, Capability.locateDigest key otherPurpose raw)

    let ``cart codecs cover every case without secret fields`` () =
        let canary = "CANARY-bearer-token-42"
        let line = makeCartLine 1uy "COFFEE" "Coffee" 24.90m 2
        let quantity = Quantity.create 2 |> Result.defaultWith Assert.Fail
        let mergeId = Guid.NewGuid()

        let states =
            [ CartState.Empty
              CartState.Active { Epoch = 1L; Lines = [ line ] }
              CartState.MergeFrozen
                  { Epoch = 1L
                    Lines = [ line ]
                    MergeId = mergeId
                    Target = "customer:x"
                    Submitted = false }
              CartState.MergedInto
                  { MergeId = mergeId
                    Target = "customer:x" }
              CartState.Converted
              CartState.Abandoned { Epoch = 1L; Lines = [ line ] } ]

        let events =
            [ LineAdded(line, 0L)
              QuantityChanged(cartProduct 1uy, quantity, 1L)
              LineRemoved(cartProduct 1uy, 1L)
              Cleared 1L
              MergeRequested(mergeId, "customer:x")
              MergeSnapshotCaptured mergeId
              MergeApplied mergeId
              MergeFailed mergeId
              ApplyMerge(mergeId, "guest:x", [ line ])
              AbandonmentTimerFired(1L, DateTimeOffset.UtcNow)
              CartConverted "order:x" ]

        let actions =
            [ RecordCartTouch(1L, true)
              CaptureMergeSnapshot(mergeId, "customer:x", [ line ])
              SubmitMergeSnapshot(mergeId, "customer:x", [ line ])
              NotifyMergeApplied(mergeId, "guest:x")
              RevokeGuestCapability mergeId ]

        let errors =
            [ CartActionError.InvalidCartEntityId
              CartActionError.CapabilityLookupFailed
              CartActionError.MergeSnapshotAlreadyCaptured
              CartActionError.MergeTargetNotFound
              CartActionError.CallbackEncodingFailed
              CartActionError.ActionReceiptMismatch ]

        Assert.Equal(unionCaseCount<CartState> (), states.Length)
        Assert.Equal(unionCaseCount<CartEvent> (), events.Length)
        Assert.Equal(unionCaseCount<CartAction> (), actions.Length)
        Assert.Equal(unionCaseCount<CartActionError> (), errors.Length)

        states
        |> List.iter (fun value ->
            CartCodec.state.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

        events
        |> List.iter (fun value ->
            CartCodec.event.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

        actions
        |> List.iter (fun value ->
            CartCodec.action.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

        errors
        |> List.iter (fun value ->
            CartCodec.error.Encode value
            |> Result.defaultWith string
            |> assertSafeJson canary)

    let ``cart codecs round trip an active cart`` () =
        let line = makeCartLine 1uy "COFFEE" "Coffee" 24.90m 2
        let state = CartState.Active { Epoch = 1L; Lines = [ line ] }

        match CartCodec.state.Encode state with
        | Error error -> Assert.Fail $"State failed to encode: %A{error}."
        | Ok json ->
            Assert.Contains("\"tag\":\"active-v1\"", json)
            Assert.Contains("\"unitPriceAmount\":\"24.9\"", json)
            Assert.Contains("\"unitPriceCurrency\":\"USD\"", json)

            match CartCodec.state.Decode json with
            | Ok decoded -> Assert.Equal(state, decoded)
            | Error error -> Assert.Fail $"State failed to decode: %A{error}."

        match CartCodec.state.Decode "{\"tag\":\"active-v1\",\"epoch\":1,\"lines\":[{\"productId\":\"x\"}]}" with
        | Error _ -> ()
        | Ok value -> Assert.Fail $"A malformed active cart decoded as %A{value}."

    let ``capture allocation distributes charges and consumes the remainder`` () =
        let lineId = OrderLineId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
        let price = Money.create 10m "USD" |> Result.defaultWith Assert.Fail
        let shipping = Money.create 5m "USD" |> Result.defaultWith Assert.Fail
        let tax = Money.create 0.80m "USD" |> Result.defaultWith Assert.Fail
        let zero = Money.create 0m "USD" |> Result.defaultWith Assert.Fail
        let authorized = Money.create 25.80m "USD" |> Result.defaultWith Assert.Fail

        let shipmentAllocation (suffix: byte) quantity =
            { AllocationId =
                ShipmentAllocationId.create (Guid.Parse $"00000000-0000-0000-0000-0000000000{suffix:X2}")
                |> Result.defaultWith Assert.Fail
              Lines = [ { LineId = lineId; Quantity = quantity } ] }

        let request =
            { OrderLines =
                [ { LineId = lineId
                    UnitPrice = price
                    Quantity = 2 } ]
              Shipments = [ shipmentAllocation 1uy 1; shipmentAllocation 2uy 1 ]
              Shipping = shipping
              Tax = tax
              AuthorizedAmount = authorized }

        match CaptureAllocation.allocate request with
        | Error error -> Assert.Fail $"Expected allocation to succeed, got %A{error}."
        | Ok allocations ->
            Assert.Equal(2, allocations.Length)

            let total =
                allocations
                |> List.fold (fun acc allocation -> Money.add acc allocation.Total) zero

            let allocatedShipping =
                allocations
                |> List.fold (fun acc allocation -> Money.add acc allocation.Shipping) zero

            let allocatedTax =
                allocations
                |> List.fold (fun acc allocation -> Money.add acc allocation.Tax) zero

            Assert.Equal(authorized, total)
            Assert.Equal(shipping, allocatedShipping)
            Assert.Equal(tax, allocatedTax)

    let private makeShipmentRequest () =
        { ShipmentId = ShipmentId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
          AllocationId = ShipmentAllocationId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
          OrderId = "order:ship"
          Lines =
            [ { LineId = OrderLineId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
                Quantity = 1 } ] }

    let private expectShipmentResolution state event =
        match Chart.resolve Shipments.chartValue state event with
        | Ok resolution -> resolution
        | Error error -> Assert.Fail $"Expected shipment event to resolve, got %A{error}."

    let ``shipment chart confirms allocation creates label and dispatches`` () =
        let request = makeShipmentRequest ()

        let started =
            expectShipmentResolution Shipments.initialState (ShipmentRequested request)

        Assert.Equal(AllocationPending(Some request), started.Next)
        Assert.Equal([ ConfirmAllocation request.AllocationId ], started.Actions)

        let confirmed =
            expectShipmentResolution started.Next (AllocationConfirmed request.AllocationId)

        match confirmed.Next with
        | Preparing preparing -> Assert.Equal(request, preparing.Request)
        | other -> Assert.Fail $"Expected preparing, got %A{other}."

        let prepared = expectShipmentResolution confirmed.Next PreparationCompleted
        Assert.Equal([ CreateCarrierLabel(request.ShipmentId, 1L) ], prepared.Actions)

        let carrierReference =
            CarrierReference.create "sim-abc123" |> Result.defaultWith Assert.Fail

        let labelled =
            expectShipmentResolution prepared.Next (LabelCreated(1L, carrierReference))

        match labelled.Next with
        | ReadyToDispatch ready -> Assert.Equal(carrierReference, ready.CarrierReference)
        | other -> Assert.Fail $"Expected ready-to-dispatch, got %A{other}."

        let dispatched =
            expectShipmentResolution labelled.Next (DispatchConfirmed DateTimeOffset.UtcNow)

        match dispatched.Next with
        | InTransit transit -> Assert.Equal(1L, transit.TrackingGeneration)
        | other -> Assert.Fail $"Expected in-transit, got %A{other}."

        match dispatched.Actions with
        | [ NotifyOrderDispatched(orderId, shipmentId, allocationId, dispatchedAt)
            RecordTrackingCheckpoint(checkedId, 1L, None, observedAt) ] ->
            Assert.Equal(request.OrderId, orderId)
            Assert.Equal(request.ShipmentId, shipmentId)
            Assert.Equal(request.AllocationId, allocationId)
            Assert.Equal(request.ShipmentId, checkedId)
            Assert.Equal(dispatchedAt, observedAt)
        | other -> Assert.Fail $"Expected dispatch notification and tracking checkpoint, got %A{other}."

        let trackingId = TrackingEventId.create "scan-1" |> Result.defaultWith Assert.Fail

        let delivered =
            expectShipmentResolution
                dispatched.Next
                (CarrierTrackingReceived
                    { EventId = trackingId
                      Generation = 1L
                      OccurredAt = DateTimeOffset.UtcNow.AddMinutes 1.
                      Status = CarrierTrackingStatus.Delivered })

        match delivered.Next with
        | ShipmentState.Delivered _ -> ()
        | other -> Assert.Fail $"Expected delivered, got %A{other}."

        match delivered.Actions with
        | [ NotifyOrderDelivered(orderId, _, _, _); StopTrackingCheck stopped ] ->
            Assert.Equal(request.OrderId, orderId)
            Assert.Equal(request.ShipmentId, stopped)
        | other -> Assert.Fail $"Expected delivery notification and stopped tracking, got %A{other}."

        let scanAt =
            DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

        let scanned =
            expectShipmentResolution
                dispatched.Next
                (CarrierTrackingReceived
                    { EventId = TrackingEventId.create "scan-2" |> Result.defaultWith Assert.Fail
                      Generation = 1L
                      OccurredAt = scanAt
                      Status = CarrierTrackingStatus.InTransit })

        Assert.Equal([ RecordTrackingCheckpoint(request.ShipmentId, 1L, Some scanAt, scanAt) ], scanned.Actions)

        // A lost check computed before the newer scan is stale and absorbed.
        let stale =
            expectShipmentResolution scanned.Next (MarkLost(1L, None, DateTimeOffset.UtcNow))

        Assert.Equal(scanned.Next, stale.Next)

        let lost =
            expectShipmentResolution scanned.Next (MarkLost(1L, Some scanAt, DateTimeOffset.UtcNow))

        match lost.Next with
        | ShipmentState.Lost _ -> ()
        | other -> Assert.Fail $"Expected lost, got %A{other}."

        for action in
            [ RecordTrackingCheckpoint(request.ShipmentId, 2L, Some scanAt, scanAt)
              RecordTrackingCheckpoint(request.ShipmentId, 1L, None, scanAt)
              StopTrackingCheck request.ShipmentId ] do
            let json =
                ShipmentCodec.action.Encode action
                |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")

            Assert.Equal(
                action,
                ShipmentCodec.action.Decode json
                |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")
            )

    let ``order fulfilment requests capture on dispatch and completes on delivery`` () =
        let reserved = reservedOrder ()
        let line = reserved.Pending.Lines.Head

        let shipmentId =
            ShipmentId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail

        let allocationId =
            ShipmentAllocationId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail

        let captureId = CaptureId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail

        let operationId =
            PaymentOperationId.create "capture:v1:op" |> Result.defaultWith Assert.Fail

        let totals = reserved.Pending.Totals

        let allocation =
            { AllocationId = allocationId
              Lines =
                [ { LineId = line.LineId
                    Quantity = Quantity.value line.Quantity } ]
              Merchandise = totals.Subtotal
              Shipping = totals.Shipping
              Tax = totals.Tax
              Total = totals.Total }

        let plan =
            { ShipmentId = shipmentId
              CaptureId = captureId
              PaymentOperationId = operationId
              Allocation = allocation }

        let placed =
            Placed
                { Reserved = reserved
                  ProviderReference = "sim-abc123" }

        let requested = expectOrderResolution placed (FulfilmentRequested [ plan ])

        match requested.Next with
        | FulfilmentPending fulfilment -> Assert.Equal(1, fulfilment.Shipments.Length)
        | other -> Assert.Fail $"Expected fulfilment pending, got %A{other}."

        Assert.Equal([ CreateShipment(plan, reserved.Pending.SnapshotId) ], requested.Actions)

        let created =
            expectOrderResolution requested.Next (ShipmentCreated(shipmentId, allocationId))

        match created.Next with
        | Processing fulfilment -> Assert.True(fulfilment.Shipments.Head.Created)
        | other -> Assert.Fail $"Expected processing, got %A{other}."

        let dispatched =
            expectOrderResolution created.Next (ShipmentDispatched(shipmentId, allocationId))

        Assert.Equal(
            [ RequestCapture(shipmentId, allocation, captureId, operationId, "sim-abc123") ],
            dispatched.Actions
        )

        match dispatched.Next with
        | Shipped _ -> ()
        | other -> Assert.Fail $"Expected shipped, got %A{other}."

        let captured =
            expectOrderResolution dispatched.Next (PaymentCaptured(captureId, operationId))

        match captured.Next with
        | Shipped fulfilment -> Assert.True(fulfilment.Shipments.Head.CaptureSucceeded)
        | other -> Assert.Fail $"Expected shipped, got %A{other}."

        let delivered =
            expectOrderResolution captured.Next (ShipmentDelivered(shipmentId, allocationId))

        match delivered.Next with
        | OrderState.Delivered _ -> ()
        | other -> Assert.Fail $"Expected delivered, got %A{other}."

    let ``payment captures respect the authorized amount`` () =
        let attempt = makeAuthorizationAttempt ()
        let expiry = DateTimeOffset.UtcNow.AddDays 6.

        let authorized =
            { Attempt = attempt
              ProviderReference = "sim-abc123"
              ExpiresAt = expiry }

        let captureRequest (suffix: string) amount =
            { OrderId = attempt.OrderId
              CaptureId = CaptureId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              OperationId =
                PaymentOperationId.create $"capture:v1:{suffix}"
                |> Result.defaultWith Assert.Fail
              Amount = Money.create amount "USD" |> Result.defaultWith Assert.Fail }

        let first = captureRequest "one" 10m

        let started =
            expectPaymentResolution (Authorized authorized) (CaptureRequested first)

        match started.Next with
        | CapturePending pending -> Assert.Equal(first, pending.Request)
        | other -> Assert.Fail $"Expected capture pending, got %A{other}."

        Assert.Equal([ CallGatewayCapture(authorized, first) ], started.Actions)

        let succeeded =
            expectPaymentResolution started.Next (CaptureSucceeded(first, "sim-cap1"))

        match succeeded.Next with
        | PartiallyCaptured payment -> Assert.Equal(1, payment.Captures.Length)
        | other -> Assert.Fail $"Expected partially captured, got %A{other}."

        Assert.Equal(
            [ NotifyOrderCaptured
                  { Request = first
                    ProviderReference = "sim-cap1" } ],
            succeeded.Actions
        )

        let second = captureRequest "two" 17m
        let secondPending = expectPaymentResolution succeeded.Next (CaptureRequested second)

        match secondPending.Next with
        | CapturePending pending -> Assert.Equal(second, pending.Request)
        | other -> Assert.Fail $"Expected capture pending, got %A{other}."

        let fully =
            expectPaymentResolution secondPending.Next (CaptureSucceeded(second, "sim-cap2"))

        match fully.Next with
        | Captured payment -> Assert.Equal(2, payment.Captures.Length)
        | other -> Assert.Fail $"Expected captured, got %A{other}."

        let excess = captureRequest "excess" 18m

        match Chart.resolve Payments.chartValue fully.Next (CaptureRequested excess) with
        | Ok _ -> Assert.Fail "Expected an excess capture to be rejected."
        | Error _ -> ()

    let private makeRefundRequest amount =
        let id = Guid.NewGuid()

        { RefundId = RefundId.create id |> Result.defaultWith Assert.Fail
          AllocationId = RefundAllocationId.create id |> Result.defaultWith Assert.Fail
          OperationId = PaymentOperationId.create $"refund:v1:{id:N}" |> Result.defaultWith Assert.Fail
          Origin = OrderCancellation $"order:{Guid.NewGuid():D}"
          Amount = Money.create amount "USD" |> Result.defaultWith Assert.Fail }

    let ``refund balance reserves pending allocations`` () =
        let captured = Money.create 27m "USD" |> Result.defaultWith Assert.Fail

        let balance =
            { Captured = captured
              Refunded = Money.create 0m "USD" |> Result.defaultWith Assert.Fail
              Pending = Money.create 0m "USD" |> Result.defaultWith Assert.Fail }

        let reserved =
            RefundAllocation.reserve balance (Money.create 10m "USD" |> Result.defaultWith Assert.Fail)
            |> Result.defaultWith Assert.Fail

        Assert.Equal(10m, Money.amount reserved.Pending)

        match RefundAllocation.reserve reserved (Money.create 18m "USD" |> Result.defaultWith Assert.Fail) with
        | Ok _ -> Assert.Fail "Expected an over-balance reservation to fail."
        | Error _ -> ()

    let private expectRefundResolution state event =
        match Chart.resolve App.Refunds.Refunds.chartValue state event with
        | Ok resolution -> resolution
        | Error error -> Assert.Fail $"Expected refund event to resolve, got %A{error}."

    let ``refund chart requests allocation and settles`` () =
        let request = makeRefundRequest 10m

        let started =
            expectRefundResolution App.Refunds.Refunds.initialState (App.Refunds.RefundEvent.RefundRequested request)

        Assert.Equal(App.Refunds.RefundState.AllocationPending request, started.Next)
        Assert.Equal([ App.Refunds.RefundAction.RequestAllocation request ], started.Actions)

        let approved =
            { Request = request
              PaymentReference = "sim-abc123" }

        let gatewaying =
            expectRefundResolution started.Next (App.Refunds.RefundEvent.AllocationApproved approved)

        Assert.Equal(App.Refunds.RefundState.PendingGateway approved, gatewaying.Next)
        Assert.Equal([ App.Refunds.RefundAction.CallGatewayRefund approved ], gatewaying.Actions)

        let settling =
            expectRefundResolution gatewaying.Next (App.Refunds.RefundEvent.GatewayRefunded(approved, "sim-ref1"))

        match settling.Next with
        | App.Refunds.RefundState.SettlementPending(_, reference) -> Assert.Equal("sim-ref1", reference)
        | other -> Assert.Fail $"Expected settlement pending, got %A{other}."

        let doneState =
            expectRefundResolution settling.Next (App.Refunds.RefundEvent.AllocationSettled request.AllocationId)

        Assert.Equal(App.Refunds.RefundState.Succeeded request, doneState.Next)
        Assert.Equal([ App.Refunds.RefundAction.NotifyOriginSucceeded request ], doneState.Actions)

    let private makeReturnRequest () =
        let id = Guid.NewGuid()
        let lineId = OrderLineId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail

        { ReturnId = ReturnId.create id |> Result.defaultWith Assert.Fail
          AuthorizationId = ReturnAuthorizationId.create id |> Result.defaultWith Assert.Fail
          OrderId = $"order:{Guid.NewGuid():D}"
          Currency = "USD"
          WindowEndsAt = DateTimeOffset.UtcNow.AddDays 10.
          Lines =
            [ { OrderLineId = lineId
                Quantity = 1
                Merchandise = Money.create 9m "USD" |> Result.defaultWith Assert.Fail
                Tax = Money.create 1m "USD" |> Result.defaultWith Assert.Fail } ] }

    let private expectReturnResolution state event =
        match Chart.resolve App.Returns.Returns.chartValue state event with
        | Ok resolution -> resolution
        | Error error -> Assert.Fail $"Expected return event to resolve, got %A{error}."

    let ``return chart authorizes receives and refunds`` () =
        let request = makeReturnRequest ()
        let lineId = request.Lines.Head.OrderLineId

        let started =
            expectReturnResolution App.Returns.Returns.initialState (App.Returns.ReturnEvent.ReturnRequested request)

        match started.Next with
        | App.Returns.ReturnState.AuthorizationPending p -> Assert.Equal(request, p.Request)
        | other -> Assert.Fail $"Expected authorization pending, got %A{other}."

        Assert.Equal([ App.Returns.ReturnAction.VerifyAuthorization request ], started.Actions)

        let approved =
            expectReturnResolution started.Next (App.Returns.ReturnEvent.AuthorizationApproved request.AuthorizationId)

        Assert.Equal([ App.Returns.ReturnAction.IssueLabel request ], approved.Actions)

        let reference =
            CarrierReference.create "sim-abc123" |> Result.defaultWith Assert.Fail

        let labelled =
            expectReturnResolution approved.Next (App.Returns.ReturnEvent.LabelCreated reference)

        let scanId = ReturnTrackingEventId.create "scan-1" |> Result.defaultWith Assert.Fail

        let inTransit =
            expectReturnResolution labelled.Next (App.Returns.ReturnEvent.CarrierScanReceived scanId)

        match inTransit.Next with
        | App.Returns.ReturnState.InTransit _ -> ()
        | other -> Assert.Fail $"Expected in transit, got %A{other}."

        let received =
            expectReturnResolution inTransit.Next (App.Returns.ReturnEvent.ItemsReceived [ lineId, 1 ])

        match received.Next with
        | App.Returns.ReturnState.Received _ -> ()
        | other -> Assert.Fail $"Expected received, got %A{other}."

        let inspected =
            expectReturnResolution received.Next (App.Returns.ReturnEvent.InspectionApproved [ lineId, 1 ])

        match inspected.Next with
        | App.Returns.ReturnState.Inspected _ -> ()
        | other -> Assert.Fail $"Expected inspected, got %A{other}."

        let restocked =
            expectReturnResolution inspected.Next (App.Returns.ReturnEvent.RestockCompleted request.ReturnId)

        let refunding =
            expectReturnResolution restocked.Next App.Returns.ReturnEvent.RefundStartRequested

        let refundId =
            match refunding.Next with
            | App.Returns.ReturnState.RefundPending(_, refund) -> refund.RefundId
            | other -> Assert.Fail $"Expected refund pending, got %A{other}."

        let refunded =
            expectReturnResolution refunding.Next (App.Returns.ReturnEvent.RefundSucceeded refundId)

        match refunded.Next with
        | App.Returns.ReturnState.Refunded _ -> ()
        | other -> Assert.Fail $"Expected refunded, got %A{other}."

    let ``payment reserves and settles a refund allocation`` () =
        let attempt = makeAuthorizationAttempt ()
        let expiry = DateTimeOffset.UtcNow.AddDays 6.

        let authorized =
            { Attempt = attempt
              ProviderReference = "sim-abc123"
              ExpiresAt = expiry }

        let captureRequest amount suffix =
            { OrderId = attempt.OrderId
              CaptureId = CaptureId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              OperationId =
                PaymentOperationId.create $"capture:v1:{suffix}"
                |> Result.defaultWith Assert.Fail
              Amount = Money.create amount "USD" |> Result.defaultWith Assert.Fail }

        let started =
            expectPaymentResolution (Authorized authorized) (CaptureRequested(captureRequest 27m "one"))

        let pendingRequest =
            match started.Next with
            | CapturePending p -> p.Request
            | other -> Assert.Fail $"Expected capture pending, got %A{other}."

        let captured =
            expectPaymentResolution started.Next (CaptureSucceeded(pendingRequest, "sim-cap1"))

        let refund =
            { makeRefundRequest 10m with
                Origin = OrderCancellation attempt.OrderId }

        let reserved =
            expectPaymentResolution captured.Next (RefundAllocationRequested refund)

        match reserved.Next with
        | RefundAllocationPending payment -> Assert.Equal(1, payment.Refunds.Length)
        | other -> Assert.Fail $"Expected refund allocation pending, got %A{other}."

        let settled =
            expectPaymentResolution reserved.Next (RefundAllocationSettled refund.AllocationId)

        match settled.Next with
        | Captured payment -> Assert.True(payment.Refunds.Head.Status = "settled")
        | other -> Assert.Fail $"Expected captured, got %A{other}."

    let ``reservation failures keep distinct reason codes`` () =
        let codes =
            [ ReservationFailure.InsufficientStock
              ReservationFailure.ProductInactive
              ReservationFailure.PriceVersionMismatch
              ReservationFailure.InvalidReservation ]
            |> List.map (ReservationFailure.code >> ReasonCode.value)

        Assert.Equal<string list>(
            [ "insufficient-stock"
              "product-inactive"
              "price-version-mismatch"
              "invalid-reservation" ],
            codes
        )

    let private invoiceRequest () =
        OrderSnapshotId.create (Guid.NewGuid())
        |> Result.defaultWith Assert.Fail
        |> InvoiceRequest.forOrder

    let private invoiceNumber sequence =
        InvoiceNumber.create "FSNIX" "INV" "2026-09" sequence
        |> Result.defaultWith Assert.Fail

    let private expectInvoiceResolution state event =
        match Chart.resolve App.Invoices.Invoices.chartValue state event with
        | Ok resolution -> resolution
        | Error error -> Assert.Fail $"Expected the invoice event to resolve, got %A{error}."

    let ``invoice numbers validate scope and format for display`` () =
        Assert.Equal("INV-2026-09-00000042", InvoiceNumber.display (invoiceNumber 42L))
        Assert.Equal("INV-2026-09-99999999", InvoiceNumber.display (invoiceNumber 99999999L))
        Assert.True(Result.isError (InvoiceNumber.create "fsnix" "INV" "2026-09" 1L))
        Assert.True(Result.isError (InvoiceNumber.create "FSNIX" "-INV" "2026-09" 1L))
        Assert.True(Result.isError (InvoiceNumber.create "FSNIX" "INV" "2026-09" 0L))
        Assert.True(Result.isError (InvoiceNumber.create "FSNIX" "INV" "2026-09" 100000000L))
        Assert.True(Result.isError (InvoiceNumber.create "FSNIX" (String('A', 17)) "2026-09" 1L))

        for period in [ "2026"; "2026-00"; "2026-13"; "2026-9"; "2026/09"; "26-09-01" ] do
            Assert.True(Result.isError (InvoiceNumber.create "FSNIX" "INV" period 1L), period)

        let request = invoiceRequest ()
        Assert.True(Result.isOk (InvoiceRequest.validate request))

        Assert.True(
            Result.isError (
                InvoiceRequest.validate
                    { request with
                        OrderId = "order:" + Guid.NewGuid().ToString("D") }
            )
        )

    let ``invoice chart issues once and parks failures for operator retry`` () =
        let request = invoiceRequest ()
        let number = invoiceNumber 7L

        let requested =
            expectInvoiceResolution App.Invoices.Invoices.initialState (App.Invoices.InvoiceRequested request)

        Assert.Equal(App.Invoices.SnapshotPending request, requested.Next)
        Assert.Equal([ App.Invoices.IssueSnapshot request ], requested.Actions)

        let duplicate =
            expectInvoiceResolution requested.Next (App.Invoices.InvoiceRequested request)

        Assert.Equal(requested.Next, duplicate.Next)
        Assert.Empty(duplicate.Actions)

        let other = invoiceRequest ()

        let stale =
            expectInvoiceResolution requested.Next (App.Invoices.SnapshotIssued(other.InvoiceId, number))

        Assert.Equal(requested.Next, stale.Next)

        let issued =
            expectInvoiceResolution requested.Next (App.Invoices.SnapshotIssued(request.InvoiceId, number))

        let issuedInvoice: App.Invoices.IssuedInvoice =
            { Request = request; Number = number }

        Assert.Equal(App.Invoices.RenderPending issuedInvoice, issued.Next)
        Assert.Equal([ App.Invoices.RenderDocument issuedInvoice ], issued.Actions)

        let late =
            expectInvoiceResolution issued.Next (App.Invoices.SnapshotIssued(request.InvoiceId, invoiceNumber 8L))

        Assert.Equal(issued.Next, late.Next)

        let digest =
            DocumentDigest.create (String('a', 64)) |> Result.defaultWith Assert.Fail

        let renderFailure = ReasonCode.ofLiteral "render-failed"

        let staleRender =
            expectInvoiceResolution issued.Next (App.Invoices.DocumentRendered(other.InvoiceId, digest))

        Assert.Equal(issued.Next, staleRender.Next)

        let renderFailed =
            expectInvoiceResolution issued.Next (App.Invoices.DocumentRenderFailed(request.InvoiceId, renderFailure))

        Assert.Equal(App.Invoices.RenderFailed(issuedInvoice, renderFailure), renderFailed.Next)

        let rerender =
            expectInvoiceResolution renderFailed.Next App.Invoices.RenderRetryRequested

        Assert.Equal(App.Invoices.RenderPending issuedInvoice, rerender.Next)
        Assert.Equal([ App.Invoices.RenderDocument issuedInvoice ], rerender.Actions)

        let rendered =
            expectInvoiceResolution rerender.Next (App.Invoices.DocumentRendered(request.InvoiceId, digest))

        Assert.Equal(App.Invoices.Rendered(issuedInvoice, digest), rendered.Next)
        Assert.Empty(rendered.Actions)

        let duplicateRender =
            expectInvoiceResolution rendered.Next (App.Invoices.DocumentRendered(request.InvoiceId, digest))

        Assert.Equal(rendered.Next, duplicateRender.Next)

        match Chart.resolve App.Invoices.Invoices.chartValue rendered.Next App.Invoices.RenderRetryRequested with
        | Ok _ -> Assert.Fail "Retrying a rendered invoice must be rejected."
        | Error _ -> ()

        let closed = expectInvoiceResolution rendered.Next App.Invoices.CloseRequested
        Assert.Equal(App.Invoices.Closed issuedInvoice, closed.Next)

        let reason = ReasonCode.ofLiteral "snapshot-missing"

        let failed =
            expectInvoiceResolution requested.Next (App.Invoices.IssuanceFailed(request.InvoiceId, reason))

        Assert.Equal(App.Invoices.ManualReview(request, reason), failed.Next)

        let retried =
            expectInvoiceResolution failed.Next App.Invoices.IssuanceRetryRequested

        Assert.Equal(App.Invoices.SnapshotPending request, retried.Next)
        Assert.Equal([ App.Invoices.IssueSnapshot request ], retried.Actions)

        match
            Chart.resolve
                App.Invoices.Invoices.chartValue
                App.Invoices.Invoices.initialState
                App.Invoices.CloseRequested
        with
        | Ok _ -> Assert.Fail "Closing an unissued invoice must be rejected."
        | Error _ -> ()

    let ``invoice codecs round trip every case and reject unknown tags`` () =
        let request = invoiceRequest ()
        let number = invoiceNumber 3L
        let reason = ReasonCode.ofLiteral "snapshot-missing"

        let issued: App.Invoices.IssuedInvoice = { Request = request; Number = number }

        let roundTrip (codec: Codec<'T>) (value: 'T) =
            let json = codec.Encode value |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")
            Assert.DoesNotContain("@", json)
            Assert.Equal(value, codec.Decode json |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}"))

        let digest =
            DocumentDigest.ofBytes (Array.init 32 byte) |> Result.defaultWith Assert.Fail

        [ App.Invoices.Initial
          App.Invoices.SnapshotPending request
          App.Invoices.RenderPending issued
          App.Invoices.Rendered(issued, digest)
          App.Invoices.RenderFailed(issued, reason)
          App.Invoices.ManualReview(request, reason)
          App.Invoices.Closed issued ]
        |> List.iter (roundTrip InvoiceCodec.state)

        [ App.Invoices.InvoiceRequested request
          App.Invoices.SnapshotIssued(request.InvoiceId, number)
          App.Invoices.IssuanceFailed(request.InvoiceId, reason)
          App.Invoices.IssuanceRetryRequested
          App.Invoices.DocumentRendered(request.InvoiceId, digest)
          App.Invoices.DocumentRenderFailed(request.InvoiceId, reason)
          App.Invoices.RenderRetryRequested
          App.Invoices.CloseRequested ]
        |> List.iter (roundTrip InvoiceCodec.event)

        roundTrip InvoiceCodec.action (App.Invoices.IssueSnapshot request)
        roundTrip InvoiceCodec.action (App.Invoices.RenderDocument issued)

        let rendered =
            InvoiceCodec.event.Encode(App.Invoices.DocumentRendered(request.InvoiceId, digest))
            |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")

        Assert.True(
            Result.isError (InvoiceCodec.event.Decode(rendered.Replace(DocumentDigest.value digest, String('Z', 64))))
        )

        [ App.Invoices.InvoiceActionError.CallbackEncodingFailed
          App.Invoices.InvoiceActionError.ActionReceiptMismatch
          App.Invoices.InvoiceActionError.InvalidAction ]
        |> List.iter (roundTrip InvoiceCodec.actionError)

        roundTrip OrderCodec.action (RequestInvoice request.SnapshotId)

        let encoded =
            InvoiceCodec.event.Encode(App.Invoices.SnapshotIssued(request.InvoiceId, number))
            |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")

        Assert.Contains("\"tag\":\"snapshot-issued-v1\"", encoded)

        Assert.True(
            Result.isError (InvoiceCodec.event.Decode(encoded.Replace("snapshot-issued-v1", "snapshot-issued-v9")))
        )

        Assert.True(Result.isError (InvoiceCodec.event.Decode(encoded.Replace("\"sequence\":3", "\"sequence\":0"))))

        let mismatched =
            InvoiceCodec.action.Encode(App.Invoices.IssueSnapshot request)
            |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")
            |> fun json -> json.Replace(request.OrderId, "order:" + Guid.NewGuid().ToString("D"))

        Assert.True(Result.isError (InvoiceCodec.action.Decode mismatched))

    let ``pdf renders deterministically with the shipped font only`` () =
        let first = Pdf.renderSample ()
        let second = Pdf.renderSample ()

        Assert.True(Pdf.isPdf first, "Expected a PDF header.")
        Assert.Equal<byte array>(first, second)
        Assert.False(QuestPDF.Settings.UseSystemFonts)

        let text = Text.Encoding.Latin1.GetString first
        Assert.Contains("Lato", text)

    let ``document digests are lowercase hex sha256`` () =
        let digest =
            DocumentDigest.ofBytes (Array.init 32 (fun index -> byte (index * 7)))
            |> Result.defaultWith Assert.Fail

        Assert.Equal(64, (DocumentDigest.value digest).Length)
        Assert.Equal<byte array>(Array.init 32 (fun index -> byte (index * 7)), DocumentDigest.bytes digest)
        Assert.True(Result.isError (DocumentDigest.create (String('A', 64))))
        Assert.True(Result.isError (DocumentDigest.create (String('a', 63))))
        Assert.True(Result.isError (DocumentDigest.ofBytes (Array.zeroCreate 31)))

    let private sampleInvoiceDocument sequence : InvoiceDocument =
        { InvoiceId =
            InvoiceId.create (Guid.Parse "00000000-0000-0000-0000-00000000000a")
            |> Result.defaultWith Assert.Fail
          Number = invoiceNumber sequence
          IssuedAt = DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)
          SellerName = "fsnix Store"
          SellerAddress = "1 Example Street"
          SellerTaxId = "TAX-1"
          BuyerEmail = "buyer@example.test"
          BillingAddress = [ "Buyer"; "2 Road"; "Town OR 97201"; "US" ]
          Lines =
            [ { Sku = "COFFEE-ESPRESSO"
                Description = "Espresso Beans — Café Ação"
                Quantity = 2
                UnitPrice = 24.90m
                Amount = 49.80m } ]
          Subtotal = 49.80m
          Shipping = 5m
          Tax = 4.38m
          Total = 59.18m
          Currency = "USD" }

    let ``invoice pdf is deterministic per snapshot`` () =
        let first = InvoicePdf.render (sampleInvoiceDocument 42L)
        let again = InvoicePdf.render (sampleInvoiceDocument 42L)
        let other = InvoicePdf.render (sampleInvoiceDocument 43L)

        Assert.True(Pdf.isPdf first, "Expected a PDF header.")
        Assert.Equal<byte array>(first, again)
        Assert.NotEqual<byte array>(first, other)
        Assert.Equal("invoice-v1", InvoicePdf.renderer.Name)

    let ``unknown provider calls reconcile on request and park when exhausted`` () =
        let attempt = makeAuthorizationAttempt ()
        let stale = (makeAuthorizationAttempt ()).OperationId

        let pending =
            AuthorizationUnknown
                { Attempt = attempt
                  CancelRequested = false }

        let checkedAgain =
            expectPaymentResolution pending (ReconcileRequested attempt.OperationId)

        Assert.Equal(pending, checkedAgain.Next)
        Assert.Equal([ QueryGatewayAuthorization attempt ], checkedAgain.Actions)

        let staleCheck = expectPaymentResolution pending (ReconcileRequested stale)
        Assert.Equal(pending, staleCheck.Next)
        Assert.Empty(staleCheck.Actions)

        let exhausted =
            expectPaymentResolution pending (ReconciliationExhausted attempt.OperationId)

        Assert.Equal(PaymentState.ManualReview(ReasonCode.ofLiteral "gateway-outcome-unknown"), exhausted.Next)

        let authorized =
            { Attempt = attempt
              ProviderReference = "sim-abc123"
              ExpiresAt = DateTimeOffset.UtcNow.AddDays 6. }

        let settled =
            expectPaymentResolution (Authorized authorized) (ReconcileRequested attempt.OperationId)

        Assert.Equal(Authorized authorized, settled.Next)
        Assert.Empty(settled.Actions)

        let capture =
            { OrderId = attempt.OrderId
              CaptureId = CaptureId.create (Guid.NewGuid()) |> Result.defaultWith Assert.Fail
              OperationId =
                PaymentOperationId.create "capture:v1:reconcile"
                |> Result.defaultWith Assert.Fail
              Amount = Money.create 10m "USD" |> Result.defaultWith Assert.Fail }

        let captureUnknown =
            CaptureUnknown
                { Payment =
                    { Authorization = authorized
                      Captures = []
                      Refunds = [] }
                  Request = capture }

        let captureCheck =
            expectPaymentResolution captureUnknown (ReconcileRequested capture.OperationId)

        Assert.Equal([ QueryGatewayCapture(authorized, capture) ], captureCheck.Actions)

        let voidOperation = PaymentOperationId.voidOf attempt.OperationId

        let voidCheck =
            expectPaymentResolution (VoidUnknown authorized) (ReconcileRequested voidOperation)

        Assert.Equal([ CallGatewayVoid authorized ], voidCheck.Actions)

        let voided =
            expectPaymentResolution (VoidUnknown authorized) (VoidSucceeded authorized)

        Assert.Equal(Voided authorized, voided.Next)
        Assert.Equal([ NotifyOrderVoided authorized ], voided.Actions)

        let refund = makeRefundRequest 10m

        let approved: ApprovedRefund =
            { Request = refund
              PaymentReference = "sim-abc123" }

        let resolveRefund state event =
            match Chart.resolve App.Refunds.Refunds.chartValue state event with
            | Ok resolution -> resolution
            | Error error -> Assert.Fail $"Expected refund event to resolve, got %A{error}."

        let refundUnknown = App.Refunds.RefundState.OutcomeUnknown approved

        let refundCheck =
            resolveRefund refundUnknown (App.Refunds.RefundEvent.ReconcileRequested refund.OperationId)

        Assert.Equal(refundUnknown, refundCheck.Next)
        Assert.Equal([ App.Refunds.RefundAction.QueryGatewayRefund approved ], refundCheck.Actions)

        let refundExhausted =
            resolveRefund refundUnknown (App.Refunds.RefundEvent.ReconciliationExhausted refund.OperationId)

        let reason = ReasonCode.ofLiteral "gateway-outcome-unknown"
        Assert.Equal(App.Refunds.RefundState.ManualReview(refund, reason), refundExhausted.Next)
        Assert.Equal([ App.Refunds.RefundAction.NotifyOriginFailed(refund, reason) ], refundExhausted.Actions)

        for event in
            [ ReconcileRequested attempt.OperationId
              ReconciliationExhausted attempt.OperationId ] do
            let json =
                PaymentCodec.event.Encode event
                |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")

            Assert.Equal(
                event,
                PaymentCodec.event.Decode json
                |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")
            )

        for event in
            [ App.Refunds.RefundEvent.ReconcileRequested refund.OperationId
              App.Refunds.RefundEvent.ReconciliationExhausted refund.OperationId ] do
            let json =
                RefundCodec.event.Encode event
                |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")

            Assert.Equal(
                event,
                RefundCodec.event.Decode json
                |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")
            )

    let ``lapsed authorizations hold the order for review`` () =
        let attempt = makeAuthorizationAttempt ()

        let authorized =
            { Attempt = attempt
              ProviderReference = "sim-abc123"
              ExpiresAt = DateTimeOffset.UtcNow.AddDays 6. }

        let expired = AuthorizationExpired(attempt.OperationId, authorized.ExpiresAt)
        let reason = ReasonCode.ofLiteral "authorization-expired"

        let open' = expectPaymentResolution (Authorized authorized) expired
        Assert.Equal(PaymentState.ManualReview reason, open'.Next)
        Assert.Equal([ NotifyOrderAuthorizationExpired authorized ], open'.Actions)

        let partial =
            PartiallyCaptured
                { Authorization = authorized
                  Captures = []
                  Refunds = [] }

        let partialExpired = expectPaymentResolution partial expired
        Assert.Equal(PaymentState.ManualReview reason, partialExpired.Next)

        let captured =
            Captured
                { Authorization = authorized
                  Captures = []
                  Refunds = [] }

        let absorbed = expectPaymentResolution captured expired
        Assert.Equal(captured, absorbed.Next)
        Assert.Empty(absorbed.Actions)

        let stale =
            expectPaymentResolution
                (Authorized authorized)
                (AuthorizationExpired((makeAuthorizationAttempt ()).OperationId, authorized.ExpiresAt))

        Assert.Equal(Authorized authorized, stale.Next)

        let reserved = reservedOrder ()

        let placed =
            Placed
                { Reserved = reserved
                  ProviderReference = "sim-abc123" }

        let review = expectOrderResolution placed PaymentAuthorizationExpired
        Assert.Equal(OrderState.ManualReview reason, review.Next)

        let fulfilment =
            { PlacedOrder =
                { Reserved = reserved
                  ProviderReference = "sim-abc123" }
              AddressSnapshotId = reserved.Pending.SnapshotId
              Shipments = []
              Returns = [] }

        let held = expectOrderResolution (Processing fulfilment) PaymentAuthorizationExpired

        Assert.Equal(
            HeldForReview
                { Fulfilment = fulfilment
                  Reason = reason },
            held.Next
        )

        expectOrderAbsorbed (OrderState.Delivered fulfilment) PaymentAuthorizationExpired

        let roundTrip (codec: Codec<'T>) (value: 'T) =
            let json = codec.Encode value |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")

            let decoded =
                codec.Decode json |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")

            decoded

        match roundTrip PaymentCodec.event expired with
        | AuthorizationExpired(operation, expiresAt) ->
            Assert.Equal(attempt.OperationId, operation)
            Assert.Equal(authorized.ExpiresAt.ToUnixTimeMilliseconds(), expiresAt.ToUnixTimeMilliseconds())
        | other -> Assert.Fail $"Expected AuthorizationExpired, got %A{other}."

        Assert.Equal(PaymentAuthorizationExpired, roundTrip OrderCodec.event PaymentAuthorizationExpired)

        match roundTrip PaymentCodec.action (NotifyOrderAuthorizationExpired authorized) with
        | NotifyOrderAuthorizationExpired decoded -> Assert.Equal(authorized.Attempt, decoded.Attempt)
        | other -> Assert.Fail $"Expected NotifyOrderAuthorizationExpired, got %A{other}."

    let ``stalled invoice renders are re-requested then parked`` () =
        let request = invoiceRequest ()

        let issued: App.Invoices.IssuedInvoice =
            { Request = request
              Number = invoiceNumber 5L }

        let pending = App.Invoices.RenderPending issued

        let again =
            expectInvoiceResolution pending (App.Invoices.RenderReconcileRequested 0)

        Assert.Equal(pending, again.Next)
        Assert.Equal([ App.Invoices.RenderDocument issued ], again.Actions)

        let parked = expectInvoiceResolution pending App.Invoices.RenderReconcileExhausted

        Assert.Equal(App.Invoices.RenderFailed(issued, ReasonCode.ofLiteral "render-timeout"), parked.Next)

        let digest =
            DocumentDigest.create (String('b', 64)) |> Result.defaultWith Assert.Fail

        let rendered = App.Invoices.Rendered(issued, digest)

        let late =
            expectInvoiceResolution rendered (App.Invoices.RenderReconcileRequested 1)

        Assert.Equal(rendered, late.Next)
        Assert.Empty(late.Actions)

        for event in
            [ App.Invoices.RenderReconcileRequested 2
              App.Invoices.RenderReconcileExhausted ] do
            let json =
                InvoiceCodec.event.Encode event
                |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")

            Assert.Equal(
                event,
                InvoiceCodec.event.Decode json
                |> Result.defaultWith (fun e -> Assert.Fail $"%A{e}")
            )

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
                  ``runtime readiness requires fresh successful components``
              testCase "cart add starts an active cart" ``cart add starts an active cart``
              testCase "converted cart starts a fresh cart" ``converted cart starts a fresh cart``
              testCase "order pricing includes shipping and tax" ``order pricing includes shipping and tax``
              testCase
                  "order chart begins stock reservation"
                  ``order chart enters reservation pending and queues next effects``
              testCase "order codecs are strict and versioned" ``order codecs use versioned tags and validate payloads``
              testCase
                  "reserved orders wait for the pay step"
                  ``reserved orders wait for the pay step without queueing authorization``
              testCase "pay step queues the authorization handoff" ``pay step queues the authorization handoff``
              testCase
                  "payment authorization commits stock and decline retries"
                  ``payment authorization commits stock and decline returns to the pay step``
              testCase "stale payment attempt results are absorbed" ``stale payment attempt results are absorbed``
              testCase "stock commitment places the order" ``stock commitment places the order``
              testCase
                  "cancellation waits for both legs"
                  ``cancellation waits for both reservations and payment to settle``
              testCase
                  "late payment results during cancellation are absorbed"
                  ``late payment results during cancellation are absorbed``
              testCase "payment chart authorizes notifies and voids" ``payment chart authorizes notifies and voids``
              testCase
                  "cancel during a pending authorization unwinds"
                  ``cancel during a pending authorization unwinds instead of notifying``
              testCase
                  "unknown outcomes park until a cancel queries"
                  ``unknown outcomes park and a cancel triggers a gateway query``
              testCase
                  "cancel before authorization closes without charge"
                  ``cancel before any authorization request closes without charge``
              testCase "declined payments accept a retry" ``declined payments accept a fresh authorization attempt``
              testCase
                  "payment codecs are strict and versioned"
                  ``payment codecs use versioned tags and validate payloads``
              testCase
                  "payment payloads never contain cardholder fields"
                  ``payment payloads never contain cardholder fields``
              testCase "cart add clamps quantity" ``cart add clamps quantity to maximum``
              testCase "stale epoch mutations are rejected" ``stale epoch mutations are rejected``
              testCase
                  "abandonment timer matches the current generation"
                  ``abandonment timer fires only for the current generation``
              testCase "cart merge unions and clamps" ``cart merge unions lines and clamps quantities``
              testCase "apply merge fills an empty cart" ``apply merge fills an empty customer cart``
              testCase "money wire round trips" ``money wire round trips exact amount and currency``
              testCase
                  "capability locate digest is purpose scoped"
                  ``capability locate digest is purpose scoped and entity free``
              testCase
                  "capture allocation distributes charges"
                  ``capture allocation distributes charges and consumes the remainder``
              testCase
                  "shipment chart confirms allocation and dispatches"
                  ``shipment chart confirms allocation creates label and dispatches``
              testCase
                  "order fulfilment captures on dispatch"
                  ``order fulfilment requests capture on dispatch and completes on delivery``
              testCase
                  "payment captures respect the authorized amount"
                  ``payment captures respect the authorized amount``
              testCase "refund balance reserves allocations" ``refund balance reserves pending allocations``
              testCase "refund chart allocates and settles" ``refund chart requests allocation and settles``
              testCase "return chart authorizes and refunds" ``return chart authorizes receives and refunds``
              testCase
                  "payment reserves and settles refund allocations"
                  ``payment reserves and settles a refund allocation``
              testCase "cart codecs contain no secret fields" ``cart codecs cover every case without secret fields``
              testCase "cart codecs round trip an active cart" ``cart codecs round trip an active cart``
              testCase "reservation failures keep distinct codes" ``reservation failures keep distinct reason codes``
              testCase "invoice numbers validate and display" ``invoice numbers validate scope and format for display``
              testCase
                  "invoice chart issues once and parks failures"
                  ``invoice chart issues once and parks failures for operator retry``
              testCase
                  "invoice codecs round trip and reject unknown tags"
                  ``invoice codecs round trip every case and reject unknown tags``
              testCase
                  "pdf renders deterministically with the shipped font"
                  ``pdf renders deterministically with the shipped font only``
              testCase "document digests are lowercase hex sha256" ``document digests are lowercase hex sha256``
              testCase "invoice pdf is deterministic per snapshot" ``invoice pdf is deterministic per snapshot``
              testCase
                  "unknown provider calls reconcile and park"
                  ``unknown provider calls reconcile on request and park when exhausted``
              testCase "lapsed authorizations hold the order" ``lapsed authorizations hold the order for review``
              testCase
                  "stalled invoice renders are re-requested"
                  ``stalled invoice renders are re-requested then parked`` ]
