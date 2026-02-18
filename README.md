# WW2 Naval History Archive

A full-stack demonstration application with intentional architectural flaws for New Relic APM monitoring and diagnostics.

## Architecture

- **Backend:** .NET 8 Web API (`NavalArchive.Api`) — runs on port 5000
- **Frontend:** Node.js + Express + EJS (`NavalArchive.Web`) — runs on port 3000

---

## Setup

### 1. Backend (.NET 8)

```bash
# Create the Web API project (if not already created)
dotnet new webapi -n NavalArchive.Api -o NavalArchive.Api --no-https -f net8.0

# Add required packages
cd NavalArchive.Api
dotnet add package Microsoft.EntityFrameworkCore.InMemory --version 8.0.0
dotnet add package Swashbuckle.AspNetCore --version 6.5.0

# Run the API
dotnet run
```

The API will be available at **http://localhost:5000**. Swagger UI: **http://localhost:5000/swagger**.

### 2. Frontend (Node.js)

```bash
# Initialize and install dependencies
cd NavalArchive.Web
npm init -y
npm install express ejs axios

# Run the web server
npm start
```

The site will be available at **http://localhost:3000**.

### 3. Run Both

1. Start the backend first: `cd NavalArchive.Api && dotnet run`
2. In another terminal, start the frontend: `cd NavalArchive.Web && npm start`
3. Open **http://localhost:3000** in your browser

---

## Pages

| Page | Route | Description |
|------|-------|-------------|
| Home | `/` | Hero, featured ship, museum news |
| Fleet Roster | `/fleet` | Table of 50 ships (N+1 latency) |
| Photo Gallery | `/gallery` | Image grid; "View Full Size" triggers memory leak |
| Daily Logs | `/logs` | Search; "aaaaaaaaaaaaaaaaaaaaX" causes CPU spike |
| Live Battle | `/simulation` | "JOIN LIVE EXERCISE" — rapid clicks trigger race condition |

---

## Wikipedia Data Sync

Ship descriptions and images are fetched from **Wikipedia** on first run (or when cache is empty):

- **Automatic:** Runs in background ~2 seconds after startup
- **Cache:** Saved to `NavalArchive.Api/Data/fetched-ships.json` (delete to re-fetch)
- **Manual:** Click "Refresh from Wikipedia" on the Home page, or `POST /api/admin/sync`

**Captain's Logs** are also fetched from Wikipedia (Battle of Midway, Leyte Gulf, Bismarck, Pearl Harbor, etc.) and cached in `Data/captain-logs.json`.

---

## Intentional Flaws (for New Relic Demo)

1. **N+1 Query** — `GET /api/ships` loads Class/Captain per ship in a loop (no `.Include()`)
2. **Memory Leak** — `GET /api/images/{id}` caches 5MB per image in a static `Dictionary`, never cleared
3. **CPU Spike** — `POST /api/logs/search` uses catastrophic backtracking regex on malicious input
4. **Race Condition** — `POST /api/simulation/join` uses non–thread-safe `List` with `Thread.Sleep`, no lock

---

## Configuration

- **API URL:** Set `API_URL` env var to change backend (default: `http://localhost:5000`)
- **Frontend Port:** Set `PORT` env var (default: `3000`)

---

## Deployment

### Automated (Terraform + Puppet)

```bash
cd terraform && terraform init && terraform apply
./scripts/deploy.sh $(terraform output -raw resource_group) $(terraform output -raw api_app_name) $(terraform output -raw web_app_name)
```

See **[terraform/README.md](terraform/README.md)** for details.

### Deployment reference (Azure, IIS, Windows)

See **[docs/DEPLOYMENT-AZURE-IIS-WINDOWS.md](docs/DEPLOYMENT-AZURE-IIS-WINDOWS.md)** for deployment on:

- Windows Server with IIS
- Azure App Service
- Azure VM
