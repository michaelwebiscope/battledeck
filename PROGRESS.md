# Naval Archive — Project Progress & Troubleshooting Guide

> 155 commits across 32 days (2026-02-17 → 2026-03-20)
> Repo: `https://github.com/michaelwebiscope/battledeck.git`

---

## Table of Contents

1. [Architecture](#architecture)
2. [Troubleshooting Runbook](#troubleshooting-runbook)
   - [API returns 500 / website says "cannot access API"](#api-returns-500--website-says-cannot-access-api)
   - [API returns 500.30 — ASP.NET Core app failed to start](#api-returns-50030--aspnet-core-app-failed-to-start)
   - [New Relic: missing golden metrics / no entity for a service](#new-relic-missing-golden-metrics--no-entity-for-a-service)
   - [Deploy hangs / stuck on a task for 10+ minutes](#deploy-hangs--stuck-on-a-task-for-10-minutes)
   - [Card or Cart service not responding (port 5002/5003)](#card-or-cart-service-not-responding-port-50025003)
   - [Go service (account/payment) not responding](#go-service-accountpayment-not-responding)
   - [Node not responding (port 3000) / website down](#node-not-responding-port-3000--website-down)
   - [IIS chain services failing (5010–5019)](#iis-chain-services-failing-5010-5019)
   - [ImagePopulator not healthy (port 5099)](#imagepopulator-not-healthy-port-5099)
   - [DB empty after deploy / ships missing](#db-empty-after-deploy--ships-missing)
   - [IIS 404 on POST endpoints](#iis-404-on-post-endpoints)
   - [HTTPS not working / cert issues](#https-not-working--cert-issues)
   - [Locked DLLs during deploy / publish fails](#locked-dlls-during-deploy--publish-fails)
   - [WinRM timeout during deploy](#winrm-timeout-during-deploy)
   - [Go PS1 wrapper crashes under SYSTEM](#go-ps1-wrapper-crashes-under-system)
   - [NR env vars bleeding into IIS / wrong app names](#nr-env-vars-bleeding-into-iis--wrong-app-names)
   - [Payment response shows "declined" when it should be "approved"](#payment-response-shows-declined-when-it-should-be-approved)
3. [Quick Diagnostic Commands](#quick-diagnostic-commands)
4. [Phase History](#phase-history)
5. [Current State](#current-state)
6. [Deploy Commands](#deploy-commands)
7. [File Map](#file-map)

---

## Architecture

Naval Archive is a distributed application deployed to an Azure Windows Server 2019 VM via Ansible and Terraform. It consists of 15+ services spanning 4 languages.

### Service Map

| Service | Technology | Port | Hosting | Observability |
|---|---|---|---|---|
| **NavalArchive.Api** | .NET 8 (ASP.NET Core) | 5000 | IIS in-process | NR .NET APM agent |
| **NavalArchive.Web** | Node.js (Express/EJS) | 3000 | Windows Service + Scheduled Task | NR Node.js agent |
| **account-service** | Go | 5005 | Scheduled Task (PS1 wrapper) | NR Go agent (env vars) |
| **payment-service** | Go | 5001 | Scheduled Task (PS1 wrapper) | NR Go agent (env vars) |
| **NavalArchive.CardService** | .NET 8 (self-contained) | 5002 | Windows Service (sc.exe) | OTEL auto-instrumentation → OTLP → NR |
| **NavalArchive.CartService** | .NET 8 (self-contained) | 5003 | Windows Service (sc.exe) | OTEL auto-instrumentation → OTLP → NR |
| **ImagePopulator** | Java 17 (Spring Boot 3.3) | 5099 | Scheduled Task | NR Java agent |
| **Gateway** | .NET 8 | 5010 | IIS out-of-process | NR .NET APM agent |
| **Auth** | .NET 8 | 5011 | IIS out-of-process | NR .NET APM agent |
| **User** | .NET 8 | 5012 | IIS out-of-process | NR .NET APM agent |
| **Catalog** | .NET 8 | 5013 | IIS out-of-process | NR .NET APM agent |
| **Inventory** | .NET 8 | 5014 | IIS out-of-process | NR .NET APM agent |
| **Basket** | .NET 8 | 5015 | IIS out-of-process | NR .NET APM agent |
| **Order** | .NET 8 (self-contained) | 5016 | Windows Service | OTEL auto-instrumentation → OTLP → NR |
| **PaymentChain** | .NET 8 (self-contained) | 5017 | Windows Service | OTEL auto-instrumentation → OTLP → NR |
| **Shipping** | .NET 8 (self-contained) | 5018 | Windows Service | OTEL auto-instrumentation → OTLP → NR |
| **Notification** | .NET 8 (self-contained) | 5019 | Windows Service | OTEL auto-instrumentation → OTLP → NR |

### Request Flow

```
Browser → HTTPS (443) → IIS NavalArchive-Web (URL Rewrite) → Node.js (3000)
  ├── /api/* → Node proxy → IIS NavalArchive-API (5000) → ASP.NET Core controllers → SQLite DB
  ├── /api/trace → API → Gateway (5010) → Auth → User → ... → Notification (5019)
  ├── /api/checkout/pay → API → Card (5002) → Payment (5001) → Cart (5003)
  └── /fleet, /stats, etc. → Node renders EJS templates using API data
```

### Infrastructure

- **VM:** Azure Standard_B2s (2 vCPU, 4GB RAM), Windows Server 2019
- **Provisioning:** Terraform (`terraform-navalansible/`)
- **Configuration:** Ansible over WinRM (`ansible/roles/navalansible/`)
- **Database:** SQLite (API: `navalarchive.db`/`logs.db`; Card: `card.db`; Cart: `cart.db`; Payment: `payment.db`; Account: `accounts.db`)
- **HTTPS:** Self-signed certificate, IIS ARR reverse proxy
- **Observability:** New Relic (APM, Infrastructure, Logs, OTEL)

---

## Troubleshooting Runbook

### API returns 500 / website says "cannot access API"

**How to diagnose:**
```bash
# From your machine — check if API is responding at all
curl -sk https://<VM_IP>/api/health
curl -sk https://<VM_IP>/api/ships?page=1&pageSize=1

# If you get HTML error page, read it — the title tells you the error type
curl -sk https://<VM_IP>/api/health 2>/dev/null | grep "<title>"
# "HTTP Error 500.30" → see next section (ASP.NET Core failed to start)
# "HTTP Error 502" → API is down, Node can't reach it
```

```powershell
# On VM — test API directly (bypasses Node proxy)
Invoke-WebRequest http://localhost:5000/health -UseBasicParsing -TimeoutSec 10
Invoke-WebRequest http://localhost:5000/api/health -UseBasicParsing -TimeoutSec 10

# Check IIS site state
$appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
& $appcmd list site NavalArchive-API
& $appcmd list apppool NavalArchive-API

# Check stdout logs for exceptions
Get-ChildItem C:\inetpub\navalarchive-api\logs\stdout*.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 50 }
```

**Common causes:**
1. **500.30 — ASP.NET Core failed to start** → see next section
2. **DB file missing or locked** → `api/health` returns `{"db":"error","error":"..."}`. Check if `navalarchive.db` exists in `C:\inetpub\navalarchive-api\`. Check IIS app pool has write permission: `icacls C:\inetpub\navalarchive-api`
3. **IIS site/pool stopped** → restart with `& $appcmd start apppool NavalArchive-API; & $appcmd start site NavalArchive-API`
4. **Node proxy can't reach API** → Node is up but API is down. Check `http://localhost:5000/health` directly

**Quick fix:** Restart IIS: `iisreset /restart`

---

### API returns 500.30 — ASP.NET Core app failed to start

This is the most dangerous error — the .NET process won't even boot. No application logs are generated.

**How to diagnose:**
```powershell
# 1. Check if the error is 500.30 specifically
(Invoke-WebRequest http://localhost:5000/health -UseBasicParsing -ErrorAction SilentlyContinue).Content
# Look for "500.30" in the HTML

# 2. Try running the app directly (outside IIS) — if this works, it's an IIS issue
Set-Location C:\inetpub\navalarchive-api
& "C:\Program Files\dotnet\dotnet.exe" NavalArchive.Api.dll --urls=http://localhost:5099
# If it starts and shows "Now listening on..." → IIS-specific problem
# If it crashes → application code problem (read the exception)

# 3. Check Windows Event Log for ANCM errors
Get-WinEvent -FilterHashtable @{LogName="Application"; ProviderName="IIS AspNetCore Module V2"; StartTime=(Get-Date).AddHours(-1)} -MaxEvents 5 | ForEach-Object { $_.Message.Substring(0, [Math]::Min(500, $_.Message.Length)) }

# 4. Check for NR .NET profiler poisoning W3SVC (the #1 cause we've seen)
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\W3SVC" -Name Environment -ErrorAction SilentlyContinue).Environment
# If you see CORECLR_ENABLE_PROFILING=1 or COR_ENABLE_PROFILING=1 → THAT'S THE PROBLEM

# 5. Check machine-level profiler vars
[Environment]::GetEnvironmentVariable("CORECLR_ENABLE_PROFILING","Machine")
[Environment]::GetEnvironmentVariable("CORECLR_PROFILER","Machine")

# 6. Check if 64-bit profiler DLL exists (IIS runs 64-bit)
Test-Path "C:\Program Files\New Relic\.NET Agent\NewRelic.Profiler.dll"
# If False but CORECLR_ENABLE_PROFILING=1 → CLR tries to load missing DLL → crash

# 7. Check directory permissions (stdout logs may fail to write)
icacls C:\inetpub\navalarchive-api
icacls C:\inetpub\navalarchive-api\logs
# Must include: IIS AppPool\NavalArchive-API:(OI)(CI)M
```

**Known cause: NR .NET profiler crash (encountered 2026-03-20)**

The NR `.NET agent` installer (`apm-dotnet`) sets `CORECLR_ENABLE_PROFILING=1` machine-wide and on the W3SVC service, but only installs the 32-bit profiler DLL. IIS runs 64-bit → tries to load non-existent 64-bit DLL → CLR crash → 500.30.

**Fix:** Re-run the NR install — the `newrelic.newrelic_install` role with `apm-dotnet` target should install the 64-bit profiler DLL. Then restart IIS:
```powershell
# Re-run NR install to get the 64-bit profiler
./scripts/deploy-navalansible.sh -newrelic

# Or manually on VM:
iisreset /restart
icacls C:\inetpub\navalarchive-api /grant "IIS AppPool\NavalArchive-API:(OI)(CI)M" /T
```

**Prevention:** The `newrelic.newrelic_install` role manages all profiler registry vars. Don't manually edit W3SVC registry or machine-level CLR profiler env vars — let the NR role own them.

**Why this was hard to find:**
- `stdoutLogEnabled` was `false` (now `true` in `webconfig_api.j2`)
- Logs directory lacked write permissions for app pool
- Process crashed before any .NET code ran — no managed exception
- `verify.yml` only checked `/trace` (no DB dependency) and Node returned misleading 200
- Windows Event Log didn't always capture the API crash

---

### New Relic: missing golden metrics / no entity for a service

**How to diagnose:**
```sql
-- NRQL: Check if ANY data is arriving for the service
FROM Metric SELECT count(*) WHERE service.name = 'NavalArchiveCard' SINCE 1 hour ago
FROM Span SELECT count(*) WHERE service.name = 'NavalArchiveCard' SINCE 1 hour ago
FROM Log SELECT count(*) WHERE service.name = 'NavalArchiveCard' SINCE 1 hour ago
```

```powershell
# On VM: Check OTEL env vars for the service
$svc = "NavalArchiveCard"  # or NavalArchiveCart, NavalArchiveOrder, etc.
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" -Name Environment -ErrorAction SilentlyContinue).Environment | Where-Object { $_ -like "OTEL_*" }

# Must have ALL of these for golden metrics:
# OTEL_TRACES_EXPORTER=otlp
# OTEL_METRICS_EXPORTER=otlp          ← THIS IS THE KEY ONE
# OTEL_LOGS_EXPORTER=otlp
# OTEL_DOTNET_AUTO_TRACES_ENABLED=true
# OTEL_DOTNET_AUTO_METRICS_ENABLED=true  ← AND THIS
# OTEL_DOTNET_AUTO_LOGS_ENABLED=true
# OTEL_EXPORTER_OTLP_HEADERS=api-key=<your_license_key>
# OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp.nr-data.net   (US) or https://otlp.eu01.nr-data.net (EU)

# Enable OTEL debug logging to verify pipeline
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
$current = (Get-ItemProperty -Path $regPath -Name "Environment" -ErrorAction SilentlyContinue).Environment
$updated = @($current | Where-Object { $_ -notlike "OTEL_DOTNET_AUTO_LOG_DIRECTORY=*" -and $_ -notlike "OTEL_LOG_LEVEL=*" })
$updated += "OTEL_DOTNET_AUTO_LOG_DIRECTORY=C:\inetpub\navalarchive-card\otel-logs"
$updated += "OTEL_LOG_LEVEL=debug"
Set-ItemProperty -Path $regPath -Name "Environment" -Value $updated -Type MultiString -Force
sc.exe stop $svc; Start-Sleep 3; sc.exe start $svc

# After restart, check logs:
Get-ChildItem C:\inetpub\navalarchive-card\otel-logs\*Managed* | ForEach-Object { Get-Content $_.FullName | Select-String "Export|error|fail" }
# Look for: "OtlpMetricExporter.Export succeeded" → data IS flowing
# Look for: "StartupHook initialized successfully!" → profiler IS attaching
# Look for: "http.server.request.duration" → the metric NR uses for golden metrics
```

**Known cause: traces-only config (encountered 2026-03-20)**

Card, Cart, and Order only had `OTEL_TRACES_EXPORTER=otlp` — missing `OTEL_METRICS_EXPORTER=otlp`. NR golden metrics (throughput, response time, error rate) are built from the `http.server.request.duration` **metric** histogram, not from traces. Without the metrics exporter, spans arrive but no golden metrics dashboard appears.

**Fix:** Ensure `services.yml` has all three exporters + enablement flags for every OTEL service. See the Card/Cart blocks in `ansible/roles/navalansible/tasks/services.yml`.

**Other causes to check:**
- **Wrong api-key** — `OTEL_EXPORTER_OTLP_HEADERS=api-key=<key>` must use the INGEST license key (starts with `FFFFNRAL` suffix for INGEST type), not the User API key
- **Wrong region** — US account uses `otlp.nr-data.net`, EU uses `otlp.eu01.nr-data.net`
- **Service not running** — Check `Get-Service $svc` and `Get-NetTCPConnection -LocalPort <port> -State Listen`
- **No traffic** — OTEL doesn't send metrics until HTTP requests arrive. Hit the service to generate data, then wait 1–2 minutes
- **OTEL auto-instrumentation not installed** — Check: `Test-Path "C:\Program Files\OpenTelemetry .NET AutoInstrumentation\net\OpenTelemetry.AutoInstrumentation.StartupHook.dll"`
- **Entity creation delay** — New OTEL entities can take 5–10 minutes to appear in NR after first data

---

### Deploy hangs / stuck on a task for 10+ minutes

**How to diagnose:** Look at the last Ansible task name printed. Common culprits:
- "Create NavalArchiveWeb service" — `sc.exe start` blocks when Node doesn't respond to SCM
- "Stop and remove Naval Archive services" — service won't stop cleanly
- "Kill navalarchive and dotnet processes" — `iisreset /stop` hanging

**Prevention:** All `win_shell` tasks now have `async` + `poll` timeouts (max 120s). If you see a hang, the timeout was probably removed or a new task was added without one.

**Quick fix:**
```bash
# Kill the stuck deploy
kill $(ps aux | grep ansible | grep -v grep | awk '{print $2}')

# Re-run — incremental deploys skip unchanged steps
./scripts/deploy-navalansible.sh -newrelic
```

**Rule:** Every `ansible.windows.win_shell` task that starts a service, creates a scheduled task, or runs `sc.exe`/`iisreset` MUST have `async: <seconds>` and `poll: 5`. Use:
- `async: 120` for service creation (sc.exe create + start)
- `async: 60` for scheduled tasks, IIS operations, stop/kill
- `async: 30` for quick commands (cert, registry, copy)

---

### Card or Cart service not responding (port 5002/5003)

```powershell
# Check service status
Get-Service NavalArchiveCard
Get-Service NavalArchiveCart

# Check if process is running
Get-CimInstance Win32_Process | Where-Object { $_.Name -like "*CardService*" -or $_.Name -like "*CartService*" }

# Check if port is listening
Get-NetTCPConnection -LocalPort 5002 -State Listen -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort 5003 -State Listen -ErrorAction SilentlyContinue

# Test endpoints (Card has Swagger but no /health; Cart has /api/cart/items)
Invoke-WebRequest http://localhost:5002/swagger/index.html -UseBasicParsing -TimeoutSec 5
Invoke-WebRequest http://localhost:5003/api/cart/items/test -UseBasicParsing -TimeoutSec 5

# Restart
sc.exe stop NavalArchiveCard; Start-Sleep 3; sc.exe start NavalArchiveCard
sc.exe stop NavalArchiveCart; Start-Sleep 3; sc.exe start NavalArchiveCart

# Check OTEL isn't crashing the service (EF Core incompatibility with OTEL 1.9.0)
# Look for this known non-fatal error in OTEL logs:
# "Unknown error processing event 'Microsoft.EntityFrameworkCore.Database.Command.CommandCreated'"
# This is harmless — the service still works, just EF Core spans are incomplete
```

---

### Go service (account/payment) not responding

```powershell
# These run as Scheduled Tasks, not Windows Services
Get-ScheduledTask -TaskName "NavalArchiveAccount" | Select-Object State
Get-ScheduledTask -TaskName "NavalArchivePayment" | Select-Object State

# Check health
Invoke-WebRequest http://localhost:5005/health -UseBasicParsing -TimeoutSec 5  # account
Invoke-WebRequest http://localhost:5001/health -UseBasicParsing -TimeoutSec 5  # payment

# Restart
Start-ScheduledTask -TaskName "NavalArchiveAccount"
Start-ScheduledTask -TaskName "NavalArchivePayment"

# Check the PS1 wrapper for errors
Get-Content C:\inetpub\navalarchive-account\run-account-service.ps1
Get-Content C:\inetpub\navalarchive-payment\run-payment-service.ps1

# Common issue: port already in use (previous process didn't die)
Get-NetTCPConnection -LocalPort 5005 -State Listen  # who's holding the port?
Stop-Process -Id <PID> -Force  # kill it, then restart task
```

---

### Node not responding (port 3000) / website down

```powershell
# Node runs as BOTH a Windows Service and a Scheduled Task (redundancy)
Get-Service NavalArchiveWeb
Get-ScheduledTask -TaskName "NavalArchiveWeb-Node" | Select-Object State

# Check if node.exe is running
Get-Process node -ErrorAction SilentlyContinue

# Test directly
Invoke-WebRequest http://localhost:3000/ -UseBasicParsing -TimeoutSec 5
Invoke-WebRequest http://localhost:3000/health -UseBasicParsing -TimeoutSec 5

# Restart via scheduled task (more reliable than sc.exe for Node)
Start-ScheduledTask -TaskName "NavalArchiveWeb-Node"

# Or kill and restart
taskkill /F /IM node.exe 2>$null
Start-Sleep 2
Start-ScheduledTask -TaskName "NavalArchiveWeb-Node"
```

---

### IIS chain services failing (5010–5019)

```powershell
# IIS-hosted (5010–5015): check IIS sites
$appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
& $appcmd list site | Select-String "Naval"

# Windows service-hosted (5016–5019): check services
foreach ($s in @("NavalArchiveOrder","NavalArchivePaymentChain","NavalArchiveShipping","NavalArchiveNotification")) {
  $svc = Get-Service $s -ErrorAction SilentlyContinue
  "$s : $($svc.Status)"
}

# Test each chain service health
for ($p = 5010; $p -le 5019; $p++) {
  try { $r = Invoke-WebRequest "http://localhost:$p/health" -UseBasicParsing -TimeoutSec 3; "$p : $($r.StatusCode)" } catch { "$p : FAIL" }
}

# Common issue: "Failed to bind to port" in Event Log
# This happens with IIS out-of-process hosting — the ANCM internal port conflicts
# Fix: iisreset /restart
```

---

### ImagePopulator not healthy (port 5099)

```powershell
# Runs as scheduled task
Get-ScheduledTask -TaskName "NavalArchiveImagePopulator" | Select-Object State
Invoke-WebRequest http://localhost:5099/health -UseBasicParsing -TimeoutSec 5

# Check if Java is running
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*image-populator*" }

# Check PS1 launcher
Get-Content C:\inetpub\run-image-populator-listener.ps1

# Restart
Start-ScheduledTask -TaskName "NavalArchiveImagePopulator"
```

---

### DB empty after deploy / ships missing

The `build.yml` task clears the API directory but now excludes `*.db` files. If ships are missing:

```powershell
# Check if DB exists
Test-Path C:\inetpub\navalarchive-api\navalarchive.db

# Check ship count via API
Invoke-WebRequest http://localhost:5000/api/health -UseBasicParsing | Select-Object -ExpandProperty Content
# Should show: {"shipCount":56,...}

# If empty, run ImagePopulator manually (fetches from Wikipedia)
# From deploy machine:
./scripts/deploy-navalansible.sh  # with run_image_populator=true
```

---

### IIS 404 on POST endpoints

IIS controller-based routing sometimes returns 404 on POST requests, especially under WebDAV or ARR.

**Known fix (checkout):** Use `app.MapPost()` Minimal API instead of `[HttpPost]` controller action. See `Program.cs` line `app.MapPost("api/checkout/pay", ...)`.

**Known fix (captain delete):** Use POST-based delete (`/api/captains/delete/:id`) instead of HTTP DELETE (WebDAV intercepts DELETE). See `server.js` explicit proxy.

---

### HTTPS not working / cert issues

```powershell
# Check cert exists
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq "NavalArchive HTTPS" }

# Check HTTPS binding
$appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
& $appcmd list site NavalArchive-Web  # should show https/*:443:

# Recreate cert
New-SelfSignedCertificate -DnsName "localhost","127.0.0.1","navalansible-vm" -CertStoreLocation "Cert:\LocalMachine\My" -NotAfter (Get-Date).AddYears(5) -FriendlyName "NavalArchive HTTPS"
```

---

### Locked DLLs during deploy / publish fails

IIS holds file locks on .NET DLLs. The deploy pipeline handles this, but if you see errors:

```powershell
# Stop IIS completely
iisreset /stop
taskkill /F /IM w3wp.exe 2>$null

# Kill all .NET processes
taskkill /F /IM dotnet.exe 2>$null
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*navalarchive*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

# Now publish/copy should succeed
```

---

### WinRM timeout during deploy

WinRM can drop mid-deploy if the VM is under heavy load (IIS restarting, all services starting, dotnet publish). When this happens, all tasks after the dropped one never run — services stay dead.

**Prevention (applied):**
- All `win_shell` tasks have `ignore_unreachable: true` — WinRM drops don't kill the play
- `stop_services.yml` no longer runs `iisreset /stop` (which killed W3SVC and made the VM sluggish) — instead stops individual IIS sites/pools while keeping W3SVC alive
- `populate.yml` does `iisreset /restart` to bring everything back up cleanly
- All health-check tasks (`win_uri`) have `ignore_unreachable: true`
- Deploy script tolerates exit code 4 (unreachable) and continues to NR playbook

**Current settings** (`ansible/inventory.yml`):
- `ansible_winrm_operation_timeout_sec: 600`
- `ansible_winrm_read_timeout_sec: 1200`

**If the deploy still fails with UNREACHABLE:** The VM services are likely already running (the task completed on the VM even though Ansible lost the connection). Just re-run — incremental deploys skip unchanged steps. Or restart services manually:
```powershell
& C:\inetpub\start-all-services.ps1
iisreset /restart
```

---

### Go PS1 wrapper crashes under SYSTEM

Go services run as Scheduled Tasks under SYSTEM. The PS1 wrapper must NOT use `$ErrorActionPreference = "Stop"` — this causes exit code `0xFFFFD000` when run as SYSTEM.

**Correct pattern:**
```powershell
# No $ErrorActionPreference = "Stop" !
$env:HTTP_PORT = '5005'
$env:DATABASE_URL = 'C:\inetpub\navalarchive-account\accounts.db'
Set-Location 'C:\inetpub\navalarchive-account'
& 'C:\inetpub\navalarchive-account\account-service.exe'
```

---

### NR env vars bleeding into IIS / wrong app names

Machine-level environment variables (like `NEW_RELIC_APP_NAME`) are inherited by ALL processes including IIS w3wp.exe workers. This causes all IIS chain services to report the same app name.

**Rule:** NR env vars for Go/Java services MUST be set in their PS1 wrapper at process level (`$env:VAR = "value"`), never at machine level.

**Check/clean:**
```powershell
# Should return empty for these:
[Environment]::GetEnvironmentVariable("NEW_RELIC_APP_NAME","Machine")
[Environment]::GetEnvironmentVariable("NEW_RELIC_LICENSE_KEY","Machine")

# If set, remove:
[Environment]::SetEnvironmentVariable("NEW_RELIC_APP_NAME",$null,"Machine")
[Environment]::SetEnvironmentVariable("NEW_RELIC_LICENSE_KEY",$null,"Machine")
```

---

### Payment response shows "declined" when it should be "approved"

The Go payment-service returns camelCase JSON (`approved: true`), but the .NET API deserializes with PascalCase by default (`Approved`).

**Fix applied:** `Program.cs` uses `PropertyNameCaseInsensitive = true` for payment response deserialization. If this regresses, check the `paymentJsonOptions` variable in `Program.cs`.

---

## Quick Diagnostic Commands

### From your machine (bash)
```bash
VM_IP="20.238.64.21"  # update with current IP

# Quick health check
./scripts/check-endpoints.sh $VM_IP

# Full endpoint test (with session)
./scripts/test-all-endpoints.sh $VM_IP

# Single endpoint check
curl -sk https://$VM_IP/api/health
curl -sk https://$VM_IP/api/ships?page=1&pageSize=1
```

### On the VM (PowerShell)
```powershell
# Run full diagnostics
& C:\inetpub\diagnose-endpoints.ps1

# Check all services
& C:\inetpub\health-check-all.ps1

# Start everything
& C:\inetpub\start-all-services.ps1
```

### Via Ansible ad-hoc (from deploy machine)
```bash
export OBJC_DISABLE_INITIALIZE_FORK_SAFETY=YES  # macOS only
VM_IP="20.238.64.21"
VM_PASS=$(awk -F'"' '/vm_admin_password/ {print $2}' terraform-navalansible/terraform.tfvars)
cd ansible

# Run any PowerShell command on the VM
python3 -m ansible adhoc navalansible-vm -i inventory.yml \
  -m ansible.windows.win_shell -a '<POWERSHELL_COMMAND>' \
  -e "ansible_host=$VM_IP" -e "vm_admin_password=$VM_PASS" -e "vm_admin_username=azureadmin"
```

---

## Phase History

### Phase 1: Foundation (Feb 17–18)
Built NavalArchive.Api (.NET 8), NavalArchive.Web (Node.js), Terraform VM config, Ansible deploy role, IIS routing (443 → URL Rewrite → Node → API proxy).

### Phase 2: Membership & Payment Chain (Feb 19)
Built CardService, CartService, Payment Simulation. Checkout flow: card validate → cart total → payment → clear cart. Solved IIS 404 on POST with Minimal API. Solved locked DLLs with iisreset before publish.

### Phase 3: Distributed Tracing (Feb 20–21)
Built 10-service trace chain (Gateway → Notification). Trace page UI. IIS hosting for chain services (5010–5015 IIS, 5016–5019 Windows services).

### Phase 4: Payment Backend (Mar 12)
Replaced .NET Payment with Go payment-service + account-service. Pay from Account with balance wallet. Go binary hot-swap deploy flag. NR Go agent instrumentation.

### Phase 5: Deploy Hardening (Mar 13)
Fixed 9 deploy failures: Go PS1 crashes, NR env var bleeding, IIS file locks, WinRM timeouts, iisreset failures, API health check timing. Split NR playbooks into modular pieces.

### Phase 6: Features & NR (Mar 15)
Combined Members page. Per-service change detection for efficient deploys. NR change tracking via GitHub Actions. ImagePopulator Spring Boot 3.3 migration. HTTPS binding fix. Payment case-insensitive deserialization.

### Phase 7: API Crash & OTEL Fix (Mar 20)
- **500.30 crash:** NR .NET profiler set `CORECLR_ENABLE_PROFILING=1` with missing 64-bit DLL → CLR crash. Fix: re-run NR install to get 64-bit DLL. NR role owns all profiler registry vars. Also enabled stdout logging, added `/api/health` with DB check, rewrote `verify.yml`.
- **Missing golden metrics:** Card/Cart/Order had traces-only OTEL config. Added metrics+logs exporters. NR golden metrics need `http.server.request.duration` metric, not traces.
- **Deploy hangs:** `sc.exe start` blocks on Node service. Fix: `async`+`poll` timeouts on all `win_shell` tasks.

---

## Current State

### Validated Deploy (2026-03-20)

Full destroy → recreate → deploy with `-newrelic`: `ok=104 changed=65 failed=0`

All endpoints 200. OTEL export confirmed (`OtlpMetricExporter.Export succeeded`). NR entities visible for all services.

### Current VM
- **IP:** `20.238.64.21`
- **Azure region:** North Europe
- **NR region:** US (`otlp.nr-data.net`)
- **Size:** Standard_B2s (2 vCPU, 4GB RAM)

---

## Deploy Commands

```bash
# Full deploy with New Relic (~45-60 min fresh, ~10 min incremental)
./scripts/deploy-navalansible.sh -newrelic

# Full deploy without New Relic
./scripts/deploy-navalansible.sh

# NR instrumentation only (skip site deploy, ~5 min)
./scripts/deploy-navalansible.sh -newrelic-only

# Quick Go binary hot-swap (~2 min)
./scripts/deploy-navalansible.sh -go-only

# Skip service recreation (faster for code-only changes)
./scripts/deploy-navalansible.sh -skip-services

# Destroy and recreate VM
cd terraform-navalansible && terraform destroy -auto-approve && terraform apply -auto-approve
```

---

## File Map

### Application Code
| Path | Description |
|---|---|
| `NavalArchive.Api/Program.cs` | App startup, middleware, /health, /api/health, /api/trace, /api/checkout/pay |
| `NavalArchive.Api/Middleware/SessionGateMiddleware.cs` | Session gate, IP blocklist, localhost bypass |
| `NavalArchive.Web/server.js` | Node.js: API proxy, EJS rendering, image proxy |
| `NavalArchive.CardService/Program.cs` | Card issuance and validation |
| `NavalArchive.CartService/Program.cs` | Shopping cart per card ID |
| `NavalArchive.ImagePopulator/` | Java: Wikipedia image fetcher |
| `NavalArchive.Gateway/` → `NavalArchive.Notification/` | 10-service trace chain |

### Ansible Tasks (execution order)
| Task file | Purpose | Timeout |
|---|---|---|
| `runtime.yml` | Install .NET 8, Node.js, Java 17, Git, IIS | WinRM default |
| `otel.yml` | Install OTEL .NET auto-instrumentation v1.9.0 | WinRM default |
| `stop_services.yml` | Stop all services, kill processes | async: 60–120 |
| `clone.yml` | Git clone/fetch, change detection | WinRM default |
| `build.yml` | dotnet publish, npm install, copy binaries | WinRM 1200s |
| `deploy.yml` | Deploy web.config templates | WinRM default |
| `iis.yml` | IIS proxy, sites, pools, certs, permissions | async: 30–300 |
| `services.yml` | Create services + scheduled tasks, OTEL env vars | async: 60–120 |
| `populate.yml` | Start IIS, wait for API, run ImagePopulator | retries: 36 |
| `firewall.yml` | Windows Firewall rules | WinRM default |
| `scheduled_task.yml` | Node startup, health check, copy scripts | async: 15–30 |
| `verify.yml` | /health, /api/health (DB), /api/ships, Node, ImagePopulator | retries: 6–12 |

### Scripts
| Script | Use |
|---|---|
| `scripts/deploy-navalansible.sh` | Main deploy (-newrelic, -go-only, -skip-services, -newrelic-only) |
| `scripts/check-endpoints.sh` | Quick validation from outside |
| `scripts/test-all-endpoints.sh` | Full regression test with session |
| `scripts/diagnose-endpoints.ps1` | On-VM: services, IIS, endpoints, ports, logs |
| `scripts/health-check-all.ps1` | Scheduled: auto-restart failed services (every 1 min) |
| `scripts/start-all-services.ps1` | Start everything (boot task) |
