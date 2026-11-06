-- End-of-day reconciliation runs and their difference reports. Runs are immutable once written: a re-uploaded
-- identical file returns the existing run (unique source + business date + file hash) instead of a second one.

CREATE TABLE ledger.recon_runs (
    id                uuid        PRIMARY KEY,
    source            text        NOT NULL,
    business_date     date        NOT NULL,
    account_id        bigint      NOT NULL REFERENCES ledger.accounts (id),
    file_name         text        NULL,
    file_sha256       bytea       NOT NULL,
    record_count      integer     NOT NULL,
    ledger_item_count integer     NOT NULL,
    matched_count     integer     NOT NULL,
    difference_count  integer     NOT NULL,
    pending_count     integer     NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT recon_runs_file_key UNIQUE (source, business_date, file_sha256),
    CONSTRAINT recon_runs_source_valid CHECK (source IN ('card', 'eft', 'correspondent')),
    CONSTRAINT recon_runs_hash_length CHECK (length(file_sha256) = 32)
);

CREATE TABLE ledger.recon_items (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id              uuid    NOT NULL REFERENCES ledger.recon_runs (id),
    kind                text    NOT NULL,
    line_number         integer NULL,
    file_reference      text    NULL,
    file_date           date    NULL,
    file_amount_minor   bigint  NULL,
    posting_id          bigint  NULL REFERENCES ledger.postings (id),
    entry_id            uuid    NULL,
    ledger_reference    text    NULL,
    ledger_value_date   date    NULL,
    ledger_amount_minor bigint  NULL,
    difference_minor    bigint  NOT NULL,
    currency            char(3) NULL,
    detail              text    NOT NULL
);

CREATE INDEX recon_items_run_idx ON ledger.recon_items (run_id, id);

-- A posting is consumed (matched or flagged with a mismatch) by at most one run. The primary key makes a double
-- match impossible even if two runs raced; the advisory lock in the service makes sure they do not.
CREATE TABLE ledger.recon_consumed_postings (
    posting_id bigint PRIMARY KEY REFERENCES ledger.postings (id),
    run_id     uuid   NOT NULL REFERENCES ledger.recon_runs (id),
    kind       text   NOT NULL
);

REVOKE UPDATE, DELETE, TRUNCATE ON ledger.recon_runs, ledger.recon_items, ledger.recon_consumed_postings FROM ledger_app;
