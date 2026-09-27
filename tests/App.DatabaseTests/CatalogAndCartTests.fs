namespace App.DatabaseTests

open System
open System.Threading
open App.Database
open App.Domain
open App.Tests
open Npgsql

type CatalogStoreTests(fixture: PostgreSqlFixture) =
    let store = CatalogStore(NpgsqlDataSource.Create fixture.ConnectionString)

    member _.``seed catalog browses and searches``() =
        task {
            let! products = store.Browse(true, CancellationToken.None)
            Assert.Equal(5, products.Length)

            let! search = store.Search("espresso", CancellationToken.None)
            Assert.Equal(1, search.Length)
            Assert.Equal("Espresso Beans", NonEmptyString.value search[0].Name)

            let! empty = store.Search("", CancellationToken.None)
            Assert.Equal(5, empty.Length)
        }

    member _.``update bumps the price version``() =
        task {
            let productId = Guid.Parse "00000000-0000-0000-0000-000000000001"

            let name =
                NonEmptyString.create 200 "Espresso Beans Premium"
                |> Result.defaultWith Assert.Fail

            let price = Money.create 27.50m "USD" |> Result.defaultWith Assert.Fail

            let! updated = store.Update(productId, name, None, price, CancellationToken.None)
            Assert.Equal(Ok(), updated)

            let! product = store.Get(productId, CancellationToken.None)

            match product with
            | Some snapshot ->
                Assert.Equal("Espresso Beans Premium", NonEmptyString.value snapshot.Name)
                Assert.Equal(2L, PriceVersion.value snapshot.PriceVersion)
            | None -> Assert.Fail "The updated product was not found."
        }

    member _.``adjust stock rejects a negative balance``() =
        task {
            let productId = Guid.Parse "00000000-0000-0000-0000-000000000003"
            let! rejected = store.AdjustStock(productId, -9999, CancellationToken.None)
            Assert.Equal(Error CatalogFailure.InsufficientStock, rejected)
        }

    member _.``create rejects a duplicate sku``() =
        task {
            let sku = Sku.create "COFFEE-ESPRESSO" |> Result.defaultWith Assert.Fail
            let name = NonEmptyString.create 200 "Duplicate" |> Result.defaultWith Assert.Fail
            let price = Money.create 1m "USD" |> Result.defaultWith Assert.Fail

            let! result = store.Create(Guid.NewGuid(), sku, name, None, price, 0, CancellationToken.None)
            Assert.Equal(Error CatalogFailure.SkuAlreadyExists, result)
        }

type CartGuestCapabilitiesTests(fixture: PostgreSqlFixture) =
    let dataSource = NpgsqlDataSource.Create fixture.ConnectionString

    let key =
        CapabilityHashKey.create "primary" (Array.init 32 byte)
        |> Result.defaultWith Assert.Fail

    member _.``issue resolves and revokes``() =
        task {
            let capability = Capability.generate ()
            let entity = $"guest:{Guid.NewGuid():D}"

            do!
                CartGuestCapabilities.issue
                    dataSource
                    key
                    entity
                    capability
                    (DateTimeOffset.UtcNow.AddHours 1.)
                    CancellationToken.None

            let! resolved = CartGuestCapabilities.tryResolve dataSource key capability CancellationToken.None
            Assert.Equal(Some entity, resolved)

            do! CartGuestCapabilities.revokeForEntity dataSource entity CancellationToken.None
            let! afterRevoke = CartGuestCapabilities.tryResolve dataSource key capability CancellationToken.None
            Assert.Equal(None, afterRevoke)
        }

    member _.``a different key cannot resolve the capability``() =
        task {
            let capability = Capability.generate ()
            let entity = $"guest:{Guid.NewGuid():D}"

            do!
                CartGuestCapabilities.issue
                    dataSource
                    key
                    entity
                    capability
                    (DateTimeOffset.UtcNow.AddHours 1.)
                    CancellationToken.None

            let otherKey =
                CapabilityHashKey.create "rotated" (Array.init 32 (fun index -> byte (index + 1)))
                |> Result.defaultWith Assert.Fail

            let! resolved = CartGuestCapabilities.tryResolve dataSource otherKey capability CancellationToken.None
            Assert.Equal(None, resolved)
        }

type CartMergeTests(fixture: PostgreSqlFixture) =
    let dataSource = NpgsqlDataSource.Create fixture.ConnectionString

    member _.``capture is insert-once and applied snapshots leave the pending set``() =
        task {
            let mergeId = Guid.NewGuid()
            let lines = """[{"sku":"COFFEE-ESPRESSO","quantity":2}]"""

            let! captured = CartMerge.capture dataSource mergeId "guest:x" "customer:y" lines CancellationToken.None
            Assert.Equal(CartMerge.CaptureOutcome.Captured, captured)

            let! duplicate = CartMerge.capture dataSource mergeId "guest:x" "customer:y" lines CancellationToken.None

            Assert.Equal(CartMerge.CaptureOutcome.AlreadyCaptured, duplicate)

            let! pending = CartMerge.pending dataSource CancellationToken.None
            Assert.Equal(1, pending.Length)
            Assert.Equal(mergeId, pending[0].MergeId)
            Assert.Equal("customer:y", pending[0].TargetCustomerId)

            do! CartMerge.markApplied dataSource mergeId CancellationToken.None
            let! afterApplied = CartMerge.pending dataSource CancellationToken.None
            Assert.Equal(0, afterApplied.Length)
        }
