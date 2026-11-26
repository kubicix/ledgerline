# Deletes only the kind cluster "ledgerline". Other kind clusters, Docker networks and volumes are never touched.
$ErrorActionPreference = 'Stop'
$Cluster = 'ledgerline'

if (-not (Get-Command kind -ErrorAction SilentlyContinue)) {
    throw 'kind not found on PATH, see docs/kubernetes.md'
}

$ErrorActionPreference = 'Continue'  # kind prints "No kind clusters found." on stderr
$existing = @(& kind get clusters 2>$null)
$ErrorActionPreference = 'Stop'
if ($existing -contains $Cluster) {
    & kind delete cluster --name $Cluster
    if ($LASTEXITCODE -ne 0) { throw "kind delete cluster failed ($LASTEXITCODE)" }
} else {
    Write-Host "kind cluster '$Cluster' does not exist"
}
