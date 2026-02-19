using Microsoft.EntityFrameworkCore;
using NavalArchive.PaymentSimulation.Data;

var builder = WebApplication.CreateBuilder(args);

var conn = builder.Configuration.GetConnectionString("PaymentDb") ?? builder.Configuration["ConnectionStrings:PaymentDb"];
if (!string.IsNullOrEmpty(conn))
    builder.Services.AddDbContext<PaymentDbContext>(o => o.UseMySql(conn, ServerVersion.AutoDetect(conn)));
else
    builder.Services.AddDbContext<PaymentDbContext>(o => o.UseInMemoryDatabase("PaymentDb"));

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
