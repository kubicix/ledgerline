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
INFRA="postgres rabbitmq redis keycloak otel-lgtm mailpit"
is_service() { case " $SERVICES " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

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

find_doc() { # find_doc <dir> <Kind> <name>: prints the path of the document, returns 1 when there is none
  local f path
  if [ ! -f "$1/.index" ]; then # one pass per directory: "<Kind> <name> <path>"
    for f in "$1"/doc*.yaml; do
      [ -f "$f" ] && echo "$(doc_kind "$f") $(doc_name "$f") $f"
    done >"$1/.index"
  fi
  path="$(awk -v k="$2" -v n="$3" '$1 == k && $2 == n { print $3; exit }' "$1/.index")"
  [ -n "$path" ] || return 1
  echo "$path"
}

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
DEPLOY_COUNT=0
for s in $SERVICES; do
  grep -qx "Deployment $s" "$WORK/default.list" && DEPLOY_COUNT=$((DEPLOY_COUNT + 1))
done
[ "$DEPLOY_COUNT" -eq 6 ] || fail "expected 6 application Deployments, found $DEPLOY_COUNT"

# ---- every application Deployment: probes, security, resources, shutdown --------------------------------
for f in "$WORK"/default/doc*.yaml; do
  [ "$(doc_kind "$f")" = "Deployment" ] || continue
  n="$(doc_name "$f")"
  is_service "$n" || continue

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

# ---- Application Services: only the gateway is a NodePort, names match Compose -------------------------
NODEPORTS=0
for f in "$WORK"/default/doc*.yaml; do
  [ "$(doc_kind "$f")" = "Service" ] || continue
  n="$(doc_name "$f")"
  is_service "$n" || continue
  if grep -q 'type: NodePort' "$f"; then
    NODEPORTS=$((NODEPORTS + 1))
    [ "$n" = "gateway" ] || fail "Service $n must not be NodePort"
    grep -q 'nodePort: 30080' "$f" || fail "gateway Service must use nodePort 30080"
  else
    grep -q 'type: ClusterIP' "$f" || fail "Service $n must be ClusterIP"
  fi
  grep -Eq '^ *port: 8080$' "$f" || fail "Service $n must expose port 8080"
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
for svc in $SERVICES; do
  f="$WORK/default/cfg-$svc.yaml"
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


# ---- Migration Jobs (M4-T04, section 0.1 C1) -------------------------------------------------------
MIGRATE_SERVICES="accounts payments risk notifications banksim"
JOB_COUNT="$(grep -c '^Job .*-migrate$' "$WORK/default.list")"
[ "$JOB_COUNT" -eq 5 ] || fail "expected 5 migration Jobs, found $JOB_COUNT"
for svc in $MIGRATE_SERVICES; do
  check "Job $svc-migrate is rendered" grep -qx "Job $svc-migrate" "$WORK/default.list"
done
grep -q '^Job gateway' "$WORK/default.list" && fail "the gateway must not have a migration Job"
for f in "$WORK"/default/doc*.yaml; do
  [ "$(doc_kind "$f")" = "Job" ] || continue
  j="$(doc_name "$f")"
  case "$j" in *-migrate) ;; *) continue ;; esac
  svc="${j%-migrate}"
  grep -Eq '"?helm.sh/hook"?: "?pre-install,pre-upgrade"?$' "$f" || fail "$j: hook must be pre-install,pre-upgrade"
  grep -Eq '"?helm.sh/hook-delete-policy"?: "?before-hook-creation,hook-succeeded"?$' "$f" \
    || fail "$j: hook-delete-policy must be before-hook-creation,hook-succeeded"
  grep -Eq '"?helm.sh/hook-weight"?: "?-?[0-9]+"?$' "$f" || fail "$j: hook-weight must be stated"
  grep -q '^  backoffLimit: 6$' "$f" || fail "$j: backoffLimit must be 6"
  grep -q 'restartPolicy: Never$' "$f" || fail "$j: restartPolicy must be Never"
  grep -Eq 'image: "?ledgerline-'"$svc"':local"?$' "$f" || fail "$j: image must be ledgerline-$svc:local"
  grep -q 'args: \["migrate"\]$' "$f" || fail "$j: args must be [\"migrate\"]"
  # Pod labels must not match the Service selector of <svc>, or the Service would route traffic to the Job pod.
  grep -Eq "^ *app.kubernetes.io/name: $svc\$" "$f" && fail "$j: pod labels must not match the $svc Service selector"
  grep -Eq "^ *app.kubernetes.io/name: $j\$" "$f" || fail "$j: labels must name $j"

  init="$(block "$f" initContainers)"
  [ -n "$init" ] || fail "$j: no initContainers"
  grep -Eq 'image: "?postgres:18.7-alpine"?$' <<<"$init" || fail "$j: init container must use postgres:18.7-alpine"
  grep -q 'pg_isready' <<<"$init" || fail "$j: init container must wait with pg_isready"
  # Without -U, pg_isready as a uid with no passwd entry reports "no attempt" forever.
  grep -Eq 'pg_isready .*-U [a-z]+' <<<"$init" || fail "$j: pg_isready needs -U"
  grep -Eq 'pg_isready .*-h "?postgres"? .*-p "?5432"?' <<<"$init" || fail "$j: pg_isready must target postgres:5432"

  grep -q 'runAsNonRoot: true' "$f" || fail "$j: runAsNonRoot must be true"
  grep -Eq 'runAsUser: [1-9][0-9]*' "$f" || fail "$j: runAsUser must be a non-zero uid"
  [ "$(grep -c 'allowPrivilegeEscalation: false' "$f")" -eq 2 ] || fail "$j: both containers need allowPrivilegeEscalation false"
  [ "$(grep -c -- '- ALL' "$f")" -eq 2 ] || fail "$j: both containers must drop ALL capabilities"
  [ "$(grep -c '^ *limits:' "$f")" -eq 2 ] || fail "$j: both containers need resource limits"
  [ "$(grep -c '^ *requests:' "$f")" -eq 2 ] || fail "$j: both containers need resource requests"

  # Same database settings as the compose <svc>-migrate job, read from the Secret.
  env="$(block "$f" env)"
  grep -q 'name: ConnectionStrings__Database' <<<"$env" || fail "$j: ConnectionStrings__Database env missing"
  grep -q "name: $svc-secrets" <<<"$env" || fail "$j: ConnectionStrings__Database must come from $svc-secrets"
  grep -q 'key: ConnectionStrings__Database' <<<"$env" || fail "$j: secretKeyRef key must be ConnectionStrings__Database"
done

# The Job list follows services.<name>.migrate, not a fixed list.
if helm_render "$WORK/nomig.yaml" --set services.risk.migrate=false; then
  split_docs "$WORK/nomig.yaml" "$WORK/nomig"
  list_docs "$WORK/nomig" >"$WORK/nomig.list"
  [ "$(grep -c '^Job .*-migrate$' "$WORK/nomig.list")" -eq 4 ] || fail "risk.migrate=false: expected 4 migration Jobs"
  grep -qx 'Job risk-migrate' "$WORK/nomig.list" && fail "risk.migrate=false: Job risk-migrate must not render"
else
  cat "$WORK/nomig.yaml.err" >&2
  fail "helm template with services.risk.migrate=false failed"
fi

# ---- Two-pass deploy switch (section 0.1 C1): infrastructure only ------------------------------
if helm_render "$WORK/infra.yaml" --set infraOnly=true; then
  split_docs "$WORK/infra.yaml" "$WORK/infra"
  list_docs "$WORK/infra" >"$WORK/infra.list"
  for svc in $SERVICES; do
    grep -qx "Deployment $svc" "$WORK/infra.list" && fail "infraOnly: Deployment $svc must not render"
    grep -qx "Service $svc" "$WORK/infra.list" && fail "infraOnly: Service $svc must not render"
    grep -qx "PodDisruptionBudget $svc" "$WORK/infra.list" && fail "infraOnly: PodDisruptionBudget $svc must not render"
  done
  grep -q '^Job ' "$WORK/infra.list" && fail "infraOnly: no Job may render"
  for svc in accounts payments risk notifications banksim; do
    grep -qx "Secret $svc-secrets" "$WORK/infra.list" || fail "infraOnly: Secret $svc-secrets must still render"
  done

  # Every Secret and ConfigMap a hook Job (5 migration Jobs and rabbitmq-definitions) references must exist in the
  # infrastructure-only render.
  # Self-check of the extractor on a Deployment, which uses envFrom the same way a Job does.
  self="$(referenced_names "$(find_doc "$WORK/default" Deployment accounts)" | sort | tr '\n' ' ')"
  case "$self" in
    *-config*-secrets*) ;;
    *) fail "reference extractor self-check failed (got: $self)" ;;
  esac
  JOBS_CHECKED=0
  for f in "$WORK"/default/doc*.yaml; do
    [ "$(doc_kind "$f")" = "Job" ] || continue
    j="$(doc_name "$f")"
    JOBS_CHECKED=$((JOBS_CHECKED + 1))
    refs="$(referenced_names "$f")"
    [ -n "$refs" ] || fail "reference extractor found nothing in hook Job $j"
    for ref in $refs; do
      grep -Eqx "(Secret|ConfigMap) $ref" "$WORK/infra.list" || fail "hook Job $j references $ref, which infraOnly does not render"
    done
  done
  [ "$JOBS_CHECKED" -eq 6 ] || fail "infraOnly reference check must see 6 hook Jobs, saw $JOBS_CHECKED"
else
  cat "$WORK/infra.yaml.err" >&2
  fail "helm template --set infraOnly=true failed"
fi

# ---- Infrastructure (M4-T05): hand-written manifests that reproduce compose.yaml ------------------------
REPO="$(cd "$CHART/../../.." && pwd)"

# Files the chart loads with .Files.Get are copies of their sources, refreshed by scripts/sync-helm-files.sh.
SYNC_PAIRS="
deploy/compose/postgres/init-databases.sql|files/init-databases.sql
deploy/compose/keycloak/ledgerline-realm.json|files/ledgerline-realm.json
deploy/observability/dashboards/ledgerline-overview.json|files/ledgerline-overview.json
deploy/observability/provisioning/dashboards.yaml|files/dashboards.yaml
deploy/compose/rabbitmq/local-events.json|files/local-events.json
"
for pair in $SYNC_PAIRS; do
  src="${pair%%|*}"; dst="${pair##*|}"
  [ -f "$REPO/$src" ] || { fail "sync source $src is missing"; continue; }
  [ -f "$CHART/$dst" ] || { fail "chart file $dst is missing (run scripts/sync-helm-files.sh)"; continue; }
  cmp -s "$REPO/$src" "$CHART/$dst" || fail "chart file $dst differs from $src (run scripts/sync-helm-files.sh)"
done
[ -f "$REPO/scripts/sync-helm-files.sh" ] || fail "scripts/sync-helm-files.sh is missing"

# Objects: PostgreSQL and RabbitMQ are StatefulSets with a PVC, the rest are Deployments; each has a Service.
for n in postgres rabbitmq; do
  grep -qx "StatefulSet $n" "$WORK/default.list" || fail "StatefulSet $n is not rendered"
done
for n in redis keycloak otel-lgtm mailpit; do
  grep -qx "Deployment $n" "$WORK/default.list" || fail "Deployment $n is not rendered"
done
for n in $INFRA; do
  grep -qx "Service $n" "$WORK/default.list" || fail "Service $n is not rendered"
done
for n in postgres rabbitmq; do
  f="$(find_doc "$WORK/default" StatefulSet "$n")" || continue
  grep -q 'volumeClaimTemplates:' "$f" || fail "StatefulSet $n needs volumeClaimTemplates"
  grep -q "serviceName: $n\$" "$f" || fail "StatefulSet $n needs serviceName $n"
done
for n in postgres rabbitmq keycloak; do
  grep -qx "Secret $n-secrets" "$WORK/default.list" || fail "Secret $n-secrets is not rendered"
done

# Every infrastructure workload: same image as compose.yaml, probes, limits, non-root, stated pull policy.
infra_workload() { # infra_workload <name>: prints the StatefulSet or Deployment document of an infrastructure component
  find_doc "$WORK/default" StatefulSet "$1" || find_doc "$WORK/default" Deployment "$1"
}
infra_image() { # infra_image <name> <image>
  local f
  f="$(infra_workload "$1")" || return 0
  grep -Eq "image: \"?$2\"?\$" "$f" || fail "$1: image must be $2"
}
infra_image postgres 'postgres:18.7-alpine'
infra_image rabbitmq 'rabbitmq:4.4-management'
infra_image redis 'redis:8.8-alpine'
infra_image keycloak 'quay.io/keycloak/keycloak:26.8.2'
infra_image otel-lgtm 'grafana/otel-lgtm:0.41.0'
infra_image mailpit 'axllent/mailpit:v1.33.1'

for n in $INFRA; do
  f="$(infra_workload "$n")" || continue
  [ -n "$(block "$f" startupProbe)" ] || fail "$n: no startupProbe"
  [ -n "$(block "$f" livenessProbe)" ] || fail "$n: no livenessProbe"
  [ -n "$(block "$f" readinessProbe)" ] || fail "$n: no readinessProbe"
  grep -q 'runAsNonRoot: true' "$f" || fail "$n: runAsNonRoot must be true"
  grep -Eq 'runAsUser: [1-9][0-9]*' "$f" || fail "$n: runAsUser must be a non-zero uid"
  grep -q 'allowPrivilegeEscalation: false' "$f" || fail "$n: allowPrivilegeEscalation must be false"
  grep -q -- '- ALL' "$f" || fail "$n: capabilities must drop ALL"
  grep -q 'requests:' <<<"$(block "$f" resources)" || fail "$n: resources.requests missing"
  grep -q 'memory:' <<<"$(block "$f" limits)" || fail "$n: resources.limits.memory missing"
  grep -q 'imagePullPolicy: IfNotPresent' "$f" || fail "$n: imagePullPolicy must be stated"
done

# PostgreSQL: max_connections=300, init script mounted into the entrypoint directory, password from a Secret.
if f="$(find_doc "$WORK/default" StatefulSet postgres)"; then
  grep -q 'max_connections=300' "$f" || fail "postgres: max_connections=300 missing"
  grep -q 'mountPath: /docker-entrypoint-initdb.d' "$f" || fail "postgres: init script must mount at /docker-entrypoint-initdb.d"
  grep -q 'mountPath: /var/lib/postgresql$' "$f" || fail "postgres: data volume must mount at /var/lib/postgresql"
  grep -q 'name: postgres-init' "$f" || fail "postgres: init ConfigMap postgres-init not referenced"
  grep -q 'pg_isready' <<<"$(block "$f" readinessProbe)" || fail "postgres: readiness must use pg_isready"
  grep -q 'POSTGRES_PASSWORD' "$f" || fail "postgres: POSTGRES_PASSWORD missing"
fi
if f="$(find_doc "$WORK/default" ConfigMap postgres-init)"; then
  grep -q 'CREATE DATABASE payments_db OWNER payments' "$f" || fail "postgres-init must carry init-databases.sql"
else
  fail "ConfigMap postgres-init is not rendered"
fi
if f="$(find_doc "$WORK/default" Secret postgres-secrets)"; then
  grep -q 'POSTGRES_PASSWORD: "ledgerline_dev"' "$f" || fail "postgres-secrets: POSTGRES_PASSWORD"
fi

# RabbitMQ: user and password from a Secret, AMQP and management ports, compose health check as readiness.
if f="$(find_doc "$WORK/default" StatefulSet rabbitmq)"; then
  grep -q 'mountPath: /var/lib/rabbitmq$' "$f" || fail "rabbitmq: data volume must mount at /var/lib/rabbitmq"
  grep -q 'check_port_connectivity' <<<"$(block "$f" readinessProbe)" || fail "rabbitmq: readiness must use check_port_connectivity"
fi
if f="$(find_doc "$WORK/default" Secret rabbitmq-secrets)"; then
  grep -q 'RABBITMQ_DEFAULT_USER: "ledgerline"' "$f" || fail "rabbitmq-secrets: RABBITMQ_DEFAULT_USER"
  grep -q 'RABBITMQ_DEFAULT_PASS: "ledgerline_dev"' "$f" || fail "rabbitmq-secrets: RABBITMQ_DEFAULT_PASS"
fi
if f="$(find_doc "$WORK/default" Service rabbitmq)"; then
  grep -Eq '^ *port: 5672$' "$f" || fail "Service rabbitmq must expose 5672"
  grep -Eq '^ *port: 15672$' "$f" || fail "Service rabbitmq must expose 15672"
fi

# Redis: append-only file on, as in compose.yaml.
if f="$(find_doc "$WORK/default" Deployment redis)"; then
  grep -q -- '--appendonly' "$f" || fail "redis: --appendonly missing"
  grep -Eq '^ *- "?yes"?$' "$f" || fail "redis: appendonly must be yes"
fi

# Keycloak: Compose settings verbatim, realm import, and a startup budget at least as generous as the Compose
# health check (30 s start period plus 30 retries of 5 s).
if f="$(find_doc "$WORK/default" Deployment keycloak)"; then
  grep -Eq '^ *- "?start-dev"?$' "$f" || fail "keycloak: start-dev missing"
  grep -Eq '^ *- "?--import-realm"?$' "$f" || fail "keycloak: --import-realm missing"
  grep -Eq '^ *- name: KC_HTTP_PORT$' "$f" || fail "keycloak: KC_HTTP_PORT missing"
  grep -Eq 'value: "?8081"?$' "$f" || fail "keycloak: KC_HTTP_PORT must be 8081"
  grep -Eq 'value: "?http://localhost:8081"?$' "$f" || fail "keycloak: KC_HOSTNAME must be http://localhost:8081"
  grep -q 'name: KC_HOSTNAME_BACKCHANNEL_DYNAMIC' "$f" || fail "keycloak: KC_HOSTNAME_BACKCHANNEL_DYNAMIC missing"
  grep -q 'name: KC_HEALTH_ENABLED' "$f" || fail "keycloak: KC_HEALTH_ENABLED missing"
  grep -q 'mountPath: /opt/keycloak/data/import' "$f" || fail "keycloak: realm must mount at /opt/keycloak/data/import"
  grep -q 'name: keycloak-realm' "$f" || fail "keycloak: realm ConfigMap keycloak-realm not referenced"
  grep -q 'port: 9000' <<<"$(block "$f" readinessProbe)" || fail "keycloak: readiness must use the management port 9000"
  grep -q 'path: /health/ready' <<<"$(block "$f" readinessProbe)" || fail "keycloak: readiness must use /health/ready"
  st="$(block "$f" startupProbe)"
  period="$(sed -n 's/^ *periodSeconds: *//p' <<<"$st")"
  threshold="$(sed -n 's/^ *failureThreshold: *//p' <<<"$st")"
  if [ -n "$period" ] && [ -n "$threshold" ]; then
    [ $((period * threshold)) -ge 180 ] || fail "keycloak: startup budget must be at least 180 s (is $((period * threshold)) s)"
  else
    fail "keycloak: startupProbe needs periodSeconds and failureThreshold"
  fi
fi
if f="$(find_doc "$WORK/default" Secret keycloak-secrets)"; then
  grep -q 'KC_BOOTSTRAP_ADMIN_USERNAME: "admin"' "$f" || fail "keycloak-secrets: KC_BOOTSTRAP_ADMIN_USERNAME"
  grep -q 'KC_BOOTSTRAP_ADMIN_PASSWORD: "ledgerline_dev"' "$f" || fail "keycloak-secrets: KC_BOOTSTRAP_ADMIN_PASSWORD"
fi
if f="$(find_doc "$WORK/default" ConfigMap keycloak-realm)"; then
  grep -q '"realm" *: *"ledgerline"' "$f" || fail "keycloak-realm must carry ledgerline-realm.json"
else
  fail "ConfigMap keycloak-realm is not rendered"
fi

# otel-lgtm: 4 GiB cap as in compose.yaml, telemetry directories in memory, dashboard provisioning.
if f="$(find_doc "$WORK/default" Deployment otel-lgtm)"; then
  grep -Eq 'memory: "?4Gi"?$' <<<"$(block "$f" limits)" || fail "otel-lgtm: memory limit must be 4Gi"
  [ "$(grep -c 'medium: Memory' "$f")" -eq 5 ] || fail "otel-lgtm: five in-memory emptyDir volumes expected (loki, tempo, /var/tempo, prometheus, pyroscope)"
  for d in /data/grafana /data/loki /data/tempo /var/tempo /data/prometheus /data/pyroscope /etc/ledgerline/dashboards; do
    grep -q "mountPath: $d\$" "$f" || fail "otel-lgtm: mountPath $d missing"
  done
  grep -q 'mountPath: /otel-lgtm/grafana/conf/provisioning/dashboards/ledgerline.yaml' "$f" \
    || fail "otel-lgtm: provisioning file must mount at /otel-lgtm/grafana/conf/provisioning/dashboards/ledgerline.yaml"
fi
if f="$(find_doc "$WORK/default" ConfigMap otel-lgtm-dashboards)"; then
  grep -q '"uid" *: *"ledgerline-overview"' "$f" || fail "otel-lgtm-dashboards must carry ledgerline-overview.json"
else
  fail "ConfigMap otel-lgtm-dashboards is not rendered"
fi
if f="$(find_doc "$WORK/default" ConfigMap otel-lgtm-provisioning)"; then
  grep -q 'path: /etc/ledgerline/dashboards' "$f" || fail "otel-lgtm-provisioning must carry dashboards.yaml"
else
  fail "ConfigMap otel-lgtm-provisioning is not rendered"
fi
if f="$(find_doc "$WORK/default" Service otel-lgtm)"; then
  for p in 3000 4317 4318; do grep -Eq "^ *port: $p\$" "$f" || fail "Service otel-lgtm must expose $p"; done
fi

# Mailpit: database file under /data as in compose.yaml; SMTP and web UI ports.
if f="$(find_doc "$WORK/default" Deployment mailpit)"; then
  grep -q 'name: MP_DATABASE' "$f" || fail "mailpit: MP_DATABASE missing"
  grep -q '/data/mailpit.db' "$f" || fail "mailpit: MP_DATABASE must be /data/mailpit.db"
fi
if f="$(find_doc "$WORK/default" Service mailpit)"; then
  grep -Eq '^ *port: 1025$' "$f" || fail "Service mailpit must expose 1025"
  grep -Eq '^ *port: 8025$' "$f" || fail "Service mailpit must expose 8025"
fi

# Exposure: among the infrastructure exactly Keycloak (30081), Grafana in otel-lgtm (30300) and Mailpit (30025) are
# NodePort. Together with the gateway (30080) they are the four mappings of deploy/kind/cluster.yaml.
for n in $INFRA; do
  f="$(find_doc "$WORK/default" Service "$n")" || continue
  case "$n" in
    keycloak) want=30081 ;;
    otel-lgtm) want=30300 ;;
    mailpit) want=30025 ;;
    *) want="" ;;
  esac
  if [ -n "$want" ]; then
    grep -q 'type: NodePort' "$f" || fail "Service $n must be NodePort"
    grep -q "nodePort: $want\$" "$f" || fail "Service $n must use nodePort $want"
  else
    grep -q 'type: ClusterIP' "$f" || fail "Service $n must be ClusterIP"
    grep -q 'nodePort' "$f" && fail "Service $n must not set a nodePort"
  fi
done
RENDERED_NODEPORTS="$(grep -h 'nodePort:' "$WORK"/default/doc*.yaml | sed 's/.*nodePort: *//' | sort -n | tr '\n' ' ')"
KIND_NODEPORTS="$(sed -n 's/^ *- containerPort: *//p' "$REPO/deploy/kind/cluster.yaml" | sort -n | tr '\n' ' ')"
[ "$RENDERED_NODEPORTS" = "$KIND_NODEPORTS" ] \
  || fail "nodePorts in the chart ($RENDERED_NODEPORTS) must equal the containerPorts of deploy/kind/cluster.yaml ($KIND_NODEPORTS)"

# The wait image of the migration Jobs and the PostgreSQL server image are one value, infra.postgres.image.
if helm_render "$WORK/pgimage.yaml" --set infra.postgres.image=postgres:99.9-alpine; then
  split_docs "$WORK/pgimage.yaml" "$WORK/pgimage"
  if f="$(find_doc "$WORK/pgimage" StatefulSet postgres)"; then
    grep -Eq 'image: "?postgres:99.9-alpine"?$' "$f" || fail "infra.postgres.image must reach the postgres StatefulSet"
  else
    fail "pgimage: StatefulSet postgres missing"
  fi
  if f="$(find_doc "$WORK/pgimage" Job accounts-migrate)"; then
    grep -Eq 'image: "?postgres:99.9-alpine"?$' "$f" || fail "infra.postgres.image must reach the migration Job wait container"
  else
    fail "pgimage: Job accounts-migrate missing"
  fi
else
  cat "$WORK/pgimage.yaml.err" >&2
  fail "helm template with infra.postgres.image failed"
fi

# infra.<name>.enabled=false removes that component's objects and nothing else.
if helm_render "$WORK/nopg.yaml" --set infra.postgres.enabled=false; then
  split_docs "$WORK/nopg.yaml" "$WORK/nopg"
  list_docs "$WORK/nopg" >"$WORK/nopg.list"
  grep -Eq '^[A-Za-z]+ postgres' "$WORK/nopg.list" && fail "infra.postgres.enabled=false: PostgreSQL objects still render"
  for n in rabbitmq redis keycloak otel-lgtm mailpit; do
    grep -Eq "^(StatefulSet|Deployment) $n\$" "$WORK/nopg.list" || fail "infra.postgres.enabled=false: $n must still render"
  done
  [ "$(grep -c '^Job .*-migrate$' "$WORK/nopg.list")" -eq 5 ] || fail "infra.postgres.enabled=false: migration Jobs must still render (external database)"
else
  cat "$WORK/nopg.yaml.err" >&2
  fail "helm template --set infra.postgres.enabled=false failed"
fi
if helm_render "$WORK/noinfra.yaml" \
  --set infra.postgres.enabled=false --set infra.rabbitmq.enabled=false --set infra.redis.enabled=false \
  --set infra.keycloak.enabled=false --set infra.otelLgtm.enabled=false --set infra.mailpit.enabled=false; then
  split_docs "$WORK/noinfra.yaml" "$WORK/noinfra"
  list_docs "$WORK/noinfra" >"$WORK/noinfra.list"
  for n in $INFRA; do
    grep -Eq "^[A-Za-z]+ $n(-[a-z]+)?\$" "$WORK/noinfra.list" && fail "all infra disabled: objects of $n still render"
  done
  grep -q '^StatefulSet ' "$WORK/noinfra.list" && fail "all infra disabled: a StatefulSet still renders"
  [ "$(grep -c '^Deployment ' "$WORK/noinfra.list")" -eq 6 ] || fail "all infra disabled: only the 6 application Deployments may remain"
else
  cat "$WORK/noinfra.yaml.err" >&2
  fail "helm template with all infrastructure disabled failed"
fi

# RabbitMQ definitions (compose.yaml: rabbitmq-init). The local.events queue is bound to
# accounts.event.journal-entry-posted.v1, which no service consumes; without the binding the Accounts relay publishes
# that event unroutable, retries it and holds back every later message (found on the first kind deploy, M4-T06).
if f="$(find_doc "$WORK/default" Job rabbitmq-definitions)"; then
  grep -Eq '"?helm.sh/hook"?: "?pre-install,pre-upgrade"?$' "$f" || fail "rabbitmq-definitions: hook must be pre-install,pre-upgrade"
  grep -Eq '"?helm.sh/hook-delete-policy"?: "?before-hook-creation,hook-succeeded"?$' "$f" \
    || fail "rabbitmq-definitions: hook-delete-policy must be before-hook-creation,hook-succeeded"
  grep -Eq '"?helm.sh/hook-weight"?: "?-1"?$' "$f" || fail "rabbitmq-definitions: hook-weight must be -1 (before the migrations)"
  grep -q '^  backoffLimit: 6$' "$f" || fail "rabbitmq-definitions: backoffLimit must be 6"
  grep -q 'restartPolicy: Never$' "$f" || fail "rabbitmq-definitions: restartPolicy must be Never"
  grep -Eq 'image: "?rabbitmq:4.4-management"?$' "$f" || fail "rabbitmq-definitions: image must be infra.rabbitmq.image"
  grep -q 'rabbitmqadmin' "$f" && grep -q 'definitions' "$f" && grep -q 'import' "$f" \
    || fail "rabbitmq-definitions: must run rabbitmqadmin definitions import"
  grep -Eq "^ *app.kubernetes.io/name: rabbitmq\$" "$f" && fail "rabbitmq-definitions: pod labels must not match the rabbitmq selector"
  env="$(block "$f" env)"
  grep -q 'name: RABBITMQADMIN_TARGET_HOST' <<<"$env" || fail "rabbitmq-definitions: RABBITMQADMIN_TARGET_HOST missing"
  grep -q 'key: RABBITMQ_DEFAULT_USER' <<<"$env" || fail "rabbitmq-definitions: user must come from rabbitmq-secrets"
  grep -q 'key: RABBITMQ_DEFAULT_PASS' <<<"$env" || fail "rabbitmq-definitions: password must come from rabbitmq-secrets"
  grep -q 'name: rabbitmq-secrets' <<<"$env" || fail "rabbitmq-definitions: credentials must come from rabbitmq-secrets"
  grep -q 'name: rabbitmq-definitions' "$f" || fail "rabbitmq-definitions: ConfigMap rabbitmq-definitions not mounted"
  grep -q 'runAsNonRoot: true' "$f" || fail "rabbitmq-definitions: runAsNonRoot must be true"
  grep -q 'allowPrivilegeEscalation: false' "$f" || fail "rabbitmq-definitions: allowPrivilegeEscalation must be false"
  grep -q -- '- ALL' "$f" || fail "rabbitmq-definitions: must drop ALL capabilities"
  grep -q '^ *limits:' "$f" || fail "rabbitmq-definitions: resource limits missing"
else
  fail "Job rabbitmq-definitions is not rendered"
fi
if f="$(find_doc "$WORK/infra" ConfigMap rabbitmq-definitions)"; then
  grep -q '"local.events"' "$f" || fail "ConfigMap rabbitmq-definitions must declare local.events"
  grep -q 'accounts.event.journal-entry-posted.v1' "$f" || fail "ConfigMap rabbitmq-definitions must bind journal-entry-posted"
else
  fail "infraOnly: ConfigMap rabbitmq-definitions must render"
fi
grep -qx 'Job rabbitmq-definitions' "$WORK/nopg.list" || fail "infra.postgres.enabled=false: Job rabbitmq-definitions must still render"

# The infrastructure renders in the infrastructure-only pass too.
for n in postgres rabbitmq; do
  grep -qx "StatefulSet $n" "$WORK/infra.list" || fail "infraOnly: StatefulSet $n must render"
done
for n in redis keycloak otel-lgtm mailpit; do
  grep -qx "Deployment $n" "$WORK/infra.list" || fail "infraOnly: Deployment $n must render"
done
for n in $INFRA; do
  grep -qx "Service $n" "$WORK/infra.list" || fail "infraOnly: Service $n must render"
done

if [ "$FAILURES" -ne 0 ]; then
  echo "RENDER FAILED ($FAILURES failure(s))" >&2
  exit 1
fi
echo "RENDER OK"
