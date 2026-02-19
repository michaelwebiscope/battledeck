# Bootstrap script for Naval Archive on Windows (Puppet-equivalent)
# Run on the VM after Terraform creates it: .\bootstrap.ps1

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Write-Host "=== Naval Archive Bootstrap ===" -ForegroundColor Cyan

# 1. Install Chocolatey
if (!(Get-Command choco -ErrorAction SilentlyContinue)) {
    Write-Host "Installing Chocolatey..." -ForegroundColor Yellow
    Set-ExecutionPolicy Bypass -Scope Process -Force
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
}

# 2. Install .NET 8, Node.js, IIS
Write-Host "Installing .NET 8 Hosting Bundle..." -ForegroundColor Yellow
choco install dotnet-8.0 -y

Write-Host "Installing Node.js LTS..." -ForegroundColor Yellow
choco install nodejs-lts -y

Write-Host "Installing IIS and features..." -ForegroundColor Yellow
Install-WindowsFeature -Name Web-Server, Web-Asp-Net45, Web-WebSockets -IncludeManagementTools

# 3. Install URL Rewrite (required for reverse proxy)
$urlRewriteUrl = "https://download.microsoft.com/download/1/2/8/128E2E22-1AE5-47A7-AC89-B6F4705211A3/rewrite_amd64_en-US.msi"
$urlRewritePath = "$env:TEMP\urlrewrite.msi"
Write-Host "Installing URL Rewrite..." -ForegroundColor Yellow
Invoke-WebRequest -Uri $urlRewriteUrl -OutFile $urlRewritePath -UseBasicParsing
Start-Process msiexec.exe -ArgumentList "/i", $urlRewritePath, "/quiet" -Wait

# 4. Refresh PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")

# 5. Create app directories
$apiPath = "C:\inetpub\navalarchive-api"
$webPath = "C:\inetpub\navalarchive-web"
New-Item -ItemType Directory -Force -Path $apiPath | Out-Null
New-Item -ItemType Directory -Force -Path $webPath | Out-Null

Write-Host "Bootstrap complete. Next: copy app files and run deploy.ps1" -ForegroundColor Green
