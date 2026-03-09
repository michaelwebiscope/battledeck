using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NavalArchive.Data;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly CacheInvalidationService _cacheInv;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(10);

    public StatsController(NavalArchiveDbContext db, IMemoryCache cache, CacheInvalidationService cacheInv)
    {
        _db = db;
        _cache = cache;
        _cacheInv = cacheInv;
    }

    [HttpGet]
    public async Task<ActionResult<object>> GetStats(CancellationToken ct = default)
    {
        var v = _cacheInv.GetStatsVersion();
        var key = $"stats:main:v{v}";
        var result = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
            var shipCount = await _db.Ships.CountAsync(ct);
            var classCount = await _db.ShipClasses.CountAsync(ct);
            var captainCount = await _db.Captains.CountAsync(ct);

            var byType = await _db.Ships
                .Include(s => s.Class)
                .GroupBy(s => s.Class != null ? s.Class.Type : "Unknown")
                .Select(g => new { Type = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var byCountry = await _db.Ships
                .Include(s => s.Class)
                .GroupBy(s => s.Class != null ? s.Class.Country : "Unknown")
                .Select(g => new { Country = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync(ct);

            return new
            {
                TotalShips = shipCount,
                TotalClasses = classCount,
                TotalCaptains = captainCount,
                ByType = byType,
                ByCountry = byCountry
            };
        });
        return Ok(result);
    }
}
