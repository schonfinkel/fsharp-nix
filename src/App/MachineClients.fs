namespace App

open System
open System.Threading
open System.Threading.Tasks
open App.Auth
open App.Cart
open App.Database
open App.Domain
open App.Orders
open App.Payments
open App.Shipments
open App.Refunds
open App.Returns
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open ByzantineSystems.Automata.Storage.Postgres
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql

/// <summary>
/// One application-lifetime client instance for a machine. Clients use
/// <c>ListenerConnection.Off</c> stores; supervised workers own LISTEN and processing. Startup
/// fails loudly when the chart is invalid or the registry refuses it, so a web pod never serves
/// traffic against an incompatible chart.
/// </summary>
[<AbstractClass>]
type MachineClient<'Id, 'State, 'Event, 'Action, 'Error when 'Id: equality>
    (
        service: RuntimeComponent,
        name: string,
        build:
            ILogger -> PostgresContext -> Result<Machine<'Id, 'State, 'Event, 'Action, 'Error>, MachineConfigError list>,
        context: PostgresContext,
        loggerFactory: ILoggerFactory,
        health: RuntimeHealth
    ) =
    let logger = loggerFactory.CreateLogger $"{name}MachineClient"

    [<VolatileField>]
    let mutable client: Machine<'Id, 'State, 'Event, 'Action, 'Error> option = None

    /// <summary>The started machine; throws before <c>StartAsync</c> completes, which hosted
    /// startup ordering guarantees before any request or worker runs.</summary>
    member _.Machine =
        match client with
        | Some machine -> machine
        | None -> invalidOp $"the {name} machine client has not started"

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                health.Starting service

                match build logger context with
                | Error _ ->
                    health.Failed(service, RuntimeFailure.InvalidChart)
                    return raise (InvalidOperationException $"{name} client machine is invalid")
                | Ok machine ->
                    match! Machine.startAsync machine (PostgresChartRegistry { Context = context }) ct with
                    | Ok(Startup.Started _) ->
                        client <- Some machine
                        health.Succeeded service
                    | Ok(Startup.Refused _) ->
                        health.Failed(service, RuntimeFailure.StartupRefused)
                        return raise (InvalidOperationException $"{name} client refused to boot")
                    | Error _ ->
                        health.Failed(service, RuntimeFailure.StartupFailed)
                        return raise (InvalidOperationException $"{name} client failed to start")
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) =
            task {
                match client with
                | Some machine -> do! Machine.stopAsync machine ct
                | None -> ()

                client <- None
                health.Stopped service
            }
            :> Task

type ProbeMachineClient(context, loggerFactory, health) =
    inherit
        MachineClient<ProbeId, ProbeState, ProbeEvent, ProbeAction, ProbeActionError>(
            RuntimeComponent.ProbeMachine,
            "probe",
            Probe.buildClient,
            context,
            loggerFactory,
            health
        )

    member this.Probe = this.Machine

type AccountFlowMachineClient(context, loggerFactory, health) =
    inherit
        MachineClient<FlowId, FlowState, FlowEvent, FlowAction, FlowActionError>(
            RuntimeComponent.AccountFlowMachine,
            "account-flow",
            AccountFlowCodec.buildClient,
            context,
            loggerFactory,
            health
        )

    member this.Flows = this.Machine

type CartMachineClient(context, loggerFactory, health) =
    inherit
        MachineClient<CartId, CartState, CartEvent, CartAction, CartActionError>(
            RuntimeComponent.CartMachine,
            "cart",
            CartCodec.buildClient,
            context,
            loggerFactory,
            health
        )

    member this.Carts = this.Machine

type OrderMachineClient(context, loggerFactory, health) =
    inherit
        MachineClient<OrderId, OrderState, OrderEvent, OrderAction, OrderActionError>(
            RuntimeComponent.OrderMachine,
            "order",
            OrderCodec.buildClient,
            context,
            loggerFactory,
            health
        )

    member this.Orders = this.Machine

type PaymentMachineClient(context, loggerFactory, health) =
    inherit
        MachineClient<PaymentId, PaymentState, PaymentEvent, PaymentAction, PaymentActionError>(
            RuntimeComponent.PaymentMachine,
            "payment",
            PaymentCodec.buildClient,
            context,
            loggerFactory,
            health
        )

    member this.Payments = this.Machine

type ShipmentMachineClient(context, loggerFactory, health) =
    inherit
        MachineClient<ShipmentEntityId, ShipmentState, ShipmentEvent, ShipmentAction, ShipmentActionError>(
            RuntimeComponent.ShipmentMachine,
            "shipment",
            ShipmentCodec.buildClient,
            context,
            loggerFactory,
            health
        )

    member this.Shipments = this.Machine

type RefundMachineClient(context, loggerFactory, health) =
    inherit
        MachineClient<RefundEntityId, RefundState, RefundEvent, RefundAction, RefundActionError>(
            RuntimeComponent.RefundMachine,
            "refund",
            RefundCodec.buildClient,
            context,
            loggerFactory,
            health
        )

    member this.Refunds = this.Machine

type ReturnMachineClient(context, loggerFactory, health) =
    inherit
        MachineClient<ReturnEntityId, ReturnState, ReturnEvent, ReturnAction, ReturnActionError>(
            RuntimeComponent.ReturnMachine,
            "return",
            ReturnCodec.buildClient,
            context,
            loggerFactory,
            health
        )

    member this.Returns = this.Machine
