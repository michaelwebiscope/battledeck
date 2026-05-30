using NavalArchive.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Services.AddTraceChainHttpClient();

var app = builder.Build();
var nextUrl = builder.Configuration["NextService:Url"] ?? "http://localhost:5015";

app.MapGet("/health", () => Results.Ok(new { service = "Inventory", status = "ok" }));
app.MapGet("/trace", async (IHttpClientFactory http) =>
{
    var client = http.CreateClient();
    var res = await client.GetAsync($"{nextUrl}/trace");
    var body = await res.Content.ReadAsStringAsync();
    return Results.Ok(new { service = "Inventory", next = body });
});

app.Run();
