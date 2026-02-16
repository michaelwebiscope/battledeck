using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;

    public StatsController(NavalArchiveDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<object>> GetStats()
    {
        var shipCount = await _db.Ships.CountAsync();
        var classCount = await _db.ShipClasses.CountAsync();
        var captainCount = await _db.Captains.CountAsync();

        var byType = await _db.Ships
            .Include(s => s.Class)
            .GroupBy(s => s.Class != null ? s.Class.Type : "Unknown")
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync();

        var byCountry = await _db.Ships
            .Include(s => s.Class)
            .GroupBy(s => s.Class != null ? s.Class.Country : "Unknown")
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        return Ok(new
        {
            TotalShips = shipCount,
            TotalClasses = classCount,
            TotalCaptains = captainCount,
            ByType = byType,
            ByCountry = byCountry
        });
    }
}
