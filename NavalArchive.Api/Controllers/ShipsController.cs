using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Data;
using NavalArchive.Data.Models;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShipsController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    public ShipsController(NavalArchiveDbContext db, IHttpClientFactory http, IConfiguration config)
    {
        _db = db;
        _http = http;
        _config = config;
    }

    private async Task<bool> VideoExistsAsync(int shipId)
    {
        var url = _config["VideoService:Url"] ?? "http://localhost:5020";
        try
        {
            var client = _http.CreateClient();
            var res = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{url.TrimEnd('/')}/api/videos/{shipId}"));
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>List ships from DB. Single query with JOINs. Paginated for scale (12k+ ships).</summary>
    [HttpGet]
    public async Task<ActionResult<object>> GetShips([FromQuery] string? country, [FromQuery] string? type, [FromQuery] int? yearMin, [FromQuery] int? yearMax, [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var query = _db.Ships
            .Include(s => s.Class)
            .Include(s => s.Captain)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(country) || !string.IsNullOrWhiteSpace(type) || yearMin.HasValue || yearMax.HasValue)
        {
            var classQuery = _db.ShipClasses.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(country))
                classQuery = classQuery.Where(c => c.Country == country);
            if (!string.IsNullOrWhiteSpace(type))
                classQuery = classQuery.Where(c => c.Type == type);
            var ids = await classQuery.Select(c => c.Id).ToListAsync();
            query = query.Where(s => ids.Contains(s.ClassId));
            if (yearMin.HasValue) query = query.Where(s => s.YearCommissioned >= yearMin.Value);
            if (yearMax.HasValue) query = query.Where(s => s.YearCommissioned <= yearMax.Value);
        }

        var total = await query.CountAsync();
        var ships = await query
            .OrderBy(s => s.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = ships.Select(s => new
        {
            s.Id,
            s.Name,
            s.Description,
            s.ImageUrl,
            s.ImageVersion,
            s.YearCommissioned,
            VideoUrl = (string?)null, // Check only on single-ship view
            Class = s.Class != null ? new { s.Class.Name, s.Class.Type, s.Class.Country } : null,
            Captain = s.Captain != null ? new { s.Captain.Name, s.Captain.Rank, s.Captain.ServiceYears } : null
        }).ToList();

        return Ok(new { items, total, page, pageSize });
    }

    /// <summary>Lightweight id+name list for dropdowns (compare, etc). Single query, no JOINs.</summary>
    [HttpGet("choices")]
    public async Task<ActionResult<IEnumerable<object>>> GetShipChoices([FromQuery] string? q, [FromQuery] int limit = 1000)
    {
        limit = Math.Clamp(limit, 1, 5000);
        IQueryable<Ship> query = _db.Ships.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(s => s.Name.Contains(term));
        }
        var list = await query.OrderBy(s => s.Name).Take(limit).Select(s => new { s.Id, s.Name }).ToListAsync();
        return Ok(list);
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
            ship.ImageVersion,
            ship.YearCommissioned,
            VideoUrl = await VideoExistsAsync(ship.Id) ? $"/api/videos/{ship.Id}" : (string?)null,
            Class = shipClass != null ? new { shipClass.Id, shipClass.Name, shipClass.Type, shipClass.Country } : null,
            Captain = captain != null ? new { captain.Id, captain.Name, captain.Rank, captain.ServiceYears, captain.ImageUrl, captain.ImageVersion } : null
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
            ship.ImageVersion,
            ship.YearCommissioned,
            VideoUrl = await VideoExistsAsync(ship.Id) ? $"/api/videos/{ship.Id}" : (string?)null,
            Class = shipClass != null ? new { shipClass.Id, shipClass.Name, shipClass.Type, shipClass.Country } : null,
            Captain = captain != null ? new { captain.Id, captain.Name, captain.Rank, captain.ServiceYears, captain.ImageUrl, captain.ImageVersion } : null
        });
    }

    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<object>>> SearchShips([FromQuery] string? q, [FromQuery] int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<object>());

        limit = Math.Clamp(limit, 1, 200);
        var term = q.Trim();

        var ships = await _db.Ships
            .Include(s => s.Class)
            .Include(s => s.Captain)
            .AsNoTracking()
            .Where(s => s.Name.Contains(term) || s.Description.Contains(term))
            .OrderBy(s => s.Name)
            .Take(limit)
            .ToListAsync();

        return Ok(ships.Select(s => new
        {
            s.Id,
            s.Name,
            s.Description,
            s.ImageUrl,
            s.ImageVersion,
            s.YearCommissioned,
            Class = s.Class != null ? new { s.Class.Name, s.Class.Type, s.Class.Country } : null,
            Captain = s.Captain != null ? new { s.Captain.Name, s.Captain.Rank } : null
        }));
    }

    /// <summary>Update ship (admin). Edits persist to DB and appear everywhere.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<object>> UpdateShip(int id, [FromBody] UpdateShipRequest? request, CancellationToken ct = default)
    {
        if (request == null) return BadRequest();
        var ship = await _db.Ships.FindAsync(id);
        if (ship == null) return NotFound();
        if (request.Name != null) ship.Name = request.Name.Trim();
        if (request.Description != null) ship.Description = request.Description;
        if (request.YearCommissioned.HasValue) ship.YearCommissioned = request.YearCommissioned.Value;
        if (request.ClassId.HasValue) ship.ClassId = request.ClassId.Value;
        if (request.CaptainId.HasValue) ship.CaptainId = request.CaptainId.Value;
        await _db.SaveChangesAsync(ct);
        var shipClass = await _db.ShipClasses.FindAsync(ship.ClassId);
        var captain = await _db.Captains.FindAsync(ship.CaptainId);
        return Ok(new
        {
            ship.Id,
            ship.Name,
            ship.Description,
            ship.YearCommissioned,
            Class = shipClass != null ? new { shipClass.Id, shipClass.Name } : null,
            Captain = captain != null ? new { captain.Id, captain.Name } : null
        });
    }
}

public class UpdateShipRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? YearCommissioned { get; set; }
    public int? ClassId { get; set; }
    public int? CaptainId { get; set; }
}
