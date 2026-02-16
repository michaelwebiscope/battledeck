# Naval Archive - Automated VM Setup (bootstrap + optional deploy)
# Runs via Azure Custom Script Extension on first boot
# Template vars: repo_url, repo_branch

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$RepoUrl = "${repo_url}"
$RepoBranch = "${repo_branch}"

Write-Host "=== Naval Archive VM Setup ===" -ForegroundColor Cyan

# --- 1. Bootstrap: Chocolatey ---
if (!(Get-Command choco -ErrorAction SilentlyContinue)) {
    Write-Host "Installing Chocolatey..." -ForegroundColor Yellow
    Set-ExecutionPolicy Bypass -Scope Process -Force
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
}

# --- 2. Bootstrap: .NET 8 SDK (for publish), Node, Git ---
Write-Host "Installing .NET 8 SDK..." -ForegroundColor Yellow
choco install dotnet-8.0-sdk -y

Write-Host "Installing Node.js LTS..." -ForegroundColor Yellow
choco install nodejs-lts -y

if ($RepoUrl) {
    Write-Host "Installing Git..." -ForegroundColor Yellow
    choco install git -y
}

# --- 3. Bootstrap: IIS ---
Write-Host "Installing IIS..." -ForegroundColor Yellow
Install-WindowsFeature -Name Web-Server, Web-Asp-Net45, Web-WebSockets -IncludeManagementTools

# --- 4. Bootstrap: URL Rewrite ---
$urlRewriteUrl = "https://download.microsoft.com/download/1/2/8/128E2E22-1AE5-47A7-AC89-B6F4705211A3/rewrite_amd64_en-US.msi"
$urlRewritePath = "$env:TEMP\urlrewrite.msi"
Write-Host "Installing URL Rewrite..." -ForegroundColor Yellow
Invoke-WebRequest -Uri $urlRewriteUrl -OutFile $urlRewritePath -UseBasicParsing
Start-Process msiexec.exe -ArgumentList "/i", $urlRewritePath, "/quiet" -Wait

# --- 5. Bootstrap: Application Request Routing (for reverse proxy) ---
$arrUrl = "https://download.microsoft.com/download/e/9/8/e9849d6a-020e-47e4-9fd0-a023e99b54eb/requestRouter_amd64.msi"
$arrPath = "$env:TEMP\arr.msi"
Write-Host "Installing Application Request Routing..." -ForegroundColor Yellow
Invoke-WebRequest -Uri $arrUrl -OutFile $arrPath -UseBasicParsing
Start-Process msiexec.exe -ArgumentList "/i", $arrPath, "/quiet" -Wait

# --- 5b. NSSM (to run Node as Windows service) ---
if ($RepoUrl) {
    Write-Host "Installing NSSM..." -ForegroundColor Yellow
    choco install nssm -y
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
}

# --- 6. Refresh PATH ---
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")

# --- 7. Create app directories ---
$apiPath = "C:\inetpub\navalarchive-api"
$webPath = "C:\inetpub\navalarchive-web"
New-Item -ItemType Directory -Force -Path $apiPath | Out-Null
New-Item -ItemType Directory -Force -Path $webPath | Out-Null

# --- 8. Deploy from Git (if repo URL provided) ---
if ($RepoUrl) {
    Write-Host "Cloning and building application..." -ForegroundColor Yellow
    $clonePath = "C:\navalarchive-src"
    if (Test-Path $clonePath) { Remove-Item -Recurse -Force $clonePath }
    git clone --branch $RepoBranch --single-branch --depth 1 $RepoUrl $clonePath

    # Find API project (handles root or nested battledeck/battledeck structure)
    $apiCsproj = Get-ChildItem -Path $clonePath -Filter "NavalArchive.Api.csproj" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($apiCsproj) {
        $apiProject = $apiCsproj.DirectoryName
        Push-Location $apiProject
        dotnet publish -c Release -o $apiPath
        Pop-Location
    }

    # Find Web project
    $webServerJs = Get-ChildItem -Path $clonePath -Filter "server.js" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.DirectoryName -like "*NavalArchive.Web*" } | Select-Object -First 1
    if ($webServerJs) {
        $webProject = $webServerJs.DirectoryName
        Push-Location $webProject
        npm ci --omit=dev 2>$null; if ($LASTEXITCODE -ne 0) { npm install --omit=dev }
        Get-ChildItem -Exclude node_modules | Copy-Item -Destination $webPath -Recurse -Force
        Copy-Item -Path "node_modules" -Destination $webPath -Recurse -Force
        Pop-Location
    }

    Remove-Item -Recurse -Force $clonePath -ErrorAction SilentlyContinue
}

# --- 9. API web.config ---
$apiWebConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\NavalArchive.Api.dll" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
"@
Set-Content -Path "$apiPath\web.config" -Value $apiWebConfig -Encoding UTF8

# --- 10. Web web.config (reverse proxy to Node) ---
$webWebConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="Node Proxy" stopProcessing="true">
          <match url="(.*)" />
          <action type="Rewrite" url="http://localhost:3000/{R:1}" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
"@
Set-Content -Path "$webPath\web.config" -Value $webWebConfig -Encoding UTF8

# --- 11. Configure IIS ---
Import-Module WebAdministration -ErrorAction SilentlyContinue

# Stop default site to free port 80
Stop-Website -Name "Default Web Site" -ErrorAction SilentlyContinue

# Enable ARR proxy
Set-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" -Filter "system.webServer/proxy" -Name "enabled" -Value "True" -ErrorAction SilentlyContinue

# API App Pool
if (!(Test-Path "IIS:\AppPools\NavalArchive-API")) {
    New-WebAppPool -Name "NavalArchive-API"
    Set-ItemProperty "IIS:\AppPools\NavalArchive-API" -Name "managedRuntimeVersion" -Value ""
}

# API Site (port 5000)
if (!(Test-Path "IIS:\Sites\NavalArchive-API")) {
    New-Website -Name "NavalArchive-API" -PhysicalPath $apiPath -ApplicationPool "NavalArchive-API" -Port 5000
}

# Web Site (port 80)
if (!(Test-Path "IIS:\Sites\NavalArchive-Web")) {
    New-Website -Name "NavalArchive-Web" -PhysicalPath $webPath -ApplicationPool "DefaultAppPool" -Port 80
}

# --- 12. Node.js as Windows service (when app deployed) ---
$webServerPath = Join-Path $webPath "server.js"
if ($RepoUrl -and (Test-Path $webServerPath)) {
    $nodePath = (Get-Command node -ErrorAction SilentlyContinue).Source
    if ($nodePath) {
        nssm stop NavalArchiveWeb 2>$null
        nssm remove NavalArchiveWeb confirm 2>$null
        nssm install NavalArchiveWeb $nodePath $webServerPath
        nssm set NavalArchiveWeb AppDirectory $webPath
        nssm set NavalArchiveWeb AppEnvironmentExtra "API_URL=http://localhost:5000" "PORT=3000"
        nssm start NavalArchiveWeb
    }
}

iisreset

Write-Host "=== Setup complete ===" -ForegroundColor Green
exit 0
