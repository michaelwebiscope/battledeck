using NavalArchive.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Services.AddTraceChainHttpClient();

var app = builder.Build();
var nextUrl = builder.Configuration["NextService:Url"] ?? "http://localhost:5012";

app.MapGet("/health", () => Results.Ok(new { service = "Auth", status = "ok" }));
app.MapGet("/trace", async (IHttpClientFactory http) =>
{
    var client = http.CreateClient();
    var res = await client.GetAsync($"{nextUrl}/trace");
    var body = await res.Content.ReadAsStringAsync();
    return Results.Ok(new { service = "Auth", next = body });
});

app.Run();
