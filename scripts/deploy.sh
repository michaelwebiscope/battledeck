#!/bin/bash
# Deploy Naval Archive to Azure App Service (run after terraform apply)
# Usage: ./deploy.sh <resource-group> <api-app-name> <web-app-name>

set -e
RG="$1"
API_APP="$2"
WEB_APP="$3"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [ -z "$RG" ] || [ -z "$API_APP" ] || [ -z "$WEB_APP" ]; then
  echo "Usage: $0 <resource-group> <api-app-name> <web-app-name>"
  echo "Get values from: terraform output"
  exit 1
fi

echo "=== Deploying Naval Archive ==="

# Publish API
echo "Publishing API..."
cd "$ROOT/NavalArchive.Api"
dotnet publish -c Release -o ./publish
cd publish && zip -r ../../api.zip . && cd ..

# Deploy API
echo "Deploying API to $API_APP..."
az webapp deployment source config-zip --resource-group "$RG" --name "$API_APP" --src "$ROOT/api.zip"

# Build Web
echo "Installing Web dependencies..."
cd "$ROOT/NavalArchive.Web"
npm install --production
zip -r ../web.zip . -x "node_modules/*"

# Deploy Web
echo "Deploying Web to $WEB_APP..."
az webapp deployment source config-zip --resource-group "$RG" --name "$WEB_APP" --src "$ROOT/web.zip"

echo "Done! Check your App Service URLs."
