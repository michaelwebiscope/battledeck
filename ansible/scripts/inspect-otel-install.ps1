$otelHome = 'C:\Program Files\OpenTelemetry .NET AutoInstrumentation'
Write-Output "=== OTEL home exists: $(Test-Path $otelHome) ==="
if (Test-Path $otelHome) {
    $verDll = Join-Path $otelHome 'net\OpenTelemetry.AutoInstrumentation.StartupHook.dll'
    if (Test-Path $verDll) {
        Write-Output "version=$([System.Diagnostics.FileVersionInfo]::GetVersionInfo($verDll).ProductVersion)"
    }
    Write-Output '=== store top-level ==='
    Get-ChildItem (Join-Path $otelHome 'store') -ErrorAction SilentlyContinue | ForEach-Object { Write-Output $_.Name }
    Write-Output '=== OTLP-related DLLs ==='
    Get-ChildItem $otelHome -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'OpenTelemetry\.Exporter|Otlp' } |
        ForEach-Object { Write-Output $_.FullName }
    Write-Output '=== net folder ==='
    Get-ChildItem (Join-Path $otelHome 'net') -ErrorAction SilentlyContinue | ForEach-Object { Write-Output $_.Name }
}
