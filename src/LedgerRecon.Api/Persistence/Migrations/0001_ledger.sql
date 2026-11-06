-- Double-entry ledger core. Runs as ledger_migrator (schema owner); the API connects as ledger_app.
-- Invariants live in the database as well as in code, so a bug, a manual psql session or a second service
-- cannot break them:
--   * every journal entry has >= 2 postings and debits = credits      (deferred constraint triggers)
--   * a posting's currency equals its entry's and its account's        (composite foreign keys)
--   * entries and postings are append-only                             (triggers + revoked privileges)
--   * an account flagged prevent_overdraft never crosses zero          (check constraint)
--   * an idempotency key is used at most once                          (unique constraint)

CREATE TABLE ledger.accounts (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code              text        NOT NULL,
    name              text        NOT NULL,
    type              text        NOT NULL,
    currency          char(3)     NOT NULL,
    prevent_overdraft boolean     NOT NULL DEFAULT false,
    -- Signed, debit-positive, in minor units. Credit-normal accounts (liability, equity, revenue) are negative.
    balance_minor     bigint      NOT NULL DEFAULT 0,
    version           bigint      NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT accounts_code_key UNIQUE (code),
    CONSTRAINT accounts_id_currency_key UNIQUE (id, currency),
    CONSTRAINT accounts_code_format CHECK (code ~ '^[A-Z0-9][A-Z0-9_.-]{1,39}$'),
    CONSTRAINT accounts_name_length CHECK (length(name) BETWEEN 1 AND 100),
    CONSTRAINT accounts_type_valid CHECK (type IN ('asset', 'liability', 'equity', 'revenue', 'expense')),
    CONSTRAINT accounts_currency_valid CHECK (currency IN ('TRY', 'USD', 'EUR')),
    CONSTRAINT accounts_no_overdraft CHECK (
        NOT prevent_overdraft
        OR CASE WHEN type IN ('asset', 'expense') THEN balance_minor >= 0 ELSE balance_minor <= 0 END)
);

CREATE TABLE ledger.journal_entries (
    id              uuid        PRIMARY KEY,
    idempotency_key text        NOT NULL,
    -- SHA-256 of the canonical request; same key + different hash is rejected (422), never re-applied.
    request_hash    bytea       NOT NULL,
    description     text        NOT NULL,
    value_date      date        NOT NULL,
    currency        char(3)     NOT NULL,
    external_ref    text        NULL,
    posted_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT journal_entries_idempotency_key_key UNIQUE (idempotency_key),
    CONSTRAINT journal_entries_id_currency_key UNIQUE (id, currency),
    CONSTRAINT journal_entries_key_length CHECK (length(idempotency_key) BETWEEN 1 AND 128),
    CONSTRAINT journal_entries_hash_length CHECK (length(request_hash) = 32),
    CONSTRAINT journal_entries_description_length CHECK (length(description) BETWEEN 1 AND 200),
    CONSTRAINT journal_entries_external_ref_length CHECK (external_ref IS NULL OR length(external_ref) BETWEEN 1 AND 64)
);

CREATE INDEX journal_entries_external_ref_idx ON ledger.journal_entries (external_ref) WHERE external_ref IS NOT NULL;

CREATE TABLE ledger.postings (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entry_id     uuid     NOT NULL,
    line_no      smallint NOT NULL,
    account_id   bigint   NOT NULL,
    currency     char(3)  NOT NULL,
    direction    char(1)  NOT NULL,
    amount_minor bigint   NOT NULL,
    CONSTRAINT postings_entry_line_key UNIQUE (entry_id, line_no),
    CONSTRAINT postings_direction_valid CHECK (direction IN ('D', 'C')),
    CONSTRAINT postings_amount_positive CHECK (amount_minor > 0),
    CONSTRAINT postings_entry_currency_fkey FOREIGN KEY (entry_id, currency) REFERENCES ledger.journal_entries (id, currency),
    CONSTRAINT postings_account_currency_fkey FOREIGN KEY (account_id, currency) REFERENCES ledger.accounts (id, currency)
);

CREATE INDEX postings_account_idx ON ledger.postings (account_id, id);

-- Balanced-entry invariant, checked at COMMIT (deferred) because the entry row and its postings are inserted
-- by separate statements. Fired from both tables: from journal_entries so an entry without postings fails,
-- from postings so lines added to an existing entry later fail too.
CREATE FUNCTION ledger.assert_entry_balanced() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_entry   uuid;
    v_debits  numeric;
    v_credits numeric;
    v_lines   integer;
BEGIN
    IF TG_TABLE_NAME = 'journal_entries' THEN
        v_entry := NEW.id;
    ELSE
        v_entry := NEW.entry_id;
    END IF;

    SELECT coalesce(sum(amount_minor) FILTER (WHERE direction = 'D'), 0),
           coalesce(sum(amount_minor) FILTER (WHERE direction = 'C'), 0),
           count(*)
      INTO v_debits, v_credits, v_lines
      FROM ledger.postings
     WHERE entry_id = v_entry;

    IF v_lines < 2 OR v_debits = 0 OR v_debits <> v_credits THEN
        RAISE EXCEPTION 'journal entry % is not balanced: debits %, credits %, lines %', v_entry, v_debits, v_credits, v_lines
            USING ERRCODE = 'check_violation', CONSTRAINT = 'journal_entry_balanced';
    END IF;

    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER journal_entry_balanced
    AFTER INSERT ON ledger.journal_entries
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION ledger.assert_entry_balanced();

CREATE CONSTRAINT TRIGGER posting_entry_balanced
    AFTER INSERT ON ledger.postings
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION ledger.assert_entry_balanced();

-- Append-only: history is corrected with reversing entries, never edited. The trigger also stops the schema
-- owner; the revoked privileges below stop the application role before the trigger is even reached.
CREATE FUNCTION ledger.reject_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% on ledger.% is not allowed: the ledger is append-only, post a reversing entry instead', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'restrict_violation';
END;
$$;

CREATE TRIGGER journal_entries_append_only BEFORE UPDATE OR DELETE ON ledger.journal_entries
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER journal_entries_no_truncate BEFORE TRUNCATE ON ledger.journal_entries
    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER postings_append_only BEFORE UPDATE OR DELETE ON ledger.postings
    FOR EACH ROW EXECUTE FUNCTION ledger.reject_mutation();
CREATE TRIGGER postings_no_truncate BEFORE TRUNCATE ON ledger.postings
    FOR EACH STATEMENT EXECUTE FUNCTION ledger.reject_mutation();

-- Operational check: an empty result means every stored balance equals the sum of its postings.
CREATE VIEW ledger.account_balance_drift AS
SELECT a.id,
       a.code,
       a.balance_minor,
       coalesce(sum(CASE p.direction WHEN 'D' THEN p.amount_minor ELSE -p.amount_minor END), 0) AS posted_minor
  FROM ledger.accounts a
  LEFT JOIN ledger.postings p ON p.account_id = a.id
 GROUP BY a.id, a.code, a.balance_minor
HAVING a.balance_minor <> coalesce(sum(CASE p.direction WHEN 'D' THEN p.amount_minor ELSE -p.amount_minor END), 0);

-- Least privilege for the runtime role (default privileges in deploy/postgres/init grant DML; narrow it here).
REVOKE UPDATE, DELETE, TRUNCATE ON ledger.journal_entries, ledger.postings FROM ledger_app;
REVOKE DELETE, TRUNCATE ON ledger.accounts FROM ledger_app;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON ledger.account_balance_drift FROM ledger_app;
