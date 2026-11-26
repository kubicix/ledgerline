# Builds the six application images, loads them into the kind cluster "ledgerline" and installs or upgrades the Helm
# release "ledgerline" (ADR 0011). Run scripts/kind-up.ps1 first. Extra arguments are passed to helm, for example
#   ./scripts/kind-deploy.ps1 --set services.payments.replicas=2
# Two passes on a first install (docs/kubernetes.md): the migration Jobs are pre-install/pre-upgrade hooks, so the
# infrastructure they wait for must exist before they run. When the release does not exist yet, the first pass installs
# the infrastructure only (infraOnly=true) and waits for it; the second, full pass is then an upgrade whose hooks find
# PostgreSQL running. When the release exists, only the full pass runs: an infrastructure-only pass on an existing
# release would delete the running services. The full pass uses --reset-values: on an upgrade without --set or -f, Helm
# reuses the last release's values, which after pass 1 would keep infraOnly=true. The release values are therefore always
# the chart defaults plus the arguments of this call.
$ErrorActionPreference = 'Stop'
$Cluster = 'ledgerline'
$Context = "kind-$Cluster"
$Release = 'ledgerline'
$Root = Split-Path -Parent $PSScriptRoot
$Chart = Join-Path $Root 'deploy/helm/ledgerline'
$Images = @('ledgerline-accounts:local', 'ledgerline-payments:local', 'ledgerline-risk:local',
    'ledgerline-notifications:local', 'ledgerline-banksim:local', 'ledgerline-gateway:local')
$HelmArgs = @($args)

foreach ($tool in 'docker', 'kind', 'helm', 'kubectl') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool not found on PATH, see docs/kubernetes.md"
    }
}

# Native tools write progress and "not found" messages to stderr, which Windows PowerShell 5.1 turns into errors
# under 'Stop'. Exit codes are checked instead.
$ErrorActionPreference = 'Continue'
$existing = @(& kind get clusters 2>$null)
if ($existing -notcontains $Cluster) { throw "kind cluster '$Cluster' does not exist, run scripts/kind-up.ps1 first" }

Push-Location $Root
try {
    Write-Host '== Building images'
    & docker compose --profile app build
    if ($LASTEXITCODE -ne 0) { throw "docker compose build failed ($LASTEXITCODE)" }

    Write-Host "== Loading images into kind cluster '$Cluster'"
    & kind load docker-image @Images --name $Cluster
    if ($LASTEXITCODE -ne 0) { throw "kind load docker-image failed ($LASTEXITCODE)" }

    # Decide on presence with helm list, not helm status: any error (an unreachable cluster, for example) must abort
    # instead of being read as "absent", which would run the infrastructure-only pass over a running release.
    $present = @(& helm list --kube-context $Context -q --filter "^$Release`$")
    if ($LASTEXITCODE -ne 0) { throw "helm list failed: cannot tell whether release '$Release' exists, nothing was installed" }
    if ($present -contains $Release) {
        Write-Host "== Release '$Release' exists: single upgrade"
    } else {
        Write-Host "== Release '$Release' does not exist: pass 1 of 2, infrastructure only"
        & helm upgrade --install $Release $Chart --kube-context $Context @HelmArgs --set infraOnly=true --wait --timeout 10m
        if ($LASTEXITCODE -ne 0) { throw "helm infrastructure-only pass failed ($LASTEXITCODE)" }
        Write-Host '== Pass 2 of 2, full release'
    }
    & helm upgrade --install $Release $Chart --kube-context $Context --reset-values @HelmArgs --wait --timeout 10m
    if ($LASTEXITCODE -ne 0) { throw "helm upgrade failed ($LASTEXITCODE)" }

    & kubectl --context $Context get pods
    if ($LASTEXITCODE -ne 0) { throw "kubectl get pods failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}
