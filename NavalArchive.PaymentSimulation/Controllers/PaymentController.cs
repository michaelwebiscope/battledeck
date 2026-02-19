using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.PaymentSimulation.Data;

namespace NavalArchive.PaymentSimulation.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentController : ControllerBase
{
    private static readonly Random _rng = new();
    private readonly PaymentDbContext _db;

    public PaymentController(PaymentDbContext db)
    {
        _db = db;
    }

    [HttpPost("simulate")]
    public async Task<IActionResult> Simulate([FromBody] PaymentRequest request)
    {
        if (request?.Amount <= 0)
            return BadRequest(new { error = "Invalid amount" });

        var approved = _rng.Next(100) < 95;
        var transactionId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        _db.Transactions.Add(new PaymentTransaction
        {
            TransactionId = transactionId,
            CardId = request?.CardId,
            Amount = request?.Amount ?? 0,
            Currency = request?.Currency ?? "USD",
            Approved = approved
        });
        await _db.SaveChangesAsync();

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
    public string? CardId { get; set; }
}
