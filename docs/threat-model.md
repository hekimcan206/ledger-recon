# Threat model

Scope: the ledger-recon API, its PostgreSQL database and the files it ingests. All data in this repository is
synthetic.

## Assets

| Asset | Why it matters |
|---|---|
| Account balances and journal history | They are the books: corruption means money created, lost or hidden |
| Idempotency guarantees | A broken guarantee means duplicate payments |
| Reconciliation files | Card files can carry card numbers (PCI DSS). EFT files carry IBANs and names, which are personal data under KVKK, Turkey's data protection law (*Kişisel Verilerin Korunması Kanunu*) |
| Difference reports | They show where money is unaccounted for and are used for operational decisions |
| Database credentials | They give access to all of the above |

## Actors

Legitimate API clients (internal services), operators uploading files, an attacker with network access to the
API, a compromised or buggy internal service, and an insider with database access.

## Threats and mitigations (STRIDE)

| # | Threat | Mitigation in this repo | Residual risk / next step |
|---|---|---|---|
| S1 | **Spoofing**: anyone who can reach the API can post entries | none (no authentication) | **Out of scope; must be added before any real use.** Use mTLS or OAuth2 client credentials, with per-client scopes (post vs read vs reconcile) |
| T1 | **Tampering** with posted history (UPDATE/DELETE of postings) | Append-only triggers (even the schema owner gets `23001`); the app role has no UPDATE/DELETE privilege (`42501`); both are tested | A superuser can still disable triggers. Add pgaudit and ship the WAL or audit log to separate storage |
| T2 | **Tampering** with balances directly | The stored balance is checked against the postings by the `account_balance_drift` view; the overdraft CHECK constraint | Drift is detected, not prevented. Alert on a non-zero drift count from `/ledger/trial-balance` |
| T3 | Unbalanced or cross-currency entries through a bug or a second writer | Deferred constraint trigger and composite currency foreign keys, enforced by the database regardless of the client | none |
| T4 | Replay of a captured request posts twice | Idempotency key plus request hash; the replay returns the original entry | A captured request with a *new* key is a new payment. Signed requests or nonces belong in the auth layer (S1) |
| T5 | Tampered statement file (amounts edited) | MT940 opening + movements must equal closing, otherwise the whole file is rejected; files are stored by SHA-256 | Card and EFT CSVs carry no control totals. A signed or SFTP-delivered source is assumed |
| R1 | **Repudiation**: who posted an entry? | `posted_at`, the idempotency key and trace ids in structured logs | No caller identity is recorded (S1). Add the client id to `journal_entries` once auth exists |
| I1 | **Information disclosure**: a full card number in a settlement file | The parser rejects any non-masked PAN line; the value is neither stored nor echoed in the report (tested) | none |
| I2 | IBANs and names in EFT files stored without need | Read for validation only and never persisted (data minimisation, KVKK art. 4) | The raw upload body passes through memory and is not logged |
| I3 | Error responses leak internals | RFC 9457 problem details with stable codes; no exception text | none |
| D1 | **Denial of service**: huge uploads | 10 MB cap on reconciliation bodies, checked while streaming | No rate limiting; add it at the gateway |
| D2 | Hot-account lock queues exhaust connections | `lock_timeout = 5 s` and a bounded pool; the client gets `503` with `Retry-After` | Throughput on a single account is bounded (see benchmarks) |
| E1 | **Elevation of privilege**: SQL injection | Parameterised SQL everywhere; there is no dynamic SQL built from input | none |
| E2 | A compromised API process changes the schema | The API runs as `ledger_app` (DML only); migrations run in a separate one-shot process as `ledger_migrator` | Credentials come from environment variables. Use a secret store in production |
| E3 | Container breakout | Chiseled runtime image: no shell or package manager, non-root UID 1654 | none |
