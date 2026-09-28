namespace App.Domain

type OrderAddress =
    { Recipient: NonEmptyString
      Line1: NonEmptyString
      Line2: string option
      City: NonEmptyString
      Region: string option
      PostalCode: NonEmptyString
      CountryCode: string }

[<RequireQualifiedAccess>]
module OrderAddress =
    let create
        (recipient: string)
        (line1: string)
        (line2: string option)
        (city: string)
        (region: string option)
        (postalCode: string)
        (countryCode: string)
        =
        let required value = NonEmptyString.create 200 value

        let optional value =
            match value with
            | Some text when not (System.String.IsNullOrWhiteSpace text) ->
                NonEmptyString.create 200 text |> Result.map (NonEmptyString.value >> Some)
            | _ -> Ok None

        required recipient
        |> Result.bind (fun recipient ->
            required line1
            |> Result.bind (fun line1 ->
                optional line2
                |> Result.bind (fun line2 ->
                    required city
                    |> Result.bind (fun city ->
                        optional region
                        |> Result.bind (fun region ->
                            required postalCode
                            |> Result.bind (fun postalCode ->
                                let code =
                                    if isNull countryCode then
                                        ""
                                    else
                                        countryCode.Trim().ToUpperInvariant()

                                if code.Length <> 2 || not (code |> Seq.forall System.Char.IsAsciiLetter) then
                                    Error "Enter a two-letter country code."
                                else
                                    Ok
                                        { Recipient = recipient
                                          Line1 = line1
                                          Line2 = line2
                                          City = city
                                          Region = region
                                          PostalCode = postalCode
                                          CountryCode = code }))))))
