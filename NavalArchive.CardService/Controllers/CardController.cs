using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.CardService.Data;

namespace NavalArchive.CardService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CardController : ControllerBase
{
    private readonly CardDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public CardController(CardDbContext db, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost("issue")]
    public async Task<IActionResult> Issue([FromBody] IssueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "Name required" });

        var cardId = "NAV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var tier = request.Tier ?? "Standard";
        var expiresAt = DateTime.UtcNow.AddYears(1);

        _db.Cards.Add(new Card { CardId = cardId, Name = request.Name, Tier = tier, ExpiresAt = expiresAt });
        await _db.SaveChangesAsync();

        return Ok(new { cardId, name = request.Name, tier, expiresAt, message = "Membership card issued" });
    }

    [HttpGet("validate/{cardId}")]
    public async Task<IActionResult> Validate(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return BadRequest(new { valid = false, message = "CardId required" });

        var card = await _db.Cards.FirstOrDefaultAsync(c => c.CardId == cardId);
        if (card == null)
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

    [HttpPost("validate-with-name")]
    public async Task<IActionResult> ValidateWithName([FromBody] ValidateWithNameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.CardId) || string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { valid = false, message = "CardId and Name required" });

        var card = await _db.Cards.FirstOrDefaultAsync(c => c.CardId == request.CardId);
        if (card == null)
            return NotFound(new { valid = false, message = "Card not found" });

        var nameMatch = string.Equals(card.Name.Trim(), request.Name.Trim(), StringComparison.OrdinalIgnoreCase);
        var notExpired = card.ExpiresAt > DateTime.UtcNow;
        var valid = nameMatch && notExpired;

        return Ok(new
        {
            valid,
            nameMatch,
            notExpired,
            cardId = card.CardId,
            name = card.Name,
            tier = card.Tier,
            message = !nameMatch ? "Name does not match card" : !notExpired ? "Card expired" : "Card valid"
        });
    }

    [HttpPost("validate-and-pay")]
    public async Task<IActionResult> ValidateAndPay([FromBody] ValidateAndPayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.CardId) || request.Amount <= 0)
            return BadRequest(new { error = "CardId and Amount required" });

        var card = await _db.Cards.FirstOrDefaultAsync(c => c.CardId == request.CardId);
        if (card == null)
            return NotFound(new { valid = false, message = "Card not found" });

        if (card.ExpiresAt <= DateTime.UtcNow)
            return Ok(new { valid = false, message = "Card expired", approved = false });

        if (!string.IsNullOrWhiteSpace(request.Name) && !string.Equals(card.Name.Trim(), request.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            return Ok(new { valid = false, message = "Name does not match card", approved = false });

        var paymentUrl = _config["PaymentService:Url"] ?? "http://localhost:5001";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{paymentUrl}/api/payment/simulate", new
        {
            amount = request.Amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Membership payment",
            cardId = request.CardId
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

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", service = "CardService" });
}

public record IssueRequest(string? Name, string? Tier);
public record ValidateWithNameRequest(string? CardId, string? Name);
public record ValidateAndPayRequest(string? CardId, string? Name, decimal Amount, string? Currency, string? Description);

public class PaymentResult
{
    [System.Text.Json.Serialization.JsonPropertyName("approved")]
    public bool Approved { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }
}
