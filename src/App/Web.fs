namespace App

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Oxpecker.Htmx

[<RequireQualifiedAccess>]
module Web =
    let isHtmx (context: HttpContext) =
        context.Request.Headers.ContainsKey HxRequestHeader.Request

    let varyHtmx (context: HttpContext) =
        context.Response.Headers.Vary <- StringValues HxRequestHeader.Request

    let noStore (context: HttpContext) =
        context.Response.Headers.CacheControl <- StringValues "no-store"
        context.Response.Headers.Pragma <- StringValues "no-cache"

    let redirect (defaultLocation: string) (context: HttpContext) =
        task {
            let location =
                if String.IsNullOrWhiteSpace defaultLocation then
                    "/"
                else
                    defaultLocation

            if isHtmx context then
                context.Response.StatusCode <- StatusCodes.Status204NoContent
                context.Response.Headers[HxResponseHeader.Redirect] <- StringValues location
            else
                context.Response.Redirect location
        }
