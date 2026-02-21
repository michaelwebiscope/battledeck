using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "Notification", status = "ok" }));
app.MapGet("/trace", () =>
{
    using var activity = new Activity("Notification.Send").Start();
    return Results.Ok(new { service = "Notification", next = (object?)null });
});

app.Run();
