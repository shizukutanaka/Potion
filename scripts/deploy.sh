#!/bin/bash

# Potion Service Deployment Script
# Deploys the Kubernetes manifests in k8s/ and verifies the rollout.
#
# Usage: ./scripts/deploy.sh
# Env:   KUBECTL (default: kubectl), NAMESPACE (default: potion-system)

set -euo pipefail

KUBECTL="${KUBECTL:-kubectl}"
NAMESPACE="${NAMESPACE:-potion-system}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
K8S_DIR="$SCRIPT_DIR/../k8s"

echo "Starting Potion Service deployment..."
echo "Namespace: $NAMESPACE"
echo "Manifests: $K8S_DIR"

if [ ! -d "$K8S_DIR" ]; then
    echo "Error: k8s/ manifests not found at $K8S_DIR" >&2
    exit 1
fi

# Create namespace and apply manifests
$KUBECTL create namespace "$NAMESPACE" --dry-run=client -o yaml | $KUBECTL apply -f -
$KUBECTL apply -n "$NAMESPACE" -f "$K8S_DIR"

# Verify rollout — the Deployment name comes from k8s/deployment.yaml
DEPLOYMENT="potion-service"
echo "Verifying rollout of deployment/$DEPLOYMENT..."
$KUBECTL rollout status "deployment/$DEPLOYMENT" -n "$NAMESPACE" --timeout=300s

echo "Pod status:"
$KUBECTL get pods -n "$NAMESPACE"

# Smoke-test the endpoints the service actually exposes.
# The aspnet:8.0 runtime image has no curl, so test via port-forward instead.
SERVICE="potion-service"
LOCAL_PORT=18080

$KUBECTL port-forward -n "$NAMESPACE" "svc/$SERVICE" "$LOCAL_PORT:80" &
FORWARD_PID=$!
trap 'kill $FORWARD_PID 2>/dev/null || true' EXIT
sleep 5

FAILED=0
for path in /health /api/health /api/health/metrics /api/health/security/summary /metrics; do
    if curl -fs -m 10 "http://localhost:$LOCAL_PORT$path" -o /dev/null; then
        echo "  PASS $path"
    else
        echo "  FAIL $path" >&2
        FAILED=1
    fi
done

kill $FORWARD_PID 2>/dev/null || true

if [ "$FAILED" -ne 0 ]; then
    echo "Deployment endpoint checks failed." >&2
    exit 1
fi

echo "Deployment completed successfully."
echo ""
echo "Useful commands:"
echo "  $KUBECTL logs -f deployment/$DEPLOYMENT -n $NAMESPACE"
echo "  $KUBECTL describe svc $DEPLOYMENT -n $NAMESPACE"
echo "  $KUBECTL port-forward -n $NAMESPACE svc/$DEPLOYMENT 8080:80"
