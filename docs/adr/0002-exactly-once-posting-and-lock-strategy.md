# 0002 — Exactly-once posting: idempotency key as the arbiter, ordered row locks for balances

- Status: accepted
- Date: 2026-11-06

## Context

Clients retry. A mobile app loses its connection after the server committed, a message broker redelivers, a
gateway times out at 30 s while the request is still running. Each retry of "move 100 TRY" must not move another
100 TRY. Two different requests touching the same account at the same moment must not lose an update or overdraw a
protected account, and two entries touching the same accounts in opposite order must not deadlock.

## Options considered

**Detecting a retry**

1. **Check, then insert** (`SELECT ... WHERE key = ?`, then insert if absent). Two concurrent retries both see
   "absent". This is the classic race and is rejected.
2. **Advisory lock on a hash of the key** (`pg_advisory_xact_lock(hashtextextended(key, 0))`), then check-then-insert.
   It works, but the lock is not tied to any data. A hash collision serialises unrelated requests, and every code
   path has to remember to take it.
3. **A separate idempotency table that stores the full response** (the Stripe model: the first response, including
   errors, is cached for 24 h). It replays failures as well, but needs a "started/finished" state machine and
   recovery for requests that crashed in the middle.
4. **A unique constraint on `journal_entries.idempotency_key`, claimed first with `INSERT ... ON CONFLICT DO NOTHING`
   in the same transaction as the effects** (chosen). The unique index is the arbiter. A concurrent request with the
   same key blocks on the uncommitted index entry. When the first commits, the second sees the row and replays;
   when the first rolls back, the second proceeds as if it were first. There is no window in which both can win,
   and the claim can never outlive a failed transaction.

The stored `request_hash` (SHA-256 of the canonical, validated entry) separates a replay (same hash: return the
original entry, `Idempotent-Replayed: true`, nothing written) from a key reused for a different payload (different
hash: `422 idempotency.key_reused`). The IETF `Idempotency-Key` draft suggested 422 for this case in its -05 revision.
Canonicalisation happens after validation, so `10`/`10.00` and `acc`/`ACC` count as the same payload.

**Keeping balances consistent under concurrency**

1. **SERIALIZABLE (SSI) with retries.** It needs no explicit locks, and PostgreSQL aborts any transaction whose
   effects could not have happened serially (`40001`). It is correct, but under contention on one hot row most
   attempts abort and retry. The retries need backoff, and after `MaxAttempts` the client gets a 503.
2. **Optimistic `lock_version` per account** (Modern Treasury's published design). This works well when conflicts
   are rare and the client wants "only if the balance is still X", but a busy account turns into a retry loop.
3. **Advisory lock per account id.** It has the same ordering requirements as row locks, without the row lock's
   automatic link to the data.
4. **READ COMMITTED with `SELECT ... FOR UPDATE` on the touched accounts in ascending `id` order** (chosen as the
   default). Contending requests wait instead of failing. The fixed lock order makes the opposing-transfer deadlock
   impossible. The overdraft check runs on the locked row, so it cannot be raced. `lock_timeout = 5 s` (set on the
   data source) turns a pathological wait into a retryable `503 ledger.busy` instead of an unbounded queue.

Both strategies are implemented (`Ledger:IsolationStrategy = RowLocks | Serializable`), tested by the same
concurrency suite, and measured with the same load test.

## Decision

- One transaction per request: claim the key, lock the accounts in id order, check rules on the locked rows,
  update the balances and insert the postings in one round trip (`NpgsqlBatch`), then commit. The deferred
  trigger re-checks the balance at commit.
- The default is `RowLocks`. `Serializable` stays as a switchable, measured alternative.
- Rejected requests (unknown account, insufficient funds, ...) roll back completely, including the key claim, so
  the same key can succeed later, for example after a deposit. This deliberately differs from Stripe, which caches
  4xx responses: a ledger posting that failed a business rule has not happened, and a client retrying it later
  after fixing the cause is legitimate.
- Lock and serialization failures that escape (lock timeout, retries exhausted) map to `503` with `Retry-After`.
  It is safe to retry with the same key.

## Consequences

- `ConcurrencyTests` runs against real PostgreSQL with both strategies. It checks that 50 identical concurrent
  requests produce exactly one entry and two postings, that 50 concurrent withdrawals against funds for 20 give
  exactly 20 successes under RowLocks and never overdraw under either strategy, and that 40 opposing transfers
  complete with no deadlock and conserved balances.
- In the hot-account load test, RowLocks still answers every request, while Serializable spends its time in
  aborted attempts. See [benchmarks](../benchmarks.md) for the measured numbers behind this choice.
- A hot account serialises its writers at roughly one commit round trip each. The usual remedies are out of scope
  and listed in the README: batching (TigerBeetle's core idea), or splitting a hot account into sub-accounts
  that are summed on read.
- Keys are kept forever, because they are columns of the entry itself. Expiring keys (Stripe expires them after
  24 h) would need a separate table and a decision about what a very late retry should do.
