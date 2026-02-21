using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;
using NavalArchive.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var mainConn = builder.Configuration.GetConnectionString("NavalArchiveDb") ?? builder.Configuration["ConnectionStrings:NavalArchiveDb"];
var provider = builder.Configuration["DatabaseProvider"] ?? "";
if (!string.IsNullOrEmpty(mainConn) && (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) || mainConn.Contains("Trusted_Connection", StringComparison.OrdinalIgnoreCase) || mainConn.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase)))
    builder.Services.AddDbContext<NavalArchiveDbContext>(o => o.UseSqlServer(mainConn));
else if (!string.IsNullOrEmpty(mainConn))
    builder.Services.AddDbContext<NavalArchiveDbContext>(o => o.UseSqlite(mainConn));
else
    builder.Services.AddDbContext<NavalArchiveDbContext>(o => o.UseSqlite("Data Source=navalarchive.db"));

builder.Services.AddDbContext<LogsDbContext>(options =>
    options.UseSqlite("Data Source=logs.db"));
builder.Services.AddSingleton<DataSyncService>();
builder.Services.AddSingleton<LogsDataService>();
builder.Services.AddSingleton<GenuineLogsFetcher>();

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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NavalArchiveDbContext>();
    var logsDb = scope.ServiceProvider.GetRequiredService<LogsDbContext>();
    db.Database.EnsureCreated();
    logsDb.Database.EnsureCreated();
}

// Fetch data from Wikipedia and genuine war diaries (runs in background)
_ = Task.Run(async () =>
{
    await Task.Delay(2000);
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NavalArchiveDbContext>();
        var logsDb = scope.ServiceProvider.GetRequiredService<LogsDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<DataSyncService>();
        var logsData = scope.ServiceProvider.GetRequiredService<LogsDataService>();
        var genuineLogs = scope.ServiceProvider.GetRequiredService<GenuineLogsFetcher>();
        await sync.SyncFromWikipediaAsync(db);
        await logsData.RefreshFromWikipediaAsync();
        await genuineLogs.FetchAndSaveAsync(logsDb);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Background sync failed: {ex.Message}");
    }
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
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

// Idempotency: prevent same transaction from being paid multiple times
var idempotencyCache = new System.Collections.Concurrent.ConcurrentDictionary<string, (object Result, DateTime Expires)>();
const int IdempotencyWindowMinutes = 10;

// Minimal API for checkout (bypasses controller routing that 404s on POST under IIS)
app.MapPost("api/checkout/pay", async (HttpContext ctx, IHttpClientFactory http, IConfiguration config) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<CheckoutPayload>();
    if (string.IsNullOrWhiteSpace(req?.CardId) || string.IsNullOrWhiteSpace(req?.Name))
        return Results.BadRequest(new { error = "CardId and Name required" });

    var cardId = req.CardId!.Trim();
    var cardUrl = config["CardService:Url"] ?? "http://localhost:5002";
    var cartUrl = config["CartService:Url"] ?? "http://localhost:5003";
    var paymentUrl = config["PaymentService:Url"] ?? "http://localhost:5001";
    var client = http.CreateClient();

    // Idempotency: if we already successfully processed this key, return cached result (no re-charge)
    var idempotencyKey = req.IdempotencyKey?.Trim();
    if (!string.IsNullOrEmpty(idempotencyKey) && idempotencyCache.TryGetValue(idempotencyKey, out var cached))
    {
        if (cached.Expires > DateTime.UtcNow)
            return Results.Json(cached.Result);
        idempotencyCache.TryRemove(idempotencyKey, out _);
    }

    var validateRes = await client.PostAsJsonAsync($"{cardUrl}/api/card/validate-with-name", new { cardId, name = req.Name });
    if (!validateRes.IsSuccessStatusCode)
        return Results.Json(new { error = await validateRes.Content.ReadAsStringAsync() }, statusCode: (int)validateRes.StatusCode);

    var validateData = await validateRes.Content.ReadFromJsonAsync<ValidatePayload>();
    if (validateData?.Valid != true)
        return Results.Ok(new { approved = false, message = validateData?.Message ?? "Card validation failed" });

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

    var payRes = await client.PostAsJsonAsync($"{paymentUrl}/api/payment/simulate", new
    {
        amount,
        currency = req.Currency ?? "USD",
        description = req.Description ?? "Museum checkout",
        cardId
    });
    if (!payRes.IsSuccessStatusCode)
        return Results.Json(new { error = "Payment service unavailable" }, statusCode: 502);

    var payData = await payRes.Content.ReadFromJsonAsync<PaymentPayload>();
    var approved = payData?.Approved ?? false;
    var result = new
    {
        approved,
        transactionId = payData?.TransactionId,
        amount,
        message = approved ? "Payment approved" : "Payment declined"
    };

    if (approved)
    {
        // Clear cart so same items cannot be paid again
        await client.PostAsync($"{cartUrl}/api/cart/clear/{Uri.EscapeDataString(cardId)}", null);

        // Cache successful result for idempotency (prevents double charge on retry)
        if (!string.IsNullOrEmpty(idempotencyKey))
            idempotencyCache[idempotencyKey] = (result, DateTime.UtcNow.AddMinutes(IdempotencyWindowMinutes));
    }

    return Results.Ok(result);
});

app.Run();

record CheckoutPayload(string? CardId, string? Name, decimal? Amount, string? Currency, string? Description, string? IdempotencyKey);
record ValidatePayload(bool Valid, string? Message);
record CartTotalPayload(decimal Total);
record PaymentPayload(bool Approved, string? TransactionId);
