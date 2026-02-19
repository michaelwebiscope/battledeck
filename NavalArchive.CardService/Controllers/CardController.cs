using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.CardService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CardController : ControllerBase
{
    private static readonly Random _rng = new();
    private static readonly Dictionary<string, CardInfo> _cards = new();

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
    public IActionResult Validate(string cardId)
    {
        if (!_cards.TryGetValue(cardId, out var card))
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

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", service = "CardService" });
}

public record IssueRequest(string? Name, string? Tier);

public record CardInfo(string CardId, string Name, string Tier, DateTime ExpiresAt);
