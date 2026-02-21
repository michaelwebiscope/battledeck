# Naval Archive - Endpoint Diagnostics
# Run on VM as Administrator to check API, Node, and chain services

$ErrorActionPreference = "Continue"
Write-Host "`n=== Naval Archive Endpoint Diagnostics ===" -ForegroundColor Cyan
Write-Host ""

# 1. Services
Write-Host "--- Services ---" -ForegroundColor Yellow
$svcs = @("NavalArchiveWeb", "NavalArchiveGateway", "NavalArchiveAuth", "NavalArchiveUser", "NavalArchiveCatalog", "NavalArchiveInventory", "NavalArchiveBasket", "NavalArchiveOrder", "NavalArchivePaymentChain", "NavalArchiveShipping", "NavalArchiveNotification")
foreach ($s in $svcs) {
    $state = (Get-Service -Name $s -ErrorAction SilentlyContinue).Status
    $color = if ($state -eq "Running") { "Green" } else { "Red" }
    Write-Host "  $s : $state" -ForegroundColor $color
}

# 2. IIS
Write-Host "`n--- IIS Sites ---" -ForegroundColor Yellow
try {
    Import-Module WebAdministration -ErrorAction SilentlyContinue
    Get-Website | Where-Object { $_.Name -like "*Naval*" } | ForEach-Object {
        Write-Host "  $($_.Name) : $($_.State) (bindings: $($_.Bindings.Collection.bindingInformation -join ', '))" -ForegroundColor $(if ($_.State -eq "Started") { "Green" } else { "Red" })
    }
} catch { Write-Host "  Could not get IIS sites" -ForegroundColor Red }

# 3. API (5000)
Write-Host "`n--- API (localhost:5000) ---" -ForegroundColor Yellow
$apiEndpoints = @("/trace", "/api/trace", "/api/ships", "/swagger/index.html", "/health")
foreach ($ep in $apiEndpoints) {
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:5000$ep" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        Write-Host "  GET $ep : $($r.StatusCode)" -ForegroundColor Green
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        Write-Host "  GET $ep : $code or failed - $($_.Exception.Message)" -ForegroundColor Red
    }
}

# 4. Node (3000)
Write-Host "`n--- Node Web (localhost:3000) ---" -ForegroundColor Yellow
$nodeEndpoints = @("/", "/trace", "/fleet")
foreach ($ep in $nodeEndpoints) {
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:3000$ep" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        Write-Host "  GET $ep : $($r.StatusCode)" -ForegroundColor Green
    } catch {
        $code = if ($_.Exception.Response) { $_.Exception.Response.StatusCode.value__ } else { "N/A" }
        Write-Host "  GET $ep : $code or failed - $($_.Exception.Message)" -ForegroundColor Red
    }
}

# 5. Chain (5010-5019)
Write-Host "`n--- Chain Services (5010-5019) ---" -ForegroundColor Yellow
$chainPorts = @(5010, 5011, 5012, 5013, 5014, 5015, 5016, 5017, 5018, 5019)
$chainNames = @("Gateway", "Auth", "User", "Catalog", "Inventory", "Basket", "Order", "Payment", "Shipping", "Notification")
for ($i = 0; $i -lt $chainPorts.Length; $i++) {
    $port = $chainPorts[$i]
    $name = $chainNames[$i]
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:${port}/health" -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        Write-Host "  $name (:$port) /health : $($r.StatusCode)" -ForegroundColor Green
    } catch {
        Write-Host "  $name (:$port) /health : failed" -ForegroundColor Red
    }
}

# 6. Trace chain
Write-Host "`n--- Trace Chain (5010/trace) ---" -ForegroundColor Yellow
try {
    $r = Invoke-WebRequest -Uri "http://localhost:5010/trace" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    Write-Host "  Gateway /trace : $($r.StatusCode)" -ForegroundColor Green
    $json = $r.Content | ConvertFrom-Json -ErrorAction SilentlyContinue
    if ($json) { Write-Host "  Response: service=$($json.service)" -ForegroundColor Gray }
} catch {
    Write-Host "  Gateway /trace : failed - $($_.Exception.Message)" -ForegroundColor Red
}

# 7. Listening ports
Write-Host "`n--- Listening Ports ---" -ForegroundColor Yellow
$ports = @(80, 443, 3000, 5000, 5010, 5011, 5012, 5013, 5014, 5015, 5016, 5017, 5018, 5019)
foreach ($p in $ports) {
    $conn = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) { Write-Host "  Port $p : listening" -ForegroundColor Green }
    else { Write-Host "  Port $p : not listening" -ForegroundColor Red }
}

# 8. API logs (last errors)
Write-Host "`n--- API Logs (last 10 lines) ---" -ForegroundColor Yellow
$logDir = "C:\inetpub\navalarchive-api\logs"
if (Test-Path $logDir) {
    $logFile = Get-ChildItem $logDir -Filter "stdout*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($logFile) {
        Get-Content $logFile.FullName -Tail 10 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    } else { Write-Host "  No stdout logs found" }
} else { Write-Host "  Log dir not found" }

Write-Host "`n=== Done ===" -ForegroundColor Cyan
