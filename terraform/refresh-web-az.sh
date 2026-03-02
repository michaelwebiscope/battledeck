#!/bin/bash
# Refresh website via az vm run-command - no Terraform, no lock, ~5-7 min
# Requires: az CLI logged in
set -e
cd "$(dirname "$0")"
RG="${1:-NE-LAB}"
VM="${2:-navalarchive-prod-vm}"
REPO="${3:-https://github.com/michaelwebiscope/battledeck}"
BRANCH="${4:-main}"

echo "Refreshing web on $VM (RG: $RG)..."
az vm run-command invoke \
  --resource-group "$RG" \
  --name "$VM" \
  --command-id RunPowerShellScript \
  --scripts "Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/michaelwebiscope/battledeck/main/scripts/refresh-web.ps1?t=$RANDOM' -OutFile 'C:\Windows\Temp\refresh-web.ps1' -UseBasicParsing -Headers @{ 'Cache-Control'='no-cache' }; powershell -ExecutionPolicy Bypass -File 'C:\Windows\Temp\refresh-web.ps1' -RepoUrl '$REPO' -RepoBranch '$BRANCH'"

echo "Done. Check site in ~1 min."
