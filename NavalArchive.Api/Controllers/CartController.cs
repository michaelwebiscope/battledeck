using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

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

    [HttpPost("add")]
    public async Task<IActionResult> Add([FromBody] AddToCartRequest request)
    {
        var cartUrl = _config["CartService:Url"] ?? "http://localhost:5003";
        var client = _httpClientFactory.CreateClient();
        var res = await client.PostAsJsonAsync($"{cartUrl}/api/cart/add", request);
        if (!res.IsSuccessStatusCode)
            return StatusCode((int)res.StatusCode, new { error = await res.Content.ReadAsStringAsync() });
        return Ok(await res.Content.ReadFromJsonAsync<object>());
    }

    [HttpGet("total/{cardId}")]
    public async Task<IActionResult> GetTotal(string cardId, [FromQuery] bool isMember = false)
    {
        var cartUrl = _config["CartService:Url"] ?? "http://localhost:5003";
        var client = _httpClientFactory.CreateClient();
        var res = await client.GetAsync($"{cartUrl}/api/cart/total/{cardId}?isMember={isMember}");
        if (!res.IsSuccessStatusCode)
            return StatusCode((int)res.StatusCode, new { error = await res.Content.ReadAsStringAsync() });
        return Ok(await res.Content.ReadFromJsonAsync<object>());
    }

    [HttpGet("items/{cardId}")]
    public async Task<IActionResult> GetItems(string cardId)
    {
        var cartUrl = _config["CartService:Url"] ?? "http://localhost:5003";
        var client = _httpClientFactory.CreateClient();
        var res = await client.GetAsync($"{cartUrl}/api/cart/items/{cardId}");
        if (!res.IsSuccessStatusCode)
            return StatusCode((int)res.StatusCode, new { error = await res.Content.ReadAsStringAsync() });
        return Ok(await res.Content.ReadFromJsonAsync<object>());
    }
}

public record AddToCartRequest(string? CardId, int ProductId, int Quantity = 1, bool IsMember = false);
