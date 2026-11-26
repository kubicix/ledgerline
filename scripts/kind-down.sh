#!/usr/bin/env bash
# Deletes only the kind cluster "ledgerline". Other kind clusters, Docker networks and volumes are never touched.
set -euo pipefail

cluster=ledgerline
command -v kind >/dev/null 2>&1 || { echo "kind not found on PATH, see docs/kubernetes.md" >&2; exit 1; }

if kind get clusters 2>/dev/null | grep -qx "$cluster"; then
  kind delete cluster --name "$cluster"
else
  echo "kind cluster '$cluster' does not exist"
fi
