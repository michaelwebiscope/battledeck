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
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{paymentUrl}/api/payment/simulate", new
        {
            amount = request.Amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Donation"
        });
        if (!response.IsSuccessStatusCode)
            return StatusCode(502, new { error = "Payment service unavailable", message = "Ensure PaymentSimulation is running on port 5001." });
        var result = await response.Content.ReadFromJsonAsync<object>();
        return Ok(result);
    }
}

public record SimulateRequest(decimal Amount, string? Currency, string? Description);
