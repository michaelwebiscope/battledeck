using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Services;

public class DataSyncService
{
    private readonly string _cachePath;
    private readonly string _captainCachePath;
    private readonly WikipediaDataFetcher _fetcher = new();

    public DataSyncService()
    {
        var baseDir = AppContext.BaseDirectory;
        var projectDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        var dataDir = Path.Combine(Directory.Exists(projectDir) ? projectDir : baseDir, "Data");
        _cachePath = Path.Combine(dataDir, "fetched-ships.json");
        _captainCachePath = Path.Combine(dataDir, "fetched-captains.json");
    }

    public async Task SyncFromWikipediaAsync(NavalArchiveDbContext db, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (forceRefresh)
        {
            try { if (File.Exists(_cachePath)) File.Delete(_cachePath); } catch { }
            try { if (File.Exists(_captainCachePath)) File.Delete(_captainCachePath); } catch { }
        }

        Dictionary<string, FetchedShipData>? cached = null;
        if (File.Exists(_cachePath) && !forceRefresh)
        {
            try
            {
                var json = await File.ReadAllTextAsync(_cachePath, ct);
                cached = JsonSerializer.Deserialize<Dictionary<string, FetchedShipData>>(json);
            }
            catch { /* ignore */ }
        }

        if (cached == null || cached.Count == 0)
        {
            Console.WriteLine("Fetching ship data from Wikipedia...");
            var fetched = await _fetcher.FetchAllAsync(ct);
            cached = fetched.ToDictionary(x => x.Name, x => x);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                await File.WriteAllTextAsync(_cachePath,
                    JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true }), ct);
                Console.WriteLine($"Cached {cached.Count} ships to {_cachePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not cache: {ex.Message}");
            }
        }

        await ApplyShipsToDatabaseAsync(db, cached, ct);

        // Captain images
        Dictionary<string, FetchedCaptainData>? captainCached = null;
        if (File.Exists(_captainCachePath) && !forceRefresh)
        {
            try
            {
                var json = await File.ReadAllTextAsync(_captainCachePath, ct);
                captainCached = JsonSerializer.Deserialize<Dictionary<string, FetchedCaptainData>>(json);
            }
            catch { /* ignore */ }
        }

        if (captainCached == null || captainCached.Count == 0)
        {
            Console.WriteLine("Fetching captain images from Wikipedia...");
            var fetched = await _fetcher.FetchCaptainsAsync(ct);
            captainCached = fetched.ToDictionary(x => x.Name, x => x);

            try
            {
                await File.WriteAllTextAsync(_captainCachePath,
                    JsonSerializer.Serialize(captainCached, new JsonSerializerOptions { WriteIndented = true }), ct);
                Console.WriteLine($"Cached {captainCached.Count} captains to {_captainCachePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not cache captains: {ex.Message}");
            }
        }

        await ApplyCaptainsToDatabaseAsync(db, captainCached, ct);
    }

    private async Task ApplyShipsToDatabaseAsync(NavalArchiveDbContext db,
        Dictionary<string, FetchedShipData> data, CancellationToken ct)
    {
        var ships = await db.Ships.ToListAsync(ct);
        foreach (var ship in ships)
        {
            if (data.TryGetValue(ship.Name, out var fetched))
            {
                if (!string.IsNullOrWhiteSpace(fetched.Description))
                    ship.Description = fetched.Description.Length > 2000
                        ? fetched.Description[..2000] + "..."
                        : fetched.Description;
                if (!string.IsNullOrWhiteSpace(fetched.ImageUrl))
                    ship.ImageUrl = fetched.ImageUrl;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyCaptainsToDatabaseAsync(NavalArchiveDbContext db,
        Dictionary<string, FetchedCaptainData> data, CancellationToken ct)
    {
        var captains = await db.Captains.ToListAsync(ct);
        foreach (var captain in captains)
        {
            if (data.TryGetValue(captain.Name, out var fetched) && !string.IsNullOrWhiteSpace(fetched.ImageUrl))
            {
                captain.ImageUrl = fetched.ImageUrl;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
