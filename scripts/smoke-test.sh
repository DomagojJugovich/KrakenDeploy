#!/usr/bin/env bash
# M1 cross-platform smoke test.
# Builds server + agent Docker images, starts them with Postgres, generates a
# registration token via the dev-only endpoint, connects an agent, and asserts
# the target goes Online by polling /healthz for connectedAgents >= 1.
#
# Usage: bash scripts/smoke-test.sh
# Requires: docker compose v2, curl, jq

set -euo pipefail

COMPOSE_FILE="docker-compose.smoke.yml"
COMPOSE="docker compose -f $COMPOSE_FILE"
SERVER_URL="http://localhost:5080"
TIMEOUT_SEC=90

cleanup() {
    echo ""
    echo "--- Tearing down smoke environment ---"
    $COMPOSE down -v --remove-orphans 2>/dev/null || true
}
trap cleanup EXIT

echo "======================================"
echo " KrakenDeploy M1 Smoke Test"
echo "======================================"

# ── 1. Build images ────────────────────────────────────────────────────────
echo ""
echo "[1/5] Building Docker images..."
$COMPOSE build --quiet

# ── 2. Start Postgres + Server ─────────────────────────────────────────────
echo "[2/5] Starting Postgres and Server..."
$COMPOSE up -d postgres server

# ── 3. Wait for server /healthz ────────────────────────────────────────────
echo "[3/5] Waiting for Server to become healthy (up to ${TIMEOUT_SEC}s)..."
deadline=$((SECONDS + TIMEOUT_SEC))
until curl -sf "$SERVER_URL/healthz" > /dev/null 2>&1; do
    if [[ $SECONDS -ge $deadline ]]; then
        echo "ERROR: Server did not become healthy in time."
        $COMPOSE logs server
        exit 1
    fi
    sleep 3
done
echo "      Server is healthy."

# ── 4. Get a smoke registration token ─────────────────────────────────────
echo "[4/5] Requesting smoke registration token..."
SMOKE_TOKEN=$(curl -sf -X POST "$SERVER_URL/api/dev/smoke-register" \
    | jq -r '.token')

if [[ -z "$SMOKE_TOKEN" || "$SMOKE_TOKEN" == "null" ]]; then
    echo "ERROR: Failed to obtain a registration token."
    $COMPOSE logs server
    exit 1
fi
echo "      Token obtained (first 8 chars): ${SMOKE_TOKEN:0:8}..."

# ── 5. Start Agent and wait for it to appear Online ───────────────────────
echo "[5/5] Starting Agent and waiting for it to connect..."
export SMOKE_TOKEN
$COMPOSE up -d agent

deadline=$((SECONDS + TIMEOUT_SEC))
until [[ $(curl -sf "$SERVER_URL/healthz" \
             | jq '.connectedAgents // 0') -ge 1 ]]; do
    if [[ $SECONDS -ge $deadline ]]; then
        echo "ERROR: Agent did not connect in time."
        $COMPOSE logs agent
        exit 1
    fi
    sleep 3
done

echo ""
echo "======================================"
echo " M1 SMOKE TEST PASSED"
echo " Agent is Online — M1 exit criterion met."
echo "======================================"
