# Contributing

Thanks for taking the time to improve ledger-recon.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start PostgreSQL through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit + integration tests
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history
readable, make the hand-written CHANGELOG easy to assemble at release time, and show which version
bump a change needs:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one. Do not
edit the old ADR in place. Use the existing files as the format (MADR: context, options considered,
decision, consequences).

## Database migrations

Migrations are numbered SQL files in `src/LedgerRecon.Api/Persistence/Migrations/` (`NNNN_name.sql`), embedded in
the assembly and applied by `--migrate-only`. Never edit a migration that has been applied anywhere: the runner
compares checksums and refuses to start. Add a new, higher-numbered file instead.

## Reconciliation baselines

The golden tests compare the engine output with the approved reports in `samples/`. After an intended behaviour
change, regenerate them and review the diff like code:

```bash
dotnet run --project tools/LedgerRecon.FileGen -- --out samples
```
