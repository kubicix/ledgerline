param(
    [string] $GatewayUrl = 'http://localhost:8080',
    [string] $KeycloakUrl = 'http://localhost:8081/realms/ledgerline',
    [string] $MailpitUrl = 'http://localhost:8025'
)
$ErrorActionPreference = 'Stop'
$AccountsUrl = "$GatewayUrl/api"
$PaymentsUrl = "$GatewayUrl/api"

function Get-Headers([string] $Owner, [string] $Key = '') {
    $headers = @{ Authorization = "Bearer $Owner" }
    if ($Key) { $headers['Idempotency-Key'] = $Key }
    return $headers
}
function Get-Token([string] $Username, [string] $Password) {
    return (Invoke-RestMethod -Uri "$KeycloakUrl/protocol/openid-connect/token" -Method Post -Body @{
        grant_type = 'password'; client_id = 'ledgerline-k6'; username = $Username; password = $Password
    }).access_token
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
function Wait-Payment([string] $Id, [string] $Owner, [string] $Expected) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ($true) {
        $payment = Invoke-RestMethod -Uri "$PaymentsUrl/payments/$Id" -Headers (Get-Headers $Owner)
        if ($payment.status -eq $Expected) { return }
        if ($Expected -eq 'Authorized' -and $payment.status -eq 'Captured') { return }
        if ($payment.status -in @('Failed', 'Rejected', 'ManualReview', 'Compensated') -or [DateTimeOffset]::UtcNow -ge $deadline) { throw "Payment $Id ended or timed out: $($payment.status)" }
        Start-Sleep -Milliseconds 200
    }
}

$alice = Get-Token 'alice' 'alice_dev'
$bob = Get-Token 'bob' 'bob_dev'
$shop = Get-Token 'shop' 'shop_dev'
$admin = Get-Token 'admin' 'admin_dev'
$aliceAccount = Post-Resource "$AccountsUrl/accounts" $alice 'smoke-alice-wallet-try-v1' @{ type = 'CustomerWallet'; currency = 'TRY' }
$bobAccount = Post-Resource "$AccountsUrl/accounts" $bob 'smoke-bob-wallet-try-v1' @{ type = 'CustomerWallet'; currency = 'TRY' }
$shopAccount = Post-Resource "$AccountsUrl/accounts" $shop 'smoke-shop-wallet-try-v1' @{ type = 'MerchantWallet'; currency = 'TRY' }
$topup = Post-Resource "$PaymentsUrl/topups" $alice 'smoke-topup-v1' @{ accountId = $aliceAccount.id; amountMinor = 10000; currency = 'TRY'; cardToken = 'tok_ok' }
Wait-Payment $topup.id $alice 'Completed'
$transfer = Post-Resource "$PaymentsUrl/transfers" $alice 'smoke-transfer-v1' @{ fromAccountId = $aliceAccount.id; toAccountId = $bobAccount.id; amountMinor = 2500; currency = 'TRY' }
Wait-Payment $transfer.id $alice 'Completed'
$merchant = Post-Resource "$PaymentsUrl/payments" $alice 'smoke-merchant-v2' @{ customerAccountId = $aliceAccount.id; merchantAccountId = $shopAccount.id; amountMinor = 3000; currency = 'TRY'; captureMode = 'Manual' }
Wait-Payment $merchant.id $alice 'Authorized'
$null = Post-Resource "$PaymentsUrl/payments/$($merchant.id)/capture" $shop 'smoke-capture-v2' @{ amountMinor = 2000 }
Wait-Payment $merchant.id $alice 'Captured'
$refund = Post-Resource "$PaymentsUrl/payments/$($merchant.id)/refunds" $shop 'smoke-refund-v2' @{ amountMinor = 500 }
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
do {
    $refunds = Invoke-RestMethod -Uri "$PaymentsUrl/payments/$($merchant.id)/refunds" -Headers (Get-Headers $shop)
    $state = ($refunds.items | Where-Object id -EQ $refund.id).status
    if ($state -eq 'Completed') { break }
    if ($state -eq 'Failed' -or [DateTimeOffset]::UtcNow -ge $deadline) { throw "Refund failed or timed out: $state" }
    Start-Sleep -Milliseconds 200
} while ($true)
$aliceBalance = (Invoke-RestMethod -Uri "$AccountsUrl/accounts/$($aliceAccount.id)" -Headers (Get-Headers $alice)).balanceMinor
$bobBalance = (Invoke-RestMethod -Uri "$AccountsUrl/accounts/$($bobAccount.id)" -Headers (Get-Headers $bob)).balanceMinor
$shopBalance = (Invoke-RestMethod -Uri "$AccountsUrl/accounts/$($shopAccount.id)" -Headers (Get-Headers $shop)).balanceMinor
if ($aliceBalance -ne 6000 -or $bobBalance -ne 2500 -or $shopBalance -ne 1500) { throw "Balance mismatch: alice=$aliceBalance bob=$bobBalance shop=$shopBalance" }
$trial = Invoke-RestMethod -Uri "$AccountsUrl/ledger/trial-balance" -Headers (Get-Headers $admin)
if (@($trial.currencies | Where-Object sumOfBalancesMinor -NE 0).Count -ne 0) { throw 'Trial balance is not zero' }
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
while ((Invoke-RestMethod -Uri "$MailpitUrl/api/v1/messages").total -lt 4) {
    if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Mailpit did not receive at least four emails' }
    Start-Sleep -Milliseconds 200
}
Write-Output 'SMOKE OK: alice=6000 bob=2500 shop=1500 trial=0 emails>=4'
