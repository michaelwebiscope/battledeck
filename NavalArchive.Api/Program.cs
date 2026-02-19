using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;
using NavalArchive.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<NavalArchiveDbContext>(options =>
    options.UseInMemoryDatabase("NavalArchiveDb"));
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

app.Run();
