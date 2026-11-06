# 0004 — Plain SQL migrations and Npgsql, two database roles

- Status: accepted
- Date: 2026-11-06

## Context

The service template (repo #0) uses EF Core. This service is different: its correctness depends on exact SQL
(`ON CONFLICT DO NOTHING RETURNING`, `FOR UPDATE` with `ORDER BY`, `unnest` batches, binary COPY, advisory locks)
and on database objects that an ORM does not model (deferred constraint triggers, composite foreign keys onto
`UNIQUE (id, currency)`, privilege changes).

## Options considered

1. **EF Core.** Its change tracking does not help here: an entry is written once and never updated. The lock
   order and the claim-first sequence would be hidden behind `SaveChanges` and raw SQL anyway. Triggers and
   grants would end up in hand-written migration SQL regardless.
2. **Dapper.** Convenient mapping, but it adds little over Npgsql's own reader for a handful of queries, and
   `DateOnly` and array parameters work natively with Npgsql.
3. **Npgsql directly, with numbered `.sql` migrations embedded in the assembly** (chosen). Every statement that
   matters is visible in one place. A small runner applies the scripts in order, each in its own transaction,
   under a session advisory lock, and refuses to start if an applied script's checksum changed.

## Decision

- Use `NpgsqlDataSource` as a singleton, with explicit SQL and positional parameters. There is no string-built SQL
  with user input.
- Migrations live in `src/LedgerRecon.Api/Persistence/Migrations/NNNN_name.sql` and are recorded in
  `ledger_admin.schema_migrations` with a SHA-256 checksum. An applied script is never edited; a change gets a new
  number.
- Two roles, created by `deploy/postgres/init/01-roles.sh` (used by docker compose and by the tests):
  - `ledger_migrator` owns the `ledger` and `ledger_admin` schemas and runs migrations through `--migrate-only`;
  - `ledger_app` can only `SELECT`/`INSERT` on entries, postings and reconciliation tables,
    `SELECT`/`INSERT`/`UPDATE` on accounts, and nothing in `ledger_admin`.
- The data source sets `lock_timeout=5s` and `idle_in_transaction_session_timeout=30s`, and disables GSS
  negotiation (no Kerberos here, and the chiseled image has no `libgssapi`).

## Consequences

- An integration test proves that `ledger_app` gets `42501` on UPDATE, DELETE, TRUNCATE, DDL and on reading the
  migration history, and that a tampered migration checksum stops the runner.
- Schema changes need SQL knowledge, which is the point for a ledger.
- The migration SQL refers to the role name `ledger_app`, so the init script and the migrations form one
  deployment contract.
