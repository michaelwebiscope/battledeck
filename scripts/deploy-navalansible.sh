#!/usr/bin/env bash
# Deploy navalansible: Ansible playbook (run terraform apply separately for first-time VM creation)
# Usage: ./scripts/deploy-navalansible.sh [-newrelic] [-skip-services] [-go-only] [-newrelic-only]
# Requires: ansible, pywinrm (pip install pywinrm)

set -e
cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"
TFVARS_FILE="$REPO_ROOT/terraform-navalansible/terraform.tfvars"
TFVARS_FALLBACK_FILE="$REPO_ROOT/terraform/terraform.tfvars"

ENABLE_NEWRELIC=false
NEWRELIC_ONLY=false
UPDATE_SERVICES=true
GO_ONLY=false
while [ $# -gt 0 ]; do
  case "$1" in
    -newrelic|--newrelic)
      ENABLE_NEWRELIC=true
      shift
      ;;
    -newrelic-only|--newrelic-only)
      NEWRELIC_ONLY=true
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
      echo "Usage: ./scripts/deploy-navalansible.sh [-newrelic] [-newrelic-only] [-skip-services] [-go-only]"
      echo "  -newrelic        also run ansible/playbooks/newrelic.yml after site deploy"
      echo "  -newrelic-only   run only newrelic.yml (skip site.yml entirely — ~2 min)"
      echo "  -skip-services   skip services.yml (faster repeated deploys)"
      echo "  -update-services force services.yml run (default)"
      echo "  -go-only         hot-swap Go binaries only (~2 min, no full redeploy)"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1"
      echo "Usage: ./scripts/deploy-navalansible.sh [-newrelic] [-newrelic-only] [-skip-services] [-go-only]"
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

# Read github_token for private repo auth (optional — omit for public repos)
[ -n "$GITHUB_TOKEN" ] || GITHUB_TOKEN=$(read_tfvar_any "github_token")

# macOS: avoid fork safety when running Ansible
export OBJC_DISABLE_INITIALIZE_FORK_SAFETY=YES

# Only read NR license key when -newrelic is used — site deploy is NR-free without the flag
if [ "$ENABLE_NEWRELIC" = true ]; then
  [ -n "$NEWRELIC_LICENSE_KEY" ] || NEWRELIC_LICENSE_KEY=$(read_tfvar_any "newrelic_license_key")
fi

SITE_ARGS=(
  -e "ansible_host=$VM_IP"
  -e "vm_admin_password=$VM_ADMIN_PASSWORD"
  -e "vm_admin_username=azureadmin"
  -e "github_repo_url=$GITHUB_REPO_URL"
  -e "update_services=$UPDATE_SERVICES"
)
if [ -n "$GITHUB_TOKEN" ]; then
  SITE_ARGS+=( -e "github_token=$GITHUB_TOKEN" )
fi
if [ "$ENABLE_NEWRELIC" = true ] && [ -n "$NEWRELIC_LICENSE_KEY" ]; then
  SITE_ARGS+=( -e "newrelic_license_key=$NEWRELIC_LICENSE_KEY" )
fi

cd ansible

run_ansible() {
  python3 -m ansible playbook "$@"
  local rc=$?
  # Exit codes: 0=ok, 2=task failed, 4=unreachable (WinRM timeout on slow compile is ok)
  # Only abort on genuine task failures (rc=2) or parse errors (rc=1). Allow rc=4 (unreachable).
  if [ $rc -eq 0 ] || [ $rc -eq 4 ]; then
    [ $rc -eq 4 ] && echo "WARNING: Some WinRM connections timed out (unreachable) but continuing deploy..."
    return 0
  fi
  echo "ERROR: Ansible failed with exit code $rc"
  exit $rc
}

if [ "$NEWRELIC_ONLY" = true ]; then
  : # skip site.yml — jump straight to NR playbook below
elif [ "$GO_ONLY" = true ]; then
  echo "=== 2. Ansible playbook (Go binaries only - ~2 min) ==="
  run_ansible playbooks/go-binaries-only.yml "${SITE_ARGS[@]}"
else
  echo "=== 2. Ansible playbook (deploy Naval Archive - ~45-60 min) ==="
  run_ansible playbooks/site.yml "${SITE_ARGS[@]}"
fi

if [ "$ENABLE_NEWRELIC" = true ]; then
  echo "=== 3. Ansible playbook (New Relic) ==="

  # Env vars take precedence over terraform.tfvars
  [ -n "$NEWRELIC_API_KEY" ] || NEWRELIC_API_KEY=$(read_tfvar_any "newrelic_api_key")
  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID=$(read_tfvar_any "newrelic_account_id")
  [ -n "$NEWRELIC_LICENSE_KEY" ] || NEWRELIC_LICENSE_KEY=$(read_tfvar_any "newrelic_license_key")

  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID="7849242"
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

  if [ "$NEWRELIC_ONLY" = true ]; then
    echo "=== Skipping infra/logs/.NET agent (already installed) ==="
    python3 -m ansible playbook playbooks/newrelic-app.yml "${NR_ARGS[@]}"
  else
    python3 -m ansible playbook playbooks/newrelic.yml "${NR_ARGS[@]}"
  fi
fi

cd ..

echo "=== Done. App at https://$VM_IP ==="
