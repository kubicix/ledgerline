param(
    [string] $AccountsUrl = 'http://localhost:5101',
    [string] $PaymentsUrl = 'http://localhost:5102'
)
$ErrorActionPreference = 'Stop'

function Get-Headers([string] $Owner, [string] $Key = '') {
    $headers = @{ 'X-Dev-User' = $Owner; 'X-Dev-Roles' = 'customer' }
    if ($Key) { $headers['Idempotency-Key'] = $Key }
    return $headers
}
function Post-Resource([string] $Url, [string] $Owner, [string] $Key, [hashtable] $Body) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ($true) {
        try { return Invoke-RestMethod -Uri $Url -Method Post -Headers (Get-Headers $Owner $Key) -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress) }
        catch {
            if ([int]$_.Exception.Response.StatusCode -ne 404 -or [DateTimeOffset]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}
function Wait-Completed([string] $Id, [string] $Owner) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ($true) {
        $payment = Invoke-RestMethod -Uri "$PaymentsUrl/payments/$Id" -Headers (Get-Headers $Owner)
        if ($payment.status -eq 'Completed') { return }
        if ($payment.status -ne 'Processing' -or [DateTimeOffset]::UtcNow -ge $deadline) { throw "Payment $Id ended or timed out: $($payment.status)" }
        Start-Sleep -Milliseconds 200
    }
}

$alice = [guid]::NewGuid().ToString()
$bob = [guid]::NewGuid().ToString()
$aliceAccount = Post-Resource "$AccountsUrl/accounts" $alice 'open' @{ type = 'CustomerWallet'; currency = 'TRY' }
$bobAccount = Post-Resource "$AccountsUrl/accounts" $bob 'open' @{ type = 'CustomerWallet'; currency = 'TRY' }
$topup = Post-Resource "$PaymentsUrl/topups" $alice 'topup' @{ accountId = $aliceAccount.id; amountMinor = 10000; currency = 'TRY'; cardToken = 'tok_ok' }
Wait-Completed $topup.id $alice
$transfer = Post-Resource "$PaymentsUrl/transfers" $alice 'transfer' @{ fromAccountId = $aliceAccount.id; toAccountId = $bobAccount.id; amountMinor = 2500; currency = 'TRY' }
Wait-Completed $transfer.id $alice
$aliceBalance = (Invoke-RestMethod -Uri "$AccountsUrl/accounts/$($aliceAccount.id)" -Headers (Get-Headers $alice)).balanceMinor
$bobBalance = (Invoke-RestMethod -Uri "$AccountsUrl/accounts/$($bobAccount.id)" -Headers (Get-Headers $bob)).balanceMinor
if ($aliceBalance -ne 7500 -or $bobBalance -ne 2500) { throw "Balance mismatch: alice=$aliceBalance bob=$bobBalance" }
Write-Output 'SMOKE OK: alice=7500 bob=2500'
