using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;

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
            Ships = captain.Ships.Select(s => new { s.Id, s.Name, s.YearCommissioned })
        });
    }
}
