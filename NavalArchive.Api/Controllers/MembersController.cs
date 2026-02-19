using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MembersController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public MembersController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMemberRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "Name required" });

        var cardUrl = _config["CardService:Url"] ?? "http://localhost:5002";
        var cartUrl = _config["CartService:Url"] ?? "http://localhost:5003";
        var client = _httpClientFactory.CreateClient();

        // 1. Issue card (add member = add to card)
        var issueRes = await client.PostAsJsonAsync($"{cardUrl}/api/card/issue", new
        {
            name = request.Name,
            tier = request.Tier ?? "Standard"
        });
        if (!issueRes.IsSuccessStatusCode)
            return StatusCode((int)issueRes.StatusCode, new { error = await issueRes.Content.ReadAsStringAsync() });

        var issueData = await issueRes.Content.ReadFromJsonAsync<IssueCardResponse>();
        if (issueData?.CardId == null)
            return StatusCode(500, new { error = "Failed to issue card" });

        return Ok(new
        {
            cardId = issueData.CardId,
            name = request.Name,
            tier = issueData.Tier ?? request.Tier ?? "Standard",
            message = "Member created with card. Use card ID for checkout."
        });
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyMemberRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.CardId) || string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "CardId and Name required" });

        var cardUrl = _config["CardService:Url"] ?? "http://localhost:5002";
        var client = _httpClientFactory.CreateClient();
        var res = await client.PostAsJsonAsync($"{cardUrl}/api/card/validate-with-name", new
        {
            cardId = request.CardId,
            name = request.Name
        });
        if (!res.IsSuccessStatusCode)
            return StatusCode((int)res.StatusCode, new { error = await res.Content.ReadAsStringAsync() });

        var data = await res.Content.ReadFromJsonAsync<object>();
        return Ok(data);
    }
}

public record CreateMemberRequest(string? Name, string? Tier);
public record VerifyMemberRequest(string? CardId, string? Name);

public class IssueCardResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("cardId")]
    public string? CardId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("tier")]
    public string? Tier { get; set; }
}
