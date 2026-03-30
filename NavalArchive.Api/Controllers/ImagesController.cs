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
    private readonly CacheInvalidationService _cacheInv;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    public ImagesController(NavalArchiveDbContext db, ImageStorageService storage, CacheInvalidationService cacheInv, IHttpClientFactory http, IConfiguration config)
    {
        _db = db;
        _storage = storage;
        _cacheInv = cacheInv;
        _http = http;
        _config = config;
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

    /// <summary>Test API keys for configured image sources. Routes to Java ImagePopulator.</summary>
    [HttpPost("test-keys")]
    public async Task<IActionResult> TestKeys([FromBody] PopulateRequest? request = null, CancellationToken ct = default)
    {
        request = await WithDbSourcesAsync(request, ct);
        return await ProxyPostAsync(JavaBaseUrl + "/test-keys", request, ct, TimeSpan.FromSeconds(30));
    }

    private string JavaBaseUrl => (_config["ImagePopulator:Url"] ?? "http://localhost:5099").TrimEnd('/');

    private async Task<PopulateRequest?> WithDbSourcesAsync(PopulateRequest? request, CancellationToken ct)
    {
        if (request == null) request = new PopulateRequest();
        if (request.ImageSources == null || request.ImageSources.Count == 0)
        {
            var dbSources = await _db.ImageSources.OrderBy(s => s.SortOrder).ToListAsync(ct);
            request.ImageSources = dbSources.Select(ImageSourcesController.ToConfig).ToList();
        }
        return request;
    }

    private static readonly JsonSerializerOptions _camelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private async Task<IActionResult> ProxyPostAsync(string javaUrl, object? body, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = timeout ?? TimeSpan.FromMinutes(10);
            var json = JsonSerializer.Serialize(body, _camelCase);
            using var res = await client.PostAsync(javaUrl, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
            var responseBody = await res.Content.ReadAsStringAsync(ct);
            return new ContentResult
            {
                Content = responseBody,
                ContentType = "application/json",
                StatusCode = (int)res.StatusCode
            };
        }
        catch (HttpRequestException ex) { return StatusCode(503, new { message = "ImagePopulator unreachable.", detail = ex.Message }); }
        catch (TaskCanceledException)   { return StatusCode(504, new { message = "ImagePopulator request timed out." }); }
    }

    /// <summary>Returns ships and captains without cached images (used by Java populate).</summary>
    [HttpGet("populate-queue")]
    public async Task<ActionResult> GetPopulateQueue(CancellationToken ct = default)
    {
        var ships = await _db.Ships
            .Where(s => s.ImageData == null && !s.ImageManuallySet)
            .Select(s => new { s.Id, s.Name, s.ImageUrl })
            .ToListAsync(ct);
        var captains = await _db.Captains
            .Where(c => c.ImageData == null && !c.ImageManuallySet)
            .Select(c => new { c.Id, c.Name, c.ImageUrl })
            .ToListAsync(ct);
        return Ok(new { ships, captains });
    }

    /// <summary>Populate all images. Routes to Java ImagePopulator.</summary>
    [HttpPost("populate")]
    public async Task<IActionResult> PopulateAll([FromBody] PopulateRequest? request = null, CancellationToken ct = default)
    {
        request = await WithDbSourcesAsync(request, ct);
        var result = await ProxyPostAsync(JavaBaseUrl + "/populate", request, ct);
        _cacheInv.OnShipUpdated();
        _cacheInv.OnCaptainUpdated();
        return result;
    }

    /// <summary>Streaming populate: SSE progress forwarded from Java ImagePopulator.</summary>
    [HttpPost("populate/stream")]
    public async Task PopulateStream([FromBody] PopulateRequest? request = null, CancellationToken ct = default)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        request = await WithDbSourcesAsync(request, ct);
        try
        {
            var client = _http.CreateClient();
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var javaReq = new HttpRequestMessage(HttpMethod.Post, JavaBaseUrl + "/populate/stream")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            using var javaResp = await client.SendAsync(javaReq, HttpCompletionOption.ResponseHeadersRead, ct);
            using var stream = await javaResp.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line != null)
                {
                    await Response.WriteAsync(line + "\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            var msg = JsonSerializer.Serialize(new { type = "error", data = ex.Message });
            await Response.WriteAsync($"data: {msg}\n\n", ct);
        }
        _cacheInv.OnShipUpdated();
        _cacheInv.OnCaptainUpdated();
    }

    /// <summary>Populate a single ship image. Routes to Java ImagePopulator.</summary>
    [HttpPost("populate/ship/{id:int}")]
    public async Task<IActionResult> PopulateShip(int id, [FromBody] PopulateRequest? request = null, CancellationToken ct = default)
    {
        request = await WithDbSourcesAsync(request, ct);
        var result = await ProxyPostAsync(JavaBaseUrl + "/populate/ship/" + id, request, ct, TimeSpan.FromMinutes(2));
        _cacheInv.OnShipUpdated();
        return result;
    }

    /// <summary>Populate a single captain image. Routes to Java ImagePopulator.</summary>
    [HttpPost("populate/captain/{id:int}")]
    public async Task<IActionResult> PopulateCaptain(int id, [FromBody] PopulateRequest? request = null, CancellationToken ct = default)
    {
        request = await WithDbSourcesAsync(request, ct);
        var result = await ProxyPostAsync(JavaBaseUrl + "/populate/captain/" + id, request, ct, TimeSpan.FromMinutes(2));
        _cacheInv.OnCaptainUpdated();
        return result;
    }

    /// <summary>Trigger the Java ImagePopulator (Wikipedia) to run. API calls the Java entity at ImagePopulator:Url/run.</summary>
    [HttpPost("populate/wikipedia")]
    public async Task<IActionResult> TriggerWikipediaPopulate(CancellationToken ct = default)
    {
        var baseUrl = _config["ImagePopulator:Url"] ?? "http://localhost:5099";
        var url = baseUrl.TrimEnd('/') + "/run";
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var res = await client.PostAsync(url, null, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.Accepted)
                return Accepted(new { message = "Wikipedia populate started. The Java ImagePopulator is fetching ship images and uploading to the API." });
            if (res.StatusCode == System.Net.HttpStatusCode.Conflict)
                return StatusCode(409, new { message = "ImagePopulator is already running a populate job." });
            var body = await res.Content.ReadAsStringAsync(ct);
            return StatusCode((int)res.StatusCode, new { message = body ?? res.ReasonPhrase });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(503, new { message = "ImagePopulator unreachable. Ensure the Java listener service is running on " + baseUrl + ".", detail = ex.Message });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { message = "ImagePopulator request timed out." });
        }
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

    /// <summary>Search images by query. Routes to Java ImagePopulator.</summary>
    [HttpPost("search")]
    public async Task<IActionResult> SearchImages([FromBody] ImageSearchRequest? request = null, CancellationToken ct = default)
    {
        var sources = await _db.ImageSources.AsNoTracking().OrderBy(s => s.SortOrder).ToListAsync(ct);
        var javaRequest = new
        {
            Query = request?.Query ?? "battleship",
            MaxCount = request?.MaxCount ?? 12,
            Provider = request?.Provider,
            PexelsApiKey = request?.PexelsApiKey,
            PixabayApiKey = request?.PixabayApiKey,
            UnsplashAccessKey = request?.UnsplashAccessKey,
            GoogleApiKey = request?.GoogleApiKey,
            GoogleCseId = request?.GoogleCseId,
            ImageSources = sources.Select(ImageSourcesController.ToConfig).ToList()
        };
        return await ProxyPostAsync(JavaBaseUrl + "/search", javaRequest, ct, TimeSpan.FromSeconds(30));
    }

    /// <summary>Set ship image from URL (fetch and store).</summary>
    [HttpPost("ship/{id:int}/from-url")]
    public async Task<IActionResult> SetShipImageFromUrl(int id, [FromBody] SetImageFromUrlRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Url)) return BadRequest(new { error = "Url required" });
        var result = await ProxyPostAsync(
            JavaBaseUrl + "/set-from-url/ship/" + id,
            new { url = request.Url },
            ct,
            TimeSpan.FromMinutes(2)
        );
        if (result is ContentResult cr && cr.StatusCode is >= 200 and < 300)
            _cacheInv.OnShipUpdated();
        return result;
    }

    /// <summary>Set captain image from URL.</summary>
    [HttpPost("captain/{id:int}/from-url")]
    public async Task<IActionResult> SetCaptainImageFromUrl(int id, [FromBody] SetImageFromUrlRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Url)) return BadRequest(new { error = "Url required" });
        var result = await ProxyPostAsync(
            JavaBaseUrl + "/set-from-url/captain/" + id,
            new { url = request.Url },
            ct,
            TimeSpan.FromMinutes(2)
        );
        if (result is ContentResult cr && cr.StatusCode is >= 200 and < 300)
            _cacheInv.OnCaptainUpdated();
        return result;
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
        ship.ImageManuallySet = true;
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
        captain.ImageManuallySet = true;
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
