#!/usr/bin/env bash
# Deploy navalansible: Ansible playbook (run terraform apply separately for first-time VM creation)
# Usage: ./scripts/deploy-navalansible.sh [-fullrun] [-newrelic] [-newrelic-only] [-skip-services] [-go-only] [-skip-winrm-check] [-monolithic]
# Requires: ansible, pywinrm (pip install pywinrm into repo .venv or your default python3)

set -e
cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"
TFVARS_FILE="$REPO_ROOT/terraform-navalansible/terraform.tfvars"
TFVARS_FALLBACK_FILE="$REPO_ROOT/terraform/terraform.tfvars"

# Homebrew `python3` often points at a Python without ansible; prefer repo venv if present.
PYTHON_FOR_ANSIBLE=""
for _py in "$REPO_ROOT/.venv/bin/python3" "$REPO_ROOT/venv/bin/python3"; do
  if [ -x "$_py" ] && "$_py" -c "import ansible" 2>/dev/null; then
    PYTHON_FOR_ANSIBLE="$_py"
    break
  fi
done
if [ -z "$PYTHON_FOR_ANSIBLE" ] && command -v python3 >/dev/null 2>&1 && python3 -c "import ansible" 2>/dev/null; then
  PYTHON_FOR_ANSIBLE="$(command -v python3)"
fi
if [ -z "$PYTHON_FOR_ANSIBLE" ]; then
  echo "ERROR: ansible is not installed for any candidate Python interpreter."
  echo "  Example: cd \"$REPO_ROOT\" && python3 -m venv .venv && . .venv/bin/activate && pip install 'ansible>=6' pywinrm"
  exit 1
fi

# -fullrun: after main deploy (site or go-only), run full New Relic stack at the end.
FULLRUN=false
# -newrelic: run ONLY full New Relic playbook (infra → java → go → otel → node); skip site/go deploy.
NEWRELIC_STANDALONE=false
# -newrelic-only: run ONLY app-layer NR (java → go → otel → node); skip site + skip infra reinstall.
NEWRELIC_APP_ONLY=false
UPDATE_SERVICES=true
GO_ONLY=false
SKIP_WINRM_CHECK=false
# Staged = multiple short ansible-playbook runs (new WinRM session each stage). Default on — long single sessions hang often.
STAGED=true
while [ $# -gt 0 ]; do
  case "$1" in
    -fullrun|--fullrun)
      FULLRUN=true
      shift
      ;;
    -newrelic|--newrelic)
      NEWRELIC_STANDALONE=true
      shift
      ;;
    -newrelic-only|--newrelic-only)
      NEWRELIC_APP_ONLY=true
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
    -skip-winrm-check|--skip-winrm-check)
      SKIP_WINRM_CHECK=true
      shift
      ;;
    -staged|--staged)
      STAGED=true
      shift
      ;;
    -monolithic|--monolithic)
      STAGED=false
      shift
      ;;
    -h|--help)
      echo "Usage: ./scripts/deploy-navalansible.sh [-fullrun] [-newrelic] [-newrelic-only] [-skip-services] [-go-only] [-skip-winrm-check] [-monolithic]"
      echo "  (no NR flags)    deploy only: site.yml (or -go-only) — no observability playbooks"
      echo "  -fullrun         after site (or -go-only), run full New Relic: infra+logs+.NET → java → go → otel → node"
      echo "  -newrelic        New Relic only: same full NR stack as -fullrun, but skip site/go deploy"
      echo "  -newrelic-only   NR app layer only (java → go → otel → node), no infra reinstall — ~2 min"
      echo "  -skip-services   skip services.yml (faster repeated deploys)"
      echo "  -update-services force services.yml run (default)"
      echo "  -go-only         hot-swap Go binaries only (~2 min, no full redeploy)"
      echo "  -skip-winrm-check  skip TCP pre-flight to port 5986 (use if you tunnel WinRM differently)"
      echo "  -monolithic        single site.yml (one long WinRM session; not recommended)"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1"
      echo "Usage: ./scripts/deploy-navalansible.sh [-fullrun] [-newrelic] [-newrelic-only] [-skip-services] [-go-only] [-skip-winrm-check] [-monolithic]"
      exit 1
      ;;
  esac
done

if [ "$NEWRELIC_STANDALONE" = true ] && [ "$NEWRELIC_APP_ONLY" = true ]; then
  echo "NOTE: -newrelic overrides -newrelic-only (full NR stack only, skip site)."
  NEWRELIC_APP_ONLY=false
fi

NEEDS_NR=false
if [ "$FULLRUN" = true ] || [ "$NEWRELIC_STANDALONE" = true ] || [ "$NEWRELIC_APP_ONLY" = true ]; then
  NEEDS_NR=true
fi

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
VM_IP="${NAVALANSIBLE_VM_IP:-}" 
if [ -z "$VM_IP" ]; then
  VM_IP=$(cd terraform-navalansible && terraform output -raw vm_public_ip 2>/dev/null || true)
fi
if [ -z "$VM_IP" ]; then
  echo "ERROR: Could not get VM IP. Set NAVALANSIBLE_VM_IP, or run 'terraform apply' in terraform-navalansible/ first."
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

if [ "$SKIP_WINRM_CHECK" = false ]; then
  echo "=== 1b. Pre-flight: WinRM TCP ($VM_IP:5986) ==="
  if ! "$PYTHON_FOR_ANSIBLE" -c "
import socket, sys
host = sys.argv[1]
port = int(sys.argv[2])
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.settimeout(15)
try:
    s.connect((host, port))
except OSError as e:
    print('connect failed:', e, file=sys.stderr)
    sys.exit(1)
finally:
    s.close()
" "$VM_IP" 5986; then
    echo "ERROR: Cannot open TCP connection to $VM_IP:5986 (HTTPS WinRM)."
    echo "  Ensure the VM is running, terraform output vm_public_ip matches, and Azure NSG allows inbound 5986 from your IP."
    echo "  Re-run with -skip-winrm-check only if you use a tunnel or other non-standard path."
    exit 1
  fi
  echo "OK: reachable"
fi

# Read github_repo_url from terraform.tfvars for Ansible
GITHUB_REPO_URL=$(read_tfvar_any "github_repo_url")
[ -n "$GITHUB_REPO_URL" ] || GITHUB_REPO_URL="https://github.com/michaelwebiscope/battledeck.git"

# Read github_token for private repo auth (optional — omit for public repos)
[ -n "$GITHUB_TOKEN" ] || GITHUB_TOKEN=$(read_tfvar_any "github_token")

# macOS: avoid fork safety when running Ansible
export OBJC_DISABLE_INITIALIZE_FORK_SAFETY=YES

if [ "$NEEDS_NR" = true ]; then
  [ -n "$NEWRELIC_LICENSE_KEY" ] || NEWRELIC_LICENSE_KEY=$(read_tfvar_any "newrelic_license_key")
fi

SITE_ARGS=(
  -e "ansible_host=$VM_IP"
  -e "vm_admin_password=$VM_ADMIN_PASSWORD"
  -e "vm_admin_username=azureadmin"
  -e "github_repo_url=$GITHUB_REPO_URL"
  -e "update_services=$UPDATE_SERVICES"
)

# Optional: API DB/Redis settings (demo). Read from terraform.tfvars if present.
# NOTE: These values can contain spaces/semicolons. Pass via JSON to avoid truncation.
API_DATABASE_PROVIDER=$(read_tfvar_any "api_database_provider")
API_CONN_MAIN=$(read_tfvar_any "api_conn_main")
API_CONN_LOGS=$(read_tfvar_any "api_conn_logs")
API_REDIS_CONFIGURATION=$(read_tfvar_any "api_redis_configuration")
API_REDIS_INSTANCE_NAME=$(read_tfvar_any "api_redis_instance_name")
API_DYNAMICLISTS_DB_MODE=$(read_tfvar_any "api_dynamiclists_db_mode")
PG_APP_PASSWORD=$(read_tfvar_any "pg_app_password")
NEWRELIC_TRACE_OBSERVER_HOST=$(read_tfvar_any "newrelic_trace_observer_host")
NEWRELIC_TRACE_OBSERVER_PORT=$(read_tfvar_any "newrelic_trace_observer_port")

export API_DATABASE_PROVIDER API_CONN_MAIN API_CONN_LOGS API_REDIS_CONFIGURATION API_REDIS_INSTANCE_NAME API_DYNAMICLISTS_DB_MODE PG_APP_PASSWORD NEWRELIC_TRACE_OBSERVER_HOST NEWRELIC_TRACE_OBSERVER_PORT

# newrelic_postgres_monitor_password is read only when running New Relic infra (-newrelic / -fullrun); never passed to site.yml
NEWRELIC_POSTGRES_MONITOR_PASSWORD=$(read_tfvar_any "newrelic_postgres_monitor_password")

API_EXTRAVARS_JSON=$("$PYTHON_FOR_ANSIBLE" - <<'PY'
import json, os
d = {}
def put(k, v):
    v = (v or "").strip()
    if v:
        d[k] = v
put("api_database_provider", os.environ.get("API_DATABASE_PROVIDER"))
put("api_conn_main", os.environ.get("API_CONN_MAIN"))
put("api_conn_logs", os.environ.get("API_CONN_LOGS"))
put("pg_app_password", os.environ.get("PG_APP_PASSWORD"))
put("api_redis_configuration", os.environ.get("API_REDIS_CONFIGURATION"))
put("api_redis_instance_name", os.environ.get("API_REDIS_INSTANCE_NAME"))
put("api_dynamiclists_db_mode", os.environ.get("API_DYNAMICLISTS_DB_MODE"))
put("newrelic_trace_observer_host", os.environ.get("NEWRELIC_TRACE_OBSERVER_HOST"))
put("newrelic_trace_observer_port", os.environ.get("NEWRELIC_TRACE_OBSERVER_PORT"))
print(json.dumps(d))
PY
)

if [ "$API_EXTRAVARS_JSON" != "{}" ]; then
  SITE_ARGS+=( -e "$API_EXTRAVARS_JSON" )
fi
if [ -n "$GITHUB_TOKEN" ]; then
  SITE_ARGS+=( -e "github_token=$GITHUB_TOKEN" )
fi
# Pass license into site deploy only when a full deploy may layer NR-related vars (full run)
if [ "$FULLRUN" = true ] && [ -n "$NEWRELIC_LICENSE_KEY" ]; then
  SITE_ARGS+=( -e "newrelic_license_key=$NEWRELIC_LICENSE_KEY" )
fi

cd ansible

run_ansible() {
  "$PYTHON_FOR_ANSIBLE" -m ansible playbook "$@"
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

# --- Main deploy (site or go-only). Skipped for -newrelic / -newrelic-only ---
if [ "$NEWRELIC_STANDALONE" = true ] || [ "$NEWRELIC_APP_ONLY" = true ]; then
  echo "=== 2. Skipping site / go-only (New Relic-only mode) ==="
elif [ "$GO_ONLY" = true ]; then
  echo "=== 2. Ansible playbook (Go binaries only - ~2 min) ==="
  run_ansible playbooks/go-binaries-only.yml "${SITE_ARGS[@]}"
elif [ "$STAGED" = true ]; then
  echo "=== 2. Staged Ansible deploy (13 short WinRM sessions — default) ==="
  STAGE_PLAYBOOKS=(
    playbooks/stages/01-runtime.yml
    playbooks/stages/02-postgres-install.yml
    playbooks/stages/03-postgres-config.yml
    playbooks/stages/04-stop_services.yml
    playbooks/stages/05-clone.yml
    playbooks/stages/06-build.yml
    playbooks/stages/07-deploy.yml
    playbooks/stages/08-iis.yml
    playbooks/stages/09-services.yml
    playbooks/stages/10-populate.yml
    playbooks/stages/11-firewall.yml
    playbooks/stages/12-scheduled_task.yml
    playbooks/stages/13-verify.yml
  )
  for pb in "${STAGE_PLAYBOOKS[@]}"; do
    echo "--- $pb ---"
    run_ansible "$pb" "${SITE_ARGS[@]}"
  done
else
  echo "=== 2. Ansible playbook (single session — deploy Naval Archive - ~45-60 min) ==="
  run_ansible playbooks/site.yml "${SITE_ARGS[@]}"
fi

# --- New Relic ---
if [ "$NEWRELIC_STANDALONE" = true ]; then
  echo "=== 3. New Relic only (full stack: infra + logs + .NET → java → go → otel → node) ==="
  [ -n "$NEWRELIC_LICENSE_KEY" ] || NEWRELIC_LICENSE_KEY=$(read_tfvar_any "newrelic_license_key")
  [ -n "$NEWRELIC_API_KEY" ] || NEWRELIC_API_KEY=$(read_tfvar_any "newrelic_api_key")
  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID=$(read_tfvar_any "newrelic_account_id")
  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID="7849242"
  if [ -z "$NEWRELIC_API_KEY" ]; then
    echo "ERROR: NEWRELIC_API_KEY is required for -newrelic (infra/.NET install)."
    echo "Set env var NEWRELIC_API_KEY, or add newrelic_api_key to terraform.tfvars"
    exit 1
  fi
  NR_ARGS=(
    -e "ansible_host=$VM_IP"
    -e "vm_admin_password=$VM_ADMIN_PASSWORD"
    -e "vm_admin_username=azureadmin"
    -e "newrelic_api_key=$NEWRELIC_API_KEY"
    -e "newrelic_account_id=$NEWRELIC_ACCOUNT_ID"
  )
  [ -n "$NEWRELIC_LICENSE_KEY" ] && NR_ARGS+=( -e "newrelic_license_key=$NEWRELIC_LICENSE_KEY" )
  [ -n "$NEWRELIC_TRACE_OBSERVER_HOST" ] && NR_ARGS+=( -e "newrelic_trace_observer_host=$NEWRELIC_TRACE_OBSERVER_HOST" )
  [ -n "$NEWRELIC_TRACE_OBSERVER_PORT" ] && NR_ARGS+=( -e "newrelic_trace_observer_port=$NEWRELIC_TRACE_OBSERVER_PORT" )
  NR_PG_JSON=$(NEWRELIC_POSTGRES_MONITOR_PASSWORD="$NEWRELIC_POSTGRES_MONITOR_PASSWORD" "$PYTHON_FOR_ANSIBLE" -c "import json,os; v=(os.environ.get('NEWRELIC_POSTGRES_MONITOR_PASSWORD') or '').strip(); print(json.dumps({'newrelic_postgres_monitor_password': v}) if v else '{}')")
  [ "$NR_PG_JSON" != "{}" ] && NR_ARGS+=( -e "$NR_PG_JSON" )
  "$PYTHON_FOR_ANSIBLE" -m ansible playbook playbooks/newrelic-dotnet-java-go.yml "${NR_ARGS[@]}"

elif [ "$NEWRELIC_APP_ONLY" = true ]; then
  echo "=== 3. New Relic app layer only (java → go → otel → node) ==="
  [ -n "$NEWRELIC_LICENSE_KEY" ] || NEWRELIC_LICENSE_KEY=$(read_tfvar_any "newrelic_license_key")
  if [ -z "$NEWRELIC_LICENSE_KEY" ]; then
    echo "ERROR: newrelic_license_key is required for -newrelic-only."
    exit 1
  fi
  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID=$(read_tfvar_any "newrelic_account_id")
  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID="7849242"
  NR_ARGS=(
    -e "ansible_host=$VM_IP"
    -e "vm_admin_password=$VM_ADMIN_PASSWORD"
    -e "vm_admin_username=azureadmin"
    -e "newrelic_account_id=$NEWRELIC_ACCOUNT_ID"
    -e "newrelic_license_key=$NEWRELIC_LICENSE_KEY"
  )
  [ -n "$NEWRELIC_TRACE_OBSERVER_HOST" ] && NR_ARGS+=( -e "newrelic_trace_observer_host=$NEWRELIC_TRACE_OBSERVER_HOST" )
  [ -n "$NEWRELIC_TRACE_OBSERVER_PORT" ] && NR_ARGS+=( -e "newrelic_trace_observer_port=$NEWRELIC_TRACE_OBSERVER_PORT" )
  "$PYTHON_FOR_ANSIBLE" -m ansible playbook playbooks/newrelic-app.yml "${NR_ARGS[@]}"

elif [ "$FULLRUN" = true ]; then
  echo "=== 3. New Relic full stack (infra + logs + .NET → java → go → otel → node) ==="
  [ -n "$NEWRELIC_LICENSE_KEY" ] || NEWRELIC_LICENSE_KEY=$(read_tfvar_any "newrelic_license_key")
  [ -n "$NEWRELIC_API_KEY" ] || NEWRELIC_API_KEY=$(read_tfvar_any "newrelic_api_key")
  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID=$(read_tfvar_any "newrelic_account_id")
  [ -n "$NEWRELIC_ACCOUNT_ID" ] || NEWRELIC_ACCOUNT_ID="7849242"
  if [ -z "$NEWRELIC_API_KEY" ]; then
    echo "ERROR: NEWRELIC_API_KEY is required with -fullrun (infra/.NET install)."
    echo "Set env var NEWRELIC_API_KEY, or add newrelic_api_key to terraform.tfvars"
    exit 1
  fi
  NR_ARGS=(
    -e "ansible_host=$VM_IP"
    -e "vm_admin_password=$VM_ADMIN_PASSWORD"
    -e "vm_admin_username=azureadmin"
    -e "newrelic_api_key=$NEWRELIC_API_KEY"
    -e "newrelic_account_id=$NEWRELIC_ACCOUNT_ID"
  )
  [ -n "$NEWRELIC_LICENSE_KEY" ] && NR_ARGS+=( -e "newrelic_license_key=$NEWRELIC_LICENSE_KEY" )
  [ -n "$NEWRELIC_TRACE_OBSERVER_HOST" ] && NR_ARGS+=( -e "newrelic_trace_observer_host=$NEWRELIC_TRACE_OBSERVER_HOST" )
  [ -n "$NEWRELIC_TRACE_OBSERVER_PORT" ] && NR_ARGS+=( -e "newrelic_trace_observer_port=$NEWRELIC_TRACE_OBSERVER_PORT" )
  NR_PG_JSON=$(NEWRELIC_POSTGRES_MONITOR_PASSWORD="$NEWRELIC_POSTGRES_MONITOR_PASSWORD" "$PYTHON_FOR_ANSIBLE" -c "import json,os; v=(os.environ.get('NEWRELIC_POSTGRES_MONITOR_PASSWORD') or '').strip(); print(json.dumps({'newrelic_postgres_monitor_password': v}) if v else '{}')")
  [ "$NR_PG_JSON" != "{}" ] && NR_ARGS+=( -e "$NR_PG_JSON" )
  "$PYTHON_FOR_ANSIBLE" -m ansible playbook playbooks/newrelic-dotnet-java-go.yml "${NR_ARGS[@]}"
fi

cd ..

echo "=== Done. App at https://$VM_IP ==="
