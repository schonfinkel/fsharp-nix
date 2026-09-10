# F#, Oxpecker, PostgreSQL and Nix

[![built with nix](https://builtwithnix.org/badge.svg)](https://builtwithnix.org)
[![[.Net] Build & Test](https://github.com/mtrsk/fsharp-nix/actions/workflows/build.yml/badge.svg)](https://github.com/mtrsk/fsharp-nix/actions/workflows/build.yml)
[![[Nix] Build Container](https://github.com/mtrsk/fsharp-nix/actions/workflows/build-container.yml/badge.svg)](https://github.com/mtrsk/fsharp-nix/actions/workflows/build-container.yml)

This .NET 10 sample stores time-bounded feature definitions in PostgreSQL 18, evaluates them through `Microsoft.FeatureManagement`, and renders full pages and HTMX 4 fragments with Oxpecker.

The MFA-protected admin can apply a flag immediately or schedule it for a future UTC time while retaining the complete interval history. Authentication uses ASP.NET Core Identity with a custom Npgsql store, TOTP, and single-use recovery codes. Multi-step bootstrap and authenticator state transitions use `FsToolkit.ErrorHandling` task results to short-circuit failures explicitly.

## Development

Enter the flake-backed devenv shell and start its PostgreSQL service:

```sh
nix develop --impure
devenv up -d
```

The shell exports `ConnectionStrings__App` for the local `fsnix` database. The database listens only on `127.0.0.1:5432`; no Docker PostgreSQL instance is used.

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

Both bootstrap and normal startup apply every pending migration before continuing. The equivalent run command outside the devenv script shortcut is:

```sh
dotnet run --project src/App/App.fsproj
```

`make migrate` (or `dotnet run --project src/App/App.fsproj -- --migrate`) remains available as a deployment preflight that applies migrations and exits.

Open <http://localhost:5000/> for the demo or <http://localhost:5000/admin/features> for feature administration. The latter redirects to email/password sign-in and requires authenticator enrollment before granting access. There is no public registration route; run `--bootstrap-user` again with unique credentials when another account is required.

Authenticator setup renders an `otpauth://` QR code with the locally vendored [QRCode.js 1.0.0](https://github.com/davidshimjs/qrcodejs) asset. Recovery codes are shown once, stored only as SHA-256 hashes, and atomically consumed. Authenticator reset and recovery-code regeneration require a session carrying the `amr=mfa` claim; password-only sessions cannot administer features or weaken MFA.

The application defaults to the `Development` .NET environment. Set `DOTNET_ENVIRONMENT=Production` in production. Serve the application over HTTPS and persist ASP.NET Core Data Protection keys when running more than one instance or replacing instances.

## Build and test

The existing solution is `fsnix.slnx`:

```sh
dotnet build fsnix.slnx
dotnet test fsnix.slnx
```

Run the suites independently with `make test-unit` or `make test-integration`. PostgreSQL integration tests use the already-running devenv server and recreate dedicated `fsnix_tests` and `fsnix_tests_production` databases. The local `fsnix` role has `CREATEDB` solely so the fixture can provision those databases; tests do not start containers. The authentication tests exercise bootstrap, lockout, password login, TOTP enrollment, MFA authorization, recovery login, one-time recovery-code redemption and regeneration, and authenticator reset through the real HTTP and PostgreSQL stack.

## Migrations and generated database types

SQL migrations live in `src/App.Migrations/Migrations` and are embedded in the separate `App.Migrations` assembly. Application startup, bootstrap, `App --migrate`, and the integration fixture all call the same DbUp migrator under a PostgreSQL advisory lock. Migration directories have distinct behavior:

- `init` runs once, before every other category.
- `main` runs once after `init`.
- `test` runs once after `main`, but only when the .NET environment is `Development`.
- `repeatable` runs last. DbUp uses `NullJournal` for these scripts, while a separate SHA-256 state table prevents execution when the embedded SQL has not changed.

All application objects, including DbUp's `schemaversions` journal, live in the `fsnix` schema. The one-time categories share that journal. Put extensions and migration infrastructure in `init`, application tables in `main`, development-only fixtures in `test`, and idempotent objects such as `CREATE OR REPLACE VIEW` statements in `repeatable`.

Static application CSS and JavaScript live under `wwwroot/style` and `wwwroot/js`; F# views only reference those assets.

Application tables deliberately contain no nullable columns. Empty optional text and JSON values use `''` and `'{}'`, lockout absence uses PostgreSQL `-infinity`, and open-ended temporal ranges use explicit `timestamptz 'infinity'`. Projection views translate those storage sentinels back into optional application values.

This refactor intentionally rewrites and relocates the original migrations instead of adding a compatibility migration. Recreate databases made with the previous migration layout before starting this version.

`src/App/Database.Generated.fs` is committed so builds do not require a live database. After changing the scalar projection views, regenerate it against the migrated devenv database:

```sh
dotnet tool restore
dotnet sqlhydra npgsql -t sqlhydra.toml -p src/App/App.fsproj
```

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
