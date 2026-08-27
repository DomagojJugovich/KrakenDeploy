#!/usr/bin/env bash
# Blue-green slot-deployment smoke test (docs/blue-green-slot-deployment.md).
# ---------------------------------------------------------------------------
# Boots one app-node slice — the slot ROUTER + two real server instances
# ("releases" rel-a / rel-b) over ONE shared KrakenDb, under the ON-PREM
# blue-green topology (BG1: Deployment__Topology=OnPremBlueGreen, release
# registry in KrakenDb's `platform` schema, NO multi-account config) — and
# walks the §8 runbook, asserting the routing contract at every step:
#
#   1. cookieless request → default release (rel-a), version cookie issued;
#   2. pre-flip health-gate: X-KD-Release header reaches the Deploying slot;
#   3. flip → NEW sessions land on rel-b, while a session pinned to rel-a
#      STAYS on rel-a (the not-dropping-work guarantee);
#   4. /slot-metrics reports each slot's own release id;
#   5. retire rel-a → the stale pin falls back to rel-b and is re-pinned.
#
# Requires: docker compose v2, curl  (no jq — headers are parsed with sed/grep)

set -euo pipefail

COMPOSE_FILE="docker-compose.smoke-bluegreen.yml"
# Dedicated project name so this stack's `down -v` can never touch the main
# docker-compose.yml stack or the other smokes.
COMPOSE="docker compose -p krakendeploy-smoke-bg -f $COMPOSE_FILE"
BASE="http://localhost:8080"
TIMEOUT_SEC=240

cleanup() {
    echo ""
    echo "--- Tearing down blue-green smoke environment ---"
    $COMPOSE down -v --remove-orphans 2>/dev/null || true
}
trap cleanup EXIT

# Fetch response headers for a GET (drop the body).
headers() { # $@ = extra curl args
    curl -s -D - -o /dev/null "$@"
}

# Extract a header value (case-insensitive name, trimmed). Emits nothing when
# the header is absent — WITHOUT failing the pipeline (absence is an expected,
# asserted-on outcome under `set -euo pipefail`).
header_value() { # $1 = header name; stdin = headers
    { grep -i "^$1:" || true; } | head -1 | sed "s/^[^:]*:[[:space:]]*//" | tr -d '\r'
}

releases_cli() { # $@ = args after `releases`
    $COMPOSE exec -T slot-a dotnet KrakenDeploy.Server.dll releases "$@"
}

wait_healthy() { # $1 = service
    local deadline=$((SECONDS + TIMEOUT_SEC))
    until [ "$($COMPOSE ps --format '{{.Health}}' "$1" 2>/dev/null)" = "healthy" ]; do
        if [ $SECONDS -ge $deadline ]; then
            echo "ERROR: $1 did not become healthy in time."
            $COMPOSE logs "$1" | tail -40
            exit 1
        fi
        sleep 3
    done
}

assert_release() { # $1 = description, $2 = expected release, $3.. = curl args
    local desc="$1" expected="$2"; shift 2
    local got
    got=$(headers "$@" | header_value "X-KD-Release")
    if [ "$got" != "$expected" ]; then
        echo "ERROR: $desc — expected X-KD-Release '$expected', got '${got:-<none>}'."
        exit 1
    fi
    echo "      $desc → $got"
}

echo "======================================"
echo " KrakenDeploy Blue-Green Slot Smoke"
echo "======================================"

echo ""
echo "[1/8] Building images..."
$COMPOSE build --quiet

echo "[2/8] Initialising the database (kraken-init's job: app schema + platform"
echo "      schema + Hangfire schema — a blue-green slot boot prepares NONE of"
echo "      these), then starting slot-a..."
$COMPOSE up -d postgres
$COMPOSE run --rm -T slot-a database setup
$COMPOSE up -d slot-a
wait_healthy slot-a

echo "[3/8] Registering release rel-a (slot 1) and flipping the default to it..."
releases_cli register --id rel-a --label "smoke v1" --slot 1
releases_cli flip --id rel-a

echo "[4/8] Starting slot-b + router; registering rel-b (slot 2, Deploying)..."
$COMPOSE up -d slot-b
wait_healthy slot-b
releases_cli register --id rel-b --label "smoke v2" --slot 2
$COMPOSE up -d router
wait_healthy router

echo "[5/8] Routing assertions BEFORE the flip..."
# Cookieless → default (rel-a) + version cookie issued.
pin_cookie=$(headers "$BASE/login" | header_value "Set-Cookie" | sed 's/;.*//')
case "$pin_cookie" in
    kd_ver=rel-a) echo "      cookieless request pinned: $pin_cookie" ;;
    *) echo "ERROR: expected Set-Cookie kd_ver=rel-a, got '${pin_cookie:-<none>}'."; exit 1 ;;
esac
assert_release "cookieless request routes to default" "rel-a" "$BASE/login"
# Pre-flip health-gate: the Deploying release is reachable ONLY via the header.
assert_release "health-gate header reaches Deploying rel-b" "rel-b" -H "X-KD-Release: rel-b" "$BASE/healthz"
# Each slot reports its own identity on /slot-metrics — probed DIRECTLY on the
# slot instance, the way the drain-watcher does (internal surface).
metrics_a=$($COMPOSE exec -T slot-a curl -s http://localhost:5080/slot-metrics)
case "$metrics_a" in
    *'"release":"rel-a"'*) echo "      slot-a /slot-metrics: $metrics_a" ;;
    *) echo "ERROR: slot-a slot-metrics unexpected: $metrics_a"; exit 1 ;;
esac
metrics_b=$($COMPOSE exec -T slot-b curl -s http://localhost:5080/slot-metrics)
case "$metrics_b" in
    *'"release":"rel-b"'*) echo "      slot-b /slot-metrics: $metrics_b" ;;
    *) echo "ERROR: slot-b slot-metrics unexpected: $metrics_b"; exit 1 ;;
esac
# ...and the router must REFUSE to forward it (internet-facing surface).
metrics_code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/slot-metrics")
if [ "$metrics_code" != "404" ]; then
    echo "ERROR: router should refuse /slot-metrics (expected 404, got $metrics_code)."; exit 1
fi
echo "      router refuses /slot-metrics to the edge (404, correct)"

echo "[6/8] Flipping the default to rel-b..."
releases_cli flip --id rel-b
sleep 4   # router cache TTL is 2 s; the CLI also push-invalidates

echo "[7/8] Routing assertions AFTER the flip..."
# New (cookieless) sessions land on the new default.
assert_release "new session routes to rel-b" "rel-b" "$BASE/login"
# THE blue-green guarantee: a session pinned to the Draining release stays there.
assert_release "pinned session STAYS on draining rel-a" "rel-a" --cookie "kd_ver=rel-a" "$BASE/login"
# No re-pin for a live pin: the draining response must NOT re-issue the cookie.
repin=$(headers --cookie "kd_ver=rel-a" "$BASE/login" | header_value "Set-Cookie" | sed 's/;.*//')
if [ -n "$repin" ] && [ "${repin#kd_ver=}" != "$repin" ]; then
    echo "ERROR: draining pin was unexpectedly re-issued: $repin"; exit 1
fi
echo "      draining pin not re-issued (correct)"

echo "[8/8] Retiring rel-a; stale pins must fall back to the default..."
releases_cli retire --id rel-a
sleep 4
assert_release "stale rel-a pin falls back to rel-b" "rel-b" --cookie "kd_ver=rel-a" "$BASE/login"
repin=$(headers --cookie "kd_ver=rel-a" "$BASE/login" | header_value "Set-Cookie" | sed 's/;.*//')
case "$repin" in
    kd_ver=rel-b) echo "      stale pin re-issued to rel-b: $repin" ;;
    *) echo "ERROR: expected re-pin Set-Cookie kd_ver=rel-b, got '${repin:-<none>}'."; exit 1 ;;
esac
echo ""
echo "--- releases status ---"
releases_cli status

echo ""
echo "======================================"
echo " BLUE-GREEN SLOT SMOKE PASSED"
echo "  - cookieless → default + pin issued"
echo "  - X-KD-Release health-gate reaches Deploying slot"
echo "  - flip: new sessions move, pinned sessions stay"
echo "  - per-slot /slot-metrics report their release (direct); router refuses it (edge)"
echo "  - retire: stale pins fall back + re-pin to default"
echo "======================================"
