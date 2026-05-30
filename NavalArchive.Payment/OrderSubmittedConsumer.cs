using MassTransit;
using NavalArchive.Messaging;

namespace NavalArchive.Payment;

public class OrderSubmittedConsumer(
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    ILogger<OrderSubmittedConsumer> logger) : IConsumer<OrderSubmitted>
{
    public async Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        logger.LogInformation("Processing order {OrderId} from bus", context.Message.OrderId);

        var nextUrl = configuration["NextService:Url"] ?? "http://localhost:5018";
        var client = httpFactory.CreateClient();
        var res = await client.GetAsync($"{nextUrl}/trace");
        var body = await res.Content.ReadAsStringAsync();
        logger.LogInformation("Chained to shipping: {Status} {Body}", (int)res.StatusCode, body);
    }
}
