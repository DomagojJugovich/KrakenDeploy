#!/usr/bin/env bash
# Multi-account (SaaS, DB-per-account) real-agent smoke test.
# ---------------------------------------------------------------------------
# Builds the server + agent images, boots one server in multi-account mode
# (which seeds the acme + globex demo accounts and their tenant DBs), then
# connects the REAL agent binary to EACH account's subdomain and asserts:
#
#   1. a valid registration token minted in acme's DB is REJECTED (HTTP 401)
#      when replayed against globex — tokens are per-account (negative test);
#   2. each tenant DB ends up with exactly ONE Online target (status = 1),
#      i.e. each agent host-derived its account and landed in the right DB;
#   3. the fleet shows >= 2 connected agents; and
#   4. neither tenant DB holds the other's target (no cross-account leakage).
#
# Routing is Caddy-free: the server carries acme.kraken.local / globex.kraken.local
# network aliases, so the agent's Host header selects the account. Host-side curls
# reach the published port and override Host with -H.
#
# Usage: bash scripts/smoke-multiaccount.sh
# Requires: docker compose v2, curl  (no jq — JSON is parsed with sed)

set -euo pipefail

COMPOSE_FILE="docker-compose.smoke-multiaccount.yml"
# Dedicated project name so this stack (and its `down -v`) is fully isolated from
# the repo's main docker-compose.yml and the single-instance smoke, which would
# otherwise share the default project name (the working-directory basename).
COMPOSE="docker compose -p krakendeploy-smoke-ma -f $COMPOSE_FILE"
BASE="http://localhost:5080"
TIMEOUT_SEC=210

ACME_HOST="acme.kraken.local"
GLOBEX_HOST="globex.kraken.local"

cleanup() {
    echo ""
    echo "--- Tearing down multi-account smoke environment ---"
    $COMPOSE down -v --remove-orphans 2>/dev/null || true
}
trap cleanup EXIT

# Run a scalar SQL query against a tenant database inside the postgres container.
tenant_query() { # $1 = db name, $2 = sql
    $COMPOSE exec -T postgres psql -U postgres -d "$1" -tAc "$2" | tr -d '[:space:]'
}

# Minimal JSON scalar extractors (the responses are flat, single-line objects) so
# the smoke needs no jq. Read stdin, echo the matched value (empty if absent).
json_str() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p"; }
json_num() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p"; }

echo "======================================"
echo " KrakenDeploy Multi-Account Smoke Test"
echo "======================================"

# ── 1. Build images ─────────────────────────────────────────────────────────
echo ""
echo "[1/7] Building Docker images..."
$COMPOSE build --quiet

# ── 2. Start Postgres + Server ───────────────────────────────────────────────
echo "[2/7] Starting Postgres and Server (server provisions acme + globex tenant DBs)..."
$COMPOSE up -d postgres server

# ── 3. Wait for the server to serve a provisioned subdomain ──────────────────
echo "[3/7] Waiting for Server to become healthy via ${ACME_HOST} (up to ${TIMEOUT_SEC}s)..."
deadline=$((SECONDS + TIMEOUT_SEC))
until curl -sf -H "Host: ${ACME_HOST}" "$BASE/healthz" > /dev/null 2>&1; do
    if [[ $SECONDS -ge $deadline ]]; then
        echo "ERROR: Server did not become healthy in time."
        $COMPOSE logs server
        exit 1
    fi
    sleep 3
done
echo "      Server is healthy; acme + globex are provisioned."

# ── 4. Mint a registration token in EACH account (host-qualified) ────────────
echo "[4/7] Minting a registration token in each account..."
SMOKE_TOKEN_ACME=$(curl -sf -H "Host: ${ACME_HOST}" -X POST "$BASE/api/dev/smoke-register" | json_str token)
SMOKE_TOKEN_GLOBEX=$(curl -sf -H "Host: ${GLOBEX_HOST}" -X POST "$BASE/api/dev/smoke-register" | json_str token)

for pair in "acme:$SMOKE_TOKEN_ACME" "globex:$SMOKE_TOKEN_GLOBEX"; do
    if [[ -z "${pair#*:}" ]]; then
        echo "ERROR: failed to obtain a registration token for ${pair%%:*}."
        $COMPOSE logs server
        exit 1
    fi
done
echo "      acme token: ${SMOKE_TOKEN_ACME:0:8}...  globex token: ${SMOKE_TOKEN_GLOBEX:0:8}..."

# ── 5. Negative: acme's token must be rejected at globex ─────────────────────
echo "[5/7] Verifying acme's token is rejected when replayed at globex (per-account isolation)..."
cross_code=$(curl -s -o /dev/null -w '%{http_code}' \
    -H "Host: ${GLOBEX_HOST}" -H 'Content-Type: application/json' \
    -X POST "$BASE/api/agents/register" \
    -d "{\"token\":\"${SMOKE_TOKEN_ACME}\"}")
if [[ "$cross_code" != "401" ]]; then
    echo "ERROR: cross-account token replay returned HTTP ${cross_code}, expected 401."
    exit 1
fi
echo "      Rejected with HTTP 401 — the token does not exist in globex's database."

# ── 6. Start both agents ─────────────────────────────────────────────────────
echo "[6/7] Starting both agents (each targets its own subdomain)..."
export SMOKE_TOKEN_ACME SMOKE_TOKEN_GLOBEX
$COMPOSE up -d agent-acme agent-globex

# ── 7. Assert per-account Online + fleet count + no leakage ──────────────────
echo "[7/7] Waiting for each agent to register + go Online in its OWN tenant DB..."
deadline=$((SECONDS + TIMEOUT_SEC))
while :; do
    # status = 1 is TargetStatus.Online (stored as int); table is snake_case.
    acme_online=$(tenant_query kraken_acct_acme   "SELECT count(*) FROM deployment_targets WHERE status = 1")
    globex_online=$(tenant_query kraken_acct_globex "SELECT count(*) FROM deployment_targets WHERE status = 1")
    connected=$(curl -sf -H "Host: ${ACME_HOST}" "$BASE/healthz" | json_num connectedAgents)
    connected=${connected:-0}

    if [[ "$acme_online" == "1" && "$globex_online" == "1" && "$connected" -ge 2 ]]; then
        break
    fi
    if [[ $SECONDS -ge $deadline ]]; then
        echo "ERROR: agents did not both go Online in time."
        echo "       acme_online=${acme_online} globex_online=${globex_online} connectedAgents=${connected}"
        $COMPOSE logs agent-acme agent-globex
        exit 1
    fi
    sleep 3
done

# No cross-account leakage: each tenant DB must hold exactly its own single target.
acme_total=$(tenant_query kraken_acct_acme   "SELECT count(*) FROM deployment_targets")
globex_total=$(tenant_query kraken_acct_globex "SELECT count(*) FROM deployment_targets")
if [[ "$acme_total" != "1" || "$globex_total" != "1" ]]; then
    echo "ERROR: cross-account leakage — acme has ${acme_total} target(s), globex has ${globex_total}."
    exit 1
fi

echo ""
echo "======================================"
echo " MULTI-ACCOUNT SMOKE TEST PASSED"
echo "  - acme  : 1 Online target in kraken_acct_acme"
echo "  - globex: 1 Online target in kraken_acct_globex"
echo "  - fleet : ${connected} connected agents"
echo "  - cross-account token replay rejected (HTTP 401)"
echo "  - no cross-account target leakage"
echo "======================================"
