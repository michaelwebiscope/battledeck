using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.CardService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CardController : ControllerBase
{
    private static readonly Random _rng = new();
    private static readonly Dictionary<string, CardInfo> _cards = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public CardController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost("issue")]
    public IActionResult Issue([FromBody] IssueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "Name required" });

        var cardId = "NAV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var tier = request.Tier ?? "Standard";
        var expiresAt = DateTime.UtcNow.AddYears(1);

        _cards[cardId] = new CardInfo(cardId, request.Name, tier, expiresAt);

        return Ok(new
        {
            cardId,
            name = request.Name,
            tier,
            expiresAt,
            message = "Membership card issued"
        });
    }

    [HttpGet("validate/{cardId}")]
    public IActionResult Validate(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId) || !_cards.TryGetValue(cardId, out var card))
            return NotFound(new { valid = false, message = "Card not found" });

        var valid = card.ExpiresAt > DateTime.UtcNow;
        return Ok(new
        {
            valid,
            cardId = card.CardId,
            name = card.Name,
            tier = card.Tier,
            expiresAt = card.ExpiresAt,
            message = valid ? "Card valid" : "Card expired"
        });
    }

    [HttpPost("validate-and-pay")]
    public async Task<IActionResult> ValidateAndPay([FromBody] ValidateAndPayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.CardId) || request.Amount <= 0)
            return BadRequest(new { error = "CardId and Amount required" });

        if (!_cards.TryGetValue(request.CardId, out var card))
            return NotFound(new { valid = false, message = "Card not found" });

        var valid = card.ExpiresAt > DateTime.UtcNow;
        if (!valid)
            return Ok(new { valid = false, message = "Card expired", approved = false });

        var paymentUrl = _config["PaymentService:Url"] ?? "http://localhost:5001";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{paymentUrl}/api/payment/simulate", new
        {
            amount = request.Amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Membership payment"
        });

        if (!response.IsSuccessStatusCode)
            return StatusCode(502, new { error = "Payment service unavailable" });

        var paymentResult = await response.Content.ReadFromJsonAsync<PaymentResult>();
        return Ok(new
        {
            valid = true,
            cardId = card.CardId,
            name = card.Name,
            tier = card.Tier,
            message = "Card valid",
            approved = paymentResult?.Approved ?? false,
            transactionId = paymentResult?.TransactionId
        });
    }

    [HttpPost("simulate-payment")]
    public async Task<IActionResult> SimulatePayment([FromBody] SimulatePaymentRequest request)
    {
        if (request.Amount <= 0)
            return BadRequest(new { error = "Invalid amount" });

        var paymentUrl = _config["PaymentService:Url"] ?? "http://localhost:5001";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{paymentUrl}/api/payment/simulate", new
        {
            amount = request.Amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Payment"
        });
        if (!response.IsSuccessStatusCode)
            return StatusCode(502, new { error = "Payment service unavailable" });
        var result = await response.Content.ReadFromJsonAsync<object>();
        return Ok(result);
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", service = "CardService" });
}

public record SimulatePaymentRequest(decimal Amount, string? Currency, string? Description);

public record ValidateAndPayRequest(string? CardId, decimal Amount, string? Currency, string? Description);

public class PaymentResult
{
    [System.Text.Json.Serialization.JsonPropertyName("approved")]
    public bool Approved { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }
}

public record IssueRequest(string? Name, string? Tier);

public record CardInfo(string CardId, string Name, string Tier, DateTime ExpiresAt);
