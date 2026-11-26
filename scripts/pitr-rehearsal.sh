#!/usr/bin/env bash
# Rehearses point-in-time recovery of the registry (plan §17 item 15, WS-A6).
#
# The restore drill (plan §A6) proved a dump restores and the controls come
# back with it. It did not prove the procedure that matters most when someone
# makes a mistake on the live database: take the last base backup, replay the
# archived WAL, and stop just before the mistake. This does that, timed, on
# throwaway containers, and checks what was recovered:
#
#   1. A Postgres archiving WAL to its own volume, migrated and seeded with the
#      dev registry (72 births) by the Api itself, so the schema, the
#      append-only triggers and the data are the real ones.
#   2. A base backup that carries no WAL of its own (-X none), so recovery can
#      only work if the archive is complete.
#   3. Work after the backup that must survive: an audit row.
#   4. The instant to recover to, and then the mistake: the audit trail
#      dropped by the database owner (the one thing the triggers and grants
#      cannot stop, CLAUDE.md "Audit-log immutability"), and a facility renamed.
#   5. The server lost. A new one built from the base backup, replaying the
#      archive to the instant.
#   6. Checked: the births, the audit row written after the backup, the trail
#      and its triggers enforcing, the facility's own name, a new timeline, a
#      new write accepted, and the Api starting against the result.
#
#   scripts/pitr-rehearsal.sh          # from the repo root; needs docker, dotnet
#   PITR_REHEARSAL_KEEP=1 scripts/pitr-rehearsal.sh   # leave the containers up
#
# Exits non-zero if any check fails, printing the containers' logs.
set -euo pipefail

export MSYS_NO_PATHCONV=1  # Git Bash: leave container paths alone

WORK="${PITR_REHEARSAL_DIR:-$(mktemp -d)}"
NET=ncbrs-pitr-rehearsal
IMAGE_PG=postgres:17.2
API_PORT="${PITR_REHEARSAL_API_PORT:-18090}"
PG_PASSWORD=pitr-rehearsal
CONTAINERS=(pitr-pg pitr-api-seed pitr-restored pitr-api)
VOLUMES=(ncbrs-pitr-archive ncbrs-pitr-backup)

hostpath() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

failures=0
pass() { echo "  PASS  $1"; }
fail() { echo "  FAIL  $1"; failures=$((failures + 1)); }
expect() { local what="$1" expected="$2" actual="$3"; [[ "$actual" == "$expected" ]] && pass "$what" || fail "$what (expected $expected, got $actual)"; }

cleanup() {
  if (( failures > 0 )); then
    for c in "${CONTAINERS[@]}"; do
      docker ps -a --format '{{.Names}}' | grep -qx "$c" || continue
      echo "----- logs: $c"; docker logs --tail 40 "$c" 2>&1 || true
    done
  fi
  docker rm -f "${CONTAINERS[@]}" > /dev/null 2>&1 || true
  docker volume rm "${VOLUMES[@]}" > /dev/null 2>&1 || true
  docker network rm "$NET" > /dev/null 2>&1 || true
}
on_exit() { if [[ -n "${PITR_REHEARSAL_KEEP:-}" ]]; then echo "(containers kept: ${CONTAINERS[*]})"; else cleanup; fi; }
trap on_exit EXIT

wait_until() {  # wait_until <seconds> <description> <command...>
  local seconds="$1" what="$2"; shift 2
  for _ in $(seq 1 "$seconds"); do "$@" > /dev/null 2>&1 && return 0; sleep 1; done
  fail "$what (timed out after ${seconds}s)"; return 1
}

echo "== Publishing the Api for linux-x64"
mkdir -p "$WORK/app"
dotnet publish src/NCBRS.Api -c Release -r linux-x64 --self-contained -p:OpenApiGenerateDocuments=false \
  --artifacts-path "$(hostpath "$WORK/artifacts")" -o "$(hostpath "$WORK/app/Api")" -v q -nologo > "$WORK/publish.log" 2>&1 \
  || { cat "$WORK/publish.log"; exit 1; }
APP="$(hostpath "$WORK/app")"

cleanup; trap on_exit EXIT
docker network create "$NET" > /dev/null
docker volume create ncbrs-pitr-archive > /dev/null
docker volume create ncbrs-pitr-backup > /dev/null

echo "== A registry archiving its WAL to its own volume"
# The same archiving as docker-compose.yml, minus archive_timeout: the
# rehearsal switches segments itself rather than waiting a minute.
docker run -d --name pitr-pg --network "$NET" --network-alias pitr.pg \
  -e POSTGRES_USER=ncbrs -e POSTGRES_PASSWORD="$PG_PASSWORD" -e POSTGRES_DB=ncbrs \
  -v ncbrs-pitr-archive:/archive -v ncbrs-pitr-backup:/backup \
  --entrypoint bash "$IMAGE_PG" -c 'chown postgres:postgres /archive /backup && exec docker-entrypoint.sh postgres -c wal_level=replica -c archive_mode=on -c "archive_command=test ! -f /archive/%f && cp %p /archive/%f"' > /dev/null
wait_until 60 "Postgres ready" docker exec pitr-pg pg_isready -U ncbrs -h localhost

sql() { docker exec "${PG_CONTAINER:-pitr-pg}" psql -U ncbrs -d ncbrs -Atc "$1" 2> /dev/null; }

run_api() {  # run_api <name> <docker args...>
  local name="$1"; shift
  docker run -d --name "$name" --network "$NET" -v "$APP:/app:ro" -w /app/Api --entrypoint /app/Api/NCBRS.Api \
    -e ASPNETCORE_ENVIRONMENT=Development -e ASPNETCORE_URLS=http://0.0.0.0:8080 -e Database__Provider=Postgres \
    "$@" "$IMAGE_PG" > /dev/null
}

echo "== Seeding: the Api migrates and seeds 72 births"
run_api pitr-api-seed -e "ConnectionStrings__Default=Host=pitr.pg;Port=5432;Database=ncbrs;Username=ncbrs;Password=$PG_PASSWORD"
seeded() { [[ "$(sql 'SELECT count(*) FROM "BirthRecords"')" == 72 ]]; }
wait_until 180 "migrated and seeded" seeded && pass "migrated and seeded 72 births"
docker rm -f pitr-api-seed > /dev/null
FACILITY_ID="$(sql 'SELECT "FacilityId" FROM "Facilities" ORDER BY "Name" LIMIT 1')"
FACILITY_NAME="$(sql "SELECT \"Name\" FROM \"Facilities\" WHERE \"FacilityId\" = '$FACILITY_ID'")"
AUDIT_ROWS="$(sql 'SELECT count(*) FROM "AuditLogs"')"

echo "== Base backup, carrying no WAL of its own"
started=$(date +%s)
docker exec pitr-pg pg_basebackup -U ncbrs -h localhost -D /backup/base -X none -c fast > "$WORK/basebackup.log" 2>&1 \
  && pass "base backup taken in $(( $(date +%s) - started ))s; Postgres confirmed its WAL was archived" \
  || { cat "$WORK/basebackup.log"; fail "base backup"; exit 1; }

echo "== After the backup: work that must survive, the instant, then the mistake"
# An audit row in the shape of the last one, whatever columns later migrations
# added: the work done between the backup and the mistake.
sql "INSERT INTO \"AuditLogs\" SELECT (jsonb_populate_record(NULL::\"AuditLogs\", to_jsonb(a) || jsonb_build_object(
       'AuditLogId', gen_random_uuid(), 'Action', 'PitrRehearsal:after-backup', 'TimestampUtc', now()))).*
     FROM \"AuditLogs\" a ORDER BY \"TimestampUtc\" DESC LIMIT 1" > /dev/null
sql 'SELECT pg_sleep(1)' > /dev/null
TARGET="$(sql "SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US') || '+00'")"
sql 'SELECT pg_sleep(1)' > /dev/null
# The owner can do what nothing else here can stop. This is why the archive
# must leave the host (item 15's second half): the owner can reach this one.
docker exec pitr-pg psql -U ncbrs -d ncbrs -qc 'DROP TABLE "AuditLogs" CASCADE' > /dev/null
sql "UPDATE \"Facilities\" SET \"Name\" = 'Renamed after the instant' WHERE \"FacilityId\" = '$FACILITY_ID'" > /dev/null
expect "the mistake happened: the audit trail is gone" "" "$(sql "SELECT to_regclass('\"AuditLogs\"')")"
LAST_WAL="$(sql 'SELECT pg_walfile_name(pg_switch_wal())')"
archived() { [[ "$(sql 'SELECT last_archived_wal FROM pg_stat_archiver')" > "$LAST_WAL" || "$(sql 'SELECT last_archived_wal FROM pg_stat_archiver')" == "$LAST_WAL" ]]; }
wait_until 60 "the mistake's WAL archived" archived && pass "every segment up to the mistake is in the archive ($LAST_WAL)"
expect "nothing failed to archive" 0 "$(sql 'SELECT failed_count FROM pg_stat_archiver')"

echo "== The server is lost. Recovering to $TARGET"
docker rm -f pitr-pg > /dev/null
cat > "$WORK/restore.sh" << 'EOF'
set -e
cp -a /backup/base/. "$PGDATA"/
chown -R postgres:postgres "$PGDATA"; chmod 700 "$PGDATA"
touch "$PGDATA/recovery.signal"
{
  echo "restore_command = 'cp /archive/%f %p'"
  echo "recovery_target_time = '$RECOVERY_TARGET'"
  echo "recovery_target_action = 'promote'"
} >> "$PGDATA/postgresql.auto.conf"
exec docker-entrypoint.sh postgres
EOF
started=$(date +%s)
docker run -d --name pitr-restored --network "$NET" --network-alias pitr.pg -e POSTGRES_PASSWORD="$PG_PASSWORD" \
  -e RECOVERY_TARGET="$TARGET" -v ncbrs-pitr-archive:/archive:ro -v ncbrs-pitr-backup:/backup:ro \
  -v "$(hostpath "$WORK"):/rehearsal:ro" --entrypoint bash "$IMAGE_PG" /rehearsal/restore.sh > /dev/null
PG_CONTAINER=pitr-restored
promoted() { [[ "$(sql 'SELECT pg_is_in_recovery()')" == f ]]; }
wait_until 120 "recovery finished and promoted" promoted \
  && pass "recovered and promoted in $(( $(date +%s) - started ))s (container start, WAL replay, promotion)"
docker logs pitr-restored 2>&1 | grep -q "recovery stopping before commit" \
  && pass "recovery stopped at the instant, before the mistake's commit" \
  || fail "the log does not show recovery stopping at the target"

echo "== What came back"
expect "every birth" 72 "$(sql 'SELECT count(*) FROM "BirthRecords"')"
expect "the audit trail, with the row written after the backup" "$(( AUDIT_ROWS + 1 ))" "$(sql 'SELECT count(*) FROM "AuditLogs"')"
expect "the row written after the backup, from the archive" 1 \
  "$(sql "SELECT count(*) FROM \"AuditLogs\" WHERE \"Action\" = 'PitrRehearsal:after-backup'")"
expect "the facility's own name, not the change after the instant" "$FACILITY_NAME" \
  "$(sql "SELECT \"Name\" FROM \"Facilities\" WHERE \"FacilityId\" = '$FACILITY_ID'")"
refused="$(docker exec pitr-restored psql -U ncbrs -d ncbrs -Atc 'UPDATE "AuditLogs" SET "Action" = "Action" WHERE "AuditLogId" = (SELECT "AuditLogId" FROM "AuditLogs" LIMIT 1)' 2>&1 || true)"
[[ "$refused" == *"append-only"* ]] && pass "the append-only trigger is enforcing again" || fail "the audit trail accepted an UPDATE after recovery: $refused"
expect "a new timeline, so this history cannot be confused with the lost one" 2 "$(sql 'SELECT timeline_id FROM pg_control_checkpoint()')"
sql "INSERT INTO \"AuditLogs\" SELECT (jsonb_populate_record(NULL::\"AuditLogs\", to_jsonb(a) || jsonb_build_object(
       'AuditLogId', gen_random_uuid(), 'Action', 'PitrRehearsal:after-recovery', 'TimestampUtc', now()))).*
     FROM \"AuditLogs\" a LIMIT 1" > /dev/null
expect "it takes new writes" 1 "$(sql "SELECT count(*) FROM \"AuditLogs\" WHERE \"Action\" = 'PitrRehearsal:after-recovery'")"

run_api pitr-api -p "$API_PORT:8080" -e "ConnectionStrings__Default=Host=pitr.pg;Port=5432;Database=ncbrs;Username=ncbrs;Password=$PG_PASSWORD"
healthy() { [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$API_PORT/health")" == 200 ]]; }
wait_until 90 "the Api starts against the recovered registry" healthy \
  && pass "the Api starts against the recovered registry and answers /health"
expect "and its startup changed nothing it should not have (no births added or lost)" 72 "$(sql 'SELECT count(*) FROM "BirthRecords"')"

echo
if (( failures > 0 )); then echo "$failures check(s) failed"; exit 1; fi
echo "Point-in-time recovery rehearsed: every check passed"
