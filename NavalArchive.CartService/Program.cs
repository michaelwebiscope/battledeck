using Microsoft.EntityFrameworkCore;
using NavalArchive.CartService.Data;

var builder = WebApplication.CreateBuilder(args);

var conn = builder.Configuration.GetConnectionString("CartDb") ?? builder.Configuration["ConnectionStrings:CartDb"];
if (!string.IsNullOrEmpty(conn))
    builder.Services.AddDbContext<CartDbContext>(o => o.UseMySql(conn, ServerVersion.AutoDetect(conn)));
else
    builder.Services.AddDbContext<CartDbContext>(o => o.UseInMemoryDatabase("CartDb"));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<CartDbContext>().Database.EnsureCreated();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.MapControllers();

app.Run();
