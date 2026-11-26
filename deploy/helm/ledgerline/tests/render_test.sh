#!/usr/bin/env bash
# Render tests for the Ledgerline Helm chart. Needs only bash, grep, sed, awk and helm.
# Prints RENDER OK and exits 0 when every assertion holds; otherwise prints FAIL lines and exits 1.
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHART="$(cd "$HERE/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAILURES=0
fail() { echo "FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
check() { # check <description> <command...>
  local desc="$1"; shift
  if ! "$@" >/dev/null 2>&1; then fail "$desc"; fi
}

SERVICES="accounts payments risk notifications banksim gateway"

# Splits a multi-document manifest into $2/doc<N>.yaml.
split_docs() { # split_docs <manifest> <dir>
  mkdir -p "$2"
  awk -v dir="$2" 'BEGIN { n = 0 } /^---[ \t]*$/ { n++; next } { print > (dir "/doc" n ".yaml") }' "$1"
}

# Prints the lines nested under the first "<key>:" line of a document (indent greater than the key's).
block() { # block <file> <key>
  awk -v key="$2" '
    found && /^[ \t]*$/ { next }
    found { match($0, /^ */); if (RLENGTH > ind) { print; next } else { exit } }
    !found { if (match($0, "^ *" key ":")) { found = 1; match($0, /^ */); ind = RLENGTH } }
  ' "$1"
}

doc_kind() { sed -n 's/^kind: *//p' "$1" | head -n 1; }
doc_name() { awk '/^metadata:/ { m = 1; next } m && /^  name:/ { sub(/^  name: */, ""); gsub(/"/, ""); print; exit }' "$1"; }

# Names of Secrets and ConfigMaps a document references (envFrom, valueFrom, volumes).
referenced_names() { # referenced_names <file>
  awk '
    /(secretRef|configMapRef|secretKeyRef|configMapKeyRef):/ { want = 1; next }
    /secretName:/ { sub(/.*secretName: */, ""); gsub(/"/, ""); print; next }
    /^[ \t]*(configMap|secret):[ \t]*$/ { want = 1; next }
    want && /name:/ { sub(/.*name: */, ""); gsub(/"/, ""); print; want = 0 }
  ' "$1"
}

# Prints "<kind> <name>" for every document in a directory produced by split_docs.
list_docs() { # list_docs <dir>
  local f
  for f in "$1"/doc*.yaml; do
    [ -f "$f" ] || continue
    echo "$(doc_kind "$f") $(doc_name "$f")"
  done
}

helm_render() { # helm_render <outfile> [helm args...]
  local out="$1"; shift
  helm template ledgerline "$CHART" "$@" >"$out" 2>"$out.err"
}

# ---- lint ---------------------------------------------------------------------------------
if ! helm lint "$CHART" --strict >"$WORK/lint.out" 2>&1; then
  cat "$WORK/lint.out" >&2
  fail "helm lint --strict is not clean"
fi

# ---- default render -----------------------------------------------------------------------
if ! helm_render "$WORK/default.yaml"; then
  cat "$WORK/default.yaml.err" >&2
  fail "helm template failed"
  echo "RENDER FAILED ($FAILURES failure(s))" >&2
  exit 1
fi
split_docs "$WORK/default.yaml" "$WORK/default"
list_docs "$WORK/default" >"$WORK/default.list"

for svc in $SERVICES; do
  check "Deployment $svc is rendered" grep -qx "Deployment $svc" "$WORK/default.list"
  check "Service $svc is rendered" grep -qx "Service $svc" "$WORK/default.list"
done
DEPLOY_COUNT="$(grep -c '^Deployment ' "$WORK/default.list")"
[ "$DEPLOY_COUNT" -eq 6 ] || fail "expected 6 Deployments, found $DEPLOY_COUNT"

# ---- every Deployment: probes, security, resources, shutdown --------------------------------
for f in "$WORK"/default/doc*.yaml; do
  [ "$(doc_kind "$f")" = "Deployment" ] || continue
  n="$(doc_name "$f")"

  startup="$(block "$f" startupProbe)"
  live="$(block "$f" livenessProbe)"
  ready="$(block "$f" readinessProbe)"
  [ -n "$startup" ] || fail "$n: no startupProbe"
  [ -n "$live" ] || fail "$n: no livenessProbe"
  [ -n "$ready" ] || fail "$n: no readinessProbe"
  grep -q 'path: /health/live' <<<"$startup" || fail "$n: startupProbe must use /health/live"
  grep -q 'failureThreshold: 30' <<<"$startup" || fail "$n: startupProbe needs failureThreshold 30"
  grep -q 'periodSeconds: 2$' <<<"$startup" || fail "$n: startupProbe needs periodSeconds 2"
  grep -q 'path: /health/live' <<<"$live" || fail "$n: livenessProbe must use /health/live"
  grep -q 'path: /health/ready' <<<"$ready" || fail "$n: readinessProbe must use /health/ready"

  grep -q 'runAsNonRoot: true' "$f" || fail "$n: runAsNonRoot must be true"
  grep -Eq 'runAsUser: [1-9][0-9]*' "$f" || fail "$n: runAsUser must be a non-zero uid"
  grep -q 'allowPrivilegeEscalation: false' "$f" || fail "$n: allowPrivilegeEscalation must be false"
  grep -q -- '- ALL' "$f" || fail "$n: capabilities must drop ALL"

  resources="$(block "$f" resources)"
  grep -q 'requests:' <<<"$resources" || fail "$n: resources.requests missing"
  limits="$(block "$f" limits)"
  grep -q 'memory:' <<<"$limits" || fail "$n: resources.limits.memory missing"

  grep -q 'terminationGracePeriodSeconds: 30$' "$f" || fail "$n: terminationGracePeriodSeconds must be 30"
  prestop="$(block "$f" preStop)"
  grep -q 'seconds: 5$' <<<"$prestop" || fail "$n: preStop sleep must be 5 seconds"
  grep -q 'imagePullPolicy: IfNotPresent' "$f" || fail "$n: imagePullPolicy must be stated"
  grep -Eq 'image: "?ledgerline-'"$n"':local' "$f" || fail "$n: image must be ledgerline-$n:local"
done

# ---- Services: only the gateway is a NodePort, names match Compose -------------------------
NODEPORTS=0
for f in "$WORK"/default/doc*.yaml; do
  [ "$(doc_kind "$f")" = "Service" ] || continue
  n="$(doc_name "$f")"
  if grep -q 'type: NodePort' "$f"; then
    NODEPORTS=$((NODEPORTS + 1))
    [ "$n" = "gateway" ] || fail "Service $n must not be NodePort"
    grep -q 'nodePort: 30080' "$f" || fail "gateway Service must use nodePort 30080"
  else
    grep -q 'type: ClusterIP' "$f" || fail "Service $n must be ClusterIP"
  fi
  grep -q 'port: 8080' "$f" || fail "Service $n must expose port 8080"
done
[ "$NODEPORTS" -eq 1 ] || fail "expected exactly 1 NodePort Service, found $NODEPORTS"

# ---- Configuration parity with compose.yaml ---------------------------------------------------
config_has() { # config_has <service> <pattern>
  grep -q -- "$2" "$WORK/default/cfg-$1.yaml" 2>/dev/null
}
for f in "$WORK"/default/doc*.yaml; do
  [ "$(doc_kind "$f")" = "ConfigMap" ] && cp "$f" "$WORK/default/cfg-$(doc_name "$f" | sed 's/-config$//').yaml"
  [ "$(doc_kind "$f")" = "Secret" ] && cp "$f" "$WORK/default/sec-$(doc_name "$f" | sed 's/-secrets$//').yaml"
done
for svc in $SERVICES; do
  config_has "$svc" 'Auth__Authority: "http://keycloak:8081/realms/ledgerline"' || fail "$svc: Auth__Authority"
  config_has "$svc" 'Auth__Audience: "ledgerline-api"' || fail "$svc: Auth__Audience"
  config_has "$svc" 'Auth__RequireHttpsMetadata: "false"' || fail "$svc: Auth__RequireHttpsMetadata"
  config_has "$svc" 'OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-lgtm:4317"' || fail "$svc: OTEL_EXPORTER_OTLP_ENDPOINT"
done
for svc in accounts payments risk notifications; do
  config_has "$svc" 'ConnectionStrings__Redis: "redis:6379"' || fail "$svc: ConnectionStrings__Redis"
done
for svc in accounts payments; do
  config_has "$svc" 'Auth__Mode: "Oidc"' || fail "$svc: Auth__Mode"
  config_has "$svc" 'ASPNETCORE_ENVIRONMENT: "Development"' || fail "$svc: ASPNETCORE_ENVIRONMENT"
done
config_has payments 'Bank__BaseUrl: "http://banksim:8080"' || fail "payments: Bank__BaseUrl"
config_has notifications 'Smtp__Host: "mailpit"' || fail "notifications: Smtp__Host"
config_has notifications 'Smtp__Port: "1025"' || fail "notifications: Smtp__Port"

secret_has() { grep -q -- "$2" "$WORK/default/sec-$1.yaml" 2>/dev/null; }
for svc in accounts payments risk notifications banksim; do
  secret_has "$svc" "ConnectionStrings__Database: \"Host=postgres;Database=${svc}_db;Username=${svc};Password=${svc}_dev;Maximum Pool Size=40\"" \
    || fail "$svc: Secret ConnectionStrings__Database"
done
for svc in accounts payments risk notifications; do
  secret_has "$svc" 'Messaging__RabbitMq__Uri: "amqp://ledgerline:ledgerline_dev@rabbitmq:5672/"' || fail "$svc: Secret Messaging__RabbitMq__Uri"
done
for svc in payments banksim; do
  secret_has "$svc" 'Bank__ApiKey: "ledgerline-bank-local-dev"' || fail "$svc: Secret Bank__ApiKey"
done
[ ! -f "$WORK/default/sec-gateway.yaml" ] || fail "gateway must not have a Secret"
# Passwords belong in Secrets only.
for f in "$WORK"/default/cfg-*.yaml; do
  if grep -Eq 'Password=|amqp://|ApiKey' "$f"; then fail "$(basename "$f"): ConfigMap carries a secret value"; fi
done

# ---- PodDisruptionBudget: only for services with more than one replica ---------------------------
grep -q '^PodDisruptionBudget ' "$WORK/default.list" && fail "no PodDisruptionBudget expected with one replica"
if helm_render "$WORK/pdb.yaml" --set services.payments.replicas=2; then
  split_docs "$WORK/pdb.yaml" "$WORK/pdb"
  list_docs "$WORK/pdb" >"$WORK/pdb.list"
  [ "$(grep -c '^PodDisruptionBudget ' "$WORK/pdb.list")" -eq 1 ] || fail "expected exactly one PodDisruptionBudget with payments at 2 replicas"
  grep -qx 'PodDisruptionBudget payments' "$WORK/pdb.list" || fail "PodDisruptionBudget payments missing"
  grep -q 'replicas: 2' "$WORK/pdb/doc"*.yaml || fail "payments Deployment must carry replicas: 2"
else
  cat "$WORK/pdb.yaml.err" >&2
  fail "helm template with payments replicas=2 failed"
fi

# ---- Two-pass deploy switch (section 0.1 C1): infrastructure only ------------------------------
if helm_render "$WORK/infra.yaml" --set infraOnly=true; then
  split_docs "$WORK/infra.yaml" "$WORK/infra"
  list_docs "$WORK/infra" >"$WORK/infra.list"
  for svc in $SERVICES; do
    grep -qx "Deployment $svc" "$WORK/infra.list" && fail "infraOnly: Deployment $svc must not render"
    grep -qx "Service $svc" "$WORK/infra.list" && fail "infraOnly: Service $svc must not render"
    grep -qx "PodDisruptionBudget $svc" "$WORK/infra.list" && fail "infraOnly: PodDisruptionBudget $svc must not render"
    grep -qx "Job $svc-migrate" "$WORK/infra.list" && fail "infraOnly: migration Job $svc must not render"
  done
  grep -q '^Job ' "$WORK/infra.list" && fail "infraOnly: no Job may render"
  for svc in accounts payments risk notifications banksim; do
    grep -qx "Secret $svc-secrets" "$WORK/infra.list" || fail "infraOnly: Secret $svc-secrets must still render"
  done

  # Every Secret and ConfigMap a migration Job references must exist in the infrastructure-only render.
  # Self-check of the extractor on a Deployment, which uses envFrom the same way a Job does.
  self="$(referenced_names "$WORK/default/$(grep -l '^kind: Deployment' "$WORK"/default/doc*.yaml | head -n 1 | xargs basename)" | sort | tr '\n' ' ')"
  case "$self" in
    *-config*-secrets*) ;;
    *) fail "reference extractor self-check failed (got: $self)" ;;
  esac
  for f in "$WORK"/default/doc*.yaml; do
    [ "$(doc_kind "$f")" = "Job" ] || continue
    j="$(doc_name "$f")"
    for ref in $(referenced_names "$f"); do
      grep -Eqx "(Secret|ConfigMap) $ref" "$WORK/infra.list" || fail "migration Job $j references $ref, which infraOnly does not render"
    done
  done
else
  cat "$WORK/infra.yaml.err" >&2
  fail "helm template --set infraOnly=true failed"
fi

if [ "$FAILURES" -ne 0 ]; then
  echo "RENDER FAILED ($FAILURES failure(s))" >&2
  exit 1
fi
echo "RENDER OK"
