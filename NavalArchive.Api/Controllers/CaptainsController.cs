using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Data;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CaptainsController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;

    public CaptainsController(NavalArchiveDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetCaptains()
    {
        var captains = await _db.Captains
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
            .ToListAsync();
        return Ok(captains);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetCaptain(int id)
    {
        var captain = await _db.Captains
            .Include(c => c.Ships)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (captain == null) return NotFound();

        return Ok(new
        {
            captain.Id,
            captain.Name,
            captain.Rank,
            captain.ServiceYears,
            captain.ImageUrl,
            captain.ImageVersion,
            Ships = captain.Ships.Select(s => new { s.Id, s.Name, s.YearCommissioned })
        });
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
        return Ok(new { captain.Id, captain.Name, captain.Rank, captain.ServiceYears });
    }
}

public class UpdateCaptainRequest
{
    public string? Name { get; set; }
    public string? Rank { get; set; }
    public int? ServiceYears { get; set; }
}
