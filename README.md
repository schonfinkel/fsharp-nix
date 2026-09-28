# F#, Oxpecker, PostgreSQL and Nix

[![built with nix](https://builtwithnix.org/badge.svg)](https://builtwithnix.org)
[![[Nix] Build & Test](https://github.com/mtrsk/fsharp-nix/actions/workflows/build.yml/badge.svg)](https://github.com/mtrsk/fsharp-nix/actions/workflows/build.yml)
[![[Nix] Build Container](https://github.com/mtrsk/fsharp-nix/actions/workflows/build-container.yml/badge.svg)](https://github.com/mtrsk/fsharp-nix/actions/workflows/build-container.yml)

This .NET 10 sample stores time-bounded feature definitions in PostgreSQL, evaluates them through `Microsoft.FeatureManagement`, and renders full pages and HTMX 4 fragments with Oxpecker.

- The MFA-protected admin can apply a flag immediately or schedule it for a future time in the browser's local time zone while retaining the complete interval history.\
- Authentication uses ASP.NET Core Identity with a custom Npgsql store, TOTP, and single-use recovery codes. 
- Multi-step bootstrap and authenticator state transitions use `FsToolkit.ErrorHandling` task results to short-circuit failures explicitly.

## Development

Enter the flake-backed devenv shell and start its PostgreSQL service:

```sh
nix develop --impure
devenv up -d
```

The shell exports `ConnectionStrings__App` for the local `fsnix` database. The database listens only on `127.0.0.1:5432`, no Docker PostgreSQL instance is used.

The environment also runs [Mailpit](https://github.com/axllent/mailpit) for development email: 
- The application relays account emails over SMTP to `127.0.0.1:1025`, and the mailbox UI plus REST API are on <http://127.0.0.1:8025>. 
- The `Email__*` shell variables point the application at it, production configures its own SMTP provider explicitly (startup rejects loopback hosts and missing TLS outside Development).

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
