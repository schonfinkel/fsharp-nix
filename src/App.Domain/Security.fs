namespace App.Domain

open System

[<RequireQualifiedAccess>]
module SafeDiagnostics =
    let forbiddenPropertyNames =
        Set.ofList
            [ "address"
              "authenticatorkey"
              "authorization"
              "capability"
              "cardnumber"
              "connectionstring"
              "cookie"
              "credential"
              "cvv"
              "email"
              "password"
              "providererror"
              "secret"
              "token" ]

    let exceptionType (error: exn) =
        if isNull error then
            "UnknownException"
        else
            error.GetType().Name
