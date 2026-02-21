using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Services.AddHttpClient();

var app = builder.Build();
var nextUrl = builder.Configuration["NextService:Url"] ?? "http://localhost:5018";

app.MapGet("/health", () => Results.Ok(new { service = "Payment", status = "ok" }));
app.MapGet("/trace", async (IHttpClientFactory http) =>
{
    using var activity = new Activity("Payment.Process").Start();
    var client = http.CreateClient();
    var res = await client.GetAsync($"{nextUrl}/trace");
    var body = await res.Content.ReadAsStringAsync();
    return Results.Ok(new { service = "Payment", next = body });
});

app.Run();
