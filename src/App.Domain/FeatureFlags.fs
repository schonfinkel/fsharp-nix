namespace App.Domain

open System
open System.Globalization
open System.Threading
open System.Threading.Tasks

type FeatureFlag =
    | NewDashboard
    | BetaCheckout

[<RequireQualifiedAccess>]
module FeatureFlag =
    let all = [ NewDashboard; BetaCheckout ]

    let persistedName =
        function
        | NewDashboard -> "NewDashboard"
        | BetaCheckout -> "BetaCheckout"

    let tryParse =
        function
        | "NewDashboard" -> Some NewDashboard
        | "BetaCheckout" -> Some BetaCheckout
        | _ -> None

type FeatureState =
    | Enabled
    | Disabled

[<RequireQualifiedAccess>]
module FeatureState =
    let ofBool value = if value then Enabled else Disabled

    let toBool =
        function
        | Enabled -> true
        | Disabled -> false

type EffectiveTime =
    | Immediate
    | ScheduledAt of DateTimeOffset

type ScheduleChange =
    { Flag: FeatureFlag
      State: FeatureState
      EffectiveTime: EffectiveTime }

type FeatureInterval =
    { Flag: FeatureFlag
      State: FeatureState
      ValidFrom: DateTimeOffset
      ValidTo: DateTimeOffset option
      RecordedAt: DateTimeOffset }

type FeatureSchedule =
    { Current: FeatureInterval
      History: FeatureInterval list }

type ScheduleValidationError =
    | InvalidEffectiveTime of string
    | EffectiveTimeIsInPast

type ScheduleFailure =
    | MissingFeature
    | TemporalGap
    | BoundaryExists

type ScheduleOutcome =
    | Changed
    | Unchanged

[<RequireQualifiedAccess>]
module Scheduling =
    let private hasExplicitOffset (value: string) =
        let separator = value.IndexOf 'T'
        let offset = max (value.LastIndexOf '+') (value.LastIndexOf '-')

        value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
        || (separator >= 0 && offset > separator)

    let parseEffectiveTime (now: DateTimeOffset) (value: string option) =
        match value |> Option.map _.Trim() with
        | None
        | Some "" -> Ok Immediate
        | Some value when not (hasExplicitOffset value) -> Error(InvalidEffectiveTime value)
        | Some value ->
            let styles = DateTimeStyles.AllowWhiteSpaces ||| DateTimeStyles.AdjustToUniversal

            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, styles) with
            | true, timestamp when timestamp >= now.ToUniversalTime() -> Ok(ScheduledAt timestamp)
            | true, _ -> Error EffectiveTimeIsInPast
            | false, _ -> Error(InvalidEffectiveTime value)

    let effectiveTimestamp (now: DateTimeOffset) =
        function
        | Immediate -> now.ToUniversalTime()
        | ScheduledAt timestamp -> timestamp.ToUniversalTime()

type IFeatureFlagStore =
    abstract member GetCurrent: FeatureFlag * CancellationToken -> Task<FeatureInterval option>
    abstract member GetAllCurrent: CancellationToken -> Task<FeatureInterval list>
    abstract member GetSchedule: FeatureFlag * CancellationToken -> Task<FeatureSchedule option>

    abstract member Schedule:
        ScheduleChange * DateTimeOffset * CancellationToken -> Task<Result<ScheduleOutcome, ScheduleFailure>>

type FeatureChange =
    | RefreshRequired
    | KeepAlive

type IFeatureChangeSubscription =
    inherit IAsyncDisposable
    abstract member Next: CancellationToken -> Task<FeatureChange>

type IFeatureChangeSource =
    abstract member Subscribe: CancellationToken -> Task<IFeatureChangeSubscription>
