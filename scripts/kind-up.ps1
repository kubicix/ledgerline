# Creates the kind cluster "ledgerline" from deploy/kind/cluster.yaml if it does not exist yet (idempotent).
# The cluster binds host ports 8080, 8081, 3000 and 8025, the same as the Compose stack: stop Compose first.
$ErrorActionPreference = 'Stop'
$Cluster = 'ledgerline'
$Root = Split-Path -Parent $PSScriptRoot

foreach ($tool in 'kind', 'kubectl', 'docker') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool not found on PATH, see docs/kubernetes.md"
    }
}

$ErrorActionPreference = 'Continue'  # kind prints "No kind clusters found." on stderr
$existing = @(& kind get clusters 2>$null)
$ErrorActionPreference = 'Stop'
if ($existing -contains $Cluster) {
    Write-Host "kind cluster '$Cluster' already exists"
} else {
    & kind create cluster --config (Join-Path $Root 'deploy/kind/cluster.yaml') --wait 120s
    if ($LASTEXITCODE -ne 0) { throw "kind create cluster failed ($LASTEXITCODE)" }
}
& kubectl config use-context "kind-$Cluster" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "kubectl config use-context failed ($LASTEXITCODE)" }
& kubectl --context "kind-$Cluster" get nodes
if ($LASTEXITCODE -ne 0) { throw "kubectl get nodes failed ($LASTEXITCODE)" }
