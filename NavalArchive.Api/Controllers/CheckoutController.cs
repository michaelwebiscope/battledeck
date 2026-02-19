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

    [HttpPost("pay")]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.CardId) || string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "CardId and Name required" });

        var cardUrl = _config["CardService:Url"] ?? "http://localhost:5002";
        var cartUrl = _config["CartService:Url"] ?? "http://localhost:5003";
        var paymentUrl = _config["PaymentService:Url"] ?? "http://localhost:5001";
        var client = _httpClientFactory.CreateClient();

        // 1. Validate card with name match
        var validateRes = await client.PostAsJsonAsync($"{cardUrl}/api/card/validate-with-name", new
        {
            cardId = request.CardId,
            name = request.Name
        });
        if (!validateRes.IsSuccessStatusCode)
            return StatusCode((int)validateRes.StatusCode, new { error = await validateRes.Content.ReadAsStringAsync() });

        var validateData = await validateRes.Content.ReadFromJsonAsync<ValidateResponse>();
        if (validateData?.Valid != true)
            return Ok(new { approved = false, message = validateData?.Message ?? "Card validation failed" });

        // 2. Get cart total (use amount from request if provided, else from cart)
        decimal amount = request.Amount ?? 0;
        if (amount <= 0)
        {
            var totalRes = await client.GetAsync($"{cartUrl}/api/cart/total/{request.CardId}?isMember=true");
            if (totalRes.IsSuccessStatusCode)
            {
                var totalData = await totalRes.Content.ReadFromJsonAsync<CartTotalResponse>();
                amount = totalData?.Total ?? 0;
            }
        }
        if (amount <= 0)
            return BadRequest(new { error = "No cart total and no amount provided" });

        // 3. Process payment
        var payRes = await client.PostAsJsonAsync($"{paymentUrl}/api/payment/simulate", new
        {
            amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Museum checkout",
            cardId = request.CardId
        });
        if (!payRes.IsSuccessStatusCode)
            return StatusCode(502, new { error = "Payment service unavailable" });

        var payData = await payRes.Content.ReadFromJsonAsync<PaymentResponse>();
        return Ok(new
        {
            approved = payData?.Approved ?? false,
            transactionId = payData?.TransactionId,
            amount,
            message = payData?.Approved == true ? "Payment approved" : "Payment declined"
        });
    }
}

public record CheckoutRequest(string? CardId, string? Name, decimal? Amount, string? Currency, string? Description);

public class ValidateResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("valid")]
    public bool Valid { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class CartTotalResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("total")]
    public decimal Total { get; set; }
}

public class PaymentResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("approved")]
    public bool Approved { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }
}
