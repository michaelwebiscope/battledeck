# Deploy Naval Archive to Azure App Service (run after terraform apply)
# Usage: .\deploy.ps1 -ResourceGroup <rg> -ApiAppName <api-app> -WebAppName <web-app>

param(
    [Parameter(Mandatory=$true)]
    [string]$ResourceGroup,
    [Parameter(Mandatory=$true)]
    [string]$ApiAppName,
    [Parameter(Mandatory=$true)]
    [string]$WebAppName
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "=== Deploying Naval Archive ===" -ForegroundColor Cyan

# Publish API
Write-Host "Publishing API..." -ForegroundColor Yellow
Push-Location "$root\NavalArchive.Api"
dotnet publish -c Release -o .\publish
Compress-Archive -Path .\publish\* -DestinationPath ..\api.zip -Force
Pop-Location

# Deploy API
Write-Host "Deploying API to $ApiAppName..." -ForegroundColor Yellow
az webapp deployment source config-zip --resource-group $ResourceGroup --name $ApiAppName --src "$root\api.zip"

# Build Web
Write-Host "Installing Web dependencies..." -ForegroundColor Yellow
Push-Location "$root\NavalArchive.Web"
npm install --production
$webTemp = "$env:TEMP\navalarchive-web-deploy"
if (Test-Path $webTemp) { Remove-Item $webTemp -Recurse -Force }
New-Item -ItemType Directory -Path $webTemp | Out-Null
Get-ChildItem -Exclude node_modules | Copy-Item -Destination $webTemp -Recurse
Compress-Archive -Path "$webTemp\*" -DestinationPath "$root\web.zip" -Force
Remove-Item $webTemp -Recurse -Force
Pop-Location

# Deploy Web
Write-Host "Deploying Web to $WebAppName..." -ForegroundColor Yellow
az webapp deployment source config-zip --resource-group $ResourceGroup --name $WebAppName --src "$root\web.zip"

Write-Host "Done! Check your App Service URLs." -ForegroundColor Green
