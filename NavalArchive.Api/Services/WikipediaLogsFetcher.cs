using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NavalArchive.Api.Services;

public class WikipediaLogsFetcher
{
    private readonly HttpClient _http;

    public WikipediaLogsFetcher()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", "NavalArchive/1.0 (https://github.com/navalarchive; contact@example.com)");
    }

    private const string ApiBase = "https://en.wikipedia.org/w/api.php";

    private async Task<string> FetchWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var res = await _http.GetAsync(url, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var delay = 2000 * (1 << attempt);
                Console.WriteLine($"Wikipedia logs 429. Waiting {delay}ms before retry {attempt + 1}/4...");
                await Task.Delay(delay, ct);
                continue;
            }
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(ct);
        }
        throw new HttpRequestException("Wikipedia rate limit (429) - too many retries.");
    }

    // Wikipedia articles for naval battles and events - real historical content
    private static readonly string[] ArticleTitles =
    {
        "Battle_of_Midway",
        "Battle_of_Leyte_Gulf",
        "Battle_of_the_Coral_Sea",
        "Battle_of_the_Atlantic",
        "German_battleship_Bismarck",
        "Attack_on_Pearl_Harbor",
        "Battle_of_Guadalcanal",
        "HMS_Hood_(51)",
        "USS_Enterprise_(CV-6)",
        "Japanese_battleship_Yamato",
        "Battle_of_the_Denmark_Strait",
        "Operation_Ten-Go",
        "USS_Indianapolis_(CA-35)",
        "Battle_of_Samar",
        "Battle_of_the_Philippine_Sea"
    };

    public async Task<string> FetchLogContentAsync(CancellationToken ct = default)
    {
        var allLines = new List<string>();
        var batchSize = 5;

        for (var i = 0; i < ArticleTitles.Length; i += batchSize)
        {
            var batch = ArticleTitles.Skip(i).Take(batchSize).ToArray();
            var titles = string.Join("|", batch);

            var url = $"{ApiBase}?action=query&titles={Uri.EscapeDataString(titles)}" +
                      "&prop=extracts&exintro&explaintext&exchars=1500&format=json";

            try
            {
                var json = await FetchWithRetryAsync(url, ct);
                var doc = JsonDocument.Parse(json);
                var pages = doc.RootElement.GetProperty("query").GetProperty("pages");

                foreach (var page in pages.EnumerateObject())
                {
                    if (page.Name == "-1") continue;
                    var title = page.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var extract = page.Value.TryGetProperty("extract", out var e) ? e.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(extract)) continue;

                    // Split into sentences for searchable log entries
                    var sentences = Regex.Split(extract, @"(?<=[.!?])\s+");
                    foreach (var sent in sentences)
                    {
                        var trimmed = sent.Trim();
                        if (trimmed.Length > 25)
                            allLines.Add($"{title}: {trimmed}");
                    }
                }

                await Task.Delay(800, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Wikipedia logs fetch failed: {ex.Message}");
            }
        }

        return string.Join("\n", allLines);
    }
}
