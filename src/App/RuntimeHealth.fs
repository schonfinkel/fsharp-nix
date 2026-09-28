namespace App

open System
open System.Collections.Generic

[<RequireQualifiedAccess>]
type RuntimeComponent =
    | Boot
    | ProbeMachine
    | AccountFlowMachine
    | CartMachine
    | OrderMachine
    | PaymentMachine
    | ShipmentMachine
    | RefundMachine
    | ReturnMachine
    | ReturnWindowScanner
    | IntegrationOutboxRelay
    | EmailDeliveryRelay
    | FlowDeadlineScanner
    | CartAbandonmentScanner
    | CartMergeScanner
    | ReservationExpiryScanner

[<RequireQualifiedAccess>]
module RuntimeComponent =
    let code =
        function
        | RuntimeComponent.Boot -> "boot"
        | RuntimeComponent.ProbeMachine -> "probe-machine"
        | RuntimeComponent.AccountFlowMachine -> "account-flow-machine"
        | RuntimeComponent.CartMachine -> "cart-machine"
        | RuntimeComponent.OrderMachine -> "order-machine"
        | RuntimeComponent.PaymentMachine -> "payment-machine"
        | RuntimeComponent.ShipmentMachine -> "shipment-machine"
        | RuntimeComponent.RefundMachine -> "refund-machine"
        | RuntimeComponent.ReturnMachine -> "return-machine"
        | RuntimeComponent.ReturnWindowScanner -> "return-window-scanner"
        | RuntimeComponent.IntegrationOutboxRelay -> "integration-outbox-relay"
        | RuntimeComponent.EmailDeliveryRelay -> "email-delivery-relay"
        | RuntimeComponent.FlowDeadlineScanner -> "flow-deadline-scanner"
        | RuntimeComponent.CartAbandonmentScanner -> "cart-abandonment-scanner"
        | RuntimeComponent.CartMergeScanner -> "cart-merge-scanner"
        | RuntimeComponent.ReservationExpiryScanner -> "reservation-expiry-scanner"

[<RequireQualifiedAccess>]
type RuntimeFailure =
    | InvalidChart
    | StartupRefused
    | StartupFailed
    | PassFailed

[<RequireQualifiedAccess>]
type RuntimePhase =
    | Starting
    | Healthy
    | Failed
    | Stopped

type RuntimeObservation =
    { Component: RuntimeComponent
      Phase: RuntimePhase
      LastAttemptAt: DateTimeOffset option
      LastSuccessAt: DateTimeOffset option
      ConsecutiveFailures: int
      Failure: RuntimeFailure option }

type RuntimeHealth(timeProvider: TimeProvider) =
    let gate = obj ()

    let initial service =
        { Component = service
          Phase = RuntimePhase.Starting
          LastAttemptAt = None
          LastSuccessAt = None
          ConsecutiveFailures = 0
          Failure = None }

    let observations =
        Dictionary<RuntimeComponent, RuntimeObservation>(
            [ RuntimeComponent.Boot
              RuntimeComponent.ProbeMachine
              RuntimeComponent.AccountFlowMachine
              RuntimeComponent.CartMachine
              RuntimeComponent.OrderMachine
              RuntimeComponent.PaymentMachine
              RuntimeComponent.ShipmentMachine
              RuntimeComponent.RefundMachine
              RuntimeComponent.ReturnMachine
              RuntimeComponent.ReturnWindowScanner
              RuntimeComponent.IntegrationOutboxRelay
              RuntimeComponent.EmailDeliveryRelay
              RuntimeComponent.FlowDeadlineScanner
              RuntimeComponent.CartAbandonmentScanner
              RuntimeComponent.CartMergeScanner
              RuntimeComponent.ReservationExpiryScanner ]
            |> Seq.map (fun service -> KeyValuePair(service, initial service))
        )

    member _.Starting(service) =
        lock gate (fun () -> observations[service] <- initial service)

    member _.Succeeded(service) =
        let now = timeProvider.GetUtcNow()

        lock gate (fun () ->
            let current = observations[service]

            observations[service] <-
                { current with
                    Phase = RuntimePhase.Healthy
                    LastAttemptAt = Some now
                    LastSuccessAt = Some now
                    ConsecutiveFailures = 0
                    Failure = None })

    member _.Failed(service, failure) =
        let now = timeProvider.GetUtcNow()

        lock gate (fun () ->
            let current = observations[service]

            observations[service] <-
                { current with
                    Phase = RuntimePhase.Failed
                    LastAttemptAt = Some now
                    ConsecutiveFailures = current.ConsecutiveFailures + 1
                    Failure = Some failure })

    member _.Stopped(service) =
        lock gate (fun () ->
            let current = observations[service]

            observations[service] <-
                { current with
                    Phase = RuntimePhase.Stopped })

    member _.Snapshot() =
        lock gate (fun () ->
            observations.Values
            |> Seq.map (fun value -> value.Component, value)
            |> Map.ofSeq)

[<RequireQualifiedAccess>]
module RuntimeHealth =
    let startupReady (snapshot: Map<RuntimeComponent, RuntimeObservation>) =
        [ RuntimeComponent.Boot
          RuntimeComponent.ProbeMachine
          RuntimeComponent.AccountFlowMachine
          RuntimeComponent.CartMachine
          RuntimeComponent.OrderMachine
          RuntimeComponent.PaymentMachine
          RuntimeComponent.ShipmentMachine ]
        @ [ RuntimeComponent.RefundMachine; RuntimeComponent.ReturnMachine ]
        |> List.forall (fun service -> snapshot[service].Phase = RuntimePhase.Healthy)

    let workersReady
        (now: DateTimeOffset)
        (maximumAge: TimeSpan)
        (snapshot: Map<RuntimeComponent, RuntimeObservation>)
        =
        [ RuntimeComponent.IntegrationOutboxRelay
          RuntimeComponent.EmailDeliveryRelay
          RuntimeComponent.FlowDeadlineScanner
          RuntimeComponent.CartAbandonmentScanner
          RuntimeComponent.CartMergeScanner ]
        @ [ RuntimeComponent.ReservationExpiryScanner
            RuntimeComponent.ReturnWindowScanner ]
        |> List.forall (fun service ->
            let observation = snapshot[service]

            observation.Phase = RuntimePhase.Healthy
            && observation.LastSuccessAt
               |> Option.exists (fun succeededAt -> now - succeededAt <= maximumAge))
