using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.CartService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CartController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public CartController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request)
    {
        if (request?.Amount <= 0 || string.IsNullOrWhiteSpace(request?.CardId))
            return BadRequest(new { error = "Amount and CardId required" });

        var cardUrl = _config["CardService:Url"] ?? "http://localhost:5002";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{cardUrl}/api/card/validate-and-pay", new
        {
            cardId = request.CardId,
            amount = request.Amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Cart checkout"
        });

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, new { error = err });
        }

        var result = await response.Content.ReadFromJsonAsync<object>();
        return Ok(result);
    }

    [HttpPost("issue-card")]
    public async Task<IActionResult> IssueCard([FromBody] IssueCardRequest request)
    {
        var cardUrl = _config["CardService:Url"] ?? "http://localhost:5002";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{cardUrl}/api/card/issue", new { name = request.Name, tier = request.Tier });
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, new { error = err });
        }
        var result = await response.Content.ReadFromJsonAsync<object>();
        return Ok(result);
    }

    [HttpGet("validate/{cardId}")]
    public async Task<IActionResult> Validate(string? cardId)
    {
        var cardUrl = _config["CardService:Url"] ?? "http://localhost:5002";
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"{cardUrl}/api/card/validate/{cardId}");
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { error = await response.Content.ReadAsStringAsync() });
        var result = await response.Content.ReadFromJsonAsync<object>();
        return Ok(result);
    }

    [HttpPost("simulate-payment")]
    public async Task<IActionResult> SimulatePayment([FromBody] SimulatePaymentRequest request)
    {
        var cardUrl = _config["CardService:Url"] ?? "http://localhost:5002";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{cardUrl}/api/card/simulate-payment", new
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

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", service = "CartService" });
}

public record CheckoutRequest(string? CardId, decimal Amount, string? Currency, string? Description);
public record IssueCardRequest(string? Name, string? Tier);
public record SimulatePaymentRequest(decimal Amount, string? Currency, string? Description);
