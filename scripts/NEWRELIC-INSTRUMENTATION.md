# New Relic Instrumentation for 56+ Windows Services

## Dynatrace vs New Relic (verified)

| Capability | Dynatrace OneAgent | New Relic |
|------------|--------------------|-----------|
| **Install once** | ✅ One agent per host | ❌ Per-app agent (or Infra only) |
| **Auto-inject into processes** | ✅ Yes, no code changes | ❌ No; agent must be in app |
| **Zero-touch APM** | ✅ Yes | ❌ Requires agent in each app |
| **Registry env vars** | Not needed | ✅ This script sets them |
| **Host + process metrics** | ✅ Yes | ✅ Infra Agent |

**Sources:**
- Dynatrace: [How OneAgent works](https://docs.dynatrace.com/docs/platform/oneagent/how-one-agent-works) – "OneAgent can perform more detailed monitoring... by **injecting itself into those processes**" (Java, Node.js, .NET)
- New Relic: [Infra intro](https://docs.newrelic.com/docs/infrastructure/introduction-infra-monitoring/) – Infra Agent = host + process metrics; APM = per-app
- Registry: [Stack Overflow](https://stackoverflow.com/questions/31414578/set-environment-variable-for-windows-service) – `HKLM\...\Services\SERVICE_NAME` + `Environment` REG_MULTI_SZ; **restart service** (not full reboot) for changes

**Bottom line:** New Relic cannot match Dynatrace's zero-touch APM. Options:
1. **New Relic Infra only** – host metrics, no APM (closest to "install once")
2. **New Relic APM + this script** – agent in each app + auto env vars via registry
3. **Dynatrace OneAgent** – true zero-touch if you switch vendors

---

## Scripts

### 0. `install-newrelic-dotnet-agent.ps1` – Full install on a system that had Dynatrace

Removes Dynatrace, installs New Relic .NET agent MSI, instruments .NET services.

```powershell
# Full run (remove Dynatrace, install agent, instrument services)
.\install-newrelic-dotnet-agent.ps1

# Dry run
.\install-newrelic-dotnet-agent.ps1 -DryRun

# Skip Dynatrace removal (already done)
.\install-newrelic-dotnet-agent.ps1 -SkipDynatraceRemoval

# Skip stopping IIS app pools
.\install-newrelic-dotnet-agent.ps1 -SkipIisStop
```

One-liner (download + run):
```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/michaelwebiscope/battledeck/main/scripts/install-newrelic-dotnet-agent.ps1" -OutFile C:\Temp\install-nr.ps1 -UseBasicParsing; powershell -NoProfile -ExecutionPolicy Bypass -File C:\Temp\install-nr.ps1
```

### 1. `discover-services.ps1` – See what will be instrumented

```powershell
.\discover-services.ps1
.\discover-services.ps1 -NamePattern "^NavalArchive"
.\discover-services.ps1 -ExportCsv services.csv
```

### 2. `newrelic-instrument-services.ps1` – Registry env vars (when agent is in app)

```powershell
# Dry run first
.\newrelic-instrument-services.ps1 -LicenseKey "YOUR_KEY" -DryRun

# Apply + restart services
.\newrelic-instrument-services.ps1 -LicenseKey "YOUR_KEY" -RestartServices

# Only our services
.\newrelic-instrument-services.ps1 -LicenseKey "YOUR_KEY" -NamePattern "^NavalArchive" -RestartServices
```

**Requires:** New Relic agent already in each app (NuGet for .NET, npm for Node). This script only sets `NEW_RELIC_APP_NAME` and `NEW_RELIC_LICENSE_KEY` per service. **Restart each service** after running for env vars to take effect (service restart is sufficient; full reboot not required).

---

## Alternatives (Dynatrace-like)

### A. New Relic Infrastructure Agent
- Install once per host
- Host + process metrics, no APM
- Add to bootstrap: download MSI, install, set license

### B. Dynatrace OneAgent
- One install, auto-injection
- No scripts needed for env vars

