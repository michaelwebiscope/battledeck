using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ClassesController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;

    public ClassesController(NavalArchiveDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetClasses()
    {
        var classes = await _db.ShipClasses
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Type,
                c.Country,
                ShipCount = c.Ships.Count
            })
            .ToListAsync();
        return Ok(classes);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetClass(int id)
    {
        var shipClass = await _db.ShipClasses
            .Include(c => c.Ships)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (shipClass == null) return NotFound();

        return Ok(new
        {
            shipClass.Id,
            shipClass.Name,
            shipClass.Type,
            shipClass.Country,
            Ships = shipClass.Ships.Select(s => new { s.Id, s.Name, s.YearCommissioned })
        });
    }
}
