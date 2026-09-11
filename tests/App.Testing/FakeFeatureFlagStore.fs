namespace App.Tests

open System
open System.Collections.Generic
open System.Threading.Tasks
open App.Domain

type FakeFeatureFlagStore(?scheduleFailure: ScheduleFailure) =
    let mutable currentCalls = 0
    let mutable allCalls = 0
    let mutable failure = scheduleFailure
    let intervals = Dictionary<FeatureFlag, FeatureInterval list>()

    let initial flag state =
        let start = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

        { Flag = flag
          State = state
          ValidFrom = start
          ValidTo = None
          RecordedAt = start }

    do
        intervals[NewDashboard] <- [ initial NewDashboard Enabled ]
        intervals[BetaCheckout] <- [ initial BetaCheckout Disabled ]

    let currentAt now entries =
        entries
        |> List.tryFind (fun entry ->
            entry.ValidFrom <= now
            && entry.ValidTo |> Option.forall (fun validTo -> now < validTo))

    member _.CurrentCalls = currentCalls
    member _.AllCalls = allCalls
    member _.SetScheduleFailure value = failure <- value

    member _.SetState(flag, state) =
        intervals[flag] <-
            intervals[flag]
            |> List.map (fun entry ->
                if entry.ValidTo.IsNone then
                    { entry with State = state }
                else
                    entry)

    interface IFeatureFlagStore with
        member _.GetCurrent(flag, _) =
            currentCalls <- currentCalls + 1

            match intervals.TryGetValue flag with
            | true, entries -> currentAt DateTimeOffset.UtcNow entries |> Task.FromResult
            | false, _ -> Task.FromResult None

        member _.GetAllCurrent(_) =
            allCalls <- allCalls + 1

            intervals.Values
            |> Seq.choose (currentAt DateTimeOffset.UtcNow)
            |> Seq.toList
            |> Task.FromResult

        member _.GetSchedule(flag, _) =
            match intervals.TryGetValue flag with
            | true, entries ->
                match currentAt DateTimeOffset.UtcNow entries with
                | Some current -> Task.FromResult(Some { Current = current; History = entries })
                | None -> Task.FromResult None
            | false, _ -> Task.FromResult None

        member _.Schedule(change, now, _) =
            match failure with
            | Some error -> Task.FromResult(Error error)
            | None ->
                match intervals.TryGetValue change.Flag with
                | false, _ -> Task.FromResult(Error MissingFeature)
                | true, entries ->
                    let effectiveAt = Scheduling.effectiveTimestamp now change.EffectiveTime

                    match currentAt effectiveAt entries with
                    | None -> Task.FromResult(Error TemporalGap)
                    | Some containing when containing.State = change.State -> Task.FromResult(Ok Unchanged)
                    | Some containing when containing.ValidFrom = effectiveAt -> Task.FromResult(Error BoundaryExists)
                    | Some containing ->
                        let shortened =
                            { containing with
                                ValidTo = Some effectiveAt }

                        let next =
                            { Flag = change.Flag
                              State = change.State
                              ValidFrom = effectiveAt
                              ValidTo = containing.ValidTo
                              RecordedAt = now }

                        intervals[change.Flag] <-
                            next :: shortened :: (entries |> List.filter ((<>) containing))
                            |> List.sortByDescending _.ValidFrom

                        Task.FromResult(Ok Changed)
