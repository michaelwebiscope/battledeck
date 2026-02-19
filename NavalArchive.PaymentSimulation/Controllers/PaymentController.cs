using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.PaymentSimulation.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentController : ControllerBase
{
    private static readonly Random _rng = new();

    [HttpPost("simulate")]
    public IActionResult Simulate([FromBody] PaymentRequest request)
    {
        if (request?.Amount <= 0)
            return BadRequest(new { error = "Invalid amount" });

        // Simulate ~95% success rate for demo
        var approved = _rng.Next(100) < 95;
        var transactionId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        return Ok(new
        {
            approved,
            transactionId,
            message = approved ? "Payment approved" : "Payment declined (simulation)",
            amount = request?.Amount,
            currency = request?.Currency ?? "USD"
        });
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", service = "PaymentSimulation" });
}

public class PaymentRequest
{
    public decimal Amount { get; set; }
    public string? Currency { get; set; }
    public string? Description { get; set; }
}
