# Naval Archive — Deployment Guide: Azure, IIS & Windows

This guide covers deploying the WW2 Naval History Archive to **Windows Server with IIS** and **Microsoft Azure**.

---

## Automated Deployment (Terraform + Puppet)

**For zero-config setup**, use Terraform and Puppet:

```bash
cd terraform
terraform init && terraform apply
./scripts/deploy.sh $(terraform output -raw resource_group) $(terraform output -raw api_app_name) $(terraform output -raw web_app_name)
```

See **[../terraform/README.md](../terraform/README.md)** for full instructions.

---

---

## Prerequisites

- **Windows Server 2019/2022** or **Windows 10/11** (for local IIS)
- **.NET 8 SDK** (for build) and **.NET 8 Runtime** (for run)
- **Node.js 18+** (LTS)
- **IIS** with ASP.NET Core Module and URL Rewrite
- **Azure subscription** (for cloud deployment)

---

## Part 1: Windows + IIS Deployment

### Step 1.1 — Install Required Software

1. **Install .NET 8 Runtime (Hosting Bundle)**  
   - Download: https://dotnet.microsoft.com/download/dotnet/8.0  
   - Choose **Hosting Bundle** (includes ASP.NET Core Module for IIS)

2. **Install Node.js LTS**  
   - Download: https://nodejs.org/  
   - Add to PATH during installation

3. **Enable IIS and Required Features**
   ```powershell
   # Run PowerShell as Administrator
   Install-WindowsFeature -Name Web-Server, Web-Asp-Net45, Web-WebSockets
   ```

4. **Install ASP.NET Core Hosting Bundle** (if not done above)  
   - Restart IIS after install: `iisreset`

5. **Install URL Rewrite Module** (for reverse proxy)  
   - Download: https://www.iis.net/downloads/microsoft/url-rewrite

---

### Step 1.2 — Publish the .NET API

```powershell
cd NavalArchive.Api
dotnet publish -c Release -o C:\inetpub\navalarchive-api
```

Ensure `logs.db` can be created. The app writes to the current directory; either:
- Run the app pool from `C:\inetpub\navalarchive-api`, or
- Set `ASPNETCORE_CONTENTROOT` to that path

---

### Step 1.3 — Create IIS Site for the API

1. Open **IIS Manager** → **Sites** → **Add Website**
2. **Site name:** `NavalArchive-API`
3. **Physical path:** `C:\inetpub\navalarchive-api`
4. **Binding:** `http`, port `5000` (or any port, e.g. `8080`)
5. **Application Pool:**
   - Create new pool: **NavalArchive-API**
   - **.NET CLR version:** No Managed Code
   - **Identity:** ApplicationPoolIdentity (or a dedicated user)
6. Ensure the **ASP.NET Core Module** is configured (it should auto-detect `NavalArchive.Api.dll`)

---

### Step 1.4 — Set Up the Node.js Web App

**Option A — Run Node as a Windows Service (recommended)**

1. Install **PM2** globally:
   ```powershell
   npm install -g pm2
   npm install -g pm2-windows-startup
   pm2-startup install
   ```

2. Build and run the web app:
   ```powershell
   cd NavalArchive.Web
   npm install
   set API_URL=http://localhost:5000
   pm2 start server.js --name "navalarchive-web"
   pm2 save
   ```

3. Node will run on port 3000. Use IIS as a reverse proxy (see Step 1.5).

**Option B — Run Node via iisnode**

1. Install **iisnode**: https://github.com/Azure/iisnode/releases  
2. Create `web.config` in `NavalArchive.Web`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <handlers>
      <add name="iisnode" path="server.js" verb="*" modules="iisnode" />
    </handlers>
    <rewrite>
      <rules>
        <rule name="Node">
          <match url="(.*)" />
          <action type="Rewrite" url="server.js" />
        </rule>
      </rules>
    </rewrite>
    <iisnode nodeProcessCommandLine="C:\Program Files\nodejs\node.exe" />
  </system.webServer>
</configuration>
```

3. Create an IIS site pointing to `NavalArchive.Web` (no reverse proxy needed).

---

### Step 1.5 — Create IIS Site for the Web (Reverse Proxy)

The Node app serves all pages and calls the .NET API internally. Use IIS as the main entry point and proxy to Node:

1. Create site **NavalArchive-Web** on port **80** (or 443 for HTTPS)
2. Physical path: `C:\inetpub\navalarchive-web` (copy `NavalArchive.Web` contents there)
3. Add `web.config` for URL Rewrite to proxy all traffic to Node:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="Node Proxy" stopProcessing="true">
          <match url="(.*)" />
          <action type="Rewrite" url="http://localhost:3000/{R:1}" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
```

4. Install **Application Request Routing (ARR)** for the proxy:
   - https://www.iis.net/downloads/microsoft/application-request-routing
   - Enable "Proxy" in ARR settings

---

### Step 1.6 — Environment Variables

**API (Application Pool or `appsettings.json`):**
- `ASPNETCORE_ENVIRONMENT=Production`
- `ASPNETCORE_URLS=http://localhost:5000` (or the port your API uses)

**Node (PM2 or system env):**
- `API_URL=http://localhost:5000` (or `http://your-server:5000`)
- `PORT=3000`

---

## Part 2: Azure Deployment

### Option A — Azure App Service (Managed)

#### 2A.1 — Deploy the .NET API

1. In Azure Portal: **Create App Service** → **Web App**
2. **Runtime:** .NET 8 (Linux or Windows)
3. **Publish:** Code (from GitHub/Azure DevOps) or **Zip Deploy**

   ```powershell
   cd NavalArchive.Api
   dotnet publish -c Release -o ./publish
   Compress-Archive -Path ./publish/* -DestinationPath api.zip
   # Upload api.zip via Azure Portal → Deployment Center → Zip Deploy
   ```

4. **Configuration:**
   - `ASPNETCORE_ENVIRONMENT=Production`
   - Ensure the app runs on the port assigned by Azure (usually `80` or `8080`)

5. **SQLite:** App Service has read/write storage; `logs.db` will work in the default directory.

#### 2A.2 — Deploy the Node.js Web App

1. Create a second **App Service** — **Web App**
2. **Runtime:** Node 18 LTS (or 20)
3. **Publish:** Code or Zip Deploy

   ```powershell
   cd NavalArchive.Web
   npm install --production
   Compress-Archive -Path * -DestinationPath web.zip
   # Upload via Deployment Center
   ```

4. **Configuration:**
   - `API_URL=https://your-api-app.azurewebsites.net`
   - `PORT=8080` (or the port Azure expects for Node — check Azure docs)
   - Set **Startup Command:** `node server.js` or `npm start`

5. **Web.config** (for Node on Azure App Service):

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <system.webServer>
       <handlers>
         <add name="iisnode" path="server.js" verb="*" modules="iisnode" />
       </handlers>
       <rewrite>
         <rules>
           <rule name="Node">
             <match url="(.*)" />
             <action type="Rewrite" url="server.js" />
           </rule>
         </rules>
       </rewrite>
     </system.webServer>
   </configuration>
   ```

   Azure App Service for Node often uses a built-in handler; check the default runtime for your stack.

---

### Option B — Azure VM with IIS

1. Create a **Windows Server** VM in Azure.
2. Follow **Part 1** (Windows + IIS) on that VM.
3. Open **NSG** (Network Security Group) ports: **80**, **443**, **5000**, **3000** (or only 80/443 if using reverse proxy).
4. Configure a **Public IP** or **Load Balancer** for the VM.

---

### Option C — Azure Container Apps

1. **Dockerfile for API** (create in `NavalArchive.Api/`):

   ```dockerfile
   FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
   WORKDIR /app
   COPY publish/ .
   ENTRYPOINT ["dotnet", "NavalArchive.Api.dll"]
   ```

2. **Dockerfile for Web** (create in `NavalArchive.Web/`):

   ```dockerfile
   FROM node:18-alpine
   WORKDIR /app
   COPY package*.json ./
   RUN npm ci --only=production
   COPY . .
   ENV PORT=8080
   EXPOSE 8080
   CMD ["node", "server.js"]
   ```

3. Build and push to **Azure Container Registry**.
4. Create **Container Apps** for API and Web; set `API_URL` for the web app to the API URL.

---

## Part 3: Quick Reference

### Ports

| Service   | Default Port | IIS/Azure Notes                    |
|-----------|--------------|------------------------------------|
| .NET API  | 5000         | Use any port; bind in IIS          |
| Node Web  | 3000         | Use 8080 or 80 for Azure/Node      |

### Environment Variables

| Variable              | Value                          | Used By |
|-----------------------|--------------------------------|---------|
| `API_URL`             | `http://localhost:5000`       | Node    |
| `PORT`                | `3000` or `8080`              | Node    |
| `ASPNETCORE_ENVIRONMENT` | `Production`               | API     |

### File Paths (Windows IIS Example)

| Component | Path                               |
|-----------|------------------------------------|
| API       | `C:\inetpub\navalarchive-api`      |
| Web       | `C:\inetpub\navalarchive-web`      |
| Logs DB   | `NavalArchive.Api\logs.db` (in app dir) |

---

## Troubleshooting

1. **API 500 / Module not found**
   - Ensure .NET 8 Hosting Bundle is installed.
   - Restart IIS: `iisreset`.

2. **Node not responding**
   - Check PM2: `pm2 status`, `pm2 logs navalarchive-web`.
   - Ensure `API_URL` and `PORT` are set correctly.

3. **SQLite / logs.db**
   - Ensure the app has write access to the directory.
   - On Azure App Service, use the default directory or configure a persistent path.

4. **CORS**
   - The API already allows all origins. For production, restrict in `Program.cs` if needed.
