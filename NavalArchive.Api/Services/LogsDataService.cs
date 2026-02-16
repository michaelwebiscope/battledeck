using System.Text.Json;

namespace NavalArchive.Api.Services;

public class LogsDataService
{
    private readonly string _cachePath;
    private readonly WikipediaLogsFetcher _fetcher = new();
    private string? _logContent;
    private readonly object _lock = new();

    public LogsDataService()
    {
        var baseDir = AppContext.BaseDirectory;
        var projectDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        _cachePath = Path.Combine(Directory.Exists(projectDir) ? projectDir : baseDir, "Data", "captain-logs.json");
    }

    public string GetLogContent()
    {
        if (_logContent != null)
            return _logContent;

        lock (_lock)
        {
            if (_logContent != null)
                return _logContent;

            if (File.Exists(_cachePath))
            {
                try
                {
                    var json = File.ReadAllText(_cachePath);
                    var cached = JsonSerializer.Deserialize<CachedLogs>(json);
                    _logContent = cached?.Content ?? GetFallbackContent();
                    return _logContent;
                }
                catch { /* ignore */ }
            }

            _logContent = GetFallbackContent();
            return _logContent;
        }
    }

    public async Task RefreshFromWikipediaAsync(CancellationToken ct = default)
    {
        try
        {
            var content = await _fetcher.FetchLogContentAsync(ct);
            if (string.IsNullOrWhiteSpace(content)) return;

            lock (_lock)
            {
                _logContent = content;
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                File.WriteAllText(_cachePath, JsonSerializer.Serialize(new CachedLogs
                {
                    Content = content,
                    FetchedAt = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = false }));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Logs fetch failed: {ex.Message}");
        }
    }

    private static string GetFallbackContent()
    {
        return string.Join("\n", new[]
        {
            "1941-05-24: Bismarck engaged HMS Hood. Hood sunk in minutes. Proceeding to France.",
            "1941-05-27: Bismarck sunk by Royal Navy. Admiral Lutjens lost.",
            "1941-12-07: Pearl Harbor attacked. USS Arizona sunk. War declared.",
            "1942-06-04: Battle of Midway. Four Japanese carriers sunk. Yorktown lost.",
            "1942-08-09: Guadalcanal. USS Enterprise heavily damaged. South Dakota engaged.",
            "1944-10-25: Leyte Gulf. USS Johnston sacrificed at Samar. Yamato engaged.",
            "1945-04-07: Yamato sunk by carrier aircraft. Operation Ten-Go failed.",
            "1945-09-02: Japanese surrender signed aboard USS Missouri in Tokyo Bay."
        });
    }

    private class CachedLogs
    {
        public string Content { get; set; } = "";
        public DateTime FetchedAt { get; set; }
    }
}
