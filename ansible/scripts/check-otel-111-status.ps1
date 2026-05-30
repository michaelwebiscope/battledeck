$ErrorActionPreference = 'Continue'
$otelHome = 'C:\Program Files\OpenTelemetry .NET AutoInstrumentation'
$hook = Join-Path $otelHome 'net\OpenTelemetry.AutoInstrumentation.StartupHook.dll'
Write-Output "otel_hook_exists=$(Test-Path $hook)"
if (Test-Path $hook) {
    Write-Output "otel_version=$([System.Diagnostics.FileVersionInfo]::GetVersionInfo($hook).ProductVersion)"
}

Write-Output '=== Order service ==='
sc.exe query NavalArchiveOrder
try {
    $r = Invoke-WebRequest 'http://localhost:5016/trace' -UseBasicParsing -TimeoutSec 20
    Write-Output "trace_status=$($r.StatusCode) body=$($r.Content)"
} catch { Write-Output "trace_error=$_" }

$orderPid = (Get-Process -Name 'NavalArchive.Order' -ErrorAction SilentlyContinue | Select-Object -First 1).Id
Write-Output "order_pid=$orderPid"
if ($orderPid) {
    Get-Process -Id $orderPid -Module -ErrorAction SilentlyContinue |
        Where-Object { $_.ModuleName -match 'OpenTelemetry' } |
        ForEach-Object { Write-Output "module=$($_.ModuleName)" }
}

Write-Output '=== env ==='
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveOrder' -Name Environment).Environment |
    Where-Object { $_ -match 'OTEL_TRACES|OTEL_EXPORTER_OTLP_TRACES|OTEL_DOTNET_AUTO_HOME|DOTNET_STARTUP' }

$logDir = 'C:\inetpub\navalarchive-order\otel-logs'
Write-Output "log_dir_exists=$(Test-Path $logDir)"
Get-ChildItem $logDir -ErrorAction SilentlyContinue | ForEach-Object { Write-Output "logfile=$($_.Name) size=$($_.Length)" }
