namespace App

open System
open System.Threading
open System.Threading.Tasks
open App.Auth
open App.Database
open App.Domain
open ByzantineSystems.Automata.Core
open ByzantineSystems.Automata.DependencyInjection
open ByzantineSystems.Automata.Storage
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Identity
open Npgsql

/// <summary>
/// Resolves the restricted request row, generates the purpose-specific Identity token, protects
/// the notification payload, and hands the whole thing to the durable effect. The raw token and
/// destination email stay out of FSM state, receipts, and the notification callback.
/// </summary>
type AccountFlowEffectHandler
    (
        dataSource: NpgsqlDataSource,
        users: UserManager<ApplicationUser>,
        dataProtection: IDataProtectionProvider,
        timeProvider: TimeProvider
    ) =
    interface IActionHandler<FlowId, FlowAction, FlowActionError> with
        member _.HandleAsync(action: LeasedAction<FlowId, FlowAction>, ct: CancellationToken) =
            task {
                let record = action.Work
                let flowIdString = EntityId.value record.EntityId

                match Guid.TryParse flowIdString with
                | false, _ -> return Error FlowActionError.InvalidFlowEntityId
                | true, flowId ->
                    match! AccountFlowEffects.tryLoadRequest dataSource flowId ct with
                    | None -> return Error FlowActionError.FlowRequestNotActive
                    | Some request ->
                        let! user = users.FindByIdAsync(request.UserId.ToString())

                        if isNull user then
                            return Error FlowActionError.FlowUserNotFound
                        else
                            let! token =
                                match request.Kind with
                                | EmailVerification -> users.GenerateEmailConfirmationTokenAsync user
                                | PasswordReset -> users.GeneratePasswordResetTokenAsync user
                                | EmailChange -> users.GenerateChangeEmailTokenAsync(user, request.DestinationEmail)

                            if String.IsNullOrWhiteSpace token then
                                return Error FlowActionError.IdentityTokenEmpty
                            else
                                let lifespan =
                                    match request.Kind with
                                    | EmailVerification -> AccountTokenLifespans.EmailConfirmation
                                    | PasswordReset -> AccountTokenLifespans.PasswordReset
                                    | EmailChange -> AccountTokenLifespans.ChangeEmail

                                let expiresAt = timeProvider.GetUtcNow() + lifespan
                                let payload = AccountEmail.buildPayload request.FlowId request.Kind token
                                let protectedPayload = AccountEmail.protect dataProtection payload
                                let (SendNotification generation) = record.Action

                                return!
                                    AccountFlowEffects.apply
                                        dataSource
                                        record
                                        request
                                        expiresAt
                                        (NotificationQueued generation)
                                        (Some protectedPayload)
                                        ct
            }
