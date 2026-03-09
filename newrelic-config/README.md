# New Relic Configuration & Deploy

**All New Relic–related config and scripts live here.** The only external call is Terraform invoking `deploy-newrelic.ps1` when `newrelic_license_key` is set.

Runs as a **separate step** on deploy (after main web refresh). Verified against [New Relic Node.js install docs](https://docs.newrelic.com/docs/apm/agents/nodejs-agent/installation-configuration/install-nodejs-agent) and [.NET env vars](https://docs.newrelic.com/docs/apm/agents/net-agent/other-installation/understanding-net-agent-environment-variables).

## Contents

- `config/newrelic.js` – Node.js agent config (deployed to NavalArchive.Web)
- `config/newrelic-dotnet.config.template` – .NET app-local config template (app name per app)
- `scripts/copy-newrelic-for-web.js` – Copies newrelic.js to NavalArchive.Web for local dev
- `deploy-newrelic.ps1` – **Single entry point** (called by Terraform); config + Node startup + .NET configs + service instrumentation
- `newrelic-instrument-services.ps1` – Registry env vars for Windows services
- `install-newrelic-dotnet-agent.ps1` – Full .NET agent install (MSI + instrumentation)
- `instrument-dotnet-services-newrelic.ps1` – .NET service registry instrumentation
- `dynadog-destroyer.ps1` – Removes Dynatrace/Datadog (used by install script)
- `discover-services.ps1` – Discover services for instrumentation planning
- `NEWRELIC-INSTRUMENTATION.md` – Documentation
- `env.example` – Example environment variables

## What the deploy script does

1. **Copies newrelic.js** to `C:\inetpub\navalarchive-web` (per Node.js docs: config in app root)
2. **Installs newrelic** npm package if missing
3. **Updates start-web.cmd** to use `node -r newrelic server.js` when license key is set (required per [Node.js install docs](https://docs.newrelic.com/docs/apm/agents/nodejs-agent/installation-configuration/install-nodejs-agent))
4. **Deploys newrelic.config** to each .NET app folder (API, Payment, Card, Cart, Gateway, Auth, User, Catalog, Inventory, Basket, Order, PaymentChain, Shipping, Notification) with app-specific names – per [.NET naming docs](https://docs.newrelic.com/docs/apm/agents/net-agent/configuration/name-your-net-application)
5. **Instruments services** – sets registry env vars (`NEW_RELIC_APP_NAME`, `NEW_RELIC_LICENSE_KEY`) for NavalArchive Windows services

## Deploy Flow

1. **Main deploy** (`refresh-web.ps1`) – Web, API, ImagePopulator
2. **New Relic deploy** (`deploy-newrelic.ps1`) – Config + Node startup + service instrumentation

The New Relic step runs automatically when `terraform apply` is used and `newrelic_license_key` is set.

## Manual Run (on VM)

```powershell
# With license key
$env:NEW_RELIC_LICENSE_KEY = "your-key"
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/michaelwebiscope/battledeck/main/newrelic-config/deploy-newrelic.ps1" -OutFile C:\Temp\deploy-newrelic.ps1 -UseBasicParsing
powershell -ExecutionPolicy Bypass -File C:\Temp\deploy-newrelic.ps1 -RestartServices

# Or with param
.\deploy-newrelic.ps1 -LicenseKey "your-key" -RestartServices
```

## Requirements

- **Node.js**: `newrelic` npm package (script installs if missing). Startup must use `node -r newrelic server.js` (script updates start-web.cmd).
- **.NET**: New Relic .NET agent must be installed (MSI or NuGet). This script only sets `NEW_RELIC_APP_NAME` and `NEW_RELIC_LICENSE_KEY` in the service registry.

## Verification against New Relic docs

| Requirement | Docs | Our setup |
|-------------|------|-----------|
| npm install newrelic | [Node.js install](https://docs.newrelic.com/docs/apm/agents/nodejs-agent/installation-configuration/install-nodejs-agent) | deploy script installs if missing |
| newrelic.js in app root | Same | Copied to `C:\inetpub\navalarchive-web\newrelic.js` |
| app_name, license_key | Same | In config with env fallbacks |
| **node -r newrelic server.js** | Same: "Add -r newrelic to your app's startup script" | start-web.cmd updated by deploy script |
| .NET app name per app | [Name your .NET app](https://docs.newrelic.com/docs/apm/agents/net-agent/configuration/name-your-net-application) | App-local newrelic.config deployed to each .NET app folder |
| .NET env vars | [.NET env vars](https://docs.newrelic.com/docs/apm/agents/net-agent/other-installation/understanding-net-agent-environment-variables) | newrelic-instrument-services.ps1 sets NEW_RELIC_APP_NAME, NEW_RELIC_LICENSE_KEY |
| .NET profiler (COR_*, CORECLR_*) | Same | Must be installed via MSI/NuGet; script does not set these |
