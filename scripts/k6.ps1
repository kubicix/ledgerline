# Runs a k6 scenario from tests/k6 in grafana/k6:2.4.0 on the Compose network of the stack under test and writes the
# end-of-test summary to docs/results/raw/<scenario>-<yyyy-mm-dd>.json. PowerShell twin of scripts/k6.sh.
# Usage: scripts/k6.ps1 <scenario> [-Project <compose project>] [-Network <docker network>] [extra k6 run arguments]
# Extra k6 arguments must use the long flag names (for example --env NAME=value, --vus 10, --quiet): PowerShell binds
# single-dash short flags to this script's own parameters (-P becomes -Project, -e is ambiguous).
# Passed through when set: GATEWAY_URL, KEYCLOAK_URL, DURATION, RUN_ID, K6_CUSTOMERS, K6_MERCHANTS, K6_ADMIN,
# SAGA_TIMEOUT_MS (can only raise the saga poll timeout, see tests/k6/lib/saga.js), and ALICE_PASSWORD, BOB_PASSWORD,
# SHOP_PASSWORD and ADMIN_PASSWORD. The load-test users always use their <username>_dev default.
param(
    [Parameter(Mandatory = $true, Position = 0)] [string] $Scenario,
    [string] $Project = $(if ($env:LEDGERLINE_PROJECT) { $env:LEDGERLINE_PROJECT } else { 'ledgerline' }),
    [string] $Network = $env:K6_NETWORK,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $K6Args
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not (Test-Path (Join-Path $root "tests/k6/$Scenario.js"))) { Write-Error "Unknown scenario: tests/k6/$Scenario.js" }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine('docker was not found on PATH.')
    exit 1
}
if (-not $Network) { $Network = "${Project}_default" }
$summary = "$Scenario-$([DateTime]::UtcNow.ToString('yyyy-MM-dd')).json"
New-Item -ItemType Directory -Force (Join-Path $root 'docs/results/raw') | Out-Null

$envArgs = @()
foreach ($name in 'GATEWAY_URL', 'KEYCLOAK_URL', 'DURATION', 'RUN_ID', 'K6_CUSTOMERS', 'K6_MERCHANTS', 'K6_ADMIN',
    'SAGA_TIMEOUT_MS', 'ALICE_PASSWORD', 'BOB_PASSWORD', 'SHOP_PASSWORD', 'ADMIN_PASSWORD') {
    if ([Environment]::GetEnvironmentVariable($name)) { $envArgs += @('-e', $name) }
}

# k6 logs to stderr. Windows PowerShell 5.1 turns redirected native stderr into errors, which 'Stop' would make fatal
# while the container keeps running, so the exit code alone decides the result.
$ErrorActionPreference = 'Continue'
& docker run --rm --network $Network @envArgs `
    -v "${root}/tests/k6:/scripts:ro" -v "${root}/docs/results/raw:/results" `
    grafana/k6:2.4.0 run --summary-export "/results/$summary" @K6Args "/scripts/$Scenario.js"
if ($null -eq $LASTEXITCODE) { exit 1 }
exit $LASTEXITCODE
