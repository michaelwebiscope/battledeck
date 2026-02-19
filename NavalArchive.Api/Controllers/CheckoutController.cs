using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CheckoutController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public CheckoutController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request)
    {
        if (request?.Amount <= 0 || string.IsNullOrWhiteSpace(request?.CardId))
            return BadRequest(new { error = "Amount and CardId required" });

        var cartUrl = _config["CartService:Url"] ?? "http://localhost:5003";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{cartUrl}/api/cart/checkout", new
        {
            cardId = request.CardId,
            amount = request.Amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Museum checkout"
        });

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, new { error = err });
        }

        var result = await response.Content.ReadFromJsonAsync<object>();
        return Ok(result);
    }
}

public record CheckoutRequest(string? CardId, decimal Amount, string? Currency, string? Description);
