using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Services;

public class DataSyncService
{
    private readonly string _cachePath;
    private readonly WikipediaDataFetcher _fetcher = new();

    public DataSyncService()
    {
        var baseDir = AppContext.BaseDirectory;
        var projectDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        _cachePath = Path.Combine(Directory.Exists(projectDir) ? projectDir : baseDir, "Data", "fetched-ships.json");
    }

    public async Task SyncFromWikipediaAsync(NavalArchiveDbContext db, CancellationToken ct = default)
    {
        Dictionary<string, FetchedShipData>? cached = null;

        if (File.Exists(_cachePath))
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

        await ApplyToDatabaseAsync(db, cached, ct);
    }

    private async Task ApplyToDatabaseAsync(NavalArchiveDbContext db,
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
}
