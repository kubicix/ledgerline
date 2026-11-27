#!/usr/bin/env bash
# Creates the kind cluster "ledgerline" from deploy/kind/cluster.yaml if it does not exist yet (idempotent).
# The cluster binds host ports 8080, 8081, 3000 and 8025, the same as the Compose stack: stop Compose first.
set -euo pipefail

cd "$(dirname "$0")/.."
cluster=ledgerline

for tool in kind kubectl docker; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool not found on PATH, see docs/kubernetes.md" >&2; exit 1; }
done

if kind get clusters 2>/dev/null | grep -qx "$cluster"; then
  echo "kind cluster '$cluster' already exists"
else
  kind create cluster --config deploy/kind/cluster.yaml --wait 120s
fi
kubectl config use-context "kind-$cluster" >/dev/null
kubectl --context "kind-$cluster" get nodes
