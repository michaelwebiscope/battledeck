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
sc.exe stop NavalArchiveWeb 2>$null
Start-Sleep -Seconds 3

Write-Host "Copying files..." -ForegroundColor Yellow
Get-ChildItem $webPath -Exclude node_modules -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $webSrcDir -Exclude node_modules | Copy-Item -Destination $webPath -Recurse -Force

Write-Host "Running npm install..." -ForegroundColor Yellow
Push-Location $webPath
cmd /c "`"$nodeDir\npm.cmd`" install --omit=dev --no-audit --no-fund"
Pop-Location

Write-Host "Starting NavalArchiveWeb..." -ForegroundColor Yellow
sc.exe start NavalArchiveWeb

Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $extractPath -ErrorAction SilentlyContinue

Write-Host "Web refreshed and restarted." -ForegroundColor Green
