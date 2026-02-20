using Microsoft.EntityFrameworkCore;
using NavalArchive.PaymentSimulation.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

var conn = builder.Configuration.GetConnectionString("PaymentDb") ?? builder.Configuration["ConnectionStrings:PaymentDb"];
var provider = builder.Configuration["DatabaseProvider"] ?? "";

if (!string.IsNullOrEmpty(conn))
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) || conn.Contains("Trusted_Connection", StringComparison.OrdinalIgnoreCase) || conn.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddDbContext<PaymentDbContext>(o => o.UseSqlServer(conn));
    else
        builder.Services.AddDbContext<PaymentDbContext>(o => o.UseMySql(conn, ServerVersion.AutoDetect(conn)));
}
else
    builder.Services.AddDbContext<PaymentDbContext>(o => o.UseSqlite("Data Source=payment.db"));

builder.Services.AddControllers();
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
    scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.EnsureCreated();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.MapControllers();

app.Run();
