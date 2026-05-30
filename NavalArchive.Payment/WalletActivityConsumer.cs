using MassTransit;
using NavalArchive.Messaging;

namespace NavalArchive.Payment;

public class WalletActivityConsumer(
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    ILogger<WalletActivityConsumer> logger) : IConsumer<WalletActivity>
{
    public async Task Consume(ConsumeContext<WalletActivity> context)
    {
        var msg = context.Message;
        logger.LogInformation(
            "Wallet activity {Step} account={AccountId} amount={Amount}",
            msg.Step, msg.AccountId, msg.Amount);

        var nextUrl = configuration["NextService:Url"] ?? "http://localhost:5018";
        var client = httpFactory.CreateClient();
        var res = await client.GetAsync($"{nextUrl}/trace");
        var body = await res.Content.ReadAsStringAsync();
        logger.LogInformation("Wallet chain → shipping: {Status} {Body}", (int)res.StatusCode, body);
    }
}
