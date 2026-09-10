namespace App.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open App
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Caching.Memory
open Oxpecker.ViewEngine
open Xunit

type FakeFeatureFlagStore(?scheduleFailure: StoreFailure) =
    let mutable currentCalls = 0
    let mutable allCalls = 0
    let mutable failure = scheduleFailure
    let definitions = Dictionary<FeatureFlag, CurrentDefinition>()
    let history = Dictionary<FeatureFlag, HistoryEntry list>()

    let initial flag enabled : CurrentDefinition =
        let start = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

        { Flag = flag
          Enabled = enabled
          FilterName = None
          FilterParameters = None
          ValidFrom = start
          ValidTo = None
          RecordedAt = start }

    do
        for flag, enabled in [ NewDashboard, true; BetaCheckout, false ] do
            let definition = initial flag enabled
            definitions[flag] <- definition

            history[flag] <-
                [ { Flag = flag
                    Enabled = enabled
                    FilterName = None
                    FilterParameters = None
                    ValidFrom = definition.ValidFrom
                    ValidTo = None
                    RecordedAt = definition.RecordedAt } ]

    member _.CurrentCalls = currentCalls
    member _.AllCalls = allCalls
    member _.SetScheduleFailure value = failure <- value

    member _.SetEnabled(flag, enabled) =
        definitions[flag] <-
            { definitions[flag] with
                Enabled = enabled }

    interface IFeatureFlagStore with
        member _.GetCurrent(flag, _) =
            currentCalls <- currentCalls + 1

            match definitions.TryGetValue flag with
            | true, definition -> Task.FromResult(Some definition)
            | false, _ -> Task.FromResult None

        member _.GetAllCurrent(_) =
            allCalls <- allCalls + 1
            definitions.Values |> Seq.toList |> Task.FromResult

        member _.GetHistory(flag, _) =
            match history.TryGetValue flag with
            | true, entries -> Task.FromResult entries
            | false, _ -> Task.FromResult []

        member _.Schedule(change, now, _) =
            match failure with
            | Some error -> Task.FromResult(Error error)
            | None ->
                let effectiveAt = Scheduling.effectiveTimestamp now change.EffectiveTime
                let previous = definitions[change.Flag]

                let next =
                    { previous with
                        Enabled = change.Enabled
                        ValidFrom = effectiveAt
                        RecordedAt = now }

                definitions[change.Flag] <- next

                history[change.Flag] <-
                    { Flag = previous.Flag
                      Enabled = previous.Enabled
                      FilterName = previous.FilterName
                      FilterParameters = previous.FilterParameters
                      ValidFrom = previous.ValidFrom
                      ValidTo = Some effectiveAt
                      RecordedAt = previous.RecordedAt }
                    :: { Flag = next.Flag
                         Enabled = next.Enabled
                         FilterName = next.FilterName
                         FilterParameters = next.FilterParameters
                         ValidFrom = next.ValidFrom
                         ValidTo = next.ValidTo
                         RecordedAt = next.RecordedAt }
                    :: history[change.Flag]

                Task.FromResult(Ok())

module UnitTests =
    [<Fact>]
    let ``development login password hash is valid`` () =
        let hash =
            "AQAAAAEAAYagAAAAEMoYqa1WkeGpOvW4HJDH1tdTStebCy7kT1nExUm64FaEi8kJttez9rCwS/lgnNHW8w=="

        let result =
            PasswordHasher<ApplicationUser>().VerifyHashedPassword(
                ApplicationUser(),
                hash,
                "Test-Operator-42!"
            )

        Assert.NotEqual(PasswordVerificationResult.Failed, result)

    [<Fact>]
    let ``persisted feature names round trip explicitly`` () =
        for flag in FeatureFlag.all do
            let name = FeatureFlag.persistedName flag
            Assert.Equal(Some flag, FeatureFlag.tryParse name)

        Assert.Equal(None, FeatureFlag.tryParse "new-dashboard")

    [<Fact>]
    let ``scheduling rejects past and malformed timestamps`` () =
        let now = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)

        Assert.Equal(Error EffectiveTimeIsInPast, Scheduling.parseEffectiveTime now (Some "2026-03-01T11:59:59Z"))

        match Scheduling.parseEffectiveTime now (Some "not-a-date") with
        | Error(InvalidEffectiveTime _) -> ()
        | actual -> Assert.Fail $"Expected invalid effective time, got {actual}."

        Assert.Equal(Ok Now, Scheduling.parseEffectiveTime now None)

    [<Fact>]
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

    [<Fact>]
    let ``cache hits expires and evicts only after success`` () =
        task {
            let inner = FakeFeatureFlagStore()
            use memory = new MemoryCache(MemoryCacheOptions())

            let cached =
                CachingFeatureFlagStore(inner, memory, TimeSpan.FromMilliseconds 30.) :> IFeatureFlagStore

            let! _ = cached.GetCurrent(NewDashboard, CancellationToken.None)
            let! _ = cached.GetCurrent(NewDashboard, CancellationToken.None)
            Assert.Equal(1, inner.CurrentCalls)

            do! Task.Delay 50
            let! _ = cached.GetCurrent(NewDashboard, CancellationToken.None)
            Assert.Equal(2, inner.CurrentCalls)

            inner.SetScheduleFailure(Some TemporalConflict)

            let change =
                { Flag = NewDashboard
                  Enabled = false
                  EffectiveTime = Now }

            let! _ = cached.Schedule(change, DateTimeOffset.UtcNow, CancellationToken.None)
            let! _ = cached.GetCurrent(NewDashboard, CancellationToken.None)
            Assert.Equal(2, inner.CurrentCalls)

            inner.SetScheduleFailure None
            let! _ = cached.Schedule(change, DateTimeOffset.UtcNow, CancellationToken.None)
            let! _ = cached.GetCurrent(NewDashboard, CancellationToken.None)
            Assert.Equal(3, inner.CurrentCalls)
        }

    [<Fact>]
    let ``demo view selects enabled and disabled variants`` () =
        let enabled = Views.demoFragment NewDashboard true |> Render.toString
        let disabled = Views.demoFragment NewDashboard false |> Render.toString

        Assert.Contains("New dashboard", enabled)
        Assert.Contains("Classic dashboard", disabled)
        Assert.Contains("hx-get=\"/demo/dashboard\"", enabled)
