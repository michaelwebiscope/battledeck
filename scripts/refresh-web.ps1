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
$env:Path = "$nodeDir;$env:Path"
Push-Location $webPath
& "$nodeDir\npm.cmd" install --omit=dev --no-audit --no-fund 2>&1
Pop-Location

# Ensure NavalArchiveWeb runs node.exe directly (host native, no cmd/batch wrapper)
$webSvc = Get-Service -Name "NavalArchiveWeb" -ErrorAction SilentlyContinue
if ($webSvc -and (Test-Path "$nodeDir\node.exe") -and (Test-Path "$webPath\server.js")) {
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\NavalArchiveWeb"
    $imagePath = "`"$nodeDir\node.exe`" `"$webPath\server.js`""
    Set-ItemProperty -Path $regPath -Name "ImagePath" -Value $imagePath -Force -ErrorAction SilentlyContinue
    $envVars = @("API_URL=http://localhost:5000", "GATEWAY_URL=http://localhost:5010", "PORT=3000")
    Set-ItemProperty -Path $regPath -Name "Environment" -Value $envVars -Type MultiString -Force -ErrorAction SilentlyContinue
}

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
    $env:API_AS_GATEWAY = "true"
    if (Test-Path "$nodeDir\node.exe") {
        Start-Process -FilePath "$nodeDir\node.exe" -ArgumentList "server.js" -WorkingDirectory $webPath -WindowStyle Hidden
        Start-Sleep -Seconds 10
    } else {
        Write-Host "Node.exe not found at $nodeDir - Web may not be running" -ForegroundColor Red
    }
}

# Refresh API first (ImagePopulator needs it)
$apiCsproj = Get-ChildItem -Path $srcDir -Filter "NavalArchive.Api.csproj" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($apiCsproj -and (Test-Path "$dotnetDir\dotnet.exe")) {
    Write-Host "Publishing NavalArchive.Api..." -ForegroundColor Yellow
    $appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
    if (Test-Path $appcmd) { & $appcmd stop apppool /apppool.name:NavalArchive-API 2>$null }
    Push-Location $apiCsproj.DirectoryName
    & "$dotnetDir\dotnet.exe" publish -c Release -o $apiPath 2>&1
    Pop-Location
    if (Test-Path $appcmd) { & $appcmd start apppool /apppool.name:NavalArchive-API 2>$null }
    Write-Host "Waiting for API to be ready..." -ForegroundColor Gray
    for ($i = 1; $i -le 12; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "http://localhost:5000/health" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
            if ($r.StatusCode -eq 200) { Write-Host "API ready." -ForegroundColor Green; break }
        } catch { }
        if ($i -lt 12) { Start-Sleep -Seconds 5 }
    }
}

# Copy populate assets while extract is still valid (before cleanup)
$populateDir = "C:\Windows\Temp\navalarchive-populate"
if (-not (Test-Path $populateDir)) { New-Item -ItemType Directory -Path $populateDir -Force | Out-Null }
$populateJar = Get-ChildItem -Path $extractPath -Filter "image-populator-*.jar" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($populateJar) { Copy-Item $populateJar.FullName -Destination $populateDir -Force -ErrorAction SilentlyContinue }
$populateScriptSrc = Join-Path $srcDir "scripts\populate-images.js"
if (Test-Path $populateScriptSrc -ErrorAction SilentlyContinue) { Copy-Item $populateScriptSrc -Destination $populateDir -Force -ErrorAction SilentlyContinue }

# Ensure API is entry point on 443 (session gate, all traffic through API)
$appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
if (Test-Path $appcmd) {
    & $appcmd stop site "Default Web Site" 2>$null
    $cert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq "NavalArchive HTTPS" } | Select-Object -First 1
    if ($cert) {
        $apiHas443 = Get-WebBinding -Name "NavalArchive-API" -Protocol "https" -ErrorAction SilentlyContinue
        if (-not $apiHas443) {
            try {
                Import-Module IISAdministration -ErrorAction SilentlyContinue
                New-IISSiteBinding -Name "NavalArchive-API" -BindingInformation "*:443:" -CertificateThumbPrint $cert.Thumbprint -CertStoreLocation "Cert:\LocalMachine\My" -Protocol https -ErrorAction Stop
                Write-Host "Added HTTPS (443) binding to NavalArchive-API" -ForegroundColor Green
            } catch {
                New-WebBinding -Name "NavalArchive-API" -Protocol https -Port 443 -ErrorAction SilentlyContinue
                $hb = Get-WebBinding -Name "NavalArchive-API" -Protocol "https" -ErrorAction SilentlyContinue
                if ($hb) { $hb.AddSslCertificate($cert.Thumbprint, "My") }
            }
        }
        $web443 = Get-WebBinding -Name "NavalArchive-Web" -Protocol "https" -ErrorAction SilentlyContinue
        if ($web443) {
            Remove-WebBinding -Name "NavalArchive-Web" -Protocol "https" -BindingInformation "*:443:"
            Write-Host "Removed HTTPS from NavalArchive-Web (API is now entry point)" -ForegroundColor Green
        }
    }
    & $appcmd start site "NavalArchive-API" 2>$null
    & $appcmd start site "NavalArchive-Web" 2>$null
}

# Ensure Java is installed (for ImagePopulator)
# Check: Eclipse Adoptium (setup-vm), Amazon Corretto (ansible/chocolatey), then PATH
$javaDir = $null
foreach ($base in @("C:\Program Files\Eclipse Adoptium", "C:\Program Files\Amazon Corretto")) {
    if (Test-Path $base) {
        $jdkFolder = Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "jdk*" } | Select-Object -First 1
        if ($jdkFolder -and (Test-Path "$($jdkFolder.FullName)\bin\java.exe")) {
            $javaDir = $jdkFolder.FullName
            break
        }
    }
}
if (-not $javaDir) {
    Write-Host "Installing Java 17..." -ForegroundColor Yellow
    $jdkZip = "$env:TEMP\openjdk17-$([Guid]::NewGuid().ToString('N').Substring(0,8)).zip"
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
$apiUrl = "http://localhost:5000"
$jarInPopulate = Get-ChildItem -Path $populateDir -Filter "image-populator-*.jar" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($jarInPopulate -and (Test-Path $javaExe)) {
    $jarName = $jarInPopulate.Name
    Write-Host ""
    Write-Host "=== Image Population (Java ImagePopulator) ===" -ForegroundColor Cyan
    Write-Host "Connecting to Java app. API: $apiUrl" -ForegroundColor Gray
    Push-Location $populateDir
    & $javaExe -jar $jarName $apiUrl
    $popExit = $LASTEXITCODE
    Pop-Location
    Write-Host "ImagePopulator exit code: $popExit" -ForegroundColor $(if ($popExit -eq 0) { "Green" } else { "Yellow" })
} else {
    $populateScript = Join-Path $populateDir "populate-images.js"
    if ((Test-Path $populateScript) -and (Test-Path "$nodeDir\node.exe")) {
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
