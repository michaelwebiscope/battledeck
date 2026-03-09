using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NavalArchive.Data;
using NavalArchive.Data.Models;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShipsController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly CacheInvalidationService _cacheInv;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(10);

    public ShipsController(NavalArchiveDbContext db, IHttpClientFactory http, IConfiguration config, IMemoryCache cache, CacheInvalidationService cacheInv)
    {
        _db = db;
        _http = http;
        _config = config;
        _cache = cache;
        _cacheInv = cacheInv;
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

    /// <summary>List ships from DB. Single query with JOINs. Paginated for scale (12k+ ships). Cached 10 min.</summary>
    [HttpGet]
    public async Task<ActionResult<object>> GetShips([FromQuery] string? country, [FromQuery] string? type, [FromQuery] int? yearMin, [FromQuery] int? yearMax, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var v = _cacheInv.GetShipsVersion();
        var key = $"ships:list:v{v}:{page}:{pageSize}:{country ?? ""}:{type ?? ""}:{yearMin}:{yearMax}";

        var result = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
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
                var ids = await classQuery.Select(c => c.Id).ToListAsync(ct);
                query = query.Where(s => ids.Contains(s.ClassId));
                if (yearMin.HasValue) query = query.Where(s => s.YearCommissioned >= yearMin.Value);
                if (yearMax.HasValue) query = query.Where(s => s.YearCommissioned <= yearMax.Value);
            }

            var total = await query.CountAsync(ct);
            var ships = await query
                .OrderBy(s => s.Name)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            var items = ships.Select(s => new
            {
                s.Id,
                s.Name,
                s.Description,
                s.ImageUrl,
                s.ImageVersion,
                s.YearCommissioned,
                VideoUrl = (string?)null,
                Class = s.Class != null ? new { s.Class.Name, s.Class.Type, s.Class.Country } : null,
                Captain = s.Captain != null ? new { s.Captain.Name, s.Captain.Rank, s.Captain.ServiceYears } : null
            }).ToList();

            return new { items, total, page, pageSize };
        });

        return Ok(result);
    }

    /// <summary>Lightweight id+name list for dropdowns (compare, etc). Single query, no JOINs. Cached 10 min.</summary>
    [HttpGet("choices")]
    public async Task<ActionResult<IEnumerable<object>>> GetShipChoices([FromQuery] string? q, [FromQuery] int limit = 1000, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var v = _cacheInv.GetShipsVersion();
        var key = $"ships:choices:v{v}:{(q ?? "").Trim()}:{limit}";
        var list = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
            IQueryable<Ship> query = _db.Ships.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(s => s.Name.Contains(term));
            }
            return await query.OrderBy(s => s.Name).Take(limit).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        });
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetShip(int id, CancellationToken ct = default)
    {
        var v = _cacheInv.GetShipsVersion();
        var key = $"ships:id:v{v}:{id}";
        var result = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
            var ship = await _db.Ships.FindAsync(id);
            if (ship == null) return null;

            var shipClass = await _db.ShipClasses.FindAsync(ship.ClassId);
            var captain = await _db.Captains.FindAsync(ship.CaptainId);
            var videoUrl = await VideoExistsAsync(ship.Id) ? $"/api/videos/{ship.Id}" : (string?)null;

            return new
            {
                ship.Id,
                ship.Name,
                ship.Description,
                ship.ImageUrl,
                ship.ImageVersion,
                ship.YearCommissioned,
                VideoUrl = videoUrl,
                Class = shipClass != null ? new { shipClass.Id, shipClass.Name, shipClass.Type, shipClass.Country } : null,
                Captain = captain != null ? new { captain.Id, captain.Name, captain.Rank, captain.ServiceYears, captain.ImageUrl, captain.ImageVersion } : null
            };
        });
        if (result == null) return NotFound();
        return Ok(result);
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
    public async Task<ActionResult<IEnumerable<object>>> SearchShips([FromQuery] string? q, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<object>());

        limit = Math.Clamp(limit, 1, 200);
        var term = q.Trim();
        var v = _cacheInv.GetShipsVersion();
        var key = $"ships:search:v{v}:{term}:{limit}";

        var list = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry!.AbsoluteExpirationRelativeToNow = CacheExpiration;
            var ships = await _db.Ships
                .Include(s => s.Class)
                .Include(s => s.Captain)
                .AsNoTracking()
                .Where(s => s.Name.Contains(term) || s.Description.Contains(term))
                .OrderBy(s => s.Name)
                .Take(limit)
                .ToListAsync(ct);

            return ships.Select(s => new
            {
                s.Id,
                s.Name,
                s.Description,
                s.ImageUrl,
                s.ImageVersion,
                s.YearCommissioned,
                Class = s.Class != null ? new { s.Class.Name, s.Class.Type, s.Class.Country } : null,
                Captain = s.Captain != null ? new { s.Captain.Name, s.Captain.Rank } : null
            }).ToList();
        });

        return Ok(list);
    }

    /// <summary>Create a new ship. Uses ClassId=1 and CaptainId=1 if not specified. If ClassName is provided and class doesn't exist, creates it automatically.</summary>
    [HttpPost]
    public async Task<ActionResult<object>> CreateShip([FromBody] CreateShipRequest? request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name required" });
        int? classId;
        if (!string.IsNullOrWhiteSpace(request.ClassName))
        {
            var name = request.ClassName.Trim();
            var existing = await _db.ShipClasses.FirstOrDefaultAsync(c => c.Name == name, ct);
            if (existing != null)
                classId = existing.Id;
            else
            {
                var newClass = new ShipClass { Name = name, Type = "Unknown", Country = "" };
                _db.ShipClasses.Add(newClass);
                await _db.SaveChangesAsync(ct);
                classId = newClass.Id;
            }
        }
        else
        {
            classId = request.ClassId ?? 1;
        }
        var resolvedClassId = classId ?? 1;
        var captainId = request.CaptainId ?? 1;
        var ship = new Ship
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? "",
            ImageUrl = request.ImageUrl?.Trim() ?? "",
            ClassId = resolvedClassId,
            CaptainId = captainId,
            YearCommissioned = request.YearCommissioned ?? 0
        };
        _db.Ships.Add(ship);
        await _db.SaveChangesAsync(ct);
        _cacheInv.OnShipUpdated();
        var shipClass = await _db.ShipClasses.FindAsync(ship.ClassId);
        var captain = await _db.Captains.FindAsync(ship.CaptainId);
        return Ok(new
        {
            ship.Id,
            ship.Name,
            ship.Description,
            ship.ImageUrl,
            ship.YearCommissioned,
            Class = shipClass != null ? new { shipClass.Id, shipClass.Name } : null,
            Captain = captain != null ? new { captain.Id, captain.Name } : null
        });
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
        _cacheInv.OnShipUpdated();
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

    /// <summary>Delete a ship from the database (admin).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteShip(int id, CancellationToken ct = default)
    {
        var ship = await _db.Ships.FindAsync(new object[] { id }, ct);
        if (ship == null) return NotFound();
        _db.Ships.Remove(ship);
        await _db.SaveChangesAsync(ct);
        _cacheInv.OnShipUpdated();
        return Ok(new { deleted = true, id });
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

public class CreateShipRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public int? YearCommissioned { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("classId")]
    public int? ClassId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("className")]
    public string? ClassName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("captainId")]
    public int? CaptainId { get; set; }
}
