using Microsoft.EntityFrameworkCore;
using NavalArchive.CardService.Data;

var builder = WebApplication.CreateBuilder(args);

var conn = builder.Configuration.GetConnectionString("CardDb") ?? builder.Configuration["ConnectionStrings:CardDb"];
var provider = builder.Configuration["DatabaseProvider"] ?? "";

if (!string.IsNullOrEmpty(conn))
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) || conn.Contains("Trusted_Connection", StringComparison.OrdinalIgnoreCase) || conn.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddDbContext<CardDbContext>(o => o.UseSqlServer(conn));
    else
        builder.Services.AddDbContext<CardDbContext>(o => o.UseMySql(conn, ServerVersion.AutoDetect(conn)));
}
else
    builder.Services.AddDbContext<CardDbContext>(o => o.UseSqlite("Data Source=card.db"));

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<CardDbContext>().Database.EnsureCreated();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.MapControllers();

app.Run();
