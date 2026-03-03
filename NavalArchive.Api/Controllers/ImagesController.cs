using Microsoft.AspNetCore.Mvc;
using NavalArchive.Api.Data;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;
    private readonly ImageStorageService _storage;

    public ImagesController(NavalArchiveDbContext db, ImageStorageService storage)
    {
        _db = db;
        _storage = storage;
    }

    /// <summary>Serve ship image from database. Id is ship id.</summary>
    [HttpGet("ship/{id:int}")]
    public async Task<IActionResult> GetShipImage(int id)
    {
        var ship = await _db.Ships.FindAsync(id);
        if (ship == null) return NotFound();

        if (ship.ImageData != null && ship.ImageData.Length > 0)
        {
            var ct = ship.ImageContentType ?? "image/jpeg";
            return File(ship.ImageData, ct);
        }

        return NotFound();
    }

    /// <summary>Serve captain image from database. Id is captain id.</summary>
    [HttpGet("captain/{id:int}")]
    public async Task<IActionResult> GetCaptainImage(int id)
    {
        var captain = await _db.Captains.FindAsync(id);
        if (captain == null) return NotFound();

        if (captain.ImageData != null && captain.ImageData.Length > 0)
        {
            var ct = captain.ImageContentType ?? "image/jpeg";
            return File(captain.ImageData, ct);
        }

        return NotFound();
    }

    /// <summary>Legacy: ship image by id. Serves from DB when present.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetImage(int id)
    {
        var ship = await _db.Ships.FindAsync(id);
        if (ship == null) return NotFound();

        if (ship.ImageData != null && ship.ImageData.Length > 0)
        {
            var ct = ship.ImageContentType ?? "image/jpeg";
            return File(ship.ImageData, ct);
        }

        return NotFound();
    }

    /// <summary>Audit: which ships/captains have images, which are missing.</summary>
    [HttpGet("audit")]
    public async Task<ActionResult<ImageAuditResult>> GetAudit()
    {
        return await _storage.GetAuditAsync(_db);
    }

    /// <summary>Populate all images from ImageUrl into ImageData.</summary>
    [HttpPost("populate")]
    public async Task<ActionResult<PopulateResult>> PopulateAll()
    {
        var result = await _storage.PopulateAllAsync(_db);
        return Ok(result);
    }

    /// <summary>Populate a single ship image.</summary>
    [HttpPost("populate/ship/{id:int}")]
    public async Task<IActionResult> PopulateShip(int id)
    {
        var (stored, reason, _) = await _storage.PopulateShipImageAsync(_db, id);
        return stored ? Ok() : NotFound(new { reason });
    }

    /// <summary>Populate a single captain image.</summary>
    [HttpPost("populate/captain/{id:int}")]
    public async Task<IActionResult> PopulateCaptain(int id)
    {
        var (stored, reason, _) = await _storage.PopulateCaptainImageAsync(_db, id);
        return stored ? Ok() : NotFound(new { reason });
    }

    /// <summary>Upload ship image from external source (e.g. populate script running where Wikipedia is reachable).</summary>
    [HttpPost("ship/{id:int}/upload")]
    public async Task<IActionResult> UploadShipImage(int id)
    {
        var ship = await _db.Ships.FindAsync(id);
        if (ship == null) return NotFound();

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var data = ms.ToArray();
        if (data.Length < 100) return BadRequest(new { error = "Image too small" });

        var ct = Request.ContentType ?? "image/jpeg";
        if (ct.Contains(";")) ct = ct.Split(';')[0].Trim();

        ship.ImageData = data;
        ship.ImageContentType = ct;
        await _db.SaveChangesAsync();
        return Ok(new { stored = data.Length });
    }
}
