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

type ReconciliationHealthModel =
    { Unknown: int64
      Due: int64
      Parked: int64
      MaximumChecks: int option }

type OperationalHealthModel =
    { CapturedAt: DateTimeOffset
      Runtime: RuntimeHealthModel list
      Outboxes: OutboxHealthModel list
      FlowRequests: FlowRequestHealthModel list
      Deadlines: DeadlineHealthModel list
      Reconciliation: ReconciliationHealthModel }

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

type CheckoutModel =
    { OrderKey: string
      Email: string
      Recipient: string
      Line1: string
      Line2: string
      City: string
      Region: string
      PostalCode: string
      CountryCode: string
      Error: string option }

type OrderPayModel =
    { Attempt: string
      Methods: (string * string) list }

type ReturnLineModel =
    { LineId: string
      Name: string
      Available: int
      ReturnKey: string }

type OrderStatusModel =
    {
        OrderId: string
        Status: string
        Total: string
        CanCancel: bool
        TooLateToCancel: bool
        Pay: OrderPayModel option
        ReturnLines: ReturnLineModel list
        ReturnStatuses: (string * string) list
        /// <summary>Download URL and display number, once the invoice PDF is stored.</summary>
        Invoice: (string * string) option
        /// <summary>Download URL and number of each rendered credit note.</summary>
        CreditNotes: (string * string) list
        Error: string option
    }

type InvoiceRowModel =
    {
        Number: string
        /// <summary>"Invoice", or "Credit note (INV-…)" naming the credited invoice.</summary>
        Kind: string
        IssuedOn: string
        Total: string
        OrderId: string
        Status: string
        DownloadUrl: string option
        /// <summary>Operator retry form: post URL and a fresh idempotency key.</summary>
        Retry: (string * string) option
    }

type InvoiceListModel =
    { Title: string
      Rows: InvoiceRowModel list
      OlderUrl: string option }
