#!/usr/bin/env bash
# Run New Relic: infra/logs/.NET APM → Java → Go → OTEL → Node (APM + browser RUM).
# Uses terraform.tfvars for secrets unless env vars override.
#
# Usage:
#   ./scripts/run-newrelic-dotnet-java-go.sh
#   VM_IP=1.2.3.4 ./scripts/run-newrelic-dotnet-java-go.sh
#   NEWRELIC_ACCOUNT_ID=7849242 ./scripts/run-newrelic-dotnet-java-go.sh

set -e
cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"
TFVARS_FILE="$REPO_ROOT/terraform-navalansible/terraform.tfvars"

read_tfvar() {
  local key="$1"
  [ -f "$TFVARS_FILE" ] || return 0
  awk -F'"' -v k="$key" '$1 ~ "^[[:space:]]*"k"[[:space:]]*=" {print $2}' "$TFVARS_FILE" | head -n 1
}

export OBJC_DISABLE_INITIALIZE_FORK_SAFETY=YES

if [ -z "${VM_IP:-}" ]; then
  VM_IP=$(cd "$REPO_ROOT/terraform-navalansible" && terraform output -raw vm_public_ip 2>/dev/null) || true
fi
if [ -z "$VM_IP" ]; then
  echo "ERROR: Set VM_IP or ensure terraform output vm_public_ip works."
  exit 1
fi

VM_ADMIN_PASSWORD="${VM_ADMIN_PASSWORD:-$(read_tfvar vm_admin_password)}"
if [ -z "$VM_ADMIN_PASSWORD" ]; then
  echo "ERROR: Set VM_ADMIN_PASSWORD or vm_admin_password in $TFVARS_FILE"
  exit 1
fi

NEWRELIC_API_KEY="${NEWRELIC_API_KEY:-$(read_tfvar newrelic_api_key)}"
NEWRELIC_LICENSE_KEY="${NEWRELIC_LICENSE_KEY:-$(read_tfvar newrelic_license_key)}"
NEWRELIC_ACCOUNT_ID="${NEWRELIC_ACCOUNT_ID:-$(read_tfvar newrelic_account_id)}"
[ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID="7849242"

if [ -z "$NEWRELIC_API_KEY" ]; then
  echo "ERROR: Set NEWRELIC_API_KEY or add newrelic_api_key to $TFVARS_FILE (required for newrelic-infra / dotnet install)."
  exit 1
fi
if [ -z "$NEWRELIC_LICENSE_KEY" ]; then
  echo "ERROR: Set NEWRELIC_LICENSE_KEY or add newrelic_license_key to $TFVARS_FILE (required for Java, Go, OTEL, and Node playbooks)."
  exit 1
fi

cd "$REPO_ROOT/ansible"
echo "=== New Relic: dotnet/infra → java → go → otel → node+browser (VM $VM_IP) ==="
python3 -m ansible playbook playbooks/newrelic-dotnet-java-go.yml -i inventory.yml \
  -e "ansible_host=$VM_IP" \
  -e "vm_admin_password=$VM_ADMIN_PASSWORD" \
  -e "vm_admin_username=${VM_ADMIN_USERNAME:-azureadmin}" \
  -e "newrelic_api_key=$NEWRELIC_API_KEY" \
  -e "newrelic_account_id=$NEWRELIC_ACCOUNT_ID" \
  -e "newrelic_license_key=$NEWRELIC_LICENSE_KEY" \
  "$@"
