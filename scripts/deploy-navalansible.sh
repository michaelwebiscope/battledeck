#!/usr/bin/env bash
# Deploy navalansible: Ansible playbook (run terraform apply separately for first-time VM creation)
# Usage: ./scripts/deploy-navalansible.sh [-newrelic] [-skip-services]
# Requires: ansible, pywinrm (pip install pywinrm)

set -e
cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"
TFVARS_FILE="$REPO_ROOT/terraform-navalansible/terraform.tfvars"
TFVARS_FALLBACK_FILE="$REPO_ROOT/terraform/terraform.tfvars"

ENABLE_NEWRELIC=false
UPDATE_SERVICES=true
GO_ONLY=false
while [ $# -gt 0 ]; do
  case "$1" in
    -newrelic|--newrelic)
      ENABLE_NEWRELIC=true
      shift
      ;;
    -skip-services|--skip-services)
      UPDATE_SERVICES=false
      shift
      ;;
    -update-services|--update-services)
      UPDATE_SERVICES=true
      shift
      ;;
    -go-only|--go-only)
      GO_ONLY=true
      shift
      ;;
    -h|--help)
      echo "Usage: ./scripts/deploy-navalansible.sh [-newrelic] [-skip-services] [-go-only]"
      echo "  -newrelic        also run ansible/playbooks/newrelic.yml after site deploy"
      echo "  -skip-services   skip services.yml (faster repeated deploys)"
      echo "  -update-services force services.yml run (default)"
      echo "  -go-only         hot-swap Go binaries only (~2 min, no full redeploy)"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1"
      echo "Usage: ./scripts/deploy-navalansible.sh [-newrelic] [-skip-services]"
      exit 1
      ;;
  esac
done

read_tfvar() {
  local key="$1"
  local file="$2"
  [ -f "$file" ] || return 0
  awk -F'"' -v k="$key" '$1 ~ "^[[:space:]]*"k"[[:space:]]*=" {print $2}' "$file" | head -n 1
}

read_tfvar_any() {
  local key="$1"
  local val
  val=$(read_tfvar "$key" "$TFVARS_FILE")
  if [ -z "$val" ]; then
    val=$(read_tfvar "$key" "$TFVARS_FALLBACK_FILE")
  fi
  printf "%s" "$val"
}

echo "=== 1. Get VM IP from Terraform state ==="
VM_IP=$(cd terraform-navalansible && terraform output -raw vm_public_ip 2>/dev/null || true)
if [ -z "$VM_IP" ]; then
  echo "ERROR: Could not get VM IP. Run 'terraform apply' in terraform-navalansible/ first."
  exit 1
fi

# Read password from terraform.tfvars if not set
if [ -z "$VM_ADMIN_PASSWORD" ]; then
  VM_ADMIN_PASSWORD=$(read_tfvar_any "vm_admin_password")
fi
if [ -z "$VM_ADMIN_PASSWORD" ]; then
  echo "Set VM_ADMIN_PASSWORD (same as terraform vm_admin_password) or add vm_admin_password to terraform.tfvars and re-run."
  exit 1
fi

# Read github_repo_url from terraform.tfvars for Ansible
GITHUB_REPO_URL=$(read_tfvar_any "github_repo_url")
[ -n "$GITHUB_REPO_URL" ] || GITHUB_REPO_URL="https://github.com/michaelwebiscope/battledeck.git"

# macOS: avoid fork safety when running Ansible
export OBJC_DISABLE_INITIALIZE_FORK_SAFETY=YES

# Always read New Relic license key so OTEL services can authenticate with New Relic's OTLP endpoint
[ -n "$NEWRELIC_LICENSE_KEY" ] || NEWRELIC_LICENSE_KEY=$(read_tfvar_any "newrelic_license_key")

SITE_ARGS=(
  -e "ansible_host=$VM_IP"
  -e "vm_admin_password=$VM_ADMIN_PASSWORD"
  -e "vm_admin_username=azureadmin"
  -e "github_repo_url=$GITHUB_REPO_URL"
  -e "update_services=$UPDATE_SERVICES"
)
if [ -n "$NEWRELIC_LICENSE_KEY" ]; then
  SITE_ARGS+=( -e "newrelic_license_key=$NEWRELIC_LICENSE_KEY" )
fi

cd ansible

if [ "$GO_ONLY" = true ]; then
  echo "=== 2. Ansible playbook (Go binaries only - ~2 min) ==="
  python3 -m ansible playbook playbooks/go-binaries-only.yml "${SITE_ARGS[@]}"
else
  echo "=== 2. Ansible playbook (deploy Naval Archive - ~45-60 min) ==="
  python3 -m ansible playbook playbooks/site.yml "${SITE_ARGS[@]}"
fi

if [ "$ENABLE_NEWRELIC" = true ]; then
  echo "=== 3. Ansible playbook (New Relic) ==="

  # Env vars take precedence over terraform.tfvars
  [ -n "$NEWRELIC_API_KEY" ] || NEWRELIC_API_KEY=$(read_tfvar_any "newrelic_api_key")
  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID=$(read_tfvar_any "newrelic_account_id")
  [ -n "$NEWRELIC_LICENSE_KEY" ] || NEWRELIC_LICENSE_KEY=$(read_tfvar_any "newrelic_license_key")

  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID="7534908"
  if [ -z "$NEWRELIC_API_KEY" ]; then
    echo "ERROR: NEWRELIC_API_KEY is required with -newrelic."
    echo "Set env var NEWRELIC_API_KEY, or add newrelic_api_key to:"
    echo "  - $TFVARS_FILE"
    echo "  - $TFVARS_FALLBACK_FILE"
    exit 1
  fi

  NR_ARGS=(
    -e "ansible_host=$VM_IP"
    -e "vm_admin_password=$VM_ADMIN_PASSWORD"
    -e "vm_admin_username=azureadmin"
    -e "newrelic_api_key=$NEWRELIC_API_KEY"
    -e "newrelic_account_id=$NEWRELIC_ACCOUNT_ID"
  )
  if [ -n "$NEWRELIC_LICENSE_KEY" ]; then
    NR_ARGS+=( -e "newrelic_license_key=$NEWRELIC_LICENSE_KEY" )
  fi

  python3 -m ansible playbook playbooks/newrelic.yml "${NR_ARGS[@]}"
fi

cd ..

echo "=== Done. App at https://$VM_IP ==="
