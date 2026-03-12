param(
    [Parameter(Mandatory = $true)]
    [string]$LicenseKey,
    [string]$NodeAppName = "NavalArchiveWeb",
    [string]$JavaAppName = "NavalArchiveImagePopulator"
)

$ErrorActionPreference = "Continue"

$profilerGuid = "{36032161-FFC0-4B61-B559-F6C5D41BAE5A}"
$profilerPath = "C:\Program Files\New Relic\.NET Agent\NewRelic.Profiler.dll"
$nrJavaAgent = "C:\ProgramData\NewRelic\java\newrelic.jar"

function Set-ServiceEnvironment {
    param(
        [string]$ServiceName,
        [string[]]$EnvVars
    )
    $svcReg = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path $svcReg)) {
        Write-Host "  [SKIP] $ServiceName (service not found)" -ForegroundColor Yellow
        return
    }

    New-ItemProperty -Path $svcReg -Name "Environment" -PropertyType MultiString -Value $EnvVars -Force | Out-Null
    Write-Host "  [OK] $ServiceName env updated" -ForegroundColor Green
}

Write-Host "=== Manual New Relic instrumentation ===" -ForegroundColor Cyan
Write-Host "License key: $($LicenseKey.Substring(0, [Math]::Min(8, $LicenseKey.Length)))..." -ForegroundColor Gray

if (-not (Test-Path $profilerPath)) {
    Write-Host "WARNING: .NET profiler not found at $profilerPath" -ForegroundColor Yellow
    Write-Host "Install New Relic .NET Agent first, then re-run." -ForegroundColor Yellow
}

$dotnetVars = @(
    "NEW_RELIC_LICENSE_KEY=$LicenseKey",
    "NEW_RELIC_DISTRIBUTED_TRACING_ENABLED=true",
    "CORECLR_ENABLE_PROFILING=1",
    "CORECLR_PROFILER=$profilerGuid",
    "CORECLR_PROFILER_PATH_64=$profilerPath",
    "COR_ENABLE_PROFILING=1",
    "COR_PROFILER=$profilerGuid",
    "COR_PROFILER_PATH_64=$profilerPath"
)

$dotnetServices = @(
    "NavalArchivePayment",
    "NavalArchiveCard",
    "NavalArchiveCart",
    "NavalArchiveGateway",
    "NavalArchiveAuth",
    "NavalArchiveUser",
    "NavalArchiveCatalog",
    "NavalArchiveInventory",
    "NavalArchiveBasket",
    "NavalArchiveOrder",
    "NavalArchivePaymentChain",
    "NavalArchiveShipping",
    "NavalArchiveNotification"
)

Write-Host "`nConfiguring .NET services..." -ForegroundColor Cyan
foreach ($svc in $dotnetServices) {
    $envVars = @("NEW_RELIC_APP_NAME=$svc") + $dotnetVars
    Set-ServiceEnvironment -ServiceName $svc -EnvVars $envVars
}

Write-Host "`nConfiguring Node service..." -ForegroundColor Cyan
$nodeVars = @(
    "API_URL=http://localhost:5000",
    "GATEWAY_URL=http://localhost:5010",
    "PORT=3000",
    "NEW_RELIC_LICENSE_KEY=$LicenseKey",
    "NEW_RELIC_APP_NAME=$NodeAppName",
    "NEW_RELIC_NO_CONFIG_FILE=true",
    "NEW_RELIC_DISTRIBUTED_TRACING_ENABLED=true",
    "NODE_OPTIONS=-r C:\inetpub\navalarchive-web\node_modules\newrelic\index.js"
)
Set-ServiceEnvironment -ServiceName "NavalArchiveWeb" -EnvVars $nodeVars

Write-Host "`nConfiguring Java listener service (if present)..." -ForegroundColor Cyan
$javaVars = @(
    "NEW_RELIC_APP_NAME=$JavaAppName",
    "NEW_RELIC_LICENSE_KEY=$LicenseKey",
    "NEW_RELIC_DISTRIBUTED_TRACING_ENABLED=true"
)
Set-ServiceEnvironment -ServiceName "NavalArchiveImagePopulator" -EnvVars $javaVars

Write-Host "`nRestarting services..." -ForegroundColor Cyan
$allServices = @("NavalArchiveWeb", "NavalArchiveImagePopulator") + $dotnetServices
foreach ($svc in $allServices) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if (-not $s) { continue }
    try {
        Restart-Service -Name $svc -Force -ErrorAction Stop
        Write-Host "  [OK] restarted $svc" -ForegroundColor Green
    } catch {
        Write-Host "  [WARN] could not restart $svc: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host "`nDone. Check New Relic APM for new entities in 2-5 minutes." -ForegroundColor Cyan
