#!/bin/bash

# Potion Service Deployment Script
# Enterprise-grade deployment automation

set -euo pipefail

# Configuration
SERVICE_NAME="potion-service"
NAMESPACE="potion-system"
IMAGE_TAG="${1:-latest}"
REPLICAS="${2:-3}"

echo "🚀 Starting Potion Service deployment..."
echo "Service: $SERVICE_NAME"
echo "Namespace: $NAMESPACE"
echo "Image Tag: $IMAGE_TAG"
echo "Replicas: $REPLICAS"

# Create namespace if it doesn't exist
kubectl create namespace $NAMESPACE --dry-run=client -o yaml | kubectl apply -f -

# Deploy using Helm
echo "📦 Deploying with Helm..."
helm upgrade --install $SERVICE_NAME ./helm \
    --namespace $NAMESPACE \
    --set image.tag=$IMAGE_TAG \
    --set replicaCount=$REPLICAS \
    --wait \
    --timeout=300s

# Verify deployment
echo "✅ Verifying deployment..."
kubectl rollout status deployment/$SERVICE_NAME -n $NAMESPACE --timeout=300s

# Check pod health
echo "🏥 Checking pod health..."
kubectl get pods -n $NAMESPACE -l app=$SERVICE_NAME

# Test endpoints
echo "🔍 Testing endpoints..."
sleep 30  # Wait for services to be ready

POD_NAME=$(kubectl get pods -n $NAMESPACE -l app=$SERVICE_NAME -o jsonpath='{.items[0].metadata.name}')

echo "Testing health endpoints..."
kubectl exec $POD_NAME -n $NAMESPACE -- curl -f http://localhost:80/api/health/liveness || exit 1
kubectl exec $POD_NAME -n $NAMESPACE -- curl -f http://localhost:80/api/health/readiness || exit 1

echo "Testing API endpoints..."
kubectl port-forward -n $NAMESPACE svc/$SERVICE_NAME 8080:80 &
FORWARD_PID=$!

sleep 5

# Test comprehensive health
curl -f http://localhost:8080/api/health/system/comprehensive || exit 1

# Test observability
curl -f http://localhost:8080/api/health/observability/tracing || exit 1

# Test security features
curl -f http://localhost:8080/api/health/security/audit || exit 1

kill $FORWARD_PID

echo "🎯 Running integration tests..."
kubectl exec $POD_NAME -n $NAMESPACE -- curl -X POST http://localhost:80/api/health/testing/integration

echo "📊 Checking monitoring..."
kubectl get servicemonitor -n $NAMESPACE
kubectl get prometheusrules -n $NAMESPACE

echo "🔒 Verifying security..."
kubectl get networkpolicy -n $NAMESPACE
kubectl get podsecuritypolicy

echo "📈 Checking autoscaling..."
kubectl get hpa -n $NAMESPACE

echo "✅ Deployment completed successfully!"
echo ""
echo "🌐 Access the service:"
echo "API Documentation: https://potion-service.local/swagger"
echo "Health Dashboard: https://potion-service.local/api/health/detailed"
echo "Metrics: https://potion-service.local/api/health/metrics/custom"
echo ""
echo "📊 Monitoring access:"
echo "Prometheus: http://prometheus.local"
echo "Grafana: http://grafana.local (admin/admin)"
echo "AlertManager: http://alertmanager.local"
echo ""
echo "🛠️  Useful commands:"
echo "kubectl logs -f deployment/$SERVICE_NAME -n $NAMESPACE"
echo "kubectl describe svc $SERVICE_NAME -n $NAMESPACE"
echo "kubectl get events -n $NAMESPACE --sort-by='.lastTimestamp'"

# Cleanup on failure
trap 'echo "❌ Deployment failed! Check logs with: kubectl logs deployment/potion-service -n potion-system"' ERR
