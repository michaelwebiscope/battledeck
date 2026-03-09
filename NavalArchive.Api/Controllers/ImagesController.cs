using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Models;
using NavalArchive.Data;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;
    private readonly ImageStorageService _storage;
    private readonly ImageSearchService _imageSearch;
    private readonly CacheInvalidationService _cacheInv;

    public ImagesController(NavalArchiveDbContext db, ImageStorageService storage, ImageSearchService imageSearch, CacheInvalidationService cacheInv)
    {
        _db = db;
        _storage = storage;
        _imageSearch = imageSearch;
        _cacheInv = cacheInv;
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

    /// <summary>Test API keys for configured image sources only. Returns per-source success/failure.</summary>
    [HttpPost("test-keys")]
    public async Task<ActionResult<List<KeyTestResult>>> TestKeys([FromBody] PopulateRequest? request = null, CancellationToken ct = default)
    {
        var keys = BuildImageSearchKeys(request);
        var sources = await _db.ImageSources.AsNoTracking().OrderBy(s => s.SortOrder).ToListAsync(ct);
        var sourceConfigs = sources.Select(ImageSourcesController.ToConfig).ToList();
        var results = await _imageSearch.TestKeysAsync(keys, sourceConfigs, ct);
        return Ok(results);
    }

    private static ImageSearchKeys? BuildImageSearchKeys(PopulateRequest? request)
    {
        if (request == null) return null;
        var hasKeys = (request.PexelsApiKey != null || request.PixabayApiKey != null || request.UnsplashAccessKey != null || request.GoogleApiKey != null) ||
            (request.CustomKeys != null && request.CustomKeys.Count > 0);
        if (!hasKeys) return null;
        return new ImageSearchKeys(request.PexelsApiKey, request.PixabayApiKey, request.UnsplashAccessKey, request.GoogleApiKey, request.GoogleCseId, request.CustomKeys);
    }

    private async Task<PopulateOptions?> BuildPopulateOptionsAsync(PopulateRequest? request, CancellationToken ct = default)
    {
        if (request == null) return null;
        var sources = request.ImageSources;
        if (sources == null || sources.Count == 0)
        {
            var dbSources = await _db.ImageSources.OrderBy(s => s.SortOrder).ToListAsync(ct);
            sources = dbSources.Select(ImageSourcesController.ToConfig).ToList();
        }
        var hasOpts = request.ShipSearchPrefix != null || request.CaptainSearchPrefix != null || (sources != null && sources.Count > 0);
        if (!hasOpts) return null;
        return new PopulateOptions(request.ShipSearchPrefix, request.CaptainSearchPrefix, sources);
    }

    /// <summary>Populate all images from ImageUrl into ImageData. Uses image sources entity from DB when not in request.</summary>
    [HttpPost("populate")]
    public async Task<ActionResult<PopulateResult>> PopulateAll([FromBody] PopulateRequest? request = null, CancellationToken ct = default)
    {
        var keys = BuildImageSearchKeys(request);
        var options = await BuildPopulateOptionsAsync(request, ct);
        var result = await _storage.PopulateAllAsync(_db, default, keys, options);
        _cacheInv.OnShipUpdated();
        _cacheInv.OnCaptainUpdated();
        return Ok(result);
    }

    /// <summary>Streaming populate: Server-Sent Events with progress after each ship/captain.</summary>
    [HttpPost("populate/stream")]
    public async Task PopulateStream([FromBody] PopulateRequest? request = null, CancellationToken ct = default)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        var keys = BuildImageSearchKeys(request);
        var options = await BuildPopulateOptionsAsync(request, ct);
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await foreach (var evt in _storage.PopulateAllStreamAsync(_db, ct, keys, options))
        {
            var json = JsonSerializer.Serialize(new { evt.Type, evt.Data }, jsonOpts);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        _cacheInv.OnShipUpdated();
        _cacheInv.OnCaptainUpdated();
    }

    /// <summary>Populate a single ship image.</summary>
    [HttpPost("populate/ship/{id:int}")]
    public async Task<IActionResult> PopulateShip(int id)
    {
        var (stored, reason, _) = await _storage.PopulateShipImageAsync(_db, id);
        if (stored) _cacheInv.OnShipUpdated();
        return stored ? Ok() : NotFound(new { reason });
    }

    /// <summary>Populate a single captain image.</summary>
    [HttpPost("populate/captain/{id:int}")]
    public async Task<IActionResult> PopulateCaptain(int id)
    {
        var (stored, reason, _) = await _storage.PopulateCaptainImageAsync(_db, id);
        if (stored) _cacheInv.OnCaptainUpdated();
        return stored ? Ok() : NotFound(new { reason });
    }

    /// <summary>Delete ship image. Clears ImageData, ImageUrl, and ImageManuallySet so sync can repopulate.</summary>
    [HttpDelete("ship/{id:int}")]
    public async Task<IActionResult> DeleteShipImage(int id)
    {
        var ship = await _db.Ships.FindAsync(id);
        if (ship == null) return NotFound();
        ship.ImageData = null;
        ship.ImageContentType = null;
        ship.ImageUrl = "";
        ship.ImageManuallySet = false;
        ship.ImageVersion++;
        await _db.SaveChangesAsync();
        _cacheInv.OnShipUpdated();
        return Ok(new { deleted = true });
    }

    /// <summary>Delete captain image. Clears ImageData, ImageUrl, and ImageManuallySet so sync can repopulate.</summary>
    [HttpDelete("captain/{id:int}")]
    public async Task<IActionResult> DeleteCaptainImage(int id)
    {
        var captain = await _db.Captains.FindAsync(id);
        if (captain == null) return NotFound();
        captain.ImageData = null;
        captain.ImageContentType = null;
        captain.ImageUrl = null;
        captain.ImageManuallySet = false;
        captain.ImageVersion++;
        await _db.SaveChangesAsync();
        _cacheInv.OnCaptainUpdated();
        return Ok(new { deleted = true });
    }

    /// <summary>Search images by query. Uses configured image sources (All = try all enabled sources). Provider filters to one source.</summary>
    [HttpPost("search")]
    public async Task<ActionResult<List<string>>> SearchImages([FromBody] ImageSearchRequest? request = null, CancellationToken ct = default)
    {
        var q = request?.Query ?? "battleship";
        var keys = request != null && (request.PexelsApiKey != null || request.PixabayApiKey != null || request.UnsplashAccessKey != null || request.GoogleApiKey != null)
            ? new ImageSearchKeys(request.PexelsApiKey, request.PixabayApiKey, request.UnsplashAccessKey, request.GoogleApiKey, request.GoogleCseId)
            : null;
        var sources = await _db.ImageSources.AsNoTracking().OrderBy(s => s.SortOrder).ToListAsync(ct);
        var sourceConfigs = sources.Select(ImageSourcesController.ToConfig).ToList();
        var (urls, source) = await _imageSearch.FindImageUrlsAsync(q, request?.MaxCount ?? 12, ct, keys, null, request?.Provider, sourceConfigs);
        return Ok(new { source = source ?? "", urls });
    }

    /// <summary>Set ship image from URL (fetch and store).</summary>
    [HttpPost("ship/{id:int}/from-url")]
    public async Task<IActionResult> SetShipImageFromUrl(int id, [FromBody] SetImageFromUrlRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Url)) return BadRequest(new { error = "Url required" });
        var ship = await _db.Ships.FindAsync(id);
        if (ship == null) return NotFound();
        var (data, contentType, _, error) = await _storage.FetchImageAsync(request.Url, ct);
        if (data == null || data.Length < 100) return BadRequest(new { error = error ?? "Failed to fetch image" });
        ship.ImageData = data;
        ship.ImageContentType = contentType ?? "image/jpeg";
        ship.ImageUrl = request.Url;
        ship.ImageManuallySet = true;
        ship.ImageVersion++;
        await _db.SaveChangesAsync(ct);
        _cacheInv.OnShipUpdated();
        return Ok(new { stored = data.Length });
    }

    /// <summary>Set captain image from URL.</summary>
    [HttpPost("captain/{id:int}/from-url")]
    public async Task<IActionResult> SetCaptainImageFromUrl(int id, [FromBody] SetImageFromUrlRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Url)) return BadRequest(new { error = "Url required" });
        var captain = await _db.Captains.FindAsync(id);
        if (captain == null) return NotFound();
        var (data, contentType, _, error) = await _storage.FetchImageAsync(request.Url, ct);
        if (data == null || data.Length < 100) return BadRequest(new { error = error ?? "Failed to fetch image" });
        captain.ImageData = data;
        captain.ImageContentType = contentType ?? "image/jpeg";
        captain.ImageUrl = request.Url;
        captain.ImageManuallySet = true;
        captain.ImageVersion++;
        await _db.SaveChangesAsync(ct);
        _cacheInv.OnCaptainUpdated();
        return Ok(new { stored = data.Length });
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
        ship.ImageVersion++;
        await _db.SaveChangesAsync();
        _cacheInv.OnShipUpdated();
        return Ok(new { stored = data.Length });
    }

    /// <summary>Upload captain image. Stores in DB (ImageData).</summary>
    [HttpPost("captain/{id:int}/upload")]
    public async Task<IActionResult> UploadCaptainImage(int id)
    {
        var captain = await _db.Captains.FindAsync(id);
        if (captain == null) return NotFound();

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var data = ms.ToArray();
        if (data.Length < 100) return BadRequest(new { error = "Image too small" });

        var ct = Request.ContentType ?? "image/jpeg";
        if (ct.Contains(";")) ct = ct.Split(';')[0].Trim();

        captain.ImageData = data;
        captain.ImageContentType = ct;
        captain.ImageVersion++;
        await _db.SaveChangesAsync();
        _cacheInv.OnCaptainUpdated();
        return Ok(new { stored = data.Length });
    }
}

/// <summary>Request body for populate endpoint. Optional API keys, search prefixes, and configurable image sources.</summary>
public class PopulateRequest
{
    public string? PexelsApiKey { get; set; }
    public string? PixabayApiKey { get; set; }
    public string? UnsplashAccessKey { get; set; }
    public string? GoogleApiKey { get; set; }
    public string? GoogleCseId { get; set; }
    public string? ShipSearchPrefix { get; set; }
    public string? CaptainSearchPrefix { get; set; }
    public List<ImageSourceConfig>? ImageSources { get; set; }
    public Dictionary<string, string>? CustomKeys { get; set; }
}

/// <summary>Image search request.</summary>
public record ImageSearchRequest(string? Query, int? MaxCount, string? Provider, string? PexelsApiKey, string? PixabayApiKey, string? UnsplashAccessKey, string? GoogleApiKey, string? GoogleCseId);

/// <summary>Set image from URL.</summary>
public record SetImageFromUrlRequest(string Url);
