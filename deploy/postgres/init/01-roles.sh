#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d).
# Least privilege:
#   ledger_migrator  owns the "ledger" and "ledger_admin" schemas and runs migrations (DDL);
#   ledger_app       is what the API connects as. It gets DML on "ledger" through default privileges, which
#                    the migrations then narrow further (journal entries and postings: SELECT and INSERT only).
#   "ledger_admin" holds the migration history; the application role cannot even see it.
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" \
     -v app_password="$APP_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE ledger_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE ledger_app LOGIN PASSWORD :'app_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO ledger_migrator, ledger_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA ledger AUTHORIZATION ledger_migrator;
CREATE SCHEMA ledger_admin AUTHORIZATION ledger_migrator;
GRANT USAGE ON SCHEMA ledger TO ledger_app;

ALTER DEFAULT PRIVILEGES FOR ROLE ledger_migrator IN SCHEMA ledger
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ledger_app;
ALTER DEFAULT PRIVILEGES FOR ROLE ledger_migrator IN SCHEMA ledger
    GRANT USAGE, SELECT ON SEQUENCES TO ledger_app;
SQL
