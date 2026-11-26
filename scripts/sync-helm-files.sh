#!/usr/bin/env bash
# Copies the files the Helm chart loads with .Files.Get from their sources into deploy/helm/ledgerline/files/.
# Helm cannot read files outside the chart directory, so the chart carries byte-identical copies. The sources
# (Compose and observability files) stay the single place to edit; run this script after changing one of them.
# deploy/helm/ledgerline/tests/render_test.sh fails when a copy differs from its source.
# Idempotent: a copy that is already identical is left untouched. Needs only bash, cp and cmp.
set -euo pipefail

cd "$(dirname "$0")/.."

chart_files="deploy/helm/ledgerline/files"

# "<source>|<name under files/>"
pairs=(
  "deploy/compose/postgres/init-databases.sql|init-databases.sql"
  "deploy/compose/keycloak/ledgerline-realm.json|ledgerline-realm.json"
  "deploy/observability/dashboards/ledgerline-overview.json|ledgerline-overview.json"
  "deploy/observability/provisioning/dashboards.yaml|dashboards.yaml"
  "deploy/compose/rabbitmq/local-events.json|local-events.json"
)

mkdir -p "$chart_files"
for pair in "${pairs[@]}"; do
  src="${pair%%|*}"
  dst="$chart_files/${pair##*|}"
  [ -f "$src" ] || { echo "missing source: $src" >&2; exit 1; }
  if [ -f "$dst" ] && cmp -s "$src" "$dst"; then
    echo "unchanged  $dst"
  else
    cp "$src" "$dst"
    echo "updated    $dst"
  fi
done
