# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within
5 working days; if the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Ledger integrity | Balanced entries, currency consistency, append-only history and overdraft limits are enforced by PostgreSQL itself (triggers, constraints, privileges), not only by the application |
| Database access | The API runs as `ledger_app` (no UPDATE/DELETE on history, no DDL); migrations run in a separate one-shot process as `ledger_migrator` |
| Card data | Lines with an unmasked card number are rejected; the number is neither stored nor echoed |
| Personal data | Counterparty names and IBANs in EFT files are never persisted; request bodies are not logged |
| Input limits | Reconciliation uploads are capped at 10 MB while streaming; strict UTF-8; parameterised SQL only |
| Container | Chiseled runtime image, no shell or package manager, non-root UID 1654 |
| Secrets | None in the repository; compose defaults are for local development and can be overridden via `.env` |
| Supply chain | Centrally pinned package versions, updated by hand; CI fails on known-vulnerable direct or transitive packages (`dotnet list package --vulnerable --include-transitive`); CodeQL `security-and-quality` on every push |

**Not included:** authentication and authorisation, rate limiting, TLS termination. This repository is a
demonstration with synthetic data and must not be exposed as-is.
