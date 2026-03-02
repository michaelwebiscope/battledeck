# Refresh NavalArchive.Web from GitHub
# Usage: powershell -ExecutionPolicy Bypass -File refresh-web.ps1
# Or with custom repo: .\refresh-web.ps1 -RepoUrl "https://github.com/user/repo" -RepoBranch "main"

param(
    [string]$RepoUrl = "https://github.com/michaelwebiscope/battledeck",
    [string]$RepoBranch = "main"
)

$webPath = "C:\inetpub\navalarchive-web"
$nodeDir = "C:\Program Files\nodejs"

$repoUrlClean = $RepoUrl -replace '\.git$', '' -replace '/$', ''
$parts = $repoUrlClean -split '/'
$owner = $parts[-2]
$repo = $parts[-1]
$zipUrl = "https://github.com/$owner/$repo/archive/refs/heads/$RepoBranch.zip"
$zipPath = "$env:TEMP\navalarchive-refresh.zip"

Write-Host "Downloading from GitHub ($RepoBranch)..." -ForegroundColor Cyan
try {
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing -Headers @{ "User-Agent" = "Azure-Refresh/1.0" } -TimeoutSec 120
} catch {
    Write-Host "Download failed: $_" -ForegroundColor Red
    exit 1
}

$extractPath = "$env:TEMP\navalarchive-refresh"
if (Test-Path $extractPath) { Remove-Item -Recurse -Force $extractPath }
Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

$extractedDir = Get-ChildItem -Path $extractPath -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
$srcDir = if ($extractedDir) { $extractedDir.FullName } else { $extractPath }

$webSrcDir = Get-ChildItem -Path $srcDir -Filter "server.js" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -like "*NavalArchive.Web*" } |
    Select-Object -First 1 -ExpandProperty DirectoryName

if (-not $webSrcDir) {
    Write-Host "NavalArchive.Web not found in archive" -ForegroundColor Red
    Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $extractPath -ErrorAction SilentlyContinue
    exit 1
}

Write-Host "Stopping NavalArchiveWeb..." -ForegroundColor Yellow
$null = sc.exe stop NavalArchiveWeb 2>&1
Start-Sleep -Seconds 5
# Ensure no stale Node process holds port 3000
Get-Process -Name node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "Copying files..." -ForegroundColor Yellow
Get-ChildItem $webPath -Exclude node_modules -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $webSrcDir -Exclude node_modules | Copy-Item -Destination $webPath -Recurse -Force

Write-Host "Running npm install..." -ForegroundColor Yellow
Push-Location $webPath
cmd /c "`"$nodeDir\npm.cmd`" install --omit=dev --no-audit --no-fund"
Pop-Location

Write-Host "Starting NavalArchiveWeb..." -ForegroundColor Yellow
$svcStarted = $false
for ($i = 1; $i -le 3; $i++) {
    $null = sc.exe start NavalArchiveWeb 2>&1
    Start-Sleep -Seconds 15
    $svc = Get-Service -Name NavalArchiveWeb -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        $svcStarted = $true
        Write-Host "Service started." -ForegroundColor Green
        break
    }
    if ($i -lt 3) { Write-Host "Retry $i/3..." -ForegroundColor Yellow }
}

if (-not $svcStarted) {
    Write-Host "Service start timed out. Starting Node directly..." -ForegroundColor Yellow
    $env:API_URL = "http://localhost:5000"
    $env:GATEWAY_URL = "http://localhost:5010"
    $env:PORT = "3000"
    Start-Process -FilePath "$nodeDir\node.exe" -ArgumentList "server.js" -WorkingDirectory $webPath -WindowStyle Hidden
    Start-Sleep -Seconds 10
}

Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $extractPath -ErrorAction SilentlyContinue

Write-Host "Web refreshed and restarted." -ForegroundColor Green
