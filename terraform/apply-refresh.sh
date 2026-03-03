#!/bin/bash
# Update website via terraform apply - runs refresh_web null_resource (~5-7 min)
# Uses: terraform apply -auto-approve with refresh_web_trigger
set -e
cd "$(dirname "$0")"
terraform apply -var="refresh_web_trigger=$(date +%s)" -auto-approve
echo "Refresh complete. Check site in ~1 min."
