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

    public ImageStorageService(IHttpClientFactory http) => _http = http;

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

    /// <summary>Fetch image from URL and store in DB. Returns true if stored.</summary>
    public async Task<bool> PopulateShipImageAsync(NavalArchiveDbContext db, int shipId, CancellationToken ct = default)
    {
        var ship = await db.Ships.FindAsync(new object[] { shipId }, ct);
        if (ship == null || string.IsNullOrWhiteSpace(ship.ImageUrl)) return false;
        if (ship.ImageData != null) return true; // already cached

        var (data, contentType) = await FetchImageAsync(ship.ImageUrl, ct);
        if (data == null || data.Length < 100) return false;

        ship.ImageData = data;
        ship.ImageContentType = contentType ?? "image/jpeg";
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Fetch image from URL and store in DB. Returns true if stored.</summary>
    public async Task<bool> PopulateCaptainImageAsync(NavalArchiveDbContext db, int captainId, CancellationToken ct = default)
    {
        var captain = await db.Captains.FindAsync(new object[] { captainId }, ct);
        if (captain == null || string.IsNullOrWhiteSpace(captain.ImageUrl)) return false;
        if (captain.ImageData != null) return true;

        var (data, contentType) = await FetchImageAsync(captain.ImageUrl!, ct);
        if (data == null || data.Length < 100) return false;

        captain.ImageData = data;
        captain.ImageContentType = contentType ?? "image/jpeg";
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Populate all ships and captains that have ImageUrl but no ImageData.</summary>
    public async Task<PopulateResult> PopulateAllAsync(NavalArchiveDbContext db, CancellationToken ct = default)
    {
        var ships = await db.Ships.Where(s => s.ImageData == null && !string.IsNullOrWhiteSpace(s.ImageUrl)).ToListAsync(ct);
        var captains = await db.Captains.Where(c => c.ImageData == null && !string.IsNullOrWhiteSpace(c.ImageUrl)).ToListAsync(ct);

        int shipsStored = 0, captainsStored = 0;
        foreach (var ship in ships)
        {
            if (await PopulateShipImageAsync(db, ship.Id, ct)) shipsStored++;
            await Task.Delay(200, ct); // be nice to Wikipedia
        }
        foreach (var captain in captains)
        {
            if (await PopulateCaptainImageAsync(db, captain.Id, ct)) captainsStored++;
            await Task.Delay(200, ct);
        }

        return new PopulateResult(shipsStored, captainsStored);
    }

    private async Task<(byte[]? Data, string? ContentType)> FetchImageAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; NavalArchive/1.0)");
            client.DefaultRequestHeaders.Add("Accept", "image/*");
            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return (null, null);
            var data = await res.Content.ReadAsByteArrayAsync(ct);
            var ctHeader = res.Content.Headers.ContentType?.ToString();
            return (data, ctHeader);
        }
        catch { return (null, null); }
    }
}

public record ImageAuditResult(EntityAudit Ships, EntityAudit Captains);
public record EntityAudit(int Total, int WithImageUrl, int WithImageData, List<MissingItem> MissingUrl, List<MissingItem> MissingCachedData);
public record MissingItem(int Id, string Name);
public record PopulateResult(int ShipsStored, int CaptainsStored);
