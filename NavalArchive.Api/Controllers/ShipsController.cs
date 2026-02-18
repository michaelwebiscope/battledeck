using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShipsController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;

    public ShipsController(NavalArchiveDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// N+1 BUG: Fetches all ships, then loops and loads Class/Captain individually.
    /// Do NOT use .Include() - this causes hundreds of DB round-trips.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetShips([FromQuery] string? country, [FromQuery] string? type, [FromQuery] int? yearMin, [FromQuery] int? yearMax)
    {
        var shipsQuery = _db.Ships.AsQueryable();
        if (!string.IsNullOrWhiteSpace(country) || !string.IsNullOrWhiteSpace(type) || yearMin.HasValue || yearMax.HasValue)
        {
            var classQuery = _db.ShipClasses.AsQueryable();
            if (!string.IsNullOrWhiteSpace(country))
                classQuery = classQuery.Where(c => c.Country == country);
            if (!string.IsNullOrWhiteSpace(type))
                classQuery = classQuery.Where(c => c.Type == type);
            var ids = await classQuery.Select(c => c.Id).ToListAsync();
            shipsQuery = shipsQuery.Where(s => ids.Contains(s.ClassId));
            if (yearMin.HasValue) shipsQuery = shipsQuery.Where(s => s.YearCommissioned >= yearMin.Value);
            if (yearMax.HasValue) shipsQuery = shipsQuery.Where(s => s.YearCommissioned <= yearMax.Value);
        }
        var ships = await shipsQuery.ToListAsync();

        var result = new List<object>();
        foreach (var ship in ships)
        {
            // CRITICAL: Manual load inside loop - N+1 query pattern
            var shipClass = await _db.ShipClasses.FindAsync(ship.ClassId);
            var captain = await _db.Captains.FindAsync(ship.CaptainId);

            result.Add(new
            {
                ship.Id,
                ship.Name,
                ship.Description,
                ship.ImageUrl,
                ship.YearCommissioned,
                Class = shipClass != null ? new { shipClass.Name, shipClass.Type, shipClass.Country } : null,
                Captain = captain != null ? new { captain.Name, captain.Rank, captain.ServiceYears } : null
            });
        }

        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetShip(int id)
    {
        var ship = await _db.Ships.FindAsync(id);
        if (ship == null) return NotFound();

        var shipClass = await _db.ShipClasses.FindAsync(ship.ClassId);
        var captain = await _db.Captains.FindAsync(ship.CaptainId);

        return Ok(new
        {
            ship.Id,
            ship.Name,
            ship.Description,
            ship.ImageUrl,
            ship.YearCommissioned,
            Class = shipClass != null ? new { shipClass.Id, shipClass.Name, shipClass.Type, shipClass.Country } : null,
            Captain = captain != null ? new { captain.Id, captain.Name, captain.Rank, captain.ServiceYears, captain.ImageUrl } : null
        });
    }

    [HttpGet("random")]
    public async Task<ActionResult<object>> GetRandomShip()
    {
        var count = await _db.Ships.CountAsync();
        if (count == 0) return NotFound();
        var skip = Random.Shared.Next(0, count);
        var ship = await _db.Ships.OrderBy(s => s.Id).Skip(skip).FirstAsync();
        var shipClass = await _db.ShipClasses.FindAsync(ship.ClassId);
        var captain = await _db.Captains.FindAsync(ship.CaptainId);
        return Ok(new
        {
            ship.Id,
            ship.Name,
            ship.Description,
            ship.ImageUrl,
            ship.YearCommissioned,
            Class = shipClass != null ? new { shipClass.Id, shipClass.Name, shipClass.Type, shipClass.Country } : null,
            Captain = captain != null ? new { captain.Id, captain.Name, captain.Rank, captain.ServiceYears, captain.ImageUrl } : null
        });
    }

    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<object>>> SearchShips([FromQuery] string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<object>());

        var ships = await _db.Ships
            .Where(s => s.Name.Contains(q) || s.Description.Contains(q))
            .ToListAsync();

        var result = new List<object>();
        foreach (var ship in ships)
        {
            var shipClass = await _db.ShipClasses.FindAsync(ship.ClassId);
            var captain = await _db.Captains.FindAsync(ship.CaptainId);
            result.Add(new
            {
                ship.Id,
                ship.Name,
                ship.Description,
                ship.ImageUrl,
                ship.YearCommissioned,
                Class = shipClass != null ? new { shipClass.Name, shipClass.Type, shipClass.Country } : null,
                Captain = captain != null ? new { captain.Name, captain.Rank } : null
            });
        }
        return Ok(result);
    }
}
