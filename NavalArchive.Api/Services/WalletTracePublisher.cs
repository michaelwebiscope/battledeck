using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NavalArchive.Messaging;

namespace NavalArchive.Api.Services;

/// <summary>
/// Fire-and-forget publish of wallet lifecycle events to Order/RabbitMQ for distributed tracing.
/// Does not block or fail the user-facing wallet API response.
/// </summary>
public sealed class WalletTracePublisher(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<WalletTracePublisher> logger)
{
    public void Trigger(string step, string? accountId = null, decimal? amount = null)
    {
        var orderUrl = config["OrderService:Url"] ?? "http://localhost:5016";
        _ = Task.Run(async () =>
        {
            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                using var res = await client.PostAsJsonAsync($"{orderUrl.TrimEnd('/')}/wallet/activity", new
                {
                    step,
                    accountId,
                    amount
                });
                if (!res.IsSuccessStatusCode)
                    logger.LogWarning("Wallet trace publish returned {Status} for step {Step}", (int)res.StatusCode, step);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Wallet trace publish failed for step {Step}", step);
            }
        });
    }

    public void TriggerIfSuccess(IActionResult result, string step, Func<JsonElement, (string? accountId, decimal? amount)> extract)
    {
        if (result is not ContentResult cr || cr.StatusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(cr.Content))
            return;
        try
        {
            using var doc = JsonDocument.Parse(cr.Content);
            var (accountId, amount) = extract(doc.RootElement);
            Trigger(step, accountId, amount);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not parse wallet response for trace step {Step}", step);
        }
    }
}
