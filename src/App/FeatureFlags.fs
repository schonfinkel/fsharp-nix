namespace App

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open App.Domain
open Microsoft.FeatureManagement

type WorkflowFailure =
    | FlagNotFound
    | Conflict

[<RequireQualifiedAccess>]
module FeatureDefinitions =
    let fromCurrent (definition: FeatureInterval) =
        let filters: IEnumerable<FeatureFilterConfiguration> =
            match definition.State with
            | Enabled -> [ FeatureFilterConfiguration(Name = "AlwaysOn") ]
            | Disabled -> []

        FeatureDefinition(Name = FeatureFlag.persistedName definition.Flag, EnabledFor = filters)

type private AsyncEnumerator<'value>(values: 'value array, cancellationToken: CancellationToken) =
    let mutable index = -1

    interface IAsyncEnumerator<'value> with
        member _.Current = values[index]

        member _.MoveNextAsync() =
            cancellationToken.ThrowIfCancellationRequested()
            index <- index + 1
            ValueTask<bool>(index < values.Length)

        member _.DisposeAsync() = ValueTask()

type DatabaseFeatureDefinitionProvider(store: IFeatureFlagStore) =
    interface IFeatureDefinitionProvider with
        member _.GetFeatureDefinitionAsync(name: string) =
            task {
                match FeatureFlag.tryParse name with
                | None -> return null
                | Some flag ->
                    let! definition = store.GetCurrent(flag, CancellationToken.None)
                    return definition |> Option.map FeatureDefinitions.fromCurrent |> Option.toObj
            }

        member _.GetAllFeatureDefinitionsAsync() =
            { new IAsyncEnumerable<FeatureDefinition> with
                member _.GetAsyncEnumerator(cancellationToken) =
                    let definitions =
                        task {
                            let! current = store.GetAllCurrent cancellationToken
                            return current |> List.map FeatureDefinitions.fromCurrent |> List.toArray
                        }

                    let mutable enumerator: IAsyncEnumerator<FeatureDefinition> option = None

                    { new IAsyncEnumerator<FeatureDefinition> with
                        member _.Current =
                            enumerator
                            |> Option.map _.Current
                            |> Option.defaultWith (fun () ->
                                invalidOp "The async feature definition enumerator has not started.")

                        member _.MoveNextAsync() =
                            ValueTask<bool>(
                                task {
                                    match enumerator with
                                    | Some current -> return! current.MoveNextAsync().AsTask()
                                    | None ->
                                        let! values = definitions
                                        let current = AsyncEnumerator(values, cancellationToken) :> IAsyncEnumerator<_>
                                        enumerator <- Some current
                                        return! current.MoveNextAsync().AsTask()
                                }
                            )

                        member _.DisposeAsync() =
                            match enumerator with
                            | Some current -> current.DisposeAsync()
                            | None -> ValueTask() } }

[<RequireQualifiedAccess>]
module Workflow =
    let schedule (store: IFeatureFlagStore) now change cancellationToken =
        task {
            let! result = store.Schedule(change, now, cancellationToken)

            return
                result
                |> Result.mapError (function
                    | MissingFeature -> FlagNotFound
                    | TemporalGap
                    | BoundaryExists -> Conflict)
        }
