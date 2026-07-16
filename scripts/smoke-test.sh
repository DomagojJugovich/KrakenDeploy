#!/usr/bin/env bash
# Cross-platform smoke test.
# Builds server + agent Docker images, starts them with Postgres, generates a
# registration token via the dev-only endpoint, connects an agent, asserts the
# target goes Online — and then (B8) TRIGGERS A REAL DEPLOYMENT through the
# production dispatch path (worker -> hub -> agent -> step-package download ->
# bash script) and asserts it reaches Succeeded. Connectivity alone proved
# nothing about the wire contract; this exercises the full round trip.
#
# Usage: bash scripts/smoke-test.sh
# Requires: docker compose v2, curl, jq

set -euo pipefail

COMPOSE_FILE="docker-compose.smoke.yml"
# Dedicated project name (smoke-multiaccount.sh precedent): without it the
# stack shares the default working-directory project name and `down -v
# --remove-orphans` REMOVES unrelated local containers named krakendeploy-*
# (e.g. a developer's dev Postgres).
COMPOSE="docker compose -p krakendeploy-smoke -f $COMPOSE_FILE"
SERVER_URL="http://localhost:5080"
TIMEOUT_SEC=90
# Throwaway DataProtection cert for the run — see step 0. A relative path so
# the compose file's bind mount resolves it (compose dir == repo root).
DP_CERT_FILE=".smoke-dp.pfx"
export DP_CERT_PASSWORD="smoke-dp-pass"

cleanup() {
    echo ""
    echo "--- Tearing down smoke environment ---"
    $COMPOSE down -v --remove-orphans 2>/dev/null || true
    rm -f "$DP_CERT_FILE" .smoke-dp-key.pem .smoke-dp-crt.pem
}
trap cleanup EXIT

echo "======================================"
echo " KrakenDeploy Smoke Test"
echo "======================================"

# ── 0. Generate a throwaway DataProtection cert ─────────────────────────────
# The server is Linux (no DPAPI); production requires an operator-provided PFX
# to encrypt the key ring. The smoke generates a self-signed one and feeds it
# via DataProtection__CertificatePath (see docker-compose.smoke.yml) so the
# real ProtectKeysWithCertificate path is exercised, not the dev bypass. The
# cert is world-readable on purpose (the container's non-root user must read
# the bind-mounted file) and is a disposable test key.
echo ""
echo "[0/6] Generating a throwaway DataProtection cert..."
# MSYS_NO_PATHCONV=1: on Git-Bash (Windows) MSYS2 would path-translate the
# leading "/" in -subj into "C:/Program Files/Git/CN=..."; it is a no-op on
# Linux (CI). Intermediates are relative (no leading slash) so they aren't
# mangled either. Errors are NOT silenced — a generation failure must be
# visible, not a set -e teardown at step 0.
MSYS_NO_PATHCONV=1 openssl req -x509 -newkey rsa:2048 -keyout .smoke-dp-key.pem \
    -out .smoke-dp-crt.pem -days 1 -nodes -subj "/CN=krakendeploy-smoke-dp"
openssl pkcs12 -export -out "$DP_CERT_FILE" \
    -inkey .smoke-dp-key.pem -in .smoke-dp-crt.pem \
    -passout "pass:${DP_CERT_PASSWORD}"
rm -f .smoke-dp-key.pem .smoke-dp-crt.pem
# World-readable: the bind-mounted file is read by the container's non-root
# `kraken` user, and the PFX is password-protected anyway.
chmod 644 "$DP_CERT_FILE"
echo "      Cert written to $DP_CERT_FILE."

# ── 1. Build images ────────────────────────────────────────────────────────
echo ""
echo "[1/6] Building Docker images..."
$COMPOSE build --quiet

# ── 2. Start Postgres + Server ─────────────────────────────────────────────
echo "[2/6] Starting Postgres and Server..."
$COMPOSE up -d postgres server

# ── 3. Wait for server /healthz ────────────────────────────────────────────
echo "[3/6] Waiting for Server to become healthy (up to ${TIMEOUT_SEC}s)..."
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
echo "[4/6] Requesting smoke registration token..."
SMOKE_TOKEN=$(curl -sf -X POST "$SERVER_URL/api/dev/smoke-register" \
    | jq -r '.token')

if [[ -z "$SMOKE_TOKEN" || "$SMOKE_TOKEN" == "null" ]]; then
    echo "ERROR: Failed to obtain a registration token."
    $COMPOSE logs server
    exit 1
fi
echo "      Token obtained (first 8 chars): ${SMOKE_TOKEN:0:8}..."

# ── 5. Start Agent and wait for it to appear Online ───────────────────────
echo "[5/6] Starting Agent and waiting for it to connect..."
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
echo "      Agent is Online."

# ── 6. Trigger a REAL deployment and assert it succeeds (B8) ───────────────
echo "[6/6] Triggering a real deployment against the connected agent..."
# No -f and a trailing `|| true`: under `set -e -o pipefail` a non-2xx would
# abort inside the substitution BEFORE the diagnostic branch below runs.
DEPLOYMENT_ID=$(curl -s -X POST "$SERVER_URL/api/dev/smoke-deploy" \
    | jq -r '.deploymentId // empty' || true)

if [[ -z "$DEPLOYMENT_ID" || "$DEPLOYMENT_ID" == "null" ]]; then
    echo "ERROR: Failed to trigger the smoke deployment."
    $COMPOSE logs server
    exit 1
fi
echo "      Deployment ${DEPLOYMENT_ID:0:8}... dispatched; polling for terminal status."

STATUS="Unknown"
deadline=$((SECONDS + TIMEOUT_SEC))
while :; do
    STATUS=$(curl -s "$SERVER_URL/api/dev/smoke-deploy/$DEPLOYMENT_ID" \
        | jq -r '.status // "Unknown"' || true)
    case "$STATUS" in
        Succeeded)
            break
            ;;
        Failed|Cancelled|SucceededWithWarnings)
            echo "ERROR: Deployment ended in status '$STATUS' (expected Succeeded)."
            $COMPOSE logs server
            $COMPOSE logs agent
            exit 1
            ;;
    esac
    if [[ $SECONDS -ge $deadline ]]; then
        echo "ERROR: Deployment did not reach a terminal status in time (last: $STATUS)."
        $COMPOSE logs server
        $COMPOSE logs agent
        exit 1
    fi
    sleep 3
done
echo "      Deployment Succeeded."

echo ""
echo "======================================"
echo " SMOKE TEST PASSED"
echo " Agent connected AND a real deployment"
echo " round-tripped to Succeeded."
echo "======================================"
