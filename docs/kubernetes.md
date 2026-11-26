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
