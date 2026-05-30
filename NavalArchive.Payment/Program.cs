using MassTransit;
using NavalArchive.Messaging;
using NavalArchive.Payment;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Services.AddTraceChainHttpClient();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderSubmittedConsumer>();
    x.AddConsumer<WalletActivityConsumer>();
    x.UsingRabbitMq((context, cfg) =>
    {
        ConfigureRabbitMq(builder.Configuration, cfg);
        cfg.UseConsumeFilter(typeof(TraceConsumeFilter<>), context);
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();
var nextUrl = builder.Configuration["NextService:Url"] ?? "http://localhost:5018";

app.MapGet("/health", () => Results.Ok(new { service = "Payment", status = "ok" }));

app.MapGet("/trace", async (IHttpClientFactory http) =>
{
    var client = http.CreateClient();
    var res = await client.GetAsync($"{nextUrl}/trace");
    var body = await res.Content.ReadAsStringAsync();
    return Results.Ok(new { service = "Payment", next = body });
});

app.Run();

static void ConfigureRabbitMq(IConfiguration configuration, IRabbitMqBusFactoryConfigurator cfg)
{
    var host = configuration["RabbitMQ:Host"] ?? "127.0.0.1";
    var user = configuration["RabbitMQ:Username"] ?? "guest";
    var pass = configuration["RabbitMQ:Password"] ?? "guest";
    var vhost = configuration["RabbitMQ:VirtualHost"] ?? "/";

    cfg.Host(host, vhost, h =>
    {
        h.Username(user);
        h.Password(pass);
    });
}
