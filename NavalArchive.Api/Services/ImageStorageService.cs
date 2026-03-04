using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Services;

/// <summary>
/// Fetches images from ImageUrl and stores in ImageData. Provides audit of missing images.
/// </summary>
public class ImageStorageService
{
    private readonly IHttpClientFactory _http;
    private readonly ImageSearchService? _imageSearch;

    public ImageStorageService(IHttpClientFactory http, ImageSearchService imageSearch)
    {
        _http = http;
        _imageSearch = imageSearch;
    }

    /// <summary>Audit: which ships/captains have ImageUrl, ImageData, or are missing both.</summary>
    public async Task<ImageAuditResult> GetAuditAsync(NavalArchiveDbContext db, CancellationToken ct = default)
    {
        var ships = await db.Ships
            .Select(s => new { s.Id, s.Name, s.ImageUrl, HasImageData = s.ImageData != null })
            .ToListAsync(ct);
        var captains = await db.Captains
            .Select(c => new { c.Id, c.Name, c.ImageUrl, HasImageData = c.ImageData != null })
            .ToListAsync(ct);

        var shipsWithUrl = ships.Count(s => !string.IsNullOrWhiteSpace(s.ImageUrl));
        var shipsWithData = ships.Count(s => s.HasImageData);
        var shipsMissingData = ships.Where(s => !string.IsNullOrWhiteSpace(s.ImageUrl) && !s.HasImageData).ToList();
        var shipsMissingUrl = ships.Where(s => string.IsNullOrWhiteSpace(s.ImageUrl)).ToList();

        var captainsWithUrl = captains.Count(c => !string.IsNullOrWhiteSpace(c.ImageUrl));
        var captainsWithData = captains.Count(c => c.HasImageData);
        var captainsMissingData = captains.Where(c => !string.IsNullOrWhiteSpace(c.ImageUrl) && !c.HasImageData).ToList();
        var captainsMissingUrl = captains.Where(c => string.IsNullOrWhiteSpace(c.ImageUrl)).ToList();

        return new ImageAuditResult(
            Ships: new EntityAudit(ships.Count, shipsWithUrl, shipsWithData,
                shipsMissingUrl.Select(x => new MissingItem(x.Id, x.Name)).ToList(),
                shipsMissingData.Select(x => new MissingItem(x.Id, x.Name)).ToList()),
            Captains: new EntityAudit(captains.Count, captainsWithUrl, captainsWithData,
                captainsMissingUrl.Select(x => new MissingItem(x.Id, x.Name)).ToList(),
                captainsMissingData.Select(x => new MissingItem(x.Id, x.Name)).ToList())
        );
    }

    /// <summary>Fetch image from URL and store in DB. Returns (stored, reason, bytesStored).</summary>
    public async Task<(bool Stored, string? Reason, int BytesStored)> PopulateShipImageAsync(NavalArchiveDbContext db, int shipId, CancellationToken ct = default, ImageSearchKeys? keys = null)
    {
        var ship = await db.Ships.FindAsync(new object[] { shipId }, ct);
        if (ship == null) return (false, "Ship not found", 0);
        if (ship.ImageData != null) return (true, "Already cached", ship.ImageData.Length);

        string? urlToTry = ship.ImageUrl;
        var triedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(urlToTry) && _imageSearch != null && (keys != null || _imageSearch.IsConfigured))
        {
            var urls = await _imageSearch.FindImageUrlsAsync($"{ship.Name} battleship ship photo", 5, ct, keys);
            if (urls.Count > 0) { urlToTry = urls[0]; ship.ImageUrl = urlToTry; }
        }
        if (string.IsNullOrWhiteSpace(urlToTry)) return (false, "No ImageUrl", 0);

        var (data, contentType, statusCode, error) = await FetchImageAsync(urlToTry, ct);
        triedUrls.Add(urlToTry);
        if (data == null || data.Length < 100)
        {
            if (_imageSearch != null && (keys != null || _imageSearch.IsConfigured))
            {
                var queries = new[] { $"{ship.Name} battleship ship", $"{ship.Name} warship", $"{ship.Name} naval" };
                foreach (var q in queries)
                {
                    var urls = await _imageSearch.FindImageUrlsAsync(q, 5, ct, keys);
                    foreach (var u in urls.Where(u => !triedUrls.Contains(u)))
                    {
                        triedUrls.Add(u);
                        (data, contentType, statusCode, error) = await FetchImageAsync(u, ct);
                        if (data != null && data.Length >= 100)
                        {
                            ship.ImageUrl = u;
                            ship.ImageData = data;
                            ship.ImageContentType = contentType ?? "image/jpeg";
                            await db.SaveChangesAsync(ct);
                            return (true, "Fallback", data.Length);
                        }
                        await Task.Delay(300, ct);
                    }
                }
            }
            var reason = statusCode.HasValue ? $"HTTP {statusCode}" : (error ?? "Fetch failed");
            return (false, reason, 0);
        }

        ship.ImageData = data;
        ship.ImageContentType = contentType ?? "image/jpeg";
        await db.SaveChangesAsync(ct);
        return (true, null, data.Length);
    }

    /// <summary>Fetch image from URL and store in DB. Returns (stored, reason, bytesStored).</summary>
    public async Task<(bool Stored, string? Reason, int BytesStored)> PopulateCaptainImageAsync(NavalArchiveDbContext db, int captainId, CancellationToken ct = default, ImageSearchKeys? keys = null)
    {
        var captain = await db.Captains.FindAsync(new object[] { captainId }, ct);
        if (captain == null) return (false, "Captain not found", 0);
        if (captain.ImageData != null) return (true, "Already cached", captain.ImageData.Length);

        string? urlToTry = captain.ImageUrl;
        var triedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(urlToTry) && _imageSearch != null && (keys != null || _imageSearch.IsConfigured))
        {
            var urls = await _imageSearch.FindImageUrlsAsync($"{captain.Name} naval captain portrait", 5, ct, keys);
            if (urls.Count > 0) { urlToTry = urls[0]; captain.ImageUrl = urlToTry; }
        }
        if (string.IsNullOrWhiteSpace(urlToTry)) return (false, "No ImageUrl", 0);

        var (data, contentType, statusCode, error) = await FetchImageAsync(urlToTry, ct);
        triedUrls.Add(urlToTry);
        if (data == null || data.Length < 100)
        {
            if (_imageSearch != null && (keys != null || _imageSearch.IsConfigured))
            {
                var queries = new[] { $"{captain.Name} admiral portrait", $"{captain.Name} naval officer", $"{captain.Name} Kriegsmarine" };
                foreach (var q in queries)
                {
                    var urls = await _imageSearch.FindImageUrlsAsync(q, 5, ct, keys);
                    foreach (var u in urls.Where(u => !triedUrls.Contains(u)))
                    {
                        triedUrls.Add(u);
                        (data, contentType, statusCode, error) = await FetchImageAsync(u, ct);
                        if (data != null && data.Length >= 100)
                        {
                            captain.ImageUrl = u;
                            captain.ImageData = data;
                            captain.ImageContentType = contentType ?? "image/jpeg";
                            await db.SaveChangesAsync(ct);
                            return (true, "Fallback", data.Length);
                        }
                        await Task.Delay(300, ct);
                    }
                }
            }
            var reason = statusCode.HasValue ? $"HTTP {statusCode}" : (error ?? "Fetch failed");
            return (false, reason, 0);
        }

        captain.ImageData = data;
        captain.ImageContentType = contentType ?? "image/jpeg";
        await db.SaveChangesAsync(ct);
        return (true, null, data.Length);
    }

    /// <summary>Populate all ships and captains that have no ImageData. Optional keys for fallback when not in config.</summary>
    public async Task<PopulateResult> PopulateAllAsync(NavalArchiveDbContext db, CancellationToken ct = default, ImageSearchKeys? keys = null)
    {
        var ships = await db.Ships.Where(s => s.ImageData == null).ToListAsync(ct);
        var captains = await db.Captains.Where(c => c.ImageData == null).ToListAsync(ct);

        var shipResults = new List<PopulateItemResult>();
        var captainResults = new List<PopulateItemResult>();

        int idx = 0;
        foreach (var ship in ships)
        {
            idx++;
            var (stored, reason, bytes) = await PopulateShipImageAsync(db, ship.Id, ct, keys);
            shipResults.Add(new PopulateItemResult(ship.Id, ship.Name ?? "Ship " + ship.Id, stored ? "ok" : "fail", reason, idx, ships.Count, bytes > 0 ? bytes : null));
            await Task.Delay(200, ct);
        }

        idx = 0;
        foreach (var captain in captains)
        {
            idx++;
            var (stored, reason, bytes) = await PopulateCaptainImageAsync(db, captain.Id, ct, keys);
            captainResults.Add(new PopulateItemResult(captain.Id, captain.Name ?? "Captain " + captain.Id, stored ? "ok" : "fail", reason, idx, captains.Count, bytes > 0 ? bytes : null));
            await Task.Delay(200, ct);
        }

        return new PopulateResult(
            shipResults.Count(r => r.Status == "ok"),
            captainResults.Count(r => r.Status == "ok"),
            shipResults,
            captainResults
        );
    }

    private async Task<(byte[]? Data, string? ContentType, int? StatusCode, string? Error)> FetchImageAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "image/webp,image/apng,image/*,*/*;q=0.8");
            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
                return (null, null, (int)res.StatusCode, $"HTTP {(int)res.StatusCode}");
            var data = await res.Content.ReadAsByteArrayAsync(ct);
            if (data.Length < 100)
                return (null, null, (int)res.StatusCode, "Response too small");
            var ctHeader = res.Content.Headers.ContentType?.ToString();
            return (data, ctHeader, null, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, null, null, ex.Message ?? "Request failed");
        }
        catch (TaskCanceledException)
        {
            return (null, null, null, "Timeout");
        }
        catch (Exception ex)
        {
            return (null, null, null, ex.Message ?? "Error");
        }
    }
}

public record ImageAuditResult(EntityAudit Ships, EntityAudit Captains);
public record EntityAudit(int Total, int WithImageUrl, int WithImageData, List<MissingItem> MissingUrl, List<MissingItem> MissingCachedData);
public record MissingItem(int Id, string Name);
public record PopulateResult(int ShipsStored, int CaptainsStored, List<PopulateItemResult> ShipResults, List<PopulateItemResult> CaptainResults);
public record PopulateItemResult(int Id, string Name, string Status, string? Reason, int Index, int Total, int? BytesStored = null);
