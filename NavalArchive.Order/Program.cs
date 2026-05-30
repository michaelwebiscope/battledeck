using System.Diagnostics;
using MassTransit;
using NavalArchive.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        ConfigureRabbitMq(builder.Configuration, cfg);
        cfg.UsePublishFilter(typeof(TracePublishFilter<>), context);
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "Order", status = "ok" }));

app.MapPost("/wallet/activity", async (WalletActivityRequest req, IPublishEndpoint publish) =>
{
    await publish.Publish(new WalletActivity(req.Step, req.AccountId, req.Amount, DateTimeOffset.UtcNow));
    return Results.Json(new { service = "Order", step = req.Step, via = "rabbitmq" }, statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/trace", async (IPublishEndpoint publish) =>
{
    var orderId = Guid.NewGuid();
    await publish.Publish(new OrderSubmitted(orderId, DateTimeOffset.UtcNow));
    return Results.Ok(new
    {
        service = "Order",
        orderId,
        via = "rabbitmq",
        traceId = Activity.Current?.TraceId.ToString()
    });
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
