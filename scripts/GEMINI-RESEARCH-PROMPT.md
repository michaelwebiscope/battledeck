# Gemini Research Prompt: New Relic Instrumentation for 56 Windows Services

Copy the text below into Gemini (research mode) to find solutions.

---

## Prompt

I need to instrument **56+ Windows services** with **New Relic APM** in a way that feels like Dynatrace OneAgent—minimal manual work, ideally automated. I do **not** want to use OpenTelemetry.

### Environment
- **OS:** Windows Server (Azure VM)
- **Services:** Registered via `sc.exe` (Windows Service Control Manager), not NSSM
- **Stack:** Mix of .NET 8 (self-contained .exe) and Node.js services
- **Deployment:** Terraform + bootstrap script; services are deployed as executables (e.g. `MyApp.Api.exe`) per customer requirements

### Current approach (what I have)
- A PowerShell script that auto-discovers services and adds registry env vars (`NEW_RELIC_APP_NAME`, `NEW_RELIC_LICENSE_KEY`) to `HKLM\SYSTEM\CurrentControlSet\Services\<ServiceName>\Environment`
- This works **only when** the New Relic agent is already embedded in each app (NuGet for .NET, npm for Node)
- I must add the agent to every project and redeploy—manual and error-prone at scale

### What I want (Dynatrace OneAgent–like)
- **Install once** per host, not per app
- **Zero or minimal code changes**—no adding NuGet/npm to 56 projects
- **Auto-discovery** of services—script or tool finds them and instruments
- **Full APM** (traces, transactions, errors), not just host metrics

### Constraints
- **Must use New Relic** (vendor lock-in)
- **No OpenTelemetry** (explicitly excluded)
- **No Dynatrace** (staying with New Relic)
- **Windows only** (sc.exe services)

### Questions to research
1. Does New Relic offer any **host-level or process-level auto-instrumentation** for .NET or Node.js on Windows—similar to how Dynatrace OneAgent injects into processes without code changes?
2. Are there **New Relic–specific** tools, integrations, or deployment patterns for bulk-instrumenting many Windows services?
3. Can the **New Relic .NET agent** be installed once (e.g. via profiler at machine level) and automatically attach to .NET processes, without adding the agent to each project?
4. Are there **third-party or community** solutions (scripts, wrappers, installers) that automate New Relic instrumentation across many Windows services?
5. What is the **least manual, most scalable** way to get New Relic APM on 56 sc.exe services today—even if it’s not true zero-touch?

Please search for recent documentation, blog posts, GitHub repos, and New Relic forum/community discussions. Cite sources and version numbers where relevant.

---

## Short version (if character limit)

**Research:** How to instrument 56+ Windows sc.exe services with New Relic APM with minimal manual work—like Dynatrace OneAgent. No OpenTelemetry, no Dynatrace. Mix of .NET 8 and Node.js. Need: install-once or bulk automation, zero/minimal code changes, full APM. Does New Relic support host-level .NET profiler? Any tools for bulk Windows service instrumentation?
