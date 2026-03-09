using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NavalArchive.Data;
using NavalArchive.Data.Models;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CaptainsController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly CacheInvalidationService _cacheInv;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(10);

    public CaptainsController(NavalArchiveDbContext db, IMemoryCache cache, CacheInvalidationService cacheInv)
    {
        _db = db;
        _cache = cache;
        _cacheInv = cacheInv;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetCaptains(CancellationToken ct = default)
    {
        var v = _cacheInv.GetCaptainsVersion();
        var key = $"captains:list:v{v}";
        var list = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
            return await _db.Captains
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Rank,
                    c.ServiceYears,
                    c.ImageUrl,
                    c.ImageVersion,
                    ShipCount = c.Ships.Count
                })
                .OrderBy(c => c.Name)
                .ToListAsync(ct);
        });
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetCaptain(int id, CancellationToken ct = default)
    {
        var v = _cacheInv.GetCaptainsVersion();
        var key = $"captains:id:v{v}:{id}";
        var result = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
            var captain = await _db.Captains
                .Include(c => c.Ships)
                .ThenInclude(s => s.Class)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (captain == null) return null;

            return new
            {
                captain.Id,
                captain.Name,
                captain.Rank,
                captain.ServiceYears,
                captain.ImageUrl,
                captain.ImageVersion,
                Ships = captain.Ships.Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.YearCommissioned,
                    Class = s.Class != null ? new { s.Class.Id, s.Class.Name } : null
                })
            };
        });
        if (result == null) return NotFound();
        return Ok(result);
    }

    /// <summary>Create a new captain.</summary>
    [HttpPost]
    public async Task<ActionResult<object>> CreateCaptain([FromBody] CreateCaptainRequest? request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name required" });
        var captain = new Captain
        {
            Name = request.Name.Trim(),
            Rank = request.Rank?.Trim() ?? "",
            ServiceYears = request.ServiceYears ?? 0,
            ImageUrl = request.ImageUrl?.Trim()
        };
        _db.Captains.Add(captain);
        await _db.SaveChangesAsync(ct);
        _cacheInv.OnCaptainCreated();
        return Ok(new { captain.Id, captain.Name, captain.Rank, captain.ServiceYears, captain.ImageUrl });
    }

    /// <summary>Update captain (admin). Edits persist to DB and appear everywhere.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<object>> UpdateCaptain(int id, [FromBody] UpdateCaptainRequest? request, CancellationToken ct = default)
    {
        if (request == null) return BadRequest();
        var captain = await _db.Captains.FindAsync(id);
        if (captain == null) return NotFound();
        if (request.Name != null) captain.Name = request.Name.Trim();
        if (request.Rank != null) captain.Rank = request.Rank.Trim();
        if (request.ServiceYears.HasValue) captain.ServiceYears = request.ServiceYears.Value;
        await _db.SaveChangesAsync(ct);
        _cacheInv.OnCaptainUpdated();
        return Ok(new { captain.Id, captain.Name, captain.Rank, captain.ServiceYears });
    }

    /// <summary>Delete captain (admin). Fails if captain has ships assigned. Supports DELETE and POST (workaround for IIS/WebDAV blocking DELETE).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteCaptain(int id, CancellationToken ct = default)
        => await DeleteCaptainCore(id, ct);

    /// <summary>POST delete workaround for IIS/WebDAV blocking DELETE.</summary>
    [HttpPost("delete/{id:int}")]
    public async Task<IActionResult> DeleteCaptainPost(int id, CancellationToken ct = default)
        => await DeleteCaptainCore(id, ct);

    private async Task<IActionResult> DeleteCaptainCore(int id, CancellationToken ct)
    {
        var captain = await _db.Captains.Include(c => c.Ships).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (captain == null) return NotFound();
        if (captain.Ships.Count > 0)
            return BadRequest(new { error = "Cannot delete captain with assigned ships. Reassign ships to another captain first." });
        _db.Captains.Remove(captain);
        await _db.SaveChangesAsync(ct);
        _cacheInv.OnCaptainUpdated();
        return Ok(new { deleted = true, id });
    }
}

public class UpdateCaptainRequest
{
    public string? Name { get; set; }
    public string? Rank { get; set; }
    public int? ServiceYears { get; set; }
}

public class CreateCaptainRequest
{
    public string? Name { get; set; }
    public string? Rank { get; set; }
    public int? ServiceYears { get; set; }
    public string? ImageUrl { get; set; }
}
