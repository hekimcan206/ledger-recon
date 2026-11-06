# Changelog

All notable changes to this project are documented here. The file is maintained by hand, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-07

### Added

- Double-entry ledger on PostgreSQL 17: accounts with signed minor-unit balances, immutable journal entries and
  postings; balanced entries enforced at COMMIT by a deferred constraint trigger, currency consistency by
  composite foreign keys, append-only history by triggers and revoked privileges, overdraft limits by a CHECK
- Exactly-once posting: `Idempotency-Key` + SHA-256 request fingerprint, unique index claimed in the same
  transaction as the effects; replays return the original entry (`Idempotent-Replayed: true`), a reused key with a
  different payload is rejected with 422
- Concurrency: READ COMMITTED with ordered `SELECT ... FOR UPDATE` (default) or SERIALIZABLE with retries,
  `lock_timeout` mapped to a retryable 503
- End-of-day reconciliation of an acquirer card settlement CSV, an EFT/FAST CSV and an MT940 correspondent statement:
  reference, then amount + date matching with per-source tolerance and date window, open-item carry-over, duplicate
  detection, difference report as JSON or CSV; runs serialised per clearing account with an advisory lock and
  de-duplicated by file hash
- Seeded synthetic data generator with injected defects, file generator CLI and committed sample files
- `docker compose up` stack: least-privilege roles, migrator job, idempotent demo seed, chiseled non-root API image
- Tests: unit, FsCheck properties (in memory and against PostgreSQL), golden reconciliation reports, Testcontainers
  concurrency and database-invariant tests; NBomber load test with recorded results

[Unreleased]: https://github.com/hekimcan206/ledger-recon/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hekimcan206/ledger-recon/releases/tag/v1.0.0
