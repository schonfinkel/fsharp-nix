namespace App.Views

open System
open App.Domain

type LoginModel =
    { Email: string
      ReturnUrl: string
      Error: string option }

type RegisterModel =
    { Email: string
      Error: string option
      IdempotencyKey: string }

type AccountRequestModel =
    { Email: string
      Message: string option
      Error: string option
      IdempotencyKey: string }

type AccountTokenModel =
    { FlowId: string
      Token: string
      Error: string option }

type PasswordResetModel =
    { FlowId: string
      Token: string
      Error: string option }

type AccountNoticeModel =
    { Message: string
      Error: string option }

type TwoFactorModel =
    { ReturnUrl: string
      DevelopmentKey: string option
      Error: string option }

type EnrollmentModel =
    { UserName: string
      Email: string
      SharedKey: string option
      AuthenticatorUri: string option
      RecoveryCodesLeft: int
      IsEnabled: bool
      Error: string option }

type AdminFormState =
    { Enabled: bool
      EffectiveAt: string
      Error: string option }

type AdminFeatureModel =
    { Schedule: FeatureSchedule
      Form: AdminFormState }

type ProbeModel =
    { Phase: string
      Runs: int
      Epoch: uint64
      CommandResult: string
      Receipts: int
      Outbox: string }

type RuntimeHealthModel =
    { Component: string
      Phase: string
      LastSuccessAt: DateTimeOffset option
      ConsecutiveFailures: int
      Failure: string option }

type OutboxHealthModel =
    { Name: string
      Pending: int64
      Claimable: int64
      Leased: int64
      Sent: int64
      Dead: int64
      OldestPendingAt: DateTimeOffset option
      MaximumPendingAttempts: int option }

type FlowRequestHealthModel =
    { Kind: string
      Status: string
      Count: int64
      ExpiredRequested: int64
      OldestCreatedAt: DateTimeOffset }

type DeadlineHealthModel =
    { Kind: string
      Status: string
      Count: int64
      Due: int64
      Leased: int64
      EarliestPendingDeadline: DateTimeOffset option }

type OperationalHealthModel =
    { CapturedAt: DateTimeOffset
      Runtime: RuntimeHealthModel list
      Outboxes: OutboxHealthModel list
      FlowRequests: FlowRequestHealthModel list
      Deadlines: DeadlineHealthModel list }

type CatalogProductModel =
    { ProductId: string
      Sku: string
      Name: string
      Description: string
      UnitPrice: string
      OnHand: int
      Active: bool }

type CatalogModel =
    { Query: string
      Products: CatalogProductModel list }

type ProductFormModel =
    { ProductId: string option
      Sku: string
      Name: string
      Description: string
      Price: string
      Currency: string
      OnHand: string
      Error: string option }

type AdminCatalogModel =
    { Products: CatalogProductModel list
      Form: ProductFormModel }

type CartLineModel =
    { ProductId: string
      Sku: string
      Name: string
      UnitPrice: string
      Quantity: int }

type CartModel =
    { Epoch: int64
      Lines: CartLineModel list
      Count: int
      Total: string
      Error: string option }
