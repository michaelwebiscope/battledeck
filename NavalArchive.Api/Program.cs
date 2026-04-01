using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Data;
using NavalArchive.Api.Services;
using NavalArchive.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

static string RedactConnectionString(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return "(empty)";
    var pairs = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Server", "Data Source", "Port", "Database", "Initial Catalog"
    };
    var filtered = new List<string>();
    foreach (var p in pairs)
    {
        var idx = p.IndexOf('=');
        if (idx <= 0) continue;
        var key = p[..idx].Trim();
        var value = p[(idx + 1)..].Trim();
        if (allowed.Contains(key) && value.Length > 0)
            filtered.Add($"{key}={value}");
    }
    return filtered.Count == 0 ? "(redacted)" : string.Join(';', filtered);
}

static string RedactRedisConfiguration(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return "(empty)";
    var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Trim())
        .Where(p => !p.StartsWith("password=", StringComparison.OrdinalIgnoreCase) &&
                    !p.StartsWith("user=", StringComparison.OrdinalIgnoreCase) &&
                    !p.StartsWith("username=", StringComparison.OrdinalIgnoreCase))
        .ToList();
    return parts.Count == 0 ? "(redacted)" : string.Join(',', parts);
}

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Session/cache: Redis when configured, otherwise in-memory.
var redisConfiguration = builder.Configuration["Redis:Configuration"];
var redisInstanceName = builder.Configuration["Redis:InstanceName"] ?? "navalarchive:";
if (!string.IsNullOrWhiteSpace(redisConfiguration))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConfiguration;
        options.InstanceName = redisInstanceName;
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.IsEssential = true;
});

// Rate limiting: per-session (or per-IP when no session)
var rateLimitPermit = builder.Configuration.GetValue<int>("RateLimit:PermitLimit");
var rateLimitWindow = TimeSpan.FromSeconds(builder.Configuration.GetValue<int>("RateLimit:WindowSeconds"));
if (rateLimitPermit <= 0) rateLimitPermit = 200;
if (rateLimitWindow.TotalSeconds <= 0) rateLimitWindow = TimeSpan.FromMinutes(1);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var path = context.Request.Path.Value ?? "";
        // Admin sync/populate: long-running, manual - exempt from rate limit to avoid 429 during populate
        if (path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/images/populate", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("admin");
        }
        // Localhost (dev): exempt from rate limit when all traffic comes through proxy from 127.0.0.1
        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (ip is "127.0.0.1" or "::1" or "localhost")
        {
            return RateLimitPartition.GetNoLimiter("localhost");
        }
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        var cookieName = config["SessionGate:SessionCookieName"] ?? ".AspNetCore.Session";
        var partitionKey = context.Request.Cookies.TryGetValue(cookieName, out var sid) && !string.IsNullOrEmpty(sid)
            ? sid
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPermit,
            Window = rateLimitWindow
        });
    });
});

var mainConn = builder.Configuration.GetConnectionString("NavalArchiveDb") ?? builder.Configuration["ConnectionStrings:NavalArchiveDb"];
var provider = builder.Configuration["DatabaseProvider"] ?? "";
if (!string.IsNullOrEmpty(mainConn) && (
        provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)))
    builder.Services.AddDbContext<NavalArchiveDbContext>(o => o.UseNpgsql(mainConn));
else if (!string.IsNullOrEmpty(mainConn) && (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) || mainConn.Contains("Trusted_Connection", StringComparison.OrdinalIgnoreCase) || mainConn.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase)))
    builder.Services.AddDbContext<NavalArchiveDbContext>(o => o.UseSqlServer(mainConn));
else if (!string.IsNullOrEmpty(mainConn))
    builder.Services.AddDbContext<NavalArchiveDbContext>(o => o.UseSqlite(mainConn));
else
    builder.Services.AddDbContext<NavalArchiveDbContext>(o => o.UseSqlite("Data Source=navalarchive.db"));

var isPostgres = provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) || provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase);
var logsConn = builder.Configuration.GetConnectionString("LogsDb")
    ?? (isPostgres ? mainConn : "Data Source=logs.db");
if (isPostgres)
    builder.Services.AddDbContext<LogsDbContext>(options => options.UseNpgsql(logsConn));
else
    builder.Services.AddDbContext<LogsDbContext>(options => options.UseSqlite(logsConn ?? "Data Source=logs.db"));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CacheInvalidationService>();
builder.Services.AddSingleton<DynamicListDiagnosticsService>();
builder.Services.AddSingleton<LogsDataService>();
builder.Services.AddSingleton<GenuineLogsFetcher>();
builder.Services.AddSingleton<WikipediaDataFetcher>();
builder.Services.AddSingleton<ImageSearchService>();
builder.Services.AddScoped<ImageStorageService>();
builder.Services.AddScoped<DynamicListService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Forwarded headers when behind IIS/ARR (X-Forwarded-For, X-Forwarded-Proto)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownNetworks = { },
    KnownProxies = { }
});

// Session gate: blocklist, require session for API, rate limit
app.UseRouting();
app.UseSession();
app.UseMiddleware<SessionGateMiddleware>();
app.UseRateLimiter();

// Reverse proxy: frontend only reachable through API. Non-API routes proxy to Web (localhost).
var webUrl = app.Configuration["WebService:Url"] ?? "http://127.0.0.1:3000";
var proxyEnabled = app.Configuration.GetValue<bool>("ApiAsGateway");
app.Use(async (context, next) =>
{
    if (!proxyEnabled)
    {
        await next();
        return;
    }
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/trace", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    try
    {
        var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
        var url = $"{webUrl}{path}{context.Request.QueryString}";
        using var req = new HttpRequestMessage(new HttpMethod(context.Request.Method), url);
        foreach (var h in context.Request.Headers.Where(x => !string.Equals(x.Key, "Host", StringComparison.OrdinalIgnoreCase)))
            req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        if (context.Request.ContentLength > 0 && context.Request.Body.CanRead)
        {
            req.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType != null)
                req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }
        var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        // Create session and add cookie BEFORE writing response (headers must be set before body)
        await context.Session.CommitAsync(context.RequestAborted);
        if (context.Session.IsAvailable && !string.IsNullOrEmpty(context.Session.Id))
        {
            var cookieName = context.RequestServices.GetRequiredService<IConfiguration>()["SessionGate:SessionCookieName"] ?? ".AspNetCore.Session";
            context.Response.Cookies.Append(cookieName, context.Session.Id, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                IsEssential = true,
                Path = "/"
            });
        }
        context.Response.StatusCode = (int)res.StatusCode;
        foreach (var h in res.Headers)
            if (!string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in res.Content.Headers)
            if (!string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers[h.Key] = h.Value.ToArray();
        await res.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 502;
        await context.Response.WriteAsJsonAsync(new { error = "Frontend unavailable", message = ex.Message });
    }
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NavalArchiveDbContext>();
    var logsDb = scope.ServiceProvider.GetRequiredService<LogsDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("StartupBootstrap");
    startupLogger.LogInformation(
        "Connection targets: provider={Provider}, mainDb={MainDb}, logsDb={LogsDb}, redis={Redis}",
        provider,
        RedactConnectionString(mainConn),
        RedactConnectionString(logsConn),
        RedactRedisConfiguration(redisConfiguration)
    );
    db.Database.EnsureCreated();
    // EnsureCreated returns false (no-op) if the DB already exists (created above by main context).
    // In that case, call CreateTables() directly so LogsDbContext tables are still created.
    if (!logsDb.Database.EnsureCreated())
    {
        // DB already exists (created by main context) — create any missing tables for LogsDbContext.
        // Use raw SQL so we don't depend on EF infrastructure internals.
        try { logsDb.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""CaptainLogs"" (""Id"" serial PRIMARY KEY, ""ShipName"" text NOT NULL, ""LogDate"" text NOT NULL, ""Entry"" text NOT NULL, ""Source"" text NOT NULL)"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_CaptainLogs_ShipName"" ON ""CaptainLogs"" (""ShipName"")"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_CaptainLogs_LogDate"" ON ""CaptainLogs"" (""LogDate"")"); } catch { }
    }
    var indexBootstrapProvider = "unknown";
    // Add VideoUrl column if missing (existing SQLite DBs)
    if (db.Database.IsSqlite())
    {
        indexBootstrapProvider = "sqlite";
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Ships ADD COLUMN VideoUrl TEXT"); } catch { /* column exists */ }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Ships ADD COLUMN ImageData BLOB"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Ships ADD COLUMN ImageContentType TEXT"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Ships ADD COLUMN ImageVersion INTEGER DEFAULT 0"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Captains ADD COLUMN ImageData BLOB"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Captains ADD COLUMN ImageContentType TEXT"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Captains ADD COLUMN ImageVersion INTEGER DEFAULT 0"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Ships ADD COLUMN ImageManuallySet INTEGER DEFAULT 0"); } catch { }
        try { db.Database.ExecuteSqlRaw("ALTER TABLE Captains ADD COLUMN ImageManuallySet INTEGER DEFAULT 0"); } catch { }
        // Indexes for fast queries at scale (SQLite)
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Ships_ClassId ON Ships(ClassId)"); } catch { }
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Ships_CaptainId ON Ships(CaptainId)"); } catch { }
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Ships_YearCommissioned ON Ships(YearCommissioned)"); } catch { }
        try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Ships_Name ON Ships(Name)"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_CaptainLogs_LogDate ON CaptainLogs(LogDate)"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_CaptainLogs_ShipName ON CaptainLogs(ShipName)"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_CaptainLogs_Source ON CaptainLogs(Source)"); } catch { }
    }
    else if (db.Database.IsNpgsql())
    {
        indexBootstrapProvider = "postgres";
        // Indexes for fast queries at scale (PostgreSQL)
        try { db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Ships_ClassId"" ON ""Ships"" (""ClassId"")"); } catch { }
        try { db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Ships_CaptainId"" ON ""Ships"" (""CaptainId"")"); } catch { }
        try { db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Ships_YearCommissioned"" ON ""Ships"" (""YearCommissioned"")"); } catch { }
        try { db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_Ships_Name"" ON ""Ships"" (""Name"")"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_CaptainLogs_LogDate"" ON ""CaptainLogs"" (""LogDate"")"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_CaptainLogs_ShipName"" ON ""CaptainLogs"" (""ShipName"")"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_CaptainLogs_Source"" ON ""CaptainLogs"" (""Source"")"); } catch { }
    }
    else if (db.Database.IsSqlServer())
    {
        indexBootstrapProvider = "sqlserver";
        // Indexes for fast queries at scale (SQL Server)
        try { db.Database.ExecuteSqlRaw(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Ships_ClassId' AND object_id = OBJECT_ID('Ships')) CREATE INDEX IX_Ships_ClassId ON Ships(ClassId)"); } catch { }
        try { db.Database.ExecuteSqlRaw(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Ships_CaptainId' AND object_id = OBJECT_ID('Ships')) CREATE INDEX IX_Ships_CaptainId ON Ships(CaptainId)"); } catch { }
        try { db.Database.ExecuteSqlRaw(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Ships_YearCommissioned' AND object_id = OBJECT_ID('Ships')) CREATE INDEX IX_Ships_YearCommissioned ON Ships(YearCommissioned)"); } catch { }
        try { db.Database.ExecuteSqlRaw(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Ships_Name' AND object_id = OBJECT_ID('Ships')) CREATE INDEX IX_Ships_Name ON Ships(Name)"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CaptainLogs_LogDate' AND object_id = OBJECT_ID('CaptainLogs')) CREATE INDEX IX_CaptainLogs_LogDate ON CaptainLogs(LogDate)"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CaptainLogs_ShipName' AND object_id = OBJECT_ID('CaptainLogs')) CREATE INDEX IX_CaptainLogs_ShipName ON CaptainLogs(ShipName)"); } catch { }
        try { logsDb.Database.ExecuteSqlRaw(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CaptainLogs_Source' AND object_id = OBJECT_ID('CaptainLogs')) CREATE INDEX IX_CaptainLogs_Source ON CaptainLogs(Source)"); } catch { }
    }

    startupLogger.LogInformation(
        "Index bootstrap completed. provider={Provider}, dynamicListsDbQueryMode={DbMode}, redisEnabled={RedisEnabled}",
        indexBootstrapProvider,
        app.Configuration.GetValue<bool>("DynamicLists:UseDatabaseQueryMode"),
        !string.IsNullOrWhiteSpace(app.Configuration["Redis:Configuration"])
    );
    // ImageSources table (entity for image search sources)
    try
    {
        if (db.Database.IsSqlite())
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ImageSources (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SourceId TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    ProviderType TEXT NOT NULL,
                    RetryCount INTEGER DEFAULT 2,
                    SortOrder INTEGER DEFAULT 0,
                    Enabled INTEGER DEFAULT 1,
                    AuthKeyRef TEXT,
                    CustomConfigJson TEXT
                )");
        }
        else if (db.Database.IsSqlServer())
        {
            db.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ImageSources')
                CREATE TABLE ImageSources (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    SourceId NVARCHAR(64) NOT NULL,
                    Name NVARCHAR(128) NOT NULL,
                    ProviderType NVARCHAR(32) NOT NULL,
                    RetryCount INT DEFAULT 2,
                    SortOrder INT DEFAULT 0,
                    Enabled BIT DEFAULT 1,
                    AuthKeyRef NVARCHAR(64),
                    CustomConfigJson NVARCHAR(MAX)
                )");
        }
        else if (db.Database.IsNpgsql())
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""ImageSources"" (
                    ""Id"" INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    ""SourceId"" TEXT NOT NULL,
                    ""Name"" TEXT NOT NULL,
                    ""ProviderType"" TEXT NOT NULL,
                    ""RetryCount"" INTEGER DEFAULT 2,
                    ""SortOrder"" INTEGER DEFAULT 0,
                    ""Enabled"" BOOLEAN DEFAULT TRUE,
                    ""AuthKeyRef"" TEXT,
                    ""CustomConfigJson"" TEXT
                )");
        }
        if (!db.ImageSources.Any())
        {
            foreach (var s in new[] {
                new NavalArchive.Data.Models.ImageSource { SourceId = "wikipedia", Name = "Wikipedia", ProviderType = "Wikipedia", RetryCount = 2, SortOrder = 0, Enabled = true },
                new NavalArchive.Data.Models.ImageSource { SourceId = "pexels", Name = "Pexels", ProviderType = "Pexels", RetryCount = 2, SortOrder = 1, AuthKeyRef = "PEXELS_API_KEY", Enabled = true },
                new NavalArchive.Data.Models.ImageSource { SourceId = "pixabay", Name = "Pixabay", ProviderType = "Pixabay", RetryCount = 2, SortOrder = 2, AuthKeyRef = "PIXABAY_API_KEY", Enabled = true },
                new NavalArchive.Data.Models.ImageSource { SourceId = "unsplash", Name = "Unsplash", ProviderType = "Unsplash", RetryCount = 2, SortOrder = 3, AuthKeyRef = "UNSPLASH_ACCESS_KEY", Enabled = true },
                new NavalArchive.Data.Models.ImageSource { SourceId = "google", Name = "Google", ProviderType = "Google", RetryCount = 1, SortOrder = 4, AuthKeyRef = "GOOGLE_API_KEY", Enabled = true },
            })
                db.ImageSources.Add(s);
            db.SaveChanges();
        }
    }
    catch { /* table exists or migration pending */ }
}

// Wikipedia sync and image populate are manual only (Admin > Image Audit > Populate) to save API calls.

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "api" }));

// /api/health: deeper check that verifies DB connectivity (used by verify.yml, Node proxy, external monitors)
app.MapGet("api/health", async (NavalArchiveDbContext db) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        var shipCount = canConnect ? await db.Ships.CountAsync() : -1;
        return Results.Ok(new { status = "ok", service = "api", db = canConnect ? "connected" : "unreachable", shipCount });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "error", service = "api", db = "error", error = ex.Message }, statusCode: 503);
    }
});

app.MapGet("api/dynamic-lists/diagnostics", (IConfiguration config, DynamicListDiagnosticsService diagnostics) =>
{
    var provider = config["DatabaseProvider"];
    if (string.IsNullOrWhiteSpace(provider))
    {
        var mainConn = config.GetConnectionString("NavalArchiveDb") ?? config["ConnectionStrings:NavalArchiveDb"] ?? "";
        provider = mainConn.Contains("Trusted_Connection", StringComparison.OrdinalIgnoreCase) ||
                   mainConn.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase)
            ? "SqlServer"
            : mainConn.Contains("Host=", StringComparison.OrdinalIgnoreCase)
                ? "Postgres"
                : "Sqlite";
    }

    var snapshot = diagnostics.Snapshot(
        dbQueryModeEnabled: config.GetValue<bool>("DynamicLists:UseDatabaseQueryMode"),
        redisEnabled: !string.IsNullOrWhiteSpace(config["Redis:Configuration"]),
        databaseProvider: provider
    );
    return Results.Ok(snapshot);
});

app.MapControllers();

// Trace page (served when /trace goes through API)
app.MapGet("trace", () => Results.Content(
    "<!DOCTYPE html><html><head><title>Distributed Trace</title><link href=\"https://cdn.jsdelivr.net/npm/bootstrap@5.3.2/dist/css/bootstrap.min.css\" rel=\"stylesheet\"></head><body class=\"p-4\">" +
    "<h1>Distributed Trace</h1><p class=\"text-muted\">10-service chain: Gateway → Auth → User → Catalog → Inventory → Basket → Order → Payment → Shipping → Notification</p>" +
    "<button id=\"run\" class=\"btn btn-primary mb-3\">Run Trace</button>" +
    "<div id=\"status\" class=\"mb-3\"></div>" +
    "<pre id=\"out\" class=\"bg-dark text-light p-3 rounded\" style=\"max-height:400px;overflow:auto\"></pre>" +
    "<script>" +
    "function flattenChain(obj,list){if(!obj)return list||[];list=list||[];list.push(obj.service||'?');" +
    "if(obj.next){try{var n=typeof obj.next==='string'?JSON.parse(obj.next):obj.next;return flattenChain(n,list);}catch(_){}}return list;}" +
    "document.getElementById('run').onclick=function(){var s=document.getElementById('status'),o=document.getElementById('out');" +
    "s.innerHTML='<span class=\"text-muted\">Running trace...</span>';o.textContent='';" +
    "fetch('/api/trace').then(function(r){if(!r.ok)return r.json().then(function(d){throw new Error(d.error||'Request failed')});return r.json();})" +
    ".then(function(d){if(d.error){s.innerHTML='<span class=\"text-danger\">'+d.error+'</span>';o.textContent=JSON.stringify(d,null,2);return;}" +
    "var c=flattenChain(d);s.innerHTML='<span class=\"text-success\">Trace complete: '+c.length+' services</span>';" +
    "o.textContent=c.join(' → ')+'\\n\\n'+JSON.stringify(d,null,2);})" +
    ".catch(function(e){s.innerHTML='<span class=\"text-danger\">Error: '+(e.message||'Request failed')+'</span>';o.textContent=e.message||'Request failed';});};" +
    "</script></body></html>", "text/html"));

// Trace chain proxy: API -> Gateway (5010) -> Auth -> User -> ... -> Notification
app.MapGet("api/trace", async (IHttpClientFactory http, IConfiguration config) =>
{
    var gatewayUrl = config["Gateway:Url"] ?? "http://localhost:5010";
    var client = http.CreateClient();
    var res = await client.GetAsync($"{gatewayUrl}/trace");
    var body = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        return Results.Json(new { error = "Trace chain unavailable" }, statusCode: 502);
    return Results.Content(body, "application/json");
});

// Video streaming proxy: API -> Video service (5020). All frontend traffic goes through API.
app.MapGet("api/videos/{shipId}", async (string shipId, HttpContext ctx, IHttpClientFactory http, IConfiguration config) =>
{
    var videoUrl = config["VideoService:Url"] ?? "http://localhost:5020";
    var client = http.CreateClient();
    var req = new HttpRequestMessage(HttpMethod.Get, $"{videoUrl.TrimEnd('/')}/api/videos/{shipId}");
    if (ctx.Request.Headers.TryGetValue("Range", out var range))
        req.Headers.TryAddWithoutValidation("Range", (string?)range);
    var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    ctx.Response.StatusCode = (int)res.StatusCode;
    foreach (var h in res.Headers)
        ctx.Response.Headers[h.Key] = h.Value.ToArray();
    foreach (var h in res.Content.Headers)
        ctx.Response.Headers[h.Key] = h.Value.ToArray();
    await res.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

// Idempotency: prevent same transaction from being paid multiple times
var idempotencyCache = new System.Collections.Concurrent.ConcurrentDictionary<string, (object Result, DateTime Expires)>();
const int IdempotencyWindowMinutes = 10;

// Payment service returns camelCase (e.g. "approved"); deserialize case-insensitively
var paymentJsonOptions = new System.Text.Json.JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

// Minimal API for checkout (bypasses controller routing that 404s on POST under IIS)
app.MapPost("api/checkout/pay", async (HttpContext ctx, IHttpClientFactory http, IConfiguration config) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<CheckoutPayload>();
    if (string.IsNullOrWhiteSpace(req?.CardId))
        return Results.BadRequest(new { error = "CardId required" });

    var cardId = req.CardId!.Trim();
    var cardUrl = config["CardService:Url"] ?? "http://localhost:5002";
    var cartUrl = config["CartService:Url"] ?? "http://localhost:5003";
    var paymentUrl = config["PaymentService:Url"] ?? "http://localhost:5001";
    var client = http.CreateClient();
    var payFromAccount = !string.IsNullOrWhiteSpace(req.ApiKey);

    // Name required only when paying with card (not when paying from account)
    if (!payFromAccount && string.IsNullOrWhiteSpace(req?.Name))
        return Results.BadRequest(new { error = "Name required for card checkout" });

    // Idempotency: if we already successfully processed this key, return cached result (no re-charge)
    var idempotencyKey = req.IdempotencyKey?.Trim();
    if (!string.IsNullOrEmpty(idempotencyKey) && idempotencyCache.TryGetValue(idempotencyKey, out var cached))
    {
        if (cached.Expires > DateTime.UtcNow)
            return Results.Json(cached.Result);
        idempotencyCache.TryRemove(idempotencyKey, out _);
    }

    decimal amount = req.Amount ?? 0;
    if (amount <= 0)
    {
        var totalRes = await client.GetAsync($"{cartUrl}/api/cart/total/{cardId}?isMember=true");
        if (totalRes.IsSuccessStatusCode)
        {
            var totalData = await totalRes.Content.ReadFromJsonAsync<CartTotalPayload>();
            amount = totalData?.Total ?? 0;
        }
    }
    if (amount <= 0)
        return Results.BadRequest(new { error = "No cart total and no amount provided" });

    bool approved;
    string? transactionId = null;

    if (payFromAccount)
    {
        // Pay from account balance: call payment service with X-API-Key (no cardId → deducts balance)
        using var payReq = new HttpRequestMessage(HttpMethod.Post, $"{paymentUrl}/api/payment/simulate");
        payReq.Headers.TryAddWithoutValidation("X-API-Key", req.ApiKey!.Trim());
        payReq.Content = JsonContent.Create(new
        {
            amount,
            currency = req.Currency ?? "USD",
            description = req.Description ?? "Museum checkout"
        });
        var payRes = await client.SendAsync(payReq);
        if (payRes.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            return Results.Json(new { error = "Insufficient funds in your account balance", code = "insufficient_funds" }, statusCode: 402);
        if (!payRes.IsSuccessStatusCode)
            return Results.Json(new { error = "Payment service unavailable" }, statusCode: 502);
        var payData = await payRes.Content.ReadFromJsonAsync<PaymentPayload>(paymentJsonOptions);
        approved = payData?.Approved ?? false;
        transactionId = payData?.TransactionId;
    }
    else
    {
        // Card checkout: validate card then charge via payment service with cardId
        var validateRes = await client.PostAsJsonAsync($"{cardUrl}/api/card/validate-with-name", new { cardId, name = req!.Name });
        if (!validateRes.IsSuccessStatusCode)
            return Results.Json(new { error = await validateRes.Content.ReadAsStringAsync() }, statusCode: (int)validateRes.StatusCode);

        var validateData = await validateRes.Content.ReadFromJsonAsync<ValidatePayload>();
        if (validateData?.Valid != true)
            return Results.Ok(new { approved = false, message = validateData?.Message ?? "Card validation failed" });

        var payRes = await client.PostAsJsonAsync($"{paymentUrl}/api/payment/simulate", new
        {
            amount,
            currency = req.Currency ?? "USD",
            description = req.Description ?? "Museum checkout",
            cardId
        });
        if (!payRes.IsSuccessStatusCode)
            return Results.Json(new { error = "Payment service unavailable" }, statusCode: 502);

        var payData = await payRes.Content.ReadFromJsonAsync<PaymentPayload>(paymentJsonOptions);
        approved = payData?.Approved ?? false;
        transactionId = payData?.TransactionId;
    }

    var result = new
    {
        approved,
        transactionId,
        amount,
        message = approved ? "Payment approved" : "Payment declined"
    };

    if (approved)
    {
        await client.PostAsync($"{cartUrl}/api/cart/clear/{Uri.EscapeDataString(cardId)}", null);
        if (!string.IsNullOrEmpty(idempotencyKey))
            idempotencyCache[idempotencyKey] = (result, DateTime.UtcNow.AddMinutes(IdempotencyWindowMinutes));
    }

    return Results.Ok(result);
});

app.Run();

record CheckoutPayload(string? CardId, string? Name, decimal? Amount, string? Currency, string? Description, string? IdempotencyKey, string? ApiKey);
record ValidatePayload(bool Valid, string? Message);
record CartTotalPayload(decimal Total);
record PaymentPayload(bool Approved, string? TransactionId);
