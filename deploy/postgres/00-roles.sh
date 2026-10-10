#!/bin/sh
# Creates the Esmbler roles and databases (ADR 0010). Runs once as the Postgres superuser on first start.
# esmbler_migrator owns the schema and runs migrations; esmbler_app is the runtime role: NOSUPERUSER,
# NOBYPASSRLS, not an owner, DML only; esmbler_definer (NOLOGIN, BYPASSRLS) owns the few privileged functions. Passwords come from the environment.
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
CREATE ROLE esmbler_migrator LOGIN PASSWORD '${ESMBLER_MIGRATOR_PASSWORD}' NOSUPERUSER NOCREATEROLE NOBYPASSRLS;
CREATE ROLE esmbler_app LOGIN PASSWORD '${ESMBLER_APP_PASSWORD}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
-- Owns only the narrow SECURITY DEFINER functions (login lookup, provisioning, worker claims). Cannot log in.
CREATE ROLE esmbler_definer NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE BYPASSRLS;
GRANT esmbler_definer TO esmbler_migrator WITH INHERIT FALSE, SET TRUE;
CREATE DATABASE esmbler OWNER esmbler_migrator;
CREATE DATABASE openfga OWNER esmbler_migrator;
REVOKE ALL ON DATABASE esmbler FROM PUBLIC;
GRANT CONNECT ON DATABASE esmbler TO esmbler_app;
SQL
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname esmbler <<SQL
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
