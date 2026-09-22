#!/bin/bash

# Potion Service System Validation Script
# Smoke-tests the endpoints the service actually exposes:
#   /health, /api/health, /api/health/metrics, /api/health/security,
#   /api/health/security/summary, /metrics, /api/health/alerts/webhook,
#   /collaboration (SignalR negotiate), / (dashboard static files)
# Exits non-zero if any required endpoint is unreachable.

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

SERVICE_URL="${SERVICE_URL:-http://localhost:5000}"
TIMEOUT="${TIMEOUT:-30}"
RETRIES="${RETRIES:-3}"

PASSED=0
FAILED=0
declare -a RESULTS=()

echo -e "${BLUE}Starting Potion Service system validation${NC}"
echo "Service URL: $SERVICE_URL"
echo "Timeout: ${TIMEOUT}s  Retries: $RETRIES"
echo ""

check_endpoint() {
    local name="$1"
    local endpoint="$2"
    local expected="${3:-200}"
    local method="${4:-GET}"
    local data="${5:-}"

    for i in $(seq 1 "$RETRIES"); do
        if [ "$method" = "GET" ]; then
            http_code=$(curl -s -o /dev/null -w "%{http_code}" -m "$TIMEOUT" "$SERVICE_URL$endpoint" 2>/dev/null || echo "000")
        else
            http_code=$(curl -s -o /dev/null -w "%{http_code}" -m "$TIMEOUT" -X "$method" -H "Content-Type: application/json" -d "$data" "$SERVICE_URL$endpoint" 2>/dev/null || echo "000")
        fi

        if [ "$http_code" = "$expected" ]; then
            echo -e "${GREEN}[PASS]${NC} $name ($method $endpoint -> $http_code)"
            PASSED=$((PASSED + 1))
            RESULTS+=("PASS $name")
            return 0
        fi

        [ "$i" -lt "$RETRIES" ] && sleep 2
    done

    echo -e "${RED}[FAIL]${NC} $name ($method $endpoint -> $http_code, expected $expected)"
    FAILED=$((FAILED + 1))
    RESULTS+=("FAIL $name")
    return 0
}

echo -e "${BLUE}Core endpoints${NC}"
check_endpoint "Health probe"            "/health"
check_endpoint "Health API"             "/api/health"
check_endpoint "Health metrics"         "/api/health/metrics"
check_endpoint "Security summary"       "/api/health/security/summary"
check_endpoint "Security detail"        "/api/health/security"
check_endpoint "Prometheus metrics"     "/metrics"
check_endpoint "Dashboard"              "/"

echo ""
echo -e "${BLUE}Auxiliary endpoints${NC}"
# The webhook only accepts POST — a GET returning 405 still proves it is mapped.
check_endpoint "Alert webhook (mapped)" "/api/health/alerts/webhook" "405" "GET"
# SignalR negotiate endpoint responds with connection metadata (JSON) on POST.
check_endpoint "Collaboration hub"      "/collaboration/negotiate?negotiateVersion=1" "200" "POST" "{}"

echo ""
echo -e "${BLUE}Summary${NC}"
echo "Passed: $PASSED  Failed: $FAILED"

{
    echo "# Potion Service Validation Report"
    echo "Generated: $(date)"
    echo "Service URL: $SERVICE_URL"
    echo ""
    for r in "${RESULTS[@]}"; do echo "- $r"; done
    echo ""
    echo "Passed: $PASSED  Failed: $FAILED"
} > validation-report.txt

if [ "$FAILED" -gt 0 ]; then
    echo -e "${RED}Validation failed — see validation-report.txt${NC}"
    exit 1
fi

echo -e "${GREEN}All checks passed.${NC}"
exit 0
