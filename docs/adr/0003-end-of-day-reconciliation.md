# 0003 — End-of-day reconciliation: three matching passes, open items, advisory lock per clearing account

- Status: accepted
- Date: 2026-11-06

## Context

Every money movement booked on a clearing account must eventually be confirmed by the outside world. There are
three external sources:

| Source | Real-world shape | Synthetic format here | Match key |
|---|---|---|---|
| Card acquirer | daily settlement file per merchant; gross, fee, net per transaction; settles T+1 | CSV: settlement date, transaction date, terminal, RRN, auth code, masked PAN, type, gross/fee/net | RRN (12 digits) |
| EFT / FAST | the central bank settlement account report. EFT is the Turkish central bank's interbank transfer system, FAST its 24/7 instant payment system | CSV: value date, channel, transfer id, direction, amount, masked IBAN, name, description | transfer id |
| Correspondent bank | SWIFT MT940 statement for our USD nostro account | MT940 subset: `:20: :25: :28C: :60F: :61: :86: :62F:` | field 61 customer reference (16 characters) |

These sources go wrong in a few typical ways: a transaction booked but never reported, a report with no booking,
amounts that differ (correspondent charges deducted from incoming wires), references that are missing, truncated or
mistyped, the same line twice, and dates a few days apart.

## Options considered

**Matching**

1. **Reference only.** Precise, but a reference garbled by an intermediary bank then looks like two differences, a
   missing item and an unbooked one, instead of one item to confirm.
2. **Amount and date only.** It finds the truncated-reference cases, but it is ambiguous whenever two transactions
   share an amount.
3. **Passes in order of evidence strength** (chosen):
   1. Reference (compared on a prefix for MT940). The pair is then classified as matched, matched within tolerance,
      amount mismatch, date mismatch or currency mismatch. File repeats of a reference beyond the ledger's count
      become `DuplicateInFile`.
   2. For records whose reference matched nothing: exact amount and currency inside the date window, closest date
      first. These are reported as `MatchedByAmountAndDate` so a person confirms them.
   3. Leftovers: file records become `MissingInLedger`. Ledger items older than the window become `MissingInFile`;
      younger ones become `Pending` and are carried to the next run.

   The engine is a pure function, and its output order is deterministic (file line, then posting id).

**Which ledger items a run sees**

1. **Postings with a value date on the business date.** This misses T+1 settlement and late items.
2. **A fixed look-back window.** An unmatched item older than the window silently falls out of every report.
3. **Open items** (chosen): every posting on the clearing account up to the business date that no earlier run has
   consumed. Matched and mismatched pairs consume their posting, with a primary key on `posting_id`, so a posting
   can be consumed at most once. Pending and missing items stay open and appear again until something reports them.

**Concurrency between runs**

1. **`SELECT ... FOR UPDATE` on the clearing account row.** This serialises runs, but it also blocks every posting
   to that clearing account, which is the busiest account in the bank, for the whole run.
2. **SERIALIZABLE for the run.** A long read-mostly transaction that conflicts with all concurrent postings would
   abort often.
3. **`pg_advisory_xact_lock(namespace, account_id)`** (chosen). It serialises runs for the same account and nothing
   else, so postings continue during a run. The run reads committed postings only (READ COMMITTED), so a posting
   committed mid-run is picked up by the next run. The consumption primary key is the backstop if the lock were
   ever bypassed.

## Decision

- Use three passes with per-source policy: card (tolerance 0, window 2 days), EFT/FAST (tolerance 0, window 1 day),
  and correspondent (tolerance 50.00 for deducted charges, window 2 days, 16-character reference prefix).
- File-level problems reject the upload with `422`: wrong header, invalid UTF-8, or an MT940 statement whose
  opening balance plus movements does not equal its closing balance. Line-level problems become `Unparseable`
  report items. A card line carrying a full PAN is rejected without the number being stored or echoed (PCI DSS).
- A run is identified by (source, business date, SHA-256 of the file). Uploading the same file again returns the
  existing run (`200`) instead of creating a second one.
- Reports are stored (`recon_items`, written with binary COPY) and served as JSON or CSV.

## Consequences

- `ReconciliationPropertyTests` generates random days with random defect mixes, and the engine must report exactly
  the injected defects (multiset equality) for 200 cases per run. Golden tests pin the parsers and the report
  format on committed sample files. `ReconciliationApiTests` repeats the exact-defect check end to end through
  PostgreSQL. It also checks that concurrent uploads of one file create one run and that pending items are matched
  by the next day's file.
- Many-to-one matches (one payout settling many card sales) are not modelled. The card file is matched per
  transaction on gross amounts, and payouts and fees would be a second reconciliation against the bank account.
- Tolerances are per source in code. Per-counterparty configuration and an operator workflow to confirm or resolve
  differences (with an audit trail) are the next step, not implemented here.
