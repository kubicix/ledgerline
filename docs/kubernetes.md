# Running Ledgerline on Kubernetes (kind)

Milestone 4 packages the system for Kubernetes and proves it on a local kind cluster. ADR 0011 records the
decisions. This page covers the tools and the cluster lifecycle; later sections cover the Helm chart and the deploy.

## Tools

| Tool | Version | Used for |
|---|---|---|
| Docker | running Docker Desktop or Docker Engine | kind runs the cluster node as a container |
| kind | v0.33.0 | creates and deletes the cluster |
| Helm | v4.3.2 | installs the chart (Helm 4; do not use Helm 3 flags from old guides) |
| kubectl | any current client | talks to the cluster (Docker Desktop ships one) |

Versions are pinned in `docs/adr/0000-verified-versions.md` and were re-checked against the upstream release pages
on 2026-11-26.

### Windows

```powershell
winget install --id Kubernetes.kind --version 0.33.0
winget install --id Helm.Helm --version 4.3.2
```

Open a new terminal afterwards so the updated `PATH` is picked up. If you have no `kubectl`, install it with
`winget install --id Kubernetes.kubectl`.

### macOS

```bash
brew install kind helm kubectl
```

Homebrew installs the newest release. To pin the exact versions, download the binaries from the kind and Helm
release pages (below) instead.

### Linux

```bash
# kind v0.33.0 (use arm64 instead of amd64 on ARM machines)
curl -Lo ./kind https://kind.sigs.k8s.io/dl/v0.33.0/kind-linux-amd64
chmod +x ./kind && sudo mv ./kind /usr/local/bin/kind

# Helm v4.3.2
curl -fsSL https://get.helm.sh/helm-v4.3.2-linux-amd64.tar.gz | tar -xz
sudo mv linux-amd64/helm /usr/local/bin/helm
```

Install `kubectl` from the Kubernetes documentation or your package manager.

### Check the install

```bash
kind version
helm version --short
kubectl version --client
```

## The cluster

`deploy/kind/cluster.yaml` defines a cluster named `ledgerline` with one control-plane node. The kubectl context is
`kind-ledgerline`. The node image is kind's default for v0.33.0 (`kindest/node:v1.37.0`, digest-pinned by kind); the CI job names the
same image and digest explicitly. Upgrading kind upgrades Kubernetes, and the CI image line must be updated with it.

The node maps four NodePorts to the host, using the same host ports as the Compose stack:

| Host port | Node port | Service |
|---|---|---|
| 8080 | 30080 | Gateway, the only public entry for the API |
| 8081 | 30081 | Keycloak, so a token requested at `localhost:8081` has the same issuer the services validate |
| 3000 | 30300 | Grafana (LGTM) |
| 8025 | 30025 | Mailpit web UI |

There is no ingress controller.

### Create and delete

```bash
./scripts/kind-up.sh      # PowerShell: ./scripts/kind-up.ps1
./scripts/kind-down.sh    # PowerShell: ./scripts/kind-down.ps1
```

`kind-up` creates the cluster only when it does not exist, so running it again is harmless, and then prints the
nodes. `kind-down` deletes only the cluster named `ledgerline`; it never touches other kind clusters, Docker networks
or volumes.

```bash
kubectl --context kind-ledgerline get nodes
```

One node in the `Ready` state means the cluster is up.

### Port conflicts

The cluster binds host ports 8080, 8081, 3000 and 8025. The Compose stack and the system-test harness (the
`ledgerline-systest` Compose project) use the same ports, so only one of them can run at a time:

- Stop a Compose project before `kind-up`: `docker compose -p <name> --profile app down` (never add `-v`).
- Run `kind-down` before the system tests.
- If `kind-up` fails with "ports are not available", another program already listens on one of those ports. Stop it
  or free the port, then run `kind-up` again.

## Deploy the system

```bash
./scripts/kind-up.sh && ./scripts/kind-deploy.sh && ./scripts/smoke.sh http://localhost:8080
# PowerShell: ./scripts/kind-up.ps1; ./scripts/kind-deploy.ps1; ./scripts/smoke.ps1 http://localhost:8080
```

`kind-deploy` does three things against the context `kind-ledgerline` (it never uses the current context):

1. builds the six application images with `docker compose --profile app build` (tags `ledgerline-<service>:local`);
2. loads them onto the node with `kind load docker-image`; the pods use them with `imagePullPolicy: IfNotPresent`;
3. installs or upgrades the Helm release `ledgerline` from `deploy/helm/ledgerline` with `--wait --timeout 10m`
   (two passes on a first install, below).

In Helm 4, `--wait` on its own selects the `watcher` strategy: Helm waits until every resource of the release reports
ready, up to `--timeout`. Without the flag Helm 4 waits only for hooks. Extra arguments go to `helm`, for example
`./scripts/kind-deploy.sh --set services.payments.replicas=2`.

The infrastructure images (about 1.5 GB to download, otel-lgtm alone about 1 GB) are pulled by the node on first use, so
the first deploy on a new cluster takes several minutes. The application images keep the tag `:local`, so a later
`kind-deploy` after a code change loads the new image but does not change the pod template: restart the Deployment to
pick it up, for example `kubectl --context kind-ledgerline rollout restart deployment/payments`.

### Two passes on a first install

Each service with a database has a migration Job that runs the service image with the `migrate` argument as a Helm
`pre-install,pre-upgrade` hook (ADR 0011). Helm runs `pre-install` hooks before it creates any regular resource of
the release, and it waits for them. On an empty cluster PostgreSQL is a regular resource of the same release, so a
migration Job of a first install would wait for a database that Helm has not created yet, and the install would time
out.

`kind-deploy` therefore installs in two passes when the release `ledgerline` does not exist (`helm list --filter`
prints nothing; if `helm list` itself fails, for example because the cluster is unreachable, `kind-deploy` stops):

1. `helm upgrade --install ledgerline deploy/helm/ledgerline --set infraOnly=true --wait --timeout 10m` installs only
   the infrastructure and the Secrets and ConfigMaps the hook Jobs read, and waits until it is ready;
2. `helm upgrade --install ledgerline deploy/helm/ledgerline --reset-values --wait --timeout 10m` is now an upgrade:
   its `pre-upgrade` migration Jobs find PostgreSQL running, then Helm creates the services.

The second pass has one more hook Job, `rabbitmq-definitions`, which runs first (hook weight -1). It is the Compose
`rabbitmq-init` job: it imports `deploy/compose/rabbitmq/local-events.json` with `rabbitmqadmin`. That file binds the
`local.events` queue to `accounts.event.journal-entry-posted.v1`, the only queue bound to that event, and to
`reconciliation.event.reconciliation-completed.v1` on the `ledgerline.reconciliation` exchange. Without it the
Accounts outbox relay cannot route the event, retries it and holds back every later Accounts message, so payment
sagas stop at the ledger step (and, with Phase 2, the Reconciliation outbox stops at its completion event). With
`infra.rabbitmq.enabled=false` the external broker must provide the same exchanges, queue and bindings, including
the reconciliation one. The import is idempotent and runs again on every upgrade.

`--reset-values` matters: on an upgrade without `--set` or `-f`, Helm reuses the values of the last release, so the
second pass would keep `infraOnly=true` and install no services. With the flag the release values are always the
chart defaults plus the arguments of that `kind-deploy` call; a `--set` from an earlier call does not stick.

When the release exists, `kind-deploy` runs only the second command. An infrastructure-only pass on an existing release
would delete the running services. After `helm --kube-context kind-ledgerline uninstall ledgerline` the release is
gone, so the next `kind-deploy` runs both passes again. The PostgreSQL and RabbitMQ volumes (PersistentVolumeClaims of
the StatefulSets) survive an uninstall, so the data does too; `kind-down` removes everything.

The smoke scripts use fixed idempotency keys. On a cluster that already ran them they replay the earlier operations:
that passes as long as the Mailpit pod keeps its mail. After `helm uninstall` Mailpit starts empty (its data is not on
a volume), so the replayed operations send no new mail and the email check fails. After an uninstall or an
interrupted smoke run, prove the flow on a freshly created cluster: `./scripts/kind-down.sh && ./scripts/kind-up.sh`.

### Phase 2 (`phase2.enabled`)

`phase2.enabled` (default `false`) adds the M5 Phase 2 parts, the Helm twin of Compose profile `phase2` with
`compose.phase2.yaml`: Kafka (single-node KRaft StatefulSet, `apache/kafka:4.4.0`) and MongoDB (StatefulSet, `mongo:9.0`),
both ClusterIP only; a `kafka-topics` hook Job that creates the topics as the Compose `kafka-init` job does; the
`reporting` and `reconciliation` services (Reconciliation with its migration Job); Kafka publishing in Accounts and
Payments; and the operations address in Notifications. No NodePort and no kind port mapping is added: Reporting and
Reconciliation are reached through the gateway under `/api/reporting/...` and `/api/reconciliation/...`. With the value
off the chart renders exactly the default release, and those two routes answer `503`.

Deploy it as a first install, on a new cluster or after `helm uninstall`. The second works only when the PostgreSQL
PVC was created by this chart version, because PVCs survive an uninstall: Phase 2 needs a data directory initialised
by this version of `init-databases.sql`, which runs only on an empty one and creates the `reconciliation` login and
database. With an older PVC, use a fresh cluster, or create the role and database once by hand
(`CREATE USER reconciliation WITH PASSWORD 'reconciliation_dev'; CREATE DATABASE reconciliation_db OWNER reconciliation;`).

```bash
./scripts/kind-down.sh && ./scripts/kind-up.sh
./scripts/kind-deploy.sh --set phase2.enabled=true
```

`kind-deploy` builds and loads the Reporting and Reconciliation images too when the arguments contain
`phase2.enabled=true`, and passes the value to both passes. `kind-deploy.sh` and `kind-deploy.ps1` detect Phase 2 only
from `--set phase2.enabled=true` among their arguments, not from a `-f` values file, so pass it with `--set`;
otherwise the two Phase 2 images are not built and loaded. Pass 1 (`infraOnly=true`) then also installs Kafka, MongoDB
and the `reconciliation-secrets` Secret, so the pass 2 hooks (`kafka-topics`, `reconciliation-migrate`) find them.

Switching Phase 2 on by upgrading a running default release is not supported. That upgrade's `pre-upgrade` hooks run
before any regular resource of the same upgrade is created, so `kafka-topics` would wait for a Kafka that does not exist
yet and `reconciliation-migrate` for a Secret that does not exist yet, and the upgrade times out. Uninstall and deploy
again (`helm --kube-context kind-ledgerline uninstall ledgerline`, then the `kind-deploy` above), or start from a new
cluster.

### Keycloak and the token issuer

The smoke script and the k6 scripts request tokens from Keycloak at `http://localhost:8081/realms/ledgerline`, from
the host. The services validate them with `Auth__Authority=http://keycloak:8081/realms/ledgerline`, from inside the
cluster. Both must see the same issuer string. The chosen approach is the one Compose already uses:

- Keycloak runs with `KC_HOSTNAME=http://localhost:8081`, `KC_HTTP_PORT=8081` and
  `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`. Every token carries the issuer `http://localhost:8081/realms/ledgerline`,
  wherever it was requested.
- The services read the OpenID metadata from `http://keycloak:8081/realms/ledgerline` (the backchannel, resolved
  dynamically to the in-cluster address for the keys) and take the valid issuer from that metadata, which is the fixed
  `KC_HOSTNAME` value.
- The `keycloak` Service is a NodePort on 30081, which `deploy/kind/cluster.yaml` maps to host port 8081. So
  `localhost:8081` on the host reaches the same Keycloak.

No `kubectl port-forward` is involved, and Keycloak is not routed through the gateway. The gateway stays the only
public entry for the API.

### Check the deploy

```bash
kubectl --context kind-ledgerline get pods
```

Every pod shows `1/1` and `Running`. The hook Jobs (migrations and `rabbitmq-definitions`) are deleted after they
succeed. Grafana is at `http://localhost:3000` and Mailpit at `http://localhost:8025`, as with Compose.

Telemetry is disposable. Its collector can exceed the 4 GiB limit under steady load (a known Milestone 3 finding), and
Kubernetes then restarts the otel-lgtm container. No service readiness check depends on otel-lgtm: with the otel-lgtm
pod deleted and recreated, every service stayed `Ready` and the API kept answering.

## Resilience

```bash
./scripts/kind-resilience.sh
```

The script proves that deleting pods and a rolling restart during a workload lose no money (ADR 0011). It runs on a
cluster prepared with `kind-up` and `kind-deploy`, and it can run again on the same cluster with no reset in between.
It prints `RESILIENCE OK` and exits 0, or names the failed check and exits non-zero. Runs took 8.5 to 11.5
minutes on a developer machine, mostly the two `kind-deploy` calls (2 to 3 minutes each, they rebuild and load the
images) and the two k6 runs (about 1.5 minutes each).

Both runs use the unchanged `tests/k6/smoke.js` (5 VUs for 60 s, top-ups and transfers), started through
`scripts/k6.sh`:

1. **Pod deletion.** Payments runs with one replica (if an earlier run left two, the script first runs `kind-deploy`
   with the chart defaults). 15 s into the load phase the script runs
   `kubectl delete pod -l app.kubernetes.io/name=payments --grace-period=0 --force`, and at 30 s the same for
   `accounts`.
2. **Rolling restart.** The script runs `kind-deploy --set services.payments.replicas=2` (which also renders the
   Payments PodDisruptionBudget), starts the workload again, and runs `kubectl rollout restart deployment/payments`
   10 s into the load phase, followed by `kubectl rollout status`.

The load phase starts when k6's `setup()` ends: about 90 s after k6 starts on a new cluster (logins, 210 wallets and
their top-ups), under 20 s later on, when the wallets exist and hold enough money. The script waits for k6's first
`running (...)` progress line and times the kill and restart points from it, so they always fall inside the 60 s of
load. k6 2.4.0 prints only `setup()` progress lines while `setup()` runs; the first `running (...)` line follows its
end within a second (checked with a short scenario whose `setup()` sleeps).

After each run the script checks, through the gateway with an admin token:

- no payment is left `Processing` or `Compensating` (polled for up to 180 s), and none is `ManualReview`. It asks
  `GET /api/payments?accountId=...&status=...` for the wallets of the VU users. `smoke.js` VU n acts for
  `load-customer-00n`, so every payment of the load phase has one of those wallets as source or destination. The
  setup top-ups of the other wallets are awaited to `Completed` by k6's `setup()` itself;
- k6's `teardown()` logged that every customer wallet was drained (it asks the same endpoint for all 200 wallets);
- the trial balance (`GET /api/ledger/trial-balance`) is zero in every currency;
- at least one saga completed and every deleted pod existed, so an empty run cannot pass. In the pod deletion run, at
  least one saga must also have completed after the last deletion, so the deletions hit a running workload. The
  script takes the time of each deletion from the Docker engine clock and compares it with the CSV timestamps of
  k6's `saga_completed` counter.

The rolling restart adds a check of the failed requests. Every failed load-phase request to the gateway must be a `503` inside the restart
window: from `rollout restart` until 15 s after `rollout status` returned. The 15 s cover one interval of the
gateway's active health check on Payments (10 s, 3 s timeout). A `502`, a `504`, a connection error or a `503` outside
the window fails the run. The script reads each request's status from k6's CSV output (`--out csv`) and takes the
window from the Docker engine clock, the clock k6 also uses. A request counts as failed when k6 marked it unexpected
(`expected_response=false`) or its status is 0 or 400 and above. The check fails closed: a CSV without one of the
columns it reads, or a load-phase row whose `expected_response` is neither `true` nor `false`, fails the run. The
load-phase gateway requests must also include at least one before the restart and one during the rollout, so the
restart hit live traffic.

k6 itself exits with 99 when a threshold is crossed. The scenario's latency thresholds were missed on this host in
Milestone 3 for reasons outside Ledgerline (disk stalls), and pods that are deleted during a run make latency worse
anyway. The script therefore accepts 0 or 99 and judges the money with its own checks. Any other k6 exit code (an
error in setup or teardown, for example) fails the run.

How the workload reaches the cluster: k6 runs in Docker, so it joins the kind node's Docker network (`kind`) and uses
the node's NodePorts (`http://ledgerline-control-plane:30080` for the gateway, `:30081` for Keycloak). The script reads
the node name, its network and the NodePorts from `kind` and `kubectl`. Tokens requested at the node name still carry
the issuer `http://localhost:8081/realms/ledgerline`, because `KC_HOSTNAME` is fixed, so the services accept them.
This works the same on Linux, where `host.docker.internal` does not exist. The script's own checks use the host
mappings (`GATEWAY_URL` and `KEYCLOAK_URL`, by default `localhost:8080` and `localhost:8081`), as `smoke.sh` does.

Every k6 run gets a new `RUN_ID`, so every run creates new payments with new idempotency keys. The k6 summary stays
inside the container. The CSV goes to `docs/results/raw` under a `kind-resilience-*` name and is deleted when the script
exits, so a run leaves no files behind.

When the script ends, Payments still runs with two replicas. The next `kind-resilience` run, or a plain
`./scripts/kind-deploy.sh`, sets it back to one.

Limits: a zero trial balance shows that every posting balanced, but it would not reveal a transfer that was posted
twice (both legs twice). The checks above cover what the plan asks for: no payment stuck, no `ManualReview`, a zero
trial balance, and only `503` during the restart.
