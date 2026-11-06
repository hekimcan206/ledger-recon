# Benchmarks

All numbers below were measured on the machine described here on 2026-11-07. None is estimated or rounded up.
They are laptop numbers, measured with other workloads running. Read them as a comparison between strategies
on the same hardware, not as a capacity claim.

## Environment

| | |
|---|---|
| CPU | Intel Core i7-13620H (10 cores / 16 threads) |
| RAM | 15.7 GB (Docker Desktop VM: 16 vCPU, 7.6 GB) |
| OS | Windows 11 Home (build 26200), Docker Desktop 29.9.1 on WSL2 |
| .NET | SDK 10.0.401, API built in Release, chiseled `aspnet:10.0` image |
| PostgreSQL | 17.11 (`postgres:17-alpine`), default configuration, in the same Docker VM |
| Load generator | NBomber 6.7.1 on the Windows host, over the published port (`localhost:58180`) |
| Background load | Not an idle machine: other containers and processes were running, and host CPU showed 67% load just before the runs |

## Method

- Stack: `docker compose up` (postgres, migrator, seed, api). The API uses a connection pool of 50 and
  `lock_timeout = 5 s`. The strategy was switched by restarting only the API container with
  `LEDGER_ISOLATION=Serializable`.
- Closed model: 32 virtual users (`Simulation.KeepConstant(32, 30 s)`) after a 5 s warm-up. Each user sends its
  next request as soon as the previous one is answered, so RPS is the sustained throughput at 32 concurrent requests.
- Every request is `POST /api/v1/journal-entries`: a 2-line TRY transfer with a fresh `Idempotency-Key`, except in
  `replay`. A request counts as OK only on `201 Created`. Latency percentiles cover OK requests.
- Scenarios:
  - **spread**: debit and credit two random accounts out of 200 (low contention). This measures the posting path:
    key claim, two row locks, balance update, postings insert, and the deferred balance check at commit.
  - **hot**: every transfer debits the same account (think of a merchant settlement account). All writers
    queue on one row.
  - **replay**: every request repeats one of 1 000 already-posted requests (same key and payload). This measures
    the idempotent replay path: conflict detection, hash comparison, reading the original entry.

## Results

| Strategy | Scenario | OK requests | Failed | Throughput (RPS) | p50 ms | p95 ms | p99 ms | max ms |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| RowLocks (default) | spread | 35 560 | 0 | **1 185** | 22.6 | 48.0 | 75.9 | 1 180.9 |
| RowLocks (default) | hot | 5 449 | 0 | **182** | 56.2 | 699.9 | 1 389.6 | 2 486.5 |
| RowLocks (default) | replay | 59 995 | 0 | **2 000** | 14.9 | 26.5 | 34.8 | 90.5 |
| Serializable (SSI + retry) | spread | 15 685 | 86 × 503 | 523 | 32.5 | 161.0 | 485.9 | 1 238.2 |
| Serializable (SSI + retry) | hot | 5 161 | 636 × 503 | 172 | 22.1 | 536.6 | 733.2 | 1 047.9 |

After all runs the trial balance was still `balanced: true` with `driftingAccounts: 0`, across 1 432 accounts and
everything posted by the seed and the load tests.

An earlier RowLocks `spread` run measured 1 272 RPS, but 130 of its requests reused an idempotency key with a
different payload (a bug in the load generator: keys restarted after warm-up) and correctly got `422`. The table
shows the run after that fix. The two runs differ by about 7%, which is a fair indication of run-to-run noise on
this machine.

## What the numbers say

- **RowLocks never failed a request.** Under hot-account contention, requests wait in the row-lock queue: p50
  stays at 56 ms, but the tail grows (p99 1.4 s) because 32 writers take turns on one row. No request reached
  the 5 s `lock_timeout`.
- **SSI was slower even on the low-contention workload** (523 vs 1 185 RPS), and it returned retryable 503s after
  12 attempts. A likely contributor is that PostgreSQL's SSI tracks index reads with page-level predicate
  locks. Unrelated accounts that sit on the same index page can then look like conflicts and abort each other. This
  was not separately instrumented here, so treat it as the probable cause, not a proven one.
- **On the hot account, SSI's p50 looks better (22 ms) only because 11% of requests gave up** (636 × 503).
  Fast failures pull the median down while the client still has to retry. For a ledger, an answer for every
  request matters more than a lower median, and this is why RowLocks is the default (ADR 0002).
- **Replays are cheap** (2 000 RPS, p99 35 ms). The duplicate is found by the unique index on the first
  statement and answered from the stored entry, so a client retry storm costs less than new postings do.
- **The single-row ceiling is about 180 transfers per second for one hot account** on this setup. Raising it means
  changing the design, not tuning: batch several transfers per transaction, or split the hot account into N
  sub-accounts that are summed on read. Both are listed as next steps in the README.

## Reproduce

```bash
docker compose up -d --build
dotnet run -c Release --project benchmarks/LedgerRecon.LoadTests -- --scenario spread --copies 32 --duration 30
dotnet run -c Release --project benchmarks/LedgerRecon.LoadTests -- --scenario hot    --copies 32 --duration 30
dotnet run -c Release --project benchmarks/LedgerRecon.LoadTests -- --scenario replay --copies 32 --duration 30

# Serializable strategy: restart only the API, then repeat the runs
LEDGER_ISOLATION=Serializable docker compose up -d api
```

Each run writes NBomber's Markdown and CSV reports to `benchmark-results/<scenario>-<label>/` (git-ignored) and
prints the table row used above. The load generator creates its own accounts (prefix `LT<label>_`), so runs can be
repeated against the same database.

## Other measurements

| What | Result |
|---|---|
| `docker compose up -d` on a fresh volume (images already built) until `/health/ready` is 200 and the seed has exited | 17.7 s (init roles, 2 migrations, 258 demo entries posted, 3 files reconciled) |
| Seed run a second time | `0 entries posted, 258 replayed`, and the 3 runs are returned as existing |
| Runtime image size (`docker image ls`) | 194 MB |
