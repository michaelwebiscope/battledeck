using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

/// <summary>
/// Proxies account and payment history requests to the Go account-service and payment-service.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AccountsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public AccountsController(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    private string AccountUrl => _config["AccountService:Url"] ?? "http://localhost:5005";
    private string PaymentUrl => _config["PaymentService:Url"] ?? "http://localhost:5001";

    // POST /api/accounts/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] object body)
    {
        return await ProxyPost(AccountUrl + "/api/accounts/register", body, forwardApiKey: false);
    }

    // GET /api/accounts/me
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        return await ProxyGet(AccountUrl + "/api/accounts/me", forwardApiKey: true);
    }

    // POST /api/accounts/funds  (add funds)
    [HttpPost("funds")]
    public async Task<IActionResult> AddFunds([FromBody] object body)
    {
        return await ProxyPost(AccountUrl + "/api/accounts/funds", body, forwardApiKey: true);
    }

    // GET /api/accounts/history  (payment history via payment-service)
    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        return await ProxyGet(PaymentUrl + "/api/payment/history", forwardApiKey: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> ProxyGet(string url, bool forwardApiKey)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            if (forwardApiKey)
            {
                var key = Request.Headers["X-API-Key"].FirstOrDefault()
                    ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
                if (!string.IsNullOrEmpty(key))
                    client.DefaultRequestHeaders.Add("X-API-Key", key);
            }
            var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = (int)response.StatusCode };
        }
        catch (HttpRequestException) { return StatusCode(502, new { error = "Account service unavailable" }); }
        catch (TaskCanceledException) { return StatusCode(504, new { error = "Account service timed out" }); }
    }

    private async Task<IActionResult> ProxyPost(string url, object body, bool forwardApiKey)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            if (forwardApiKey)
            {
                var key = Request.Headers["X-API-Key"].FirstOrDefault()
                    ?? Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
                if (!string.IsNullOrEmpty(key))
                    client.DefaultRequestHeaders.Add("X-API-Key", key);
            }
            var response = await client.PostAsJsonAsync(url, body);
            var responseBody = await response.Content.ReadAsStringAsync();
            return new ContentResult { Content = responseBody, ContentType = "application/json", StatusCode = (int)response.StatusCode };
        }
        catch (HttpRequestException) { return StatusCode(502, new { error = "Account service unavailable" }); }
        catch (TaskCanceledException) { return StatusCode(504, new { error = "Account service timed out" }); }
    }
}
