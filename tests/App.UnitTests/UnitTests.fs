namespace App.Tests

open System
open System.Threading
open App
open App.Database
open App.Domain
open App.Views
open Expecto
open Microsoft.AspNetCore.Identity
open Oxpecker.ViewEngine

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
                  ``demo view selects variants and distinguishes event and button swaps`` ]
