# Refresh NavalArchive.Web from GitHub
# Usage: powershell -ExecutionPolicy Bypass -File refresh-web.ps1
# Or with custom repo: .\refresh-web.ps1 -RepoUrl "https://github.com/user/repo" -RepoBranch "main"

param(
    [string]$RepoUrl = "https://github.com/michaelwebiscope/battledeck",
    [string]$RepoBranch = "main"
)

$webPath = "C:\inetpub\navalarchive-web"
$apiPath = "C:\inetpub\navalarchive-api"
$nodeDir = "C:\Program Files\nodejs"
$dotnetDir = "C:\Program Files\dotnet"

$repoUrlClean = $RepoUrl -replace '\.git$', '' -replace '/$', ''
$parts = $repoUrlClean -split '/'
$owner = $parts[-2]
$repo = $parts[-1]
# Cache-bust so we always get the latest (GitHub ignores query params)
$zipUrl = "https://github.com/$owner/$repo/archive/refs/heads/$RepoBranch.zip?t=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
$zipPath = "$env:TEMP\navalarchive-refresh.zip"

Write-Host "Downloading from GitHub ($RepoBranch)..." -ForegroundColor Cyan
try {
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing -Headers @{
        "User-Agent" = "Azure-Refresh/1.0"
        "Cache-Control" = "no-cache, no-store"
        "Pragma" = "no-cache"
    } -TimeoutSec 120
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

# Restore web.config (IIS rewrite to localhost:3000) - refresh deletes it since repo has none
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="Redirect to HTTPS" stopProcessing="true">
          <match url="(.*)" />
          <conditions>
            <add input="{HTTPS}" pattern="off" ignoreCase="true" />
          </conditions>
          <action type="Redirect" url="https://{HTTP_HOST}/{R:1}" redirectType="Permanent" />
        </rule>
        <rule name="Node" stopProcessing="true">
          <match url="(.*)" />
          <action type="Rewrite" url="http://localhost:3000/{R:1}" appendQueryString="true" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
"@ | Set-Content -Path "$webPath\web.config" -Encoding UTF8

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

# Refresh API (serves images from DB)
$apiCsproj = Get-ChildItem -Path $srcDir -Filter "NavalArchive.Api.csproj" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($apiCsproj -and (Test-Path "$dotnetDir\dotnet.exe")) {
    Write-Host "Publishing NavalArchive.Api..." -ForegroundColor Yellow
    $appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
    if (Test-Path $appcmd) { & $appcmd stop apppool /apppool.name:NavalArchive-API 2>$null }
    Push-Location $apiCsproj.DirectoryName
    & "$dotnetDir\dotnet.exe" publish -c Release -o $apiPath 2>&1
    Pop-Location
    if (Test-Path $appcmd) { & $appcmd start apppool /apppool.name:NavalArchive-API 2>$null }
}

# Ensure IIS serves NavalArchive-Web on 80/443 (not Default Web Site)
$appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
if (Test-Path $appcmd) {
    & $appcmd stop site "Default Web Site" 2>$null
    & $appcmd start site "NavalArchive-Web" 2>$null
}

# Ensure Java is installed (for ImagePopulator)
$javaDir = $null
$adoptiumBase = "C:\Program Files\Eclipse Adoptium"
if (Test-Path $adoptiumBase) {
    $jdkFolder = Get-ChildItem $adoptiumBase -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "jdk*" } | Select-Object -First 1
    if ($jdkFolder -and (Test-Path "$($jdkFolder.FullName)\bin\java.exe")) { $javaDir = $jdkFolder.FullName }
}
if (-not $javaDir) {
    Write-Host "Installing Java 17..." -ForegroundColor Yellow
    $jdkZip = "$env:TEMP\openjdk17.zip"
    $jdkExtract = "C:\Program Files\Eclipse Adoptium"
    try {
        Invoke-WebRequest -Uri "https://github.com/adoptium/temurin17-binaries/releases/download/jdk-17.0.13%2B11/OpenJDK17U-jdk_x64_windows_hotspot_17.0.13_11.zip" -OutFile $jdkZip -UseBasicParsing -TimeoutSec 120
        if (-not (Test-Path $jdkExtract)) { New-Item -ItemType Directory -Path $jdkExtract -Force | Out-Null }
        Expand-Archive -Path $jdkZip -DestinationPath $jdkExtract -Force
        $extracted = Get-ChildItem $jdkExtract -Directory | Where-Object { $_.Name -like "jdk*" } | Select-Object -First 1
        if ($extracted -and (Test-Path "$($extracted.FullName)\bin\java.exe")) {
            $javaDir = $extracted.FullName
            [System.Environment]::SetEnvironmentVariable("Path", "$javaDir\bin;" + [System.Environment]::GetEnvironmentVariable("Path","Machine"), "Machine")
        }
        Remove-Item $jdkZip -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "Java install failed: $_" -ForegroundColor Yellow
    }
}
$javaExe = if ($javaDir) { "$javaDir\bin\java.exe" } else { "java" }

# Populate ship images from Wikipedia -> API (foreground, visible progress and exit code)
$populateDir = "C:\Windows\Temp\navalarchive-populate"
if (-not (Test-Path $populateDir)) { New-Item -ItemType Directory -Path $populateDir -Force | Out-Null }
$apiUrl = "http://localhost:5000"
$populateJar = Get-ChildItem -Path $srcDir -Filter "image-populator-*.jar" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($populateJar -and (Test-Path $javaExe)) {
    Copy-Item $populateJar.FullName -Destination $populateDir -Force
    $jarName = Split-Path $populateJar.FullName -Leaf
    Write-Host ""
    Write-Host "=== Image Population (Java ImagePopulator) ===" -ForegroundColor Cyan
    Write-Host "Connecting to Java app. API: $apiUrl" -ForegroundColor Gray
    Push-Location $populateDir
    & $javaExe -jar $jarName $apiUrl
    $popExit = $LASTEXITCODE
    Pop-Location
    Write-Host "ImagePopulator exit code: $popExit" -ForegroundColor $(if ($popExit -eq 0) { "Green" } else { "Yellow" })
} else {
    $populateScript = Join-Path $srcDir "scripts\populate-images.js"
    if ((Test-Path $populateScript) -and (Test-Path "$nodeDir\node.exe")) {
        Copy-Item $populateScript -Destination $populateDir -Force
        Write-Host ""
        Write-Host "=== Image Population (Node fallback) ===" -ForegroundColor Cyan
        Write-Host "Connecting to Node script. API: $apiUrl" -ForegroundColor Gray
        Push-Location $populateDir
        & "$nodeDir\node.exe" populate-images.js $apiUrl
        $popExit = $LASTEXITCODE
        Pop-Location
        Write-Host "populate-images.js exit code: $popExit" -ForegroundColor $(if ($popExit -eq 0) { "Green" } else { "Yellow" })
    }
}

# Cleanup (after populate - it needs $srcDir)
Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $extractPath -ErrorAction SilentlyContinue

Write-Host "Web refreshed and restarted." -ForegroundColor Green
Write-Host "Tip: Push local changes to GitHub before refresh - the script pulls from the repo." -ForegroundColor DarkGray
