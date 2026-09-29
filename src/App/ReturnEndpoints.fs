namespace App

open System
open System.Threading.Tasks
open App.Database
open App.Domain
open App.Returns
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.Runtime
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Identity
open Npgsql
open Oxpecker
open Oxpecker.ViewEngine

[<RequireQualifiedAccess>]
module ReturnEndpoints =
    let private routeId (context: HttpContext) =
        context.TryGetRouteValue("returnId")
        |> Option.defaultValue ""
        |> ReturnId.tryParse

    let private progress =
        function
        | ReturnState.Initial -> None
        | AuthorizationPending p
        | Approved p
        | LabelPending p
        | Closed p -> Some p
        | ReturnState.Rejected(p, _)
        | LabelIssued(p, _)
        | InTransit(p, _)
        | Received(p, _)
        | Inspected(p, _)
        | RefundPending(p, _)
        | Refunded(p, _)
        | RejectedAfterInspection(p, _)
        | ManualReview(p, _) -> Some p

    let private notFound (context: HttpContext) =
        context.Response.StatusCode <- StatusCodes.Status404NotFound
        context.WriteHtmlView(p () { "Return not found." })

    let private formValue (form: IFormCollection) name =
        match form.TryGetValue name with
        | true, value -> string value
        | _ -> ""

    let show: EndpointHandler =
        fun context ->
            task {
                Web.noStore context

                match routeId context with
                | Error _ -> return! notFound context
                | Ok id ->
                    let returns = context.GetService<ReturnMachineClient>()
                    let! state = Machine.state returns.Returns (Returns.returnEntityId id) context.RequestAborted

                    match state with
                    | Ok(Some snapshot) ->
                        match progress snapshot.State with
                        | None -> return! notFound context
                        | Some details ->
                            let users = context.GetService<UserManager<ApplicationUser>>()
                            let! user = users.GetUserAsync context.User
                            let dataSource = context.GetService<NpgsqlDataSource>()

                            let! owned =
                                if isNull user then
                                    System.Threading.Tasks.Task.FromResult false
                                else
                                    OrderSnapshots.isOwnedBy
                                        dataSource
                                        details.Request.OrderId
                                        user.Id
                                        context.RequestAborted

                            if not owned then
                                return! notFound context
                            else
                                return!
                                    context.WriteHtmlView(
                                        App.Views.SharedViews.layout
                                            context
                                            "Return status"
                                            (section (class' = "card") {
                                                h1 () { "Return status" }
                                                p () { $"Return: {ReturnId.wireString id}" }
                                                p () { $"Status: {Returns.classifyState snapshot.State}" }

                                                a (href = $"/orders/{details.Request.OrderId.Substring(6)}") {
                                                    "Back to order"
                                                }
                                            })
                                    )
                    | _ -> return! notFound context
            }

    let adminPage: EndpointHandler =
        fun context ->
            task {
                Web.noStore context

                match routeId context with
                | Error _ -> return! notFound context
                | Ok id ->
                    let returns = context.GetService<ReturnMachineClient>()
                    let! state = Machine.state returns.Returns (Returns.returnEntityId id) context.RequestAborted

                    match state with
                    | Ok(Some snapshot) ->
                        match progress snapshot.State with
                        | None -> return! notFound context
                        | Some details ->
                            let actionRoot = $"/admin/returns/{ReturnId.wireString id}"

                            return!
                                context.WriteHtmlView(
                                    App.Views.SharedViews.layout
                                        context
                                        "Return operations"
                                        (section (class' = "card") {
                                            h1 () { $"Return {ReturnId.wireString id}" }
                                            p () { $"Status: {Returns.classifyState snapshot.State}" }

                                            if
                                                (match snapshot.State with
                                                 | LabelIssued _ -> true
                                                 | _ -> false)
                                            then
                                                form (action = $"{actionRoot}/scan", method = "post") {
                                                    context.GetAntiforgeryInput()

                                                    input (
                                                        type' = "hidden",
                                                        name = "key",
                                                        value = Guid.NewGuid().ToString("D")
                                                    )

                                                    button (type' = "submit") { "Mark in transit" }
                                                }

                                            match snapshot.State with
                                            | InTransit _ ->
                                                for line in details.Request.Lines do
                                                    form (action = $"{actionRoot}/receive", method = "post") {
                                                        context.GetAntiforgeryInput()

                                                        input (
                                                            type' = "hidden",
                                                            name = "key",
                                                            value = Guid.NewGuid().ToString("D")
                                                        )

                                                        input (
                                                            type' = "hidden",
                                                            name = "lineId",
                                                            value = OrderLineId.wireString line.OrderLineId
                                                        )

                                                        label () {
                                                            $"Receive up to {line.Quantity}"

                                                            input (
                                                                type' = "number",
                                                                name = "quantity",
                                                                min = "1",
                                                                max = string line.Quantity,
                                                                required = true
                                                            )
                                                        }

                                                        button (type' = "submit") { "Receive" }
                                                    }
                                            | Received(p, _) ->
                                                for (lineId, quantity) in p.Received do
                                                    form (action = $"{actionRoot}/approve", method = "post") {
                                                        context.GetAntiforgeryInput()

                                                        input (
                                                            type' = "hidden",
                                                            name = "key",
                                                            value = Guid.NewGuid().ToString("D")
                                                        )

                                                        input (
                                                            type' = "hidden",
                                                            name = "lineId",
                                                            value = OrderLineId.wireString lineId
                                                        )

                                                        input (
                                                            type' = "hidden",
                                                            name = "quantity",
                                                            value = string quantity
                                                        )

                                                        button (type' = "submit") { "Inspect and restock" }
                                                    }
                                            | _ -> ()

                                            if
                                                (match snapshot.State with
                                                 | Received _ -> true
                                                 | _ -> false)
                                            then
                                                form (action = $"{actionRoot}/reject", method = "post") {
                                                    context.GetAntiforgeryInput()

                                                    input (
                                                        type' = "hidden",
                                                        name = "key",
                                                        value = Guid.NewGuid().ToString("D")
                                                    )

                                                    button (type' = "submit") { "Reject after inspection" }
                                                }

                                            if
                                                (match snapshot.State with
                                                 | Inspected(p, _) -> p.RestockSettled
                                                 | _ -> false)
                                            then
                                                form (action = $"{actionRoot}/refund", method = "post") {
                                                    context.GetAntiforgeryInput()

                                                    input (
                                                        type' = "hidden",
                                                        name = "key",
                                                        value = Guid.NewGuid().ToString("D")
                                                    )

                                                    button (type' = "submit") { "Start refund" }
                                                }

                                            if
                                                (match snapshot.State with
                                                 | ReturnState.Rejected _
                                                 | Refunded _
                                                 | RejectedAfterInspection _ -> true
                                                 | _ -> false)
                                            then
                                                form (action = $"{actionRoot}/close", method = "post") {
                                                    context.GetAntiforgeryInput()

                                                    input (
                                                        type' = "hidden",
                                                        name = "key",
                                                        value = Guid.NewGuid().ToString("D")
                                                    )

                                                    button (type' = "submit") { "Close return" }
                                                }
                                        })
                                )
                    | _ -> return! notFound context
            }

    /// <summary>The operator operations exposed on <c>/admin/returns/{id}/{action}</c>.</summary>
    [<RequireQualifiedAccess>]
    type private AdminOperation =
        | Scan
        | Receive
        | Approve
        | Reject
        | Refund
        | Close

    let private parseOperation =
        function
        | "scan" -> Some AdminOperation.Scan
        | "receive" -> Some AdminOperation.Receive
        | "approve" -> Some AdminOperation.Approve
        | "reject" -> Some AdminOperation.Reject
        | "refund" -> Some AdminOperation.Refund
        | "close" -> Some AdminOperation.Close
        | _ -> None

    let private operatorRejected = ReasonCode.ofLiteral "operator-rejected"

    let adminAction: EndpointHandler =
        fun context ->
            task {
                match routeId context, context.TryGetRouteValue("action") |> Option.bind parseOperation with
                | Error _, _
                | _, None -> return! HttpErrors.notFound "Return operation not found." context
                | Ok id, Some operation ->
                    let! form = context.Request.ReadFormAsync context.RequestAborted
                    let key = formValue form "key"

                    match Guid.TryParseExact(key, "D") with
                    | false, _ -> return! HttpErrors.badRequest "Invalid operation key." context
                    | true, _ ->
                        let quantities =
                            match
                                Guid.TryParseExact(formValue form "lineId", "D"),
                                Int32.TryParse(formValue form "quantity")
                            with
                            | (true, lineId), (true, quantity) when quantity > 0 ->
                                OrderLineId.create lineId
                                |> Result.toOption
                                |> Option.map (fun line -> [ line, quantity ])
                            | _ -> None

                        let event =
                            match operation, quantities with
                            | AdminOperation.Scan, _ ->
                                ReturnTrackingEventId.create $"scan:{key}"
                                |> Result.map CarrierScanReceived
                                |> Result.toOption
                            | AdminOperation.Receive, Some lines -> Some(ItemsReceived lines)
                            | AdminOperation.Approve, Some lines -> Some(InspectionApproved lines)
                            | AdminOperation.Reject, _ -> Some(InspectionRejected operatorRejected)
                            | AdminOperation.Refund, _ -> Some RefundStartRequested
                            | AdminOperation.Close, _ -> Some CloseRequested
                            | (AdminOperation.Receive | AdminOperation.Approve), None -> None

                        match event with
                        | None -> return! HttpErrors.unprocessable "Invalid return operation." context
                        | Some event ->
                            let returns = context.GetService<ReturnMachineClient>()
                            let action = context.TryGetRouteValue("action") |> Option.defaultValue ""

                            let! outcome =
                                Machine.send
                                    returns.Returns
                                    (Returns.returnEntityId id)
                                    (EventEnvelope.create $"return-op:{ReturnId.wireString id}:{action}:{key}" event)
                                    context.RequestAborted

                            return!
                                HttpErrors.respond
                                    (HttpOutcome.ofSend outcome)
                                    "The return cannot make that transition."
                                    (fun context ->
                                        Web.redirect $"/admin/returns/{ReturnId.wireString id}" context :> Task)
                                    context
            }
