using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Data;
using NavalArchive.Data.Models;

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

    /// <summary>Fetch image from URL and store in DB. Returns (stored, reason, bytesStored). Optional onProgress for integration status.</summary>
    public async Task<(bool Stored, string? Reason, int BytesStored)> PopulateShipImageAsync(NavalArchiveDbContext db, int shipId, CancellationToken ct = default, ImageSearchKeys? keys = null, Action<string>? onProgress = null)
    {
        var ship = await db.Ships.FindAsync(new object[] { shipId }, ct);
        if (ship == null) return (false, "Ship not found", 0);
        // Re-query ImageManuallySet from DB to avoid overwriting manual selections
        var manuallySet = await db.Ships.AsNoTracking().Where(s => s.Id == shipId).Select(s => s.ImageManuallySet).FirstOrDefaultAsync(ct);
        if (manuallySet) return (true, "Manually set (skip)", ship.ImageData?.Length ?? 0);
        if (ship.ImageData != null) return (true, "Already cached", ship.ImageData.Length);

        string? urlToTry = ship.ImageUrl;
        var triedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(urlToTry) && _imageSearch != null && (keys != null || _imageSearch.IsConfigured))
        {
            onProgress?.Invoke($"[{ship.Name}] No ImageUrl, searching...");
            var urls = await _imageSearch.FindImageUrlsAsync($"{ship.Name} battleship ship photo", 5, ct, keys, onProgress);
            if (urls.Count > 0) { urlToTry = urls[0]; ship.ImageUrl = urlToTry; }
        }
        if (string.IsNullOrWhiteSpace(urlToTry)) return (false, "No ImageUrl", 0);

        var (data, contentType, statusCode, error) = await FetchImageAsync(urlToTry, ct);
        triedUrls.Add(urlToTry);
        if (data == null || data.Length < 100)
        {
            if (_imageSearch != null && (keys != null || _imageSearch.IsConfigured))
            {
                onProgress?.Invoke($"[{ship.Name}] Fetch failed, trying fallback search...");
                var queries = new[] { $"{ship.Name} battleship ship", $"{ship.Name} warship", $"{ship.Name} naval" };
                foreach (var q in queries)
                {
                    var urls = await _imageSearch.FindImageUrlsAsync(q, 5, ct, keys, onProgress);
                    foreach (var u in urls.Where(u => !triedUrls.Contains(u)))
                    {
                        triedUrls.Add(u);
                        (data, contentType, statusCode, error) = await FetchImageAsync(u, ct);
                        if (data != null && data.Length >= 100)
                        {
                            ship.ImageUrl = u;
                            ship.ImageData = data;
                            ship.ImageContentType = contentType ?? "image/jpeg";
                            ship.ImageVersion++;
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
        ship.ImageVersion++;
        await db.SaveChangesAsync(ct);
        return (true, null, data.Length);
    }

    /// <summary>Fetch image from URL and store in DB. Returns (stored, reason, bytesStored). usedCaptainUrls: skip URLs already used. usedCaptainHashes: skip images with same hash (catches duplicates from different URLs).</summary>
    public async Task<(bool Stored, string? Reason, int BytesStored)> PopulateCaptainImageAsync(NavalArchiveDbContext db, int captainId, CancellationToken ct = default, ImageSearchKeys? keys = null, HashSet<string>? usedCaptainUrls = null, HashSet<string>? usedCaptainHashes = null, Action<string>? onProgress = null)
    {
        var captain = await db.Captains.FindAsync(new object[] { captainId }, ct);
        if (captain == null) return (false, "Captain not found", 0);
        // Re-query ImageManuallySet from DB to avoid overwriting manual selections
        var manuallySet = await db.Captains.AsNoTracking().Where(c => c.Id == captainId).Select(c => c.ImageManuallySet).FirstOrDefaultAsync(ct);
        if (manuallySet) return (true, "Manually set (skip)", captain.ImageData?.Length ?? 0);
        if (captain.ImageData != null) return (true, "Already cached", captain.ImageData.Length);

        string? urlToTry = captain.ImageUrl;
        var triedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(urlToTry) && _imageSearch != null && (keys != null || _imageSearch.IsConfigured))
        {
            onProgress?.Invoke($"[{captain.Name}] No ImageUrl, searching...");
            var urls = await _imageSearch.FindImageUrlsAsync($"{captain.Name} naval captain portrait", 8, ct, keys, onProgress);
            urlToTry = urls.FirstOrDefault(u => (usedCaptainUrls == null || !usedCaptainUrls.Contains(u)) && !triedUrls.Contains(u));
            if (urlToTry != null) captain.ImageUrl = urlToTry;
        }
        if (string.IsNullOrWhiteSpace(urlToTry)) return (false, "No ImageUrl", 0);

        var (data, contentType, statusCode, error) = await FetchImageAsync(urlToTry, ct);
        triedUrls.Add(urlToTry);
        if (data == null || data.Length < 100)
        {
            if (_imageSearch != null && (keys != null || _imageSearch.IsConfigured))
            {
                onProgress?.Invoke($"[{captain.Name}] Fetch failed, trying fallback search...");
                var queries = new[] { $"{captain.Name} admiral portrait", $"{captain.Name} naval officer", $"{captain.Name} WWII admiral", $"{captain.Name} Kriegsmarine" };
                foreach (var q in queries)
                {
                    var urls = await _imageSearch.FindImageUrlsAsync(q, 8, ct, keys, onProgress);
                    foreach (var u in urls.Where(u => !triedUrls.Contains(u) && (usedCaptainUrls == null || !usedCaptainUrls.Contains(u))))
                    {
                        triedUrls.Add(u);
                        (data, contentType, statusCode, error) = await FetchImageAsync(u, ct);
                        if (data != null && data.Length >= 100)
                        {
                            var hash = ComputeImageHash(data);
                            if (usedCaptainHashes != null && usedCaptainHashes.Contains(hash))
                            {
                                onProgress?.Invoke($"[{captain.Name}] Skipping duplicate image (same hash)");
                                data = null;
                                continue;
                            }
                            captain.ImageUrl = u;
                            captain.ImageData = data;
                            captain.ImageContentType = contentType ?? "image/jpeg";
                            captain.ImageVersion++;
                            await db.SaveChangesAsync(ct);
                            usedCaptainUrls?.Add(u);
                            usedCaptainHashes?.Add(hash);
                            return (true, "Fallback", data.Length);
                        }
                        await Task.Delay(300, ct);
                    }
                }
            }
            var reason = statusCode.HasValue ? $"HTTP {statusCode}" : (error ?? "Fetch failed");
            return (false, reason, 0);
        }

        var imgHash = ComputeImageHash(data);
        if (usedCaptainHashes != null && usedCaptainHashes.Contains(imgHash))
        {
            onProgress?.Invoke($"[{captain.Name}] Skipping duplicate image (same hash as another captain)");
            return (false, "Duplicate image", 0);
        }
        captain.ImageData = data;
        captain.ImageContentType = contentType ?? "image/jpeg";
        captain.ImageVersion++;
        await db.SaveChangesAsync(ct);
        usedCaptainUrls?.Add(urlToTry);
        usedCaptainHashes?.Add(imgHash);
        return (true, null, data.Length);
    }

    private static string ComputeImageHash(byte[] data)
    {
        var len = Math.Min(data.Length, 8192);
        var hash = SHA256.HashData(data.AsSpan(0, len));
        return Convert.ToHexString(hash);
    }

    /// <summary>Populate all ships and captains that have no ImageData. Optional keys for fallback when not in config.</summary>
    public async Task<PopulateResult> PopulateAllAsync(NavalArchiveDbContext db, CancellationToken ct = default, ImageSearchKeys? keys = null)
    {
        var ships = await db.Ships.Where(s => s.ImageData == null && !s.ImageManuallySet).ToListAsync(ct);
        var captains = await db.Captains.Where(c => c.ImageData == null && !c.ImageManuallySet).ToListAsync(ct);

        var shipResults = new List<PopulateItemResult>();
        var captainResults = new List<PopulateItemResult>();
        var usedCaptainUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedCaptainHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            var (stored, reason, bytes) = await PopulateCaptainImageAsync(db, captain.Id, ct, keys, usedCaptainUrls, usedCaptainHashes);
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

    /// <summary>Streaming populate: yields progress (integrations) and results after each ship and captain.</summary>
    public async IAsyncEnumerable<PopulateProgressEvent> PopulateAllStreamAsync(NavalArchiveDbContext db, [EnumeratorCancellation] CancellationToken ct = default, ImageSearchKeys? keys = null)
    {
        if (_imageSearch != null)
        {
            var keyResults = await _imageSearch.TestKeysForPopulateAsync(keys, ct);
            foreach (var r in keyResults)
                yield return new PopulateProgressEvent("key", new { r.Provider, r.Ok, r.Message });
        }

        var ships = await db.Ships.Where(s => s.ImageData == null && !s.ImageManuallySet).ToListAsync(ct);
        var captains = await db.Captains.Where(c => c.ImageData == null && !c.ImageManuallySet).ToListAsync(ct);
        var usedCaptainUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedCaptainHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var progressQueue = new ConcurrentQueue<string>();
        Action<string> onProgress = s => progressQueue.Enqueue(s);

        yield return new PopulateProgressEvent("info", $"Processing {ships.Count} ships, {captains.Count} captains without cached images.");

        int idx = 0;
        foreach (var ship in ships)
        {
            idx++;
            var (stored, reason, bytes) = await PopulateShipImageAsync(db, ship.Id, ct, keys, onProgress);
            while (progressQueue.TryDequeue(out var msg))
                yield return new PopulateProgressEvent("progress", msg);
            var item = new PopulateItemResult(ship.Id, ship.Name ?? "Ship " + ship.Id, stored ? "ok" : "fail", reason, idx, ships.Count, bytes > 0 ? bytes : null);
            yield return new PopulateProgressEvent("ship", item);
            await Task.Delay(200, ct);
        }

        idx = 0;
        foreach (var captain in captains)
        {
            idx++;
            var (stored, reason, bytes) = await PopulateCaptainImageAsync(db, captain.Id, ct, keys, usedCaptainUrls, usedCaptainHashes, onProgress);
            while (progressQueue.TryDequeue(out var msg))
                yield return new PopulateProgressEvent("progress", msg);
            var item = new PopulateItemResult(captain.Id, captain.Name ?? "Captain " + captain.Id, stored ? "ok" : "fail", reason, idx, captains.Count, bytes > 0 ? bytes : null);
            yield return new PopulateProgressEvent("captain", item);
            await Task.Delay(200, ct);
        }

        yield return new PopulateProgressEvent("done", null);
    }

    public async Task<(byte[]? Data, string? ContentType, int? StatusCode, string? Error)> FetchImageAsync(string url, CancellationToken ct)
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
public record PopulateProgressEvent(string Type, object? Data);
