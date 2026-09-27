-- Least privilege for the NCBRS application role (plan §17 item 16, WS-A2).
--
-- Run as the DATABASE OWNER, after every migration, with the application
-- role's name as a psql variable:
--
--     psql -v app_role=ncbrs_app -f deploy/postgres/app-role-grants.sql
--
-- Idempotent: running it twice leaves the same privileges as running it once.
--
-- Why this exists alongside the append-only triggers. The trigger stops any
-- UPDATE or DELETE on "AuditLogs" that reaches the table, from anyone on the
-- connection. This stops the application's credentials from having the verb at
-- all. They stop different people, and a production tier should have both
-- (see CLAUDE.md, "Audit-log immutability"):
--
--   * The trigger stops a bug, or a developer running SQL through the app's
--     connection.
--   * This stops anyone holding the app's credentials, including anyone who has
--     stolen them. It also stops them disabling the trigger, because only the
--     table's owner can alter it. So the app role must never own the tables:
--     migrations run as the owner, the application runs as this role.
--
-- Neither stops the owner. That is why the WAL archive has to leave the host
-- (plan §17 item 15).
--
-- Deliberately no ALTER DEFAULT PRIVILEGES. A table added by a later migration
-- is not granted until this script runs again, so it fails at first use,
-- during the deployment, where someone will see it. Default privileges would
-- grant it silently, and a migration that recreated "AuditLogs" would come
-- back with UPDATE and DELETE granted.

\set ON_ERROR_STOP on

GRANT USAGE ON SCHEMA public TO :"app_role";

-- Everything the application reads and writes...
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO :"app_role";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO :"app_role";

-- ...except rewriting the audit trail. Revoked after the grant above, so a
-- re-run can never leave it granted. The application appends and reads; it
-- never corrects a row, because a corrected audit row is indistinguishable
-- from a falsified one.
REVOKE UPDATE, DELETE, TRUNCATE ON "AuditLogs" FROM :"app_role";

-- The migration history is the owner's business, not the application's.
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON "__EFMigrationsHistory" FROM :"app_role";
