# F#, Oxpecker, PostgreSQL and Nix

[![built with nix](https://builtwithnix.org/badge.svg)](https://builtwithnix.org)
[![[.Net] Build & Test](https://github.com/mtrsk/fsharp-nix/actions/workflows/build.yml/badge.svg)](https://github.com/mtrsk/fsharp-nix/actions/workflows/build.yml)
[![[Nix] Build Container](https://github.com/mtrsk/fsharp-nix/actions/workflows/build-container.yml/badge.svg)](https://github.com/mtrsk/fsharp-nix/actions/workflows/build-container.yml)

This .NET 10 sample stores time-bounded feature definitions in PostgreSQL, evaluates them through `Microsoft.FeatureManagement`, and renders full pages and HTMX 4 fragments with Oxpecker.

- The MFA-protected admin can apply a flag immediately or schedule it for a future time in the browser's local time zone while retaining the complete interval history.\
- Authentication uses ASP.NET Core Identity with a custom Npgsql store, TOTP, and single-use recovery codes. 
- Multi-step bootstrap and authenticator state transitions use `FsToolkit.ErrorHandling` task results to short-circuit failures explicitly.

The demo and admin feature cards stay current through HTMX 4 server-sent events. Scheduling publishes a PostgreSQL notification, and each event stream also waits for the next persisted temporal boundary before telling the cards to refresh. PostgreSQL is queried directly as the cross-instance source of truth; there is no process-local feature cache to invalidate.

The catalog is browsed and full-text searched from a relational `products`/`product_stock` schema (PostgreSQL `tsvector` GIN search) and managed through an MFA-protected operator page. Guest and customer carts are one Automata `carts` machine: a guest's identity is a 256-bit bearer capability whose purpose-scoped keyed hash is stored (the raw token never is), every mutation carries an epoch for optimistic concurrency (a stale epoch is a `409` with the authoritative fragment), and signing in merges a guest cart into the customer cart through a durable saga. Cart mutations return HTMX 4 fragments with out-of-band badge and alert swaps, and other tabs stay current through the cart SSE stream.

Authenticated customers check out through Automata `orders` and `payments` machines backed by immutable order snapshots, a relational stock-reservation ledger, and an idempotent gateway-operation ledger. Reservation effects lock product rows in canonical order and reserve the complete order or none of it. A separate pay step authorizes an opaque payment-method reference, commits stock only after approval, permits retry after decline, and queries an unknown authorization before cancellation can release stock. Gateway calls run outside database transactions and use stable operation ids; provider references and bounded result codes are durable, but card credentials are not representable. Scheduled authorization-expiry and unknown-outcome reconciliation remain P7 work. Shipping and tax default to `5.00` and `0.08` and can be set with `Checkout__ShippingAmount` and `Checkout__TaxRate`.

## Development

Enter the flake-backed devenv shell and start its PostgreSQL service:

```sh
nix develop --impure
devenv up -d
```

The shell exports `ConnectionStrings__App` for the local `fsnix` database. The database listens only on `127.0.0.1:5432`; no Docker PostgreSQL instance is used.

The environment also runs [Mailpit](https://github.com/axllent/mailpit) for development email: the application relays account emails over SMTP to `127.0.0.1:1025`, and the mailbox UI plus REST API are on <http://127.0.0.1:8025>. The `Email__*` shell variables point the application at it; production configures its own SMTP provider explicitly (startup rejects loopback hosts and missing TLS outside Development).

Development migrations create a manual test account:

- Email: `test.operator@example.test`
- Password: `Test-Operator-42!`

The account is development-only and must enroll an authenticator on its first login. Alternatively, bootstrap another account, then run the application:

```sh
Bootstrap__Username=operator \
Bootstrap__Email=operator@example.test \
Bootstrap__Password='replace-with-a-strong-password' \
dotnet run --project src/App/App.fsproj -- --bootstrap-user
run-app
```

Apply migrations before bootstrap or normal startup. Both runtime modes perform read-only schema and migration-journal checks and refuse to start when the database is missing or stale:

```sh
make migrate
dotnet run --project src/App/App.fsproj
```

`make migrate` (or `dotnet run --project src/App/App.fsproj -- --migrate`) is the only application mode that mutates schemas. Run it once as the deployment owner before starting runtime instances.

Open <http://localhost:5000/> for the demo, <http://localhost:5000/account/register> to register, or <http://localhost:5000/admin/features> for feature administration. New accounts must confirm their email before using account recovery. Password-reset requests return a uniform response regardless of whether the address exists. Authenticated users can start email changes from `/account/email`; confirmation and password changes are applied only after the antiforgery-protected form submission.

Authenticator setup renders an `otpauth://` QR code with the locally vendored [QRCode.js 1.0.0](https://github.com/davidshimjs/qrcodejs) asset. Recovery codes are shown once, stored only as SHA-256 hashes, and atomically consumed. Authenticator reset and recovery-code regeneration require a session carrying the `amr=mfa` claim; password-only sessions cannot administer features or weaken MFA.

For local testing without an authenticator app, run `make totp`, paste the manual authenticator key when prompted, and enter the generated six-digit code in the application. Treat the key as a password and never commit it.

The application defaults to the `Development` .NET environment. Set `DOTNET_ENVIRONMENT=Production` in production. Production startup requires `DataProtection__KeyRingPath` to name durable storage shared by every instance and retains the stable `fsnix` application discriminator across deployments. It likewise requires an explicit `Email__Host`, `Email__UseTls=true`, and `Email__PublicOrigin`. Protect that storage at rest with the platform's encrypted volume or secret-store controls; the application does not treat a writable plaintext host path as key encryption. Serve the application over HTTPS.

## Health and security

The application exposes three minimal, anonymous, non-cacheable probes. `/health/live` reports
only that the HTTP process is serving, `/health/startup` confirms boot checks and machine-client
registration, and `/health/ready` additionally checks PostgreSQL plus recent successful passes by
the integration-outbox relay, email relay, and deadline scanner. Worker freshness defaults to 30
seconds and can be changed with `Health__WorkerMaximumAgeSeconds`.

MFA-authenticated operators can inspect aggregate runtime, outbox, account-flow, and deadline
health at `/admin/operations`. The page never renders payloads, destination addresses, callback or
entity identifiers, lease owners, or durable error text. Dead rows are an operational warning,
not a readiness failure; recovery uses audited re-enqueue/reconciliation commands rather than
editing FSM state.

Capability and redaction requirements are defined in [`docs/security-policy.md`](docs/security-policy.md).
Capability primitives generate versioned 256-bit bearer values and purpose/entity-bound keyed
digests. The guest cart capability stores only a purpose-scoped keyed hash and is revoked after a
successful merge; order-tracking capabilities and their exchange endpoint are introduced with the
tracking feature. Durable action failures and manual-review reasons use closed versioned codes,
and logs record exception types rather than unrestricted exception messages.

## Build and test

The existing solution is `fsnix.slnx`:

```sh
dotnet build fsnix.slnx
make test
```

Tests use Expecto 11 and are split into focused executable projects. Run the suites independently with `make test-unit`, `make test-database`, or `make test-http`; `make test-integration` depends on the latter two. Use `make test` for the supported serial order because concurrent integration executables can exceed the development PostgreSQL connection limit. CI runs the serial suite and schema check through the flake's `devenv-test` derivation against the pinned PostgreSQL/extension stack. Additional Expecto arguments can be passed after `--` to any test project, such as `dotnet run --project tests/App.DatabaseTests/App.DatabaseTests.fsproj -- --filter scheduling`.

`App.UnitTests` contains fast domain/application/view tests, `App.DatabaseTests` exercises persistence and migrations, `App.HttpTests` covers the real HTTP/htmx and authentication stack, and `App.Testing` contains shared fakes, assertions, and database fixtures. PostgreSQL integration cases use the already-running devenv server. Every case creates a uniquely named database, runs sequentially within its executable, and drops its database afterward. The local `fsnix` role has `CREATEDB` solely so the test fixture can provision those databases; tests do not start containers. The authentication tests exercise bootstrap, lockout, password login, TOTP enrollment, MFA authorization, recovery login, one-time recovery-code redemption and regeneration, and authenticator reset through the real HTTP and PostgreSQL stack.

`make schema-check` regenerates into the workspace temporarily, compares it with the checked-in output, and restores the original files. It fails when the live migrated schema and generated contract differ. Normal compilation does not connect to PostgreSQL; it compiles against the checked-in generated file.

## Nix packaging

Regenerate the NuGet dependency lock after changing package references:

```sh
make nix-lock
```

The Make target updates `deps.json` when solution or project dependency metadata changes. It restores packages into the ignored `out/` directory and converts them with nixpkgs' `nuget-to-json` tool.

Build/check the application and build the non-root layered OCI image with:

```sh
nix flake check
nix build
nix build .#oci
```

The image exposes port 8080 and expects `ConnectionStrings__App` to be supplied by the runtime environment. Load `result` with the OCI-compatible container tool of your choice.
