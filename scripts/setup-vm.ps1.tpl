# Naval Archive - Bootstrap: 1) Download GitHub 2) Setup IIS 3) Port 80
# Template vars: repo_url, repo_branch, repo_token, newrelic_license_key

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$apiPath = "C:\inetpub\navalarchive-api"
$webPath = "C:\inetpub\navalarchive-web"
$paymentPath = "C:\inetpub\navalarchive-payment"
$cardPath = "C:\inetpub\navalarchive-card"
$cartPath = "C:\inetpub\navalarchive-cart"
$dotnetDir = "C:\Program Files\dotnet"
$nodeDir = "C:\Program Files\nodejs"
$nssmDir = "C:\Program Files\nssm"

$RepoUrl = "${repo_url}"
$RepoBranch = "${repo_branch}"
$RepoToken = "${repo_token}"
$NewRelicLicenseKey = "${newrelic_license_key}"

# ========== 1. DOWNLOAD GITHUB ==========
Write-Host "=== 1. Downloading from GitHub ===" -ForegroundColor Cyan
$repoUrlClean = $RepoUrl -replace '\.git$', '' -replace '/$', ''
$parts = $repoUrlClean -split '/'
$owner = $parts[-2]
$repo = $parts[-1]
$zipUrl = "https://github.com/$owner/$repo/archive/refs/heads/$RepoBranch.zip"
$zipPath = "$env:TEMP\navalarchive.zip"
$headers = @{ "User-Agent" = "Azure-Bootstrap/1.0" }
if ($RepoToken) { $headers["Authorization"] = "token $RepoToken" }

for ($i = 1; $i -le 5; $i++) {
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing -Headers $headers -TimeoutSec 120
        break
    } catch {
        if ($i -eq 5) { throw }
        Write-Host "Retry $i/5 in 20s..." -ForegroundColor Yellow
        Start-Sleep -Seconds 20
    }
}

$clonePath = "C:\navalarchive-src"
if (Test-Path $clonePath) { Remove-Item -Recurse -Force $clonePath }
New-Item -ItemType Directory -Force -Path $clonePath | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $clonePath -Force
$extractedDir = Get-ChildItem -Path $clonePath -Directory | Select-Object -First 1
$clonePath = if ($extractedDir) { $extractedDir.FullName } else { $clonePath }

# ========== 2. SETUP IIS ==========
Write-Host "=== 2. Setting up IIS ===" -ForegroundColor Cyan

if (!(Test-Path "$dotnetDir\dotnet.exe")) {
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile "$env:TEMP\dotnet-install.ps1" -UseBasicParsing
    & "$env:TEMP\dotnet-install.ps1" -Channel 8.0 -InstallDir $dotnetDir
    [System.Environment]::SetEnvironmentVariable("Path", "$dotnetDir;" + [System.Environment]::GetEnvironmentVariable("Path","Machine"), "Machine")
}
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
if (Test-Path "$dotnetDir\dotnet.exe") { $env:Path = "$dotnetDir;$env:Path" }

if (!(Test-Path "$nodeDir\node.exe")) {
    $nodeVer = "20.18.1"
    Invoke-WebRequest -Uri "https://nodejs.org/dist/v$nodeVer/node-v$nodeVer-win-x64.zip" -OutFile "$env:TEMP\node.zip" -UseBasicParsing
    Expand-Archive -Path "$env:TEMP\node.zip" -DestinationPath "C:\Program Files" -Force
    $ext = "C:\Program Files\node-v$nodeVer-win-x64"
    if (Test-Path $nodeDir) { Remove-Item -Recurse -Force $nodeDir }
    Rename-Item $ext $nodeDir
    [System.Environment]::SetEnvironmentVariable("Path", "$nodeDir;" + [System.Environment]::GetEnvironmentVariable("Path","Machine"), "Machine")
}
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
if (Test-Path "$nodeDir\node.exe") { $env:Path = "$nodeDir;$env:Path" }

$ps64 = "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
Start-Process -FilePath $ps64 -ArgumentList "-NoProfile -Command", "Install-WindowsFeature -Name Web-Server, Web-Asp-Net45, Web-WebSockets, Web-Scripting-Tools -IncludeManagementTools" -Wait

$appcmd = $null
foreach ($p in @("$env:windir\SysNative\inetsrv\appcmd.exe", "$env:windir\System32\inetsrv\appcmd.exe")) {
    if (Test-Path $p) { $appcmd = $p; break }
}

Invoke-WebRequest -Uri "https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi" -OutFile "$env:TEMP\urlrewrite.msi" -UseBasicParsing
Start-Process msiexec -ArgumentList "/i", "$env:TEMP\urlrewrite.msi", "/quiet" -Wait

$hostingPaths = @("C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll", "$env:windir\System32\inetsrv\aspnetcorev2.dll")
$hostingInstalled = $hostingPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$hostingInstalled) {
    Invoke-WebRequest -Uri "https://download.visualstudio.microsoft.com/download/pr/4956ec5e-8502-4454-8f28-40239428820f/e7181890eed8dfa11cefbf817c4e86b0/dotnet-hosting-8.0.11-win.exe" -OutFile "$env:TEMP\hosting.exe" -UseBasicParsing
    Start-Process -FilePath "$env:TEMP\hosting.exe" -ArgumentList "/install", "/quiet", "/norestart" -Wait
}

Invoke-WebRequest -Uri "https://download.microsoft.com/download/e/9/8/e9849d6a-020e-47e4-9fd0-a023e99b54eb/requestRouter_amd64.msi" -OutFile "$env:TEMP\arr.msi" -UseBasicParsing
Start-Process msiexec -ArgumentList "/i", "$env:TEMP\arr.msi", "/quiet" -Wait

if (!(Test-Path "$nssmDir\nssm.exe")) {
    Invoke-WebRequest -Uri "https://nssm.cc/release/nssm-2.24.zip" -OutFile "$env:TEMP\nssm.zip" -UseBasicParsing
    Expand-Archive -Path "$env:TEMP\nssm.zip" -DestinationPath $env:TEMP -Force
    New-Item -ItemType Directory -Force -Path $nssmDir | Out-Null
    Copy-Item "$env:TEMP\nssm-2.24\win64\nssm.exe" $nssmDir -Force
}
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
if (Test-Path "$nssmDir\nssm.exe") { $env:Path = "$nssmDir;$env:Path" }

New-Item -ItemType Directory -Force -Path $apiPath | Out-Null
New-Item -ItemType Directory -Force -Path $webPath | Out-Null
New-Item -ItemType Directory -Force -Path $paymentPath | Out-Null
New-Item -ItemType Directory -Force -Path $cardPath | Out-Null
New-Item -ItemType Directory -Force -Path $cartPath | Out-Null
New-Item -ItemType Directory -Force -Path "$apiPath\logs" | Out-Null

# Stop API site/app pool before publishing so DLLs are not locked
if ($appcmd) {
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    & $appcmd stop site NavalArchive-API 2>$null
    & $appcmd stop apppool NavalArchive-API 2>$null
    $ErrorActionPreference = $prevErr
    Start-Sleep -Seconds 3
}

$apiCsproj = Get-ChildItem -Path $clonePath -Filter "NavalArchive.Api.csproj" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($apiCsproj) {
    Push-Location $apiCsproj.DirectoryName
    & "$dotnetDir\dotnet.exe" publish -c Release -o $apiPath
    if ($LASTEXITCODE -ne 0) { throw "API publish failed" }
    Pop-Location
}

# Stop NSSM services before publishing so DLLs are not locked
$prevErr = $ErrorActionPreference
$ErrorActionPreference = "SilentlyContinue"
foreach ($svc in @("NavalArchivePayment", "NavalArchiveCard", "NavalArchiveCart", "NavalArchiveWeb")) {
    & "$nssmDir\nssm.exe" stop $svc 2>$null
}
$ErrorActionPreference = $prevErr
Start-Sleep -Seconds 2

$paymentCsproj = Get-ChildItem -Path $clonePath -Filter "NavalArchive.PaymentSimulation.csproj" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($paymentCsproj) {
    Push-Location $paymentCsproj.DirectoryName
    & "$dotnetDir\dotnet.exe" publish -c Release -o $paymentPath
    if ($LASTEXITCODE -ne 0) { throw "Payment publish failed" }
    Pop-Location
}

$cardCsproj = Get-ChildItem -Path $clonePath -Filter "NavalArchive.CardService.csproj" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cardCsproj) {
    Push-Location $cardCsproj.DirectoryName
    & "$dotnetDir\dotnet.exe" publish -c Release -o $cardPath
    if ($LASTEXITCODE -ne 0) { throw "Card publish failed" }
    Pop-Location
}

$cartCsproj = Get-ChildItem -Path $clonePath -Filter "NavalArchive.CartService.csproj" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cartCsproj) {
    Push-Location $cartCsproj.DirectoryName
    & "$dotnetDir\dotnet.exe" publish -c Release -o $cartPath
    if ($LASTEXITCODE -ne 0) { throw "Cart publish failed" }
    Pop-Location
}

$webServerJs = Get-ChildItem -Path $clonePath -Filter "server.js" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.DirectoryName -like "*NavalArchive.Web*" } | Select-Object -First 1
if ($webServerJs) {
    $webSrcDir = $webServerJs.DirectoryName
    Get-ChildItem -Path $webSrcDir -Exclude node_modules | Copy-Item -Destination $webPath -Recurse -Force
    Push-Location $webPath
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    cmd /c "`"$nodeDir\npm.cmd`" install --omit=dev --no-audit --no-fund >nul 2>&1"
    if ($NewRelicLicenseKey) {
        cmd /c "`"$nodeDir\npm.cmd`" install newrelic --save --no-audit --no-fund >nul 2>&1"
    }
    $ErrorActionPreference = $prevErr
    Pop-Location
}

$dotnetExe = "C:\Program Files\dotnet\dotnet.exe"
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="$dotnetExe" arguments=".\NavalArchive.Api.dll" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
"@ | Set-Content -Path "$apiPath\web.config" -Encoding UTF8

@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rule name="Redirect to HTTPS" stopProcessing="true">
        <match url="(.*)" />
        <conditions>
          <add input="{HTTPS}" pattern="off" ignoreCase="true" />
        </conditions>
        <action type="Redirect" url="https://{HTTP_HOST}/{R:1}" redirectType="Permanent" />
      </rule>
      <rule name="API" stopProcessing="true">
        <match url="^api/(.*)" />
        <action type="Rewrite" url="http://localhost:5000/api/{R:1}" />
      </rule>
      <rule name="Node" stopProcessing="true">
        <match url="(.*)" />
        <action type="Rewrite" url="http://localhost:3000/{R:1}" />
      </rule>
    </rewrite>
  </system.webServer>
</configuration>
"@ | Set-Content -Path "$webPath\web.config" -Encoding UTF8

if (Test-Path "$webPath\server.js") {
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    cmd /c "`"$nssmDir\nssm.exe`" stop NavalArchiveWeb >nul 2>&1"
    cmd /c "`"$nssmDir\nssm.exe`" remove NavalArchiveWeb confirm >nul 2>&1"
    $ErrorActionPreference = $prevErr
    if ($NewRelicLicenseKey) {
        cmd /c "`"$nssmDir\nssm.exe`" install NavalArchiveWeb `"$nodeDir\node.exe`" `"-r newrelic server.js`""
    } else {
        cmd /c "`"$nssmDir\nssm.exe`" install NavalArchiveWeb `"$nodeDir\node.exe`" `"server.js`""
    }
    cmd /c "`"$nssmDir\nssm.exe`" set NavalArchiveWeb AppDirectory $webPath"
    $envVars = "API_URL=http://localhost:5000", "PORT=3000"
    if ($NewRelicLicenseKey) {
        $envVars += "NEW_RELIC_AI_MONITORING_ENABLED=true", "NEW_RELIC_CUSTOM_INSIGHTS_EVENTS_MAX_SAMPLES_STORED=100k", "NEW_RELIC_SPAN_EVENTS_MAX_SAMPLES_STORED=10k", "NEW_RELIC_APP_NAME=Navalarchive", "NEW_RELIC_LICENSE_KEY=$NewRelicLicenseKey"
    }
    $envArgs = ($envVars | ForEach-Object { "`"$_`"" }) -join " "
    cmd /c "`"$nssmDir\nssm.exe`" set NavalArchiveWeb AppEnvironmentExtra $envArgs"
    cmd /c "`"$nssmDir\nssm.exe`" start NavalArchiveWeb"
}

# Payment Simulation service (runs outside IIS on port 5001)
if (Test-Path "$paymentPath\NavalArchive.PaymentSimulation.dll") {
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    cmd /c "`"$nssmDir\nssm.exe`" stop NavalArchivePayment >nul 2>&1"
    cmd /c "`"$nssmDir\nssm.exe`" remove NavalArchivePayment confirm >nul 2>&1"
    $ErrorActionPreference = $prevErr
    cmd /c "`"$nssmDir\nssm.exe`" install NavalArchivePayment `"$dotnetExe`" `"$paymentPath\NavalArchive.PaymentSimulation.dll`""
    cmd /c "`"$nssmDir\nssm.exe`" set NavalArchivePayment AppDirectory $paymentPath"
    cmd /c "`"$nssmDir\nssm.exe`" set NavalArchivePayment AppEnvironmentExtra `"ASPNETCORE_URLS=http://localhost:5001`""
    cmd /c "`"$nssmDir\nssm.exe`" start NavalArchivePayment"
}

# Card service (runs outside IIS on port 5002)
if (Test-Path "$cardPath\NavalArchive.CardService.dll") {
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    cmd /c "`"$nssmDir\nssm.exe`" stop NavalArchiveCard >nul 2>&1"
    cmd /c "`"$nssmDir\nssm.exe`" remove NavalArchiveCard confirm >nul 2>&1"
    $ErrorActionPreference = $prevErr
    cmd /c "`"$nssmDir\nssm.exe`" install NavalArchiveCard `"$dotnetExe`" `"$cardPath\NavalArchive.CardService.dll`""
    cmd /c "`"$nssmDir\nssm.exe`" set NavalArchiveCard AppDirectory $cardPath"
    cmd /c "`"$nssmDir\nssm.exe`" set NavalArchiveCard AppEnvironmentExtra `"ASPNETCORE_URLS=http://localhost:5002`""
    cmd /c "`"$nssmDir\nssm.exe`" start NavalArchiveCard"
}

# Cart service (runs outside IIS on port 5003)
if (Test-Path "$cartPath\NavalArchive.CartService.dll") {
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    cmd /c "`"$nssmDir\nssm.exe`" stop NavalArchiveCart >nul 2>&1"
    cmd /c "`"$nssmDir\nssm.exe`" remove NavalArchiveCart confirm >nul 2>&1"
    $ErrorActionPreference = $prevErr
    cmd /c "`"$nssmDir\nssm.exe`" install NavalArchiveCart `"$dotnetExe`" `"$cartPath\NavalArchive.CartService.dll`""
    cmd /c "`"$nssmDir\nssm.exe`" set NavalArchiveCart AppDirectory $cartPath"
    cmd /c "`"$nssmDir\nssm.exe`" set NavalArchiveCart AppEnvironmentExtra `"ASPNETCORE_URLS=http://localhost:5003`""
    cmd /c "`"$nssmDir\nssm.exe`" start NavalArchiveCart"
}

# ========== 3. PORTS 80 & 443 ==========
Write-Host "=== 3. Configuring ports 80 and 443 ===" -ForegroundColor Cyan

netsh advfirewall firewall add rule name="HTTP" dir=in action=allow protocol=TCP localport=80 2>$null
netsh advfirewall firewall add rule name="HTTPS" dir=in action=allow protocol=TCP localport=443 2>$null
netsh advfirewall firewall add rule name="API" dir=in action=allow protocol=TCP localport=5000 2>$null
netsh advfirewall firewall add rule name="Payment" dir=in action=allow protocol=TCP localport=5001 2>$null
netsh advfirewall firewall add rule name="Card" dir=in action=allow protocol=TCP localport=5002 2>$null
netsh advfirewall firewall add rule name="Cart" dir=in action=allow protocol=TCP localport=5003 2>$null

Import-Module WebAdministration -ErrorAction SilentlyContinue
Set-WebConfigurationProperty -pspath 'MACHINE/WEBROOT/APPHOST' -filter "system.webServer/proxy" -name "enabled" -value "True" -ErrorAction SilentlyContinue

$existingCert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq "NavalArchive HTTPS" } | Select-Object -First 1
if (!$existingCert) {
    $cert = New-SelfSignedCertificate -DnsName "localhost", "127.0.0.1", "navalarchive-vm" -CertStoreLocation "Cert:\LocalMachine\My" -NotAfter (Get-Date).AddYears(5) -FriendlyName "NavalArchive HTTPS"
} else { $cert = $existingCert }
$certThumbprint = $cert.Thumbprint

if ($appcmd) {
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    cmd /c "`"$appcmd`" set site `"Default Web Site`" `"/bindings.[protocol='http',bindingInformation='*:80:'].bindingInformation:*:8080:`" >nul 2>&1"
    cmd /c "`"$appcmd`" stop site `"Default Web Site`" >nul 2>&1"
    cmd /c "`"$appcmd`" delete site NavalArchive-API >nul 2>&1"
    cmd /c "`"$appcmd`" delete apppool NavalArchive-API >nul 2>&1"
    $ErrorActionPreference = $prevErr
    & $appcmd add apppool /name:NavalArchive-API "/managedRuntimeVersion:"
    & $appcmd add site /name:NavalArchive-API "/physicalPath:$apiPath" "/bindings:http/*:5000:"
    & $appcmd set app "NavalArchive-API/" /applicationPool:NavalArchive-API
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    cmd /c "`"$appcmd`" delete site NavalArchive-Web >nul 2>&1"
    $ErrorActionPreference = $prevErr
    & $appcmd add site /name:NavalArchive-Web "/physicalPath:$webPath" "/bindings:http/*:80:"
    & $appcmd set app "NavalArchive-Web/" /applicationPool:DefaultAppPool 2>$null
    try {
        Import-Module IISAdministration -ErrorAction SilentlyContinue
        New-IISSiteBinding -Name "NavalArchive-Web" -BindingInformation "*:443:" -CertificateThumbPrint $certThumbprint -CertStoreLocation "Cert:\LocalMachine\My" -Protocol https -ErrorAction SilentlyContinue
    } catch {
        New-WebBinding -Name "NavalArchive-Web" -Protocol https -Port 443 -ErrorAction SilentlyContinue
        $httpsBinding = Get-WebBinding -Name "NavalArchive-Web" -Protocol "https" -ErrorAction SilentlyContinue
        if ($httpsBinding) { $httpsBinding.AddSslCertificate($certThumbprint, "My") }
    }
    & $appcmd start site NavalArchive-API
    & $appcmd start site NavalArchive-Web
    icacls $apiPath /grant "IIS AppPool\NavalArchive-API:(OI)(CI)M" /T 2>$null
}

$iisreset = if (Test-Path "$env:windir\SysNative\inetsrv\iisreset.exe") { "$env:windir\SysNative\inetsrv\iisreset.exe" } else { "$env:windir\System32\inetsrv\iisreset.exe" }
if (Test-Path $iisreset) { Start-Process -FilePath $iisreset -ArgumentList "/noforce" -Wait -NoNewWindow }
if ($appcmd) { & $appcmd stop site "Default Web Site" 2>$null }

Remove-Item -Recurse -Force $clonePath -ErrorAction SilentlyContinue
Remove-Item -Force $zipPath -ErrorAction SilentlyContinue

Write-Host "=== Done. https://<vm-ip> | API: :5000 | Payment: :5001 | Card: :5002 | Cart: :5003 ===" -ForegroundColor Green
exit 0
