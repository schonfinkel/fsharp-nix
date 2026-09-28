# P0 runbook — Automata infrastructure proof

Operational procedures required by phase P0 of `PLAN.md`. The development
database is the Automata repository's exact configuration: same `flake.lock`
nixpkgs pin, same PostgreSQL 19 build, same extension set.

## Roles

Development runs everything as the `fsnix` database owner. Production must
split that into two roles before the first deployment:

| Role | Privileges | Used by |
|---|---|---|
| `fsnix_owner` | owns `fsnix` + `fsm` schemas; installs `btree_gist`, `pgmq` | the deployment migration job only |
| `fsnix_app` | `USAGE` on both schemas; `SELECT/INSERT/UPDATE` on application tables; `EXECUTE` on `fsm.*` routines; no `CREATE`, no `CREATEDB`, no `CREATEROLE` | the web/worker runtime |

```sql
CREATE ROLE fsnix_owner LOGIN PASSWORD '...';
CREATE ROLE fsnix_app LOGIN PASSWORD '...';
-- after migrations, as fsnix_owner:
GRANT USAGE ON SCHEMA fsnix, fsm TO fsnix_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA fsnix TO fsnix_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA fsm TO fsnix_app;
```

`pgmq` installs without superuser (database owners may install it; only
`pg_cron` requires superuser, and maintenance runs in-process instead).

## Migration order

One deployment job, serialized at the deployment level, never per web pod:

1. `dotnet run --project src/App/App.fsproj -- --migrate` runs, in order:
   Automata's embedded DbUp migrations (`fsm` schema, journal
   `public.schemaversions`), then the application migrations (journal
   `fsnix.schemaversions`), then repeatable views.
2. Start web/worker instances with the runtime role. Before serving traffic,
   the application performs read-only checks of the `fsnix` one-time and
   repeatable journals and refuses to start when a migration is missing.
3. `Machine.startAsync` independently refuses incompatible or missing `fsm`
   schema/chart registrations. Runtime and bootstrap modes never run schema
   migrations.

## Data Protection keys

Production startup requires `DataProtection__KeyRingPath`. Mount that path from
durable storage shared by all instances, retain the `fsnix` application
discriminator, and protect the key ring at rest using encrypted volume or
secret-store controls supplied by the deployment platform. Back up and restore
the key ring with the database: losing it invalidates Identity links and makes
protected token rows unreadable.

## Backup and restore drill

A consistent backup must include `fsnix`, `fsm`, the pgmq queue tables, and
sequences from the same recovery point — never restore app and FSM schemas from
different snapshots:

```sh
pg_dump --format=custom --file fsnix.dump postgresql://fsnix:fsnix@127.0.0.1:5432/fsnix
```

Restore drill (repeat after any retention or queue change):

1. Restore into a scratch database: `pg_restore --dbname fsnix_drill fsnix.dump`.
2. Point `ConnectionStrings__App` at the drill database and start the app.
3. Assert:
   - pending `fsnix.integration_outbox` rows are re-delivered by the relay and
     resolve to `AlreadySubmitted` or commit exactly once;
   - `fsnix.action_receipts` still covers every redelivered action, so no
     effect executes twice;
   - `/admin/probe` completes a fresh command → `effect-pending` → `done` cycle
     against the restored database.
4. Effects that reached external providers after the backup point must be
   reconciled manually (gateway query by operation id) — the machine remains in
   its pending/unknown state and the operator health surface shows it.

## Pinning rules

- Never update `flake.lock` independently: PostgreSQL 19 Beta 4 removed
  `UPDATE ... FOR PORTION OF`, which Automata 0.5.0's `fsm.split_belief` uses.
  Any lock bump must pass Automata's integration suite and an empty-database
  migration first.
- Automata packages stay at 0.5.0; Npgsql at 10.0.3; FsToolkit at 5.2.0
  (aligned with Automata's own dependency floor).
