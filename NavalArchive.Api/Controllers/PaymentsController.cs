using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public PaymentsController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost("simulate")]
    public async Task<IActionResult> Simulate([FromBody] SimulateRequest request)
    {
        var paymentUrl = _config["PaymentService:Url"] ?? "http://localhost:5001";
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            // Forward API key so payment-service can deduct from account balance
            var apiKey = Request.Headers["X-API-Key"].FirstOrDefault()
                ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            var response = await client.PostAsJsonAsync($"{paymentUrl}/api/payment/simulate", new
            {
                amount = request.Amount,
                currency = request.Currency ?? "USD",
                description = request.Description ?? "Donation",
                paymentMethodToken = request.PaymentMethodToken
            });
            var body = await response.Content.ReadAsStringAsync();
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = (int)response.StatusCode };
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { error = "Payment service unavailable" });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { error = "Payment request timed out" });
        }
    }
}

public record SimulateRequest(decimal Amount, string? Currency, string? Description, string? PaymentMethodToken);
