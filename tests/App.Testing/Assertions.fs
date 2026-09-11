namespace App.Tests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Expecto

[<AbstractClass; Sealed>]
type Assert private () =
    static member Equal<'value when 'value: equality>(expected: 'value, actual: 'value) : unit =
        Expect.equal actual expected "Values should be equal."

    static member NotEqual<'value when 'value: equality>(expected: 'value, actual: 'value) : unit =
        Expect.notEqual actual expected "Values should differ."

    static member True(actual: bool) =
        Expect.isTrue actual "Value should be true."

    static member True(actual: bool, message: string) = Expect.isTrue actual message

    static member False(actual: bool) =
        Expect.isFalse actual "Value should be false."

    static member Empty<'value>(actual: IEnumerable<'value>) =
        Expect.isEmpty actual "Collection should be empty."

    static member Contains(expectedSubstring: string, actual: string) =
        Expect.stringContains actual expectedSubstring "Text should contain the expected value."

    static member Contains<'value>(actual: IEnumerable<'value>, predicate: 'value -> bool) : unit =
        Expect.isTrue (actual |> Seq.exists predicate) "Collection should contain a matching value."

    static member DoesNotContain(expectedSubstring: string, actual: string) =
        Expect.isFalse
            (actual.Contains(expectedSubstring, StringComparison.Ordinal))
            "Text should not contain the value."

    static member DoesNotContain<'value>(actual: IEnumerable<'value>, predicate: 'value -> bool) : unit =
        Expect.isFalse (actual |> Seq.exists predicate) "Collection should not contain a matching value."

    static member StartsWith(expectedStart: string, actual: string) =
        Expect.stringStarts actual expectedStart "Text should start with the expected value."

    static member All<'value>(actual: IEnumerable<'value>, assertion: 'value -> unit) : unit =
        actual |> Seq.iter assertion

    static member Fail(message: string) : 'value = Tests.failtest message

    static member ThrowsAsync<'error when 'error :> exn>(action: unit -> Task) =
        task {
            try
                do! action ()
                return Tests.failtest $"Expected {typeof<'error>.Name} to be thrown."
            with
            | :? 'error as error -> return error
            | error -> return Tests.failtest $"Expected {typeof<'error>.Name}, but got {error.GetType().Name}."
        }
