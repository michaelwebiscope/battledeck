using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CardsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public CardsController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost("issue")]
    public async Task<IActionResult> Issue([FromBody] IssueRequest request)
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
}

public record IssueRequest(string? Name, string? Tier);
