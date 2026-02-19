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
        var cartUrl = _config["CartService:Url"] ?? "http://localhost:5003";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{cartUrl}/api/cart/simulate-payment", new
        {
            amount = request.Amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Donation"
        });
        if (!response.IsSuccessStatusCode)
            return StatusCode(502, new { error = "Payment chain unavailable" });
        var result = await response.Content.ReadFromJsonAsync<object>();
        return Ok(result);
    }
}

public record SimulateRequest(decimal Amount, string? Currency, string? Description);
