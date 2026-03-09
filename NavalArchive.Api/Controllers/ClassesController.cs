using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NavalArchive.Data;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ClassesController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly CacheInvalidationService _cacheInv;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(10);

    public ClassesController(NavalArchiveDbContext db, IMemoryCache cache, CacheInvalidationService cacheInv)
    {
        _db = db;
        _cache = cache;
        _cacheInv = cacheInv;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetClasses(CancellationToken ct = default)
    {
        var v = _cacheInv.GetClassesVersion();
        var key = $"classes:list:v{v}";
        var list = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
            return await _db.ShipClasses
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Type,
                    c.Country,
                    ShipCount = c.Ships.Count
                })
                .ToListAsync(ct);
        });
        return Ok(list);
    }

    [HttpPost]
    public async Task<ActionResult<object>> CreateClass([FromBody] CreateClassRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "Name is required" });
        var shipClass = new NavalArchive.Data.Models.ShipClass
        {
            Name = request.Name.Trim(),
            Type = request.Type?.Trim() ?? "Unknown",
            Country = request.Country?.Trim() ?? ""
        };
        _db.ShipClasses.Add(shipClass);
        await _db.SaveChangesAsync();
        _cacheInv.OnClassCreated();
        return Ok(new { id = shipClass.Id, name = shipClass.Name, type = shipClass.Type, country = shipClass.Country });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetClass(int id, CancellationToken ct = default)
    {
        var v = _cacheInv.GetClassesVersion();
        var key = $"classes:id:v{v}:{id}";
        var result = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
            var shipClass = await _db.ShipClasses
                .Include(c => c.Ships)
                .ThenInclude(s => s.Captain)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (shipClass == null) return null;

            return new
            {
                shipClass.Id,
                shipClass.Name,
                shipClass.Type,
                shipClass.Country,
                Ships = shipClass.Ships.Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.YearCommissioned,
                    Captain = s.Captain != null ? new { s.Captain.Id, s.Captain.Name } : null
                })
            };
        });
        if (result == null) return NotFound();
        return Ok(result);
    }

    /// <summary>Update a ship class (admin).</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<object>> UpdateClass(int id, [FromBody] UpdateClassRequest? request, CancellationToken ct = default)
    {
        if (request == null) return BadRequest();
        var shipClass = await _db.ShipClasses.FindAsync(id);
        if (shipClass == null) return NotFound();
        if (request.Name != null) shipClass.Name = request.Name.Trim();
        if (request.Type != null) shipClass.Type = request.Type.Trim();
        if (request.Country != null) shipClass.Country = request.Country.Trim();
        await _db.SaveChangesAsync(ct);
        _cacheInv.OnClassUpdated();
        return Ok(new { shipClass.Id, shipClass.Name, shipClass.Type, shipClass.Country });
    }
}

public class CreateClassRequest
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Country { get; set; }
}

public class UpdateClassRequest
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Country { get; set; }
}
