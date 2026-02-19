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

// Minimal API for checkout (bypasses controller routing that 404s on POST under IIS)
app.MapPost("api/checkout/pay", async (HttpContext ctx, IHttpClientFactory http, IConfiguration config) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<CheckoutPayload>();
    if (string.IsNullOrWhiteSpace(req?.CardId) || string.IsNullOrWhiteSpace(req?.Name))
        return Results.BadRequest(new { error = "CardId and Name required" });

    var cardUrl = config["CardService:Url"] ?? "http://localhost:5002";
    var cartUrl = config["CartService:Url"] ?? "http://localhost:5003";
    var paymentUrl = config["PaymentService:Url"] ?? "http://localhost:5001";
    var client = http.CreateClient();

    var validateRes = await client.PostAsJsonAsync($"{cardUrl}/api/card/validate-with-name", new { cardId = req.CardId, name = req.Name });
    if (!validateRes.IsSuccessStatusCode)
        return Results.Json(new { error = await validateRes.Content.ReadAsStringAsync() }, statusCode: (int)validateRes.StatusCode);

    var validateData = await validateRes.Content.ReadFromJsonAsync<ValidatePayload>();
    if (validateData?.Valid != true)
        return Results.Ok(new { approved = false, message = validateData?.Message ?? "Card validation failed" });

    decimal amount = req.Amount ?? 0;
    if (amount <= 0)
    {
        var totalRes = await client.GetAsync($"{cartUrl}/api/cart/total/{req.CardId}?isMember=true");
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
        cardId = req.CardId
    });
    if (!payRes.IsSuccessStatusCode)
        return Results.Json(new { error = "Payment service unavailable" }, statusCode: 502);

    var payData = await payRes.Content.ReadFromJsonAsync<PaymentPayload>();
    return Results.Ok(new
    {
        approved = payData?.Approved ?? false,
        transactionId = payData?.TransactionId,
        amount,
        message = payData?.Approved == true ? "Payment approved" : "Payment declined"
    });
});

app.Run();

record CheckoutPayload(string? CardId, string? Name, decimal? Amount, string? Currency, string? Description);
record ValidatePayload(bool Valid, string? Message);
record CartTotalPayload(decimal Total);
record PaymentPayload(bool Approved, string? TransactionId);
