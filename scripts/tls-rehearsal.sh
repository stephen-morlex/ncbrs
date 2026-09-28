#!/usr/bin/env bash
# Rehearses the production transport configuration against real TLS.
#
# The central tier refuses to start outside Development unless every link is
# encrypted and verified: Postgres with SSL Mode=VerifyFull, Kafka on SASL_SSL,
# Keycloak over HTTPS, HTTPS-only browser origins. Unit tests pin each rule,
# but the rules only matter if a service configured to satisfy them actually
# works -- and the first rehearsal, run by hand, found two faults no unit test
# could: an http:// Keycloak authority that passed the startup check and then
# failed every request (#119), and an unreachable identity provider that
# refused every signed-in request while logging nothing (#121).
#
# Extended to the District tier, it found a third on its first run: the node
# answered 500 to every batch named in the X-Transaction-Id header, a shape
# the centre accepts, and never forwarded that header (#123).
#
# Throwaway everything: its own CA, certificates for pg.tls, kafka.tls,
# keycloak.tls, api.tls and district.tls, and a private Docker network, so
# nothing depends on this machine's hostname or trust store and nothing
# collides with the dev stack. The Api, Relay, Consumer, District node and
# load driver are published self-contained for linux-x64 and run in
# Production inside the postgres image the rehearsal pulls anyway.
#
#   scripts/tls-rehearsal.sh          # from the repo root; needs docker, dotnet, openssl, node
#   TLS_REHEARSAL_KEEP=1 scripts/tls-rehearsal.sh   # leave the containers up to investigate
#
# Exits non-zero if any check fails, printing the containers' logs.
set -euo pipefail

export MSYS_NO_PATHCONV=1  # Git Bash: leave /certs and -subj values alone

WORK="${TLS_REHEARSAL_DIR:-$(mktemp -d)}"
NET=ncbrs-tls-rehearsal
IMAGE_PG=postgres:17.2
IMAGE_KAFKA=apache/kafka:3.9.0
IMAGE_KEYCLOAK=quay.io/keycloak/keycloak:26.0
RUNTIME_IMAGE="${TLS_REHEARSAL_RUNTIME_IMAGE:-$IMAGE_PG}"  # glibc + ICU, already pulled
KC_PORT="${TLS_REHEARSAL_KC_PORT:-18443}"
API_PORT="${TLS_REHEARSAL_API_PORT:-18080}"
UNTRUSTED_PORT="${TLS_REHEARSAL_UNTRUSTED_PORT:-18082}"
CONSUMER_PORT="${TLS_REHEARSAL_CONSUMER_PORT:-18081}"
DISTRICT_PORT="${TLS_REHEARSAL_DISTRICT_PORT:-18084}"
PG_PASSWORD=tls-rehearsal
AUTHORITY=https://keycloak.tls:8443/realms/ncbrs
PG_CS="Host=pg.tls;Port=5432;Database=ncbrs;Username=ncbrs;Password=$PG_PASSWORD;SSL Mode=VerifyFull;Root Certificate=/certs/ca.pem"
CONTAINERS=(tls-pg tls-kafka tls-keycloak tls-api-seed tls-api tls-api-untrusted tls-relay tls-consumer tls-district tls-loaddriver)

# A Windows docker needs Windows paths for bind mounts.
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
  docker network rm "$NET" > /dev/null 2>&1 || true
}
# TLS_REHEARSAL_KEEP=1 leaves the containers running at the end, to
# investigate a failure; the next run removes them before it starts.
on_exit() { if [[ -n "${TLS_REHEARSAL_KEEP:-}" ]]; then echo "(containers kept: ${CONTAINERS[*]})"; else cleanup; fi; }
trap on_exit EXIT

wait_until() {  # wait_until <seconds> <description> <command...>
  local seconds="$1" what="$2"; shift 2
  for _ in $(seq 1 "$seconds"); do "$@" > /dev/null 2>&1 && return 0; sleep 1; done
  fail "$what (timed out after ${seconds}s)"; return 1
}

echo "== Certificates"
mkdir -p "$WORK/certs" "$WORK/app"
printf '[req]\ndistinguished_name=dn\n[dn]\n' > "$WORK/openssl.cnf"
export OPENSSL_CONF="$(hostpath "$WORK/openssl.cnf")"  # a native Windows openssl reads Windows paths
(
  cd "$WORK/certs"
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 -keyout ca.key -out ca.pem -subj "/CN=NCBRS TLS rehearsal CA" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
  openssl req -newkey rsa:2048 -nodes -keyout server.key -out server.csr -subj "/CN=tls-rehearsal"
  printf 'subjectAltName=DNS:pg.tls,DNS:kafka.tls,DNS:keycloak.tls,DNS:api.tls,DNS:district.tls,DNS:localhost\nextendedKeyUsage=serverAuth\n' > san.ext
  openssl x509 -req -in server.csr -CA ca.pem -CAkey ca.key -CAcreateserial -out server.pem -days 2 -extfile san.ext
  cat server.key server.pem > kafka-keystore.pem
  printf 'KafkaServer {\n  org.apache.kafka.common.security.scram.ScramLoginModule required;\n};\n' > kafka-jaas.conf
  chmod a+r ./*  # read by the Kafka and Keycloak containers' own users; throwaway keys
) 2> "$WORK/openssl.log" || { cat "$WORK/openssl.log"; exit 1; }
CERTS="$(hostpath "$WORK/certs")"

echo "== Publishing the Api, Relay, Consumer, District node and load driver for linux-x64"
for service in Api Relay Consumer District LoadTest; do
  project="src/NCBRS.$service"; [[ "$service" == LoadTest ]] && project="tools/NCBRS.LoadTest"
  dotnet publish "$project" -c Release -r linux-x64 --self-contained -p:OpenApiGenerateDocuments=false \
    --artifacts-path "$(hostpath "$WORK/artifacts")" -o "$(hostpath "$WORK/app/$service")" -v q -nologo > "$WORK/publish-$service.log" 2>&1 \
    || { cat "$WORK/publish-$service.log"; exit 1; }
done
APP="$(hostpath "$WORK/app")"

echo "== Starting TLS-only Postgres, SASL_SSL Kafka and HTTPS Keycloak"
cleanup; trap on_exit EXIT
docker network create "$NET" > /dev/null

# Postgres refuses a key readable by others, and a bind mount cannot say so,
# so the key is copied in and its permissions set before the server starts.
# hostssl only: a plaintext client is refused outright.
docker run -d --name tls-pg --network "$NET" --network-alias pg.tls \
  -e POSTGRES_USER=ncbrs -e POSTGRES_PASSWORD="$PG_PASSWORD" -e POSTGRES_DB=ncbrs -v "$CERTS:/certs:ro" \
  --entrypoint bash "$IMAGE_PG" -c 'mkdir -p /ssl && cp /certs/server.pem /certs/server.key /ssl/ && chown postgres:postgres /ssl/* && chmod 600 /ssl/server.key && printf "local all all trust\nhostssl all all 0.0.0.0/0 scram-sha-256\nhostssl all all ::/0 scram-sha-256\n" > /ssl/pg_hba.conf && chown postgres /ssl/pg_hba.conf && exec docker-entrypoint.sh postgres -c ssl=on -c ssl_cert_file=/ssl/server.pem -c ssl_key_file=/ssl/server.key -c hba_file=/ssl/pg_hba.conf' > /dev/null

docker run -d --name tls-kafka --network "$NET" --network-alias kafka.tls -v "$CERTS:/certs:ro" \
  -e KAFKA_NODE_ID=1 -e KAFKA_PROCESS_ROLES=broker,controller \
  -e KAFKA_LISTENERS=INTERNAL://:29092,EXTERNAL://:9095,CONTROLLER://:9093 \
  -e KAFKA_ADVERTISED_LISTENERS=INTERNAL://localhost:29092,EXTERNAL://kafka.tls:9095 \
  -e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,INTERNAL:PLAINTEXT,EXTERNAL:SASL_SSL \
  -e KAFKA_INTER_BROKER_LISTENER_NAME=INTERNAL -e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER \
  -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093 \
  -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 -e KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1 \
  -e KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1 -e KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0 \
  -e KAFKA_SASL_ENABLED_MECHANISMS=SCRAM-SHA-512 \
  -e KAFKA_SSL_KEYSTORE_TYPE=PEM -e KAFKA_SSL_KEYSTORE_LOCATION=/certs/kafka-keystore.pem \
  -e KAFKA_OPTS=-Djava.security.auth.login.config=/certs/kafka-jaas.conf \
  "$IMAGE_KAFKA" > /dev/null

# start-dev also listens on 8080 inside the container; only 8443 is published.
docker run -d --name tls-keycloak --network "$NET" --network-alias keycloak.tls -p "$KC_PORT:8443" \
  -v "$CERTS:/certs:ro" -v "$(hostpath "$PWD/keycloak/ncbrs-realm.json"):/opt/keycloak/data/import/ncbrs-realm.json:ro" \
  "$IMAGE_KEYCLOAK" start-dev --import-realm --https-port=8443 \
  --https-certificate-file=/certs/server.pem --https-certificate-key-file=/certs/server.key \
  --hostname=https://keycloak.tls:8443 > /dev/null

wait_until 90 "Postgres ready" docker exec tls-pg pg_isready -U ncbrs -h localhost
wait_until 120 "Kafka ready" bash -c 'docker logs tls-kafka 2>&1 | grep -q "Kafka Server started"'
wait_until 180 "Keycloak ready" bash -c 'docker logs tls-keycloak 2>&1 | grep -q "started in"'
for user in relay consumer; do
  docker exec tls-kafka /opt/kafka/bin/kafka-configs.sh --bootstrap-server localhost:29092 --alter \
    --add-config "SCRAM-SHA-512=[password=$user-rehearsal]" --entity-type users --entity-name "$user" > /dev/null
done

# The services' shared container shape: the published app, the certificates,
# on the rehearsal network. Extra arguments are environment and ports.
run_service() {  # run_service <name> <service> <docker args...>
  local name="$1" service="$2"; shift 2
  docker run -d --name "$name" --network "$NET" -v "$APP:/app:ro" -v "$CERTS:/certs:ro" -w "/app/$service" \
    --entrypoint "/app/$service/NCBRS.$service" "$@" "$RUNTIME_IMAGE" > /dev/null
}

echo "== Seeding: the Api in Development over VerifyFull (it migrates and seeds 72 births)"
run_service tls-api-seed Api -e ASPNETCORE_ENVIRONMENT=Development -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
  -e Database__Provider=Postgres -e "ConnectionStrings__Default=$PG_CS"
sql() { docker exec tls-pg psql -U ncbrs -d ncbrs -Atc "$1" 2> /dev/null; }
seeded() { [[ "$(sql 'SELECT count(*) FROM "BirthRecords"')" == 72 ]]; }
wait_until 180 "seed over VerifyFull" seeded && pass "migrated and seeded 72 births over SSL Mode=VerifyFull"
docker rm -f tls-api-seed > /dev/null

echo "== The central tier in Production, every link verified"
PROD=(-e ASPNETCORE_ENVIRONMENT=Production -e DOTNET_ENVIRONMENT=Production -e ASPNETCORE_URLS=http://0.0.0.0:8080)
# Also serves HTTPS as api.tls: the District node refuses a plain-HTTP centre.
run_service tls-api Api "${PROD[@]}" --network-alias api.tls -p "$API_PORT:8080" \
  -e "ASPNETCORE_URLS=http://0.0.0.0:8080;https://0.0.0.0:8443" \
  -e ASPNETCORE_Kestrel__Certificates__Default__Path=/certs/server.pem \
  -e ASPNETCORE_Kestrel__Certificates__Default__KeyPath=/certs/server.key \
  -e Database__Provider=Postgres -e "ConnectionStrings__Default=$PG_CS" \
  -e "Keycloak__Authority=$AUTHORITY" -e SSL_CERT_FILE=/certs/ca.pem
run_service tls-api-untrusted Api "${PROD[@]}" -p "$UNTRUSTED_PORT:8080" -e Database__Provider=Postgres -e "ConnectionStrings__Default=$PG_CS" \
  -e "Keycloak__Authority=$AUTHORITY"
run_service tls-relay Relay "${PROD[@]}" -e Database__Provider=Postgres -e "ConnectionStrings__Default=$PG_CS" \
  -e Kafka__BootstrapServers=kafka.tls:9095 -e Kafka__SaslUsername=relay -e Kafka__SaslPassword=relay-rehearsal \
  -e Kafka__SslCaLocation=/certs/ca.pem
run_service tls-consumer Consumer "${PROD[@]}" -p "$CONSUMER_PORT:8080" -e "Keycloak__Authority=$AUTHORITY" -e SSL_CERT_FILE=/certs/ca.pem \
  -e "ConnectionStrings__ReadModel=Data Source=/tmp/readmodel.db" -e Kafka__ConsumerGroupId=ncbrs-rehearsal \
  -e Kafka__BootstrapServers=kafka.tls:9095 -e Kafka__SaslUsername=consumer -e Kafka__SaslPassword=consumer-rehearsal \
  -e Kafka__SslCaLocation=/certs/ca.pem

wait_until 60 "Api answers /health" curl -sf "http://localhost:$API_PORT/health" && pass "Api answers /health in Production over VerifyFull"
expect "every registry session from the services is TLS" "t" \
  "$(sql "SELECT bool_and(ssl) FROM pg_stat_ssl JOIN pg_stat_activity USING (pid) WHERE usename = 'ncbrs' AND client_addr IS NOT NULL" || echo none)"

drained() { [[ "$(sql 'SELECT count(*) FROM "OutboxMessages" WHERE "DispatchedAtUtc" IS NULL')" == 0 ]]; }
wait_until 120 "Relay drains the outbox" drained && pass "Relay published all 72 events to Kafka over SASL_SSL/SCRAM"

# The Consumer commits an offset only after writing the projection, so a
# committed offset of 72 with no lag is 72 births projected.
committed() {
  docker exec tls-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:29092 --describe --group ncbrs-rehearsal 2> /dev/null \
    | awk '$2 == "ncbrs.birth-records.registered" { print $4 "/" $6 }'
}
projected() { [[ "$(committed)" == 72/0 ]]; }
wait_until 120 "Consumer projects every registration" projected \
  && pass "Consumer projected all 72 registrations over SASL_SSL/SCRAM (committed 72, lag 0)"

echo "== Tokens from Keycloak over HTTPS"
cat > "$WORK/call.mjs" << 'EOF'
// node call.mjs <kcPort> <user> <url> [forged]  ->  prints the HTTP status
const [kcPort, user, url, forged] = process.argv.slice(2)
const form = new URLSearchParams({ grant_type: 'password', client_id: 'ncbrs-device', username: user, password: 'password' })
const response = await fetch(`https://localhost:${kcPort}/realms/ncbrs/protocol/openid-connect/token`, { method: 'POST', body: form })
let token = (await response.json()).access_token
if (forged) token = token.split('.').slice(0, 2).join('.') + '.' + Buffer.from('not-the-signature').toString('base64url')
console.log((await fetch(url, { headers: { authorization: `Bearer ${token}` } })).status)
EOF
call() { NODE_EXTRA_CA_CERTS="$(hostpath "$WORK/certs/ca.pem")" node "$(hostpath "$WORK/call.mjs")" "$KC_PORT" "$@"; }

expect "HTTPS-realm token accepted by the Api (district officer)" 200 "$(call district.officer "http://localhost:$API_PORT/api/amendments/pending")"
expect "HTTPS-realm token accepted by the Consumer (Ministry dashboard)" 200 "$(call ministry.admin "http://localhost:$CONSUMER_PORT/api/dashboard/summary")"
expect "a forged signature is refused" 401 "$(call district.officer "http://localhost:$API_PORT/api/amendments/pending" forged)"

# #121: an Api that cannot verify Keycloak must refuse -- and must say why.
expect "an Api that does not trust Keycloak's CA refuses the token" 401 "$(call district.officer "http://localhost:$UNTRUSTED_PORT/api/amendments/pending")"
# Waited for, not read once: .NET's console logger writes from a background
# queue, so the line can land just after the response does.
said_why() { docker logs tls-api-untrusted 2>&1 | grep -q "Cannot obtain the identity provider's metadata"; }
wait_until 15 "the untrusted Api logs that it cannot reach the identity provider" said_why \
  && pass "...and logs that it cannot reach the identity provider"

echo "== The District tier in Production: signed batches, post -> District -> centre over HTTPS"
# The node authenticates as itself (over HTTPS to Keycloak) and forwards to the
# centre's HTTPS listener. The centre enforces device signatures, so a batch
# the node carried without its signature header (the fault #100 fixed) comes
# back refused and shows as Rejected below.
run_service tls-district District "${PROD[@]}" --network-alias district.tls -p "$DISTRICT_PORT:8080" \
  -e Central__BaseUrl=https://api.tls:8443 -e "Central__TokenEndpoint=$AUTHORITY/protocol/openid-connect/token" \
  -e Central__Username=district.officer -e Central__Password=password -e SSL_CERT_FILE=/certs/ca.pem \
  -e "ConnectionStrings__Default=Data Source=/tmp/district.db" -e Forwarder__PollInterval=00:00:02
wait_until 60 "District answers" curl -sf "http://localhost:$DISTRICT_PORT/api/Sync/status"

births_before="$(sql 'SELECT count(*) FROM "BirthRecords"')"
BATCHES=8 BATCH_SIZE=5
docker run --rm --name tls-loaddriver --network "$NET" -v "$APP:/app:ro" -v "$CERTS:/certs:ro" -w /app/LoadTest \
  --entrypoint /app/LoadTest/NCBRS.LoadTest -e SSL_CERT_FILE=/certs/ca.pem \
  -e NCBRS_LOAD_API_BASE=http://api.tls:8080/ -e NCBRS_LOAD_SYNC_BASE=http://district.tls:8080/ \
  -e "NCBRS_LOAD_TOKEN_URL=$AUTHORITY/protocol/openid-connect/token" -e NCBRS_LOAD_USERNAME=district.officer \
  -e NCBRS_LOAD_DEVICES=4 -e NCBRS_LOAD_BATCHES=$BATCHES -e NCBRS_LOAD_WARMUP=0 -e NCBRS_LOAD_BATCH_SIZE=$BATCH_SIZE \
  -e NCBRS_LOAD_CONCURRENCY=4 "$RUNTIME_IMAGE" > "$WORK/loaddriver.log" 2>&1 \
  && pass "$BATCHES batches from 4 enrolled devices, each signed, accepted by the District node" \
  || { fail "the load driver's batches were not all accepted"; tail -30 "$WORK/loaddriver.log"; }

district_status() { curl -sf "http://localhost:$DISTRICT_PORT/api/Sync/status"; }
settled() { [[ "$(district_status)" == *"\"queued\":0,"* ]]; }
wait_until 60 "the District settles every batch" settled
status="$(district_status)"
expect "the District forwarded every batch to the centre over HTTPS" "\"forwarded\":$BATCHES" "$(grep -o '"forwarded":[0-9]*' <<< "$status")"
expect "the centre rejected none (the device signature survived the hop)" '"rejected":0' "$(grep -o '"rejected":[0-9]*' <<< "$status")"
expect "the centre registered every record the District carried" "$((births_before + BATCHES * BATCH_SIZE))" "$(sql 'SELECT count(*) FROM "BirthRecords"')"
expect "no device was refused at the centre" 0 "$(sql "SELECT count(*) FROM \"AuditLogs\" WHERE \"Action\" LIKE 'DeviceRefused%'")"

echo "== Refusals at startup outside Development"
refuses() {  # refuses <description> <expected message> <service> <docker args...>
  local what="$1" message="$2" service="$3"; shift 3
  local output
  output="$(timeout 90 docker run --rm --network "$NET" -v "$APP:/app:ro" -v "$CERTS:/certs:ro" -w "/app/$service" \
    --entrypoint "/app/$service/NCBRS.$service" "${PROD[@]}" "$@" "$RUNTIME_IMAGE" 2>&1 || true)"
  grep -qF "$message" <<< "$output" && pass "$what" || fail "$what (no '$message')"
}
refuses "#119: an http:// Keycloak authority" "Keycloak:Authority is 'http://" Api \
  -e Database__Provider=Postgres -e "ConnectionStrings__Default=$PG_CS"
refuses "#114: a Postgres connection that does not verify the server" "SSL Mode=Require outside Development" Api \
  -e Database__Provider=Postgres -e "ConnectionStrings__Default=${PG_CS/VerifyFull/Require}" -e "Keycloak__Authority=$AUTHORITY"
refuses "#113: a plaintext Kafka link" "Kafka:SecurityProtocol is Plaintext outside Development" Relay \
  -e Database__Provider=Postgres -e "ConnectionStrings__Default=$PG_CS" -e Kafka__BootstrapServers=kafka.tls:9095 \
  -e Kafka__SecurityProtocol=Plaintext
refuses "#110: a District node forwarding to a plain-HTTP centre" "must be HTTPS outside Development" District \
  -e Central__BaseUrl=http://api.tls:8080 -e "Central__TokenEndpoint=$AUTHORITY/protocol/openid-connect/token"

echo
if (( failures > 0 )); then echo "TLS rehearsal: $failures check(s) failed"; exit 1; fi
echo "TLS rehearsal: all checks passed"
