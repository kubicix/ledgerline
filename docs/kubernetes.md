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
`kind-ledgerline`. The node image is kind's default for v0.33.0 (`kindest/node:v1.37.0`); it is not pinned
separately, so upgrading kind upgrades Kubernetes.

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
`local.events` queue to `accounts.event.journal-entry-posted.v1`, the only queue bound to that event. Without it the
Accounts outbox relay cannot route the event, retries it and holds back every later Accounts message, so payment
sagas stop at the ledger step. The import is idempotent and runs again on every upgrade.

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
