open System
open System.Collections.Generic
open System.Security.Cryptography

let decodeBase32 (value: string) =
    let alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
    let bytes = ResizeArray<byte>()
    let mutable buffer = 0
    let mutable bits = 0

    for character in value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant() do
        let digit = alphabet.IndexOf character

        if digit < 0 then
            invalidArg (nameof value) $"'{character}' is not a valid Base32 character."

        buffer <- (buffer <<< 5) ||| digit
        bits <- bits + 5

        if bits >= 8 then
            bits <- bits - 8
            bytes.Add(byte (buffer >>> bits))
            buffer <- if bits = 0 then 0 else buffer &&& ((1 <<< bits) - 1)

    bytes.ToArray()

let totpAt (timestamp: DateTimeOffset) key =
    let counter = timestamp.ToUnixTimeSeconds() / 30L
    let counterBytes = BitConverter.GetBytes counter

    if BitConverter.IsLittleEndian then
        Array.Reverse counterBytes

    use hmac = new HMACSHA1(decodeBase32 key)
    let hash = hmac.ComputeHash counterBytes
    let offset = int hash[hash.Length - 1] &&& 0x0f

    let binary =
        ((int hash[offset] &&& 0x7f) <<< 24)
        ||| ((int hash[offset + 1] &&& 0xff) <<< 16)
        ||| ((int hash[offset + 2] &&& 0xff) <<< 8)
        ||| (int hash[offset + 3] &&& 0xff)

    (binary % 1_000_000).ToString("D6")

try
    printf "Authenticator key: "
    let key = Console.ReadLine()

    if String.IsNullOrWhiteSpace key then
        invalidArg (nameof key) "An authenticator key is required."

    let now = DateTimeOffset.UtcNow
    let remainingSeconds = 30L - now.ToUnixTimeSeconds() % 30L
    printfn "Code: %s (%d seconds remaining)" (totpAt now key) remainingSeconds
with :? ArgumentException as error ->
    eprintfn "Error: %s" error.Message
    Environment.ExitCode <- 1
