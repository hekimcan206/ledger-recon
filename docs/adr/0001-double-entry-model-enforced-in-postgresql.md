# 0001 — Double-entry model, enforced in PostgreSQL as well as in code

- Status: accepted
- Date: 2026-11-06

## Context

The ledger has to answer "how much is in this account" quickly, and it must be impossible for money to appear or
vanish: every journal entry balances (debits = credits), and history is never edited. The C# code validates this,
but code is not the only writer a database ever sees. A bug, a migration script, a second service or someone
in `psql` can write rows too. The invariants should hold even then.

## Options considered

**Balance representation**

1. **Derive balances on read** (`SUM(postings)` per account). There is a single source of truth and nothing can
   drift. The cost of every balance check grows with history, and an overdraft check then has to lock a range of
   postings instead of one row.
2. **Two running totals per account** (`debits_posted`, `credits_posted`), as in TigerBeetle. This is explicit and
   the totals only ever grow.
3. **One signed running balance per account, in minor units, debit-positive** (chosen). Credit-normal accounts
   (liability, equity, revenue) are negative, and the API also returns the balance on the normal side. The sum of
   all balances in a currency is zero by construction, which gives a one-query trial balance. Blnk and Modern
   Treasury also keep a stored balance next to immutable entries.

Amounts are `bigint` minor units (kuruş, cents). `numeric` or floating point never touches money.

**Where to enforce "every entry balances"**

1. **Application only.** This is simple, but any other writer can break it.
2. **A CHECK constraint.** It cannot work, because the rule spans several rows.
3. **A statement-level trigger with transition tables.** It sees all the rows of one statement, but constraint
   triggers can only be `FOR EACH ROW`, so it cannot be deferred to commit. The entry and its postings are
   separate statements.
4. **A deferred constraint trigger** (`DEFERRABLE INITIALLY DEFERRED`), fired from both `journal_entries` and
   `postings` (chosen). At COMMIT it checks that the entry has at least 2 lines and that debits equal credits.
   Firing from `journal_entries` catches an entry without postings. Firing from `postings` catches lines added to
   an existing entry later.

## Decision

- `accounts` stores the signed `balance_minor` and a `version` that is incremented on every change.
  `journal_entries` and `postings` are immutable.
- Database-enforced invariants (migration `0001_ledger.sql`):
  - balanced entries: the deferred constraint trigger `journal_entry_balanced`;
  - a posting has the same currency as its entry and its account: composite foreign keys onto
    `UNIQUE (id, currency)`;
  - append-only history: `BEFORE UPDATE/DELETE/TRUNCATE` triggers raise `restrict_violation` even for the schema
    owner, and the application role has no `UPDATE`/`DELETE` privilege at all;
  - no overdraft on protected accounts: a CHECK constraint on the normal-side balance;
  - an idempotency key is used once: a unique constraint (see ADR 0002).
- The view `account_balance_drift` lists any account whose stored balance differs from the sum of its postings.
  The trial-balance endpoint reports both the per-currency net and the drift count.
- Corrections are reversing entries. There is no edit or delete endpoint.

## Consequences

- `DatabaseInvariantTests` proves each rule with raw SQL that bypasses the service: an unbalanced entry fails at
  commit (`23514 journal_entry_balanced`), the application role gets `42501` on UPDATE/DELETE/TRUNCATE/DDL, and
  even the migrator role gets `23001` when it edits posted history.
- The trigger runs one indexed aggregate per inserted row at commit, so a 2-line entry costs 3 small queries. That
  cost is part of every number in [benchmarks](../benchmarks.md). A cheaper design (checking only the entry row,
  with postings inserted only in the same transaction) would rely on discipline instead of a guarantee.
- The stored balance is a cache of the postings. It is updated in the same transaction as the postings it
  summarises, and the property tests check `balance = Σ postings` after random concurrent workloads.
- One hot account means one hot row. That is the price of the stored balance and is discussed in ADR 0002.
