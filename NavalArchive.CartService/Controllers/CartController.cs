using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.CartService.Data;

namespace NavalArchive.CartService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CartController : ControllerBase
{
    private readonly CartDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public CartController(CartDbContext db, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [HttpPost("add")]
    public async Task<IActionResult> Add([FromBody] AddToCartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.CardId) || request.ProductId <= 0)
            return BadRequest(new { error = "CardId and ProductId required" });

        var product = await _db.Products.FindAsync(request.ProductId);
        if (product == null)
            return NotFound(new { error = "Product not found" });

        var isMember = request.IsMember;
        var existing = await _db.CartItems
            .Include(x => x.Product)
            .FirstOrDefaultAsync(x => x.CardId == request.CardId && x.ProductId == request.ProductId);
        if (existing != null)
        {
            existing.Quantity += request.Quantity;
        }
        else
        {
            _db.CartItems.Add(new CartItem
            {
                CardId = request.CardId,
                ProductId = request.ProductId,
                Quantity = request.Quantity,
                IsMember = isMember
            });
        }
        await _db.SaveChangesAsync();
        return Ok(new { message = "Added to cart" });
    }

    [HttpGet("total/{cardId}")]
    public async Task<IActionResult> GetTotal(string cardId, [FromQuery] bool isMember = false)
    {
        var items = await _db.CartItems
            .Include(x => x.Product)
            .Where(x => x.CardId == cardId)
            .ToListAsync();
        decimal total = 0;
        foreach (var i in items)
        {
            total += (i.IsMember || isMember ? i.Product.MemberPrice : i.Product.Price) * i.Quantity;
        }
        return Ok(new { cardId, total, itemCount = items.Sum(x => x.Quantity) });
    }

    [HttpGet("items/{cardId}")]
    public async Task<IActionResult> GetItems(string cardId)
    {
        var items = await _db.CartItems
            .Include(x => x.Product)
            .Where(x => x.CardId == cardId)
            .Select(x => new
            {
                x.ProductId,
                x.Product.Name,
                x.Quantity,
                x.IsMember,
                UnitPrice = x.IsMember ? x.Product.MemberPrice : x.Product.Price,
                LineTotal = (x.IsMember ? x.Product.MemberPrice : x.Product.Price) * x.Quantity
            })
            .ToListAsync();
        return Ok(items);
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request)
    {
        if (request?.Amount <= 0 || string.IsNullOrWhiteSpace(request?.CardId))
            return BadRequest(new { error = "Amount and CardId required" });

        var cardUrl = _config["CardService:Url"] ?? "http://localhost:5002";
        var client = _httpClientFactory.CreateClient();
        var response = await client.PostAsJsonAsync($"{cardUrl}/api/card/validate-and-pay", new
        {
            cardId = request.CardId,
            name = request.Name,
            amount = request.Amount,
            currency = request.Currency ?? "USD",
            description = request.Description ?? "Cart checkout"
        });

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            return StatusCode((int)response.StatusCode, new { error = err });
        }

        var result = await response.Content.ReadFromJsonAsync<object>();
        return Ok(result);
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", service = "CartService" });
}

public record AddToCartRequest(string? CardId, int ProductId, int Quantity = 1, bool IsMember = false);
public record CheckoutRequest(string? CardId, string? Name, decimal Amount, string? Currency, string? Description);
