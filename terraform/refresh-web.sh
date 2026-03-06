#!/bin/bash
# Fast website refresh - uses az directly (no Terraform lock), ~5-7 min
# For Terraform-based refresh: terraform apply -target=null_resource.refresh_web -var="refresh_web_trigger=$(date +%s)" -auto-approve
set -e
cd "$(dirname "$0")"
REPO_ROOT="$(cd .. && pwd)"
if [ -d "$REPO_ROOT/.git" ]; then
  UNCOMMITTED=$(git -C "$REPO_ROOT" status --porcelain NavalArchive.Web 2>/dev/null | head -1)
  UNPUSHED=$(git -C "$REPO_ROOT" rev-list origin/main..HEAD 2>/dev/null | head -1)
  if [ -n "$UNCOMMITTED" ] || [ -n "$UNPUSHED" ]; then
    echo "WARNING: Uncommitted or unpushed changes. VM pulls from GitHub - push first!"
    read -p "Continue anyway? [y/N] " -n 1 -r; echo
    [[ ! $REPLY =~ ^[Yy]$ ]] && exit 1
  fi
fi
exec ./refresh-web-az.sh "${1:-NE-LAB}" "${2:-navalarchive-prod-vm}"
