#!/usr/bin/env bash
# Builds the six application images, loads them into the kind cluster "ledgerline" and installs or upgrades the Helm
# release "ledgerline" (ADR 0011). Run scripts/kind-up.sh first. Extra arguments are passed to helm, for example
#   ./scripts/kind-deploy.sh --set services.payments.replicas=2
# Two passes on a first install (docs/kubernetes.md): the migration Jobs are pre-install/pre-upgrade hooks, so the
# infrastructure they wait for must exist before they run. When the release does not exist yet, the first pass installs
# the infrastructure only (infraOnly=true) and waits for it; the second, full pass is then an upgrade whose hooks find
# PostgreSQL running. When the release exists, only the full pass runs: an infrastructure-only pass on an existing
# release would delete the running services. The full pass uses --reset-values: on an upgrade without --set or -f, Helm
# reuses the last release's values, which after pass 1 would keep infraOnly=true. The release values are therefore always
# the chart defaults plus the arguments of this call.
set -euo pipefail

cd "$(dirname "$0")/.."
cluster=ledgerline
context="kind-$cluster"
release=ledgerline
chart=deploy/helm/ledgerline
images=(ledgerline-accounts:local ledgerline-payments:local ledgerline-risk:local ledgerline-notifications:local
  ledgerline-banksim:local ledgerline-gateway:local)

for tool in docker kind helm kubectl; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool not found on PATH, see docs/kubernetes.md" >&2; exit 1; }
done
if ! kind get clusters 2>/dev/null | grep -qx "$cluster"; then
  echo "kind cluster '$cluster' does not exist, run scripts/kind-up.sh first" >&2
  exit 1
fi

echo "== Building images"
docker compose --profile app build

echo "== Loading images into kind cluster '$cluster'"
kind load docker-image "${images[@]}" --name "$cluster"

# Decide on presence with helm list, not helm status: any error (an unreachable cluster, for example) must abort instead
# of being read as "absent", which would run the infrastructure-only pass over a running release.
if ! present="$(helm list --kube-context "$context" -q --filter "^$release\$")"; then
  echo "helm list failed: cannot tell whether release '$release' exists, nothing was installed" >&2
  exit 1
fi
if [[ -n "$present" ]]; then
  echo "== Release '$release' exists: single upgrade"
else
  echo "== Release '$release' does not exist: pass 1 of 2, infrastructure only"
  helm upgrade --install "$release" "$chart" --kube-context "$context" "$@" --set infraOnly=true --wait --timeout 10m
  echo "== Pass 2 of 2, full release"
fi
helm upgrade --install "$release" "$chart" --kube-context "$context" --reset-values "$@" --wait --timeout 10m

kubectl --context "$context" get pods
