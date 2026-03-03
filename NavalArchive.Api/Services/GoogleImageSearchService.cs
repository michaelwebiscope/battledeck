using System.Text.Json;

namespace NavalArchive.Api.Services;

/// <summary>
/// Optional fallback: search Google for images when Wikipedia fails.
/// Requires GOOGLE_API_KEY and GOOGLE_CSE_ID environment variables.
/// Free tier: 100 queries/day.
/// </summary>
public class GoogleImageSearchService
{
    private readonly IHttpClientFactory _http;
    private readonly string? _apiKey;
    private readonly string? _cx;

    public GoogleImageSearchService(IHttpClientFactory http, IConfiguration config)
    {
        _http = http;
        _apiKey = config["GOOGLE_API_KEY"] ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        _cx = config["GOOGLE_CSE_ID"] ?? Environment.GetEnvironmentVariable("GOOGLE_CSE_ID");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(_cx);

    /// <summary>Search for an image by query. Returns first image URL or null.</summary>
    public async Task<string?> FindImageUrlAsync(string query, CancellationToken ct = default)
    {
        var urls = await FindImageUrlsAsync(query, 1, ct);
        return urls.Count > 0 ? urls[0] : null;
    }

    /// <summary>Search for images by query. Returns up to maxCount image URLs (direct links).</summary>
    public async Task<List<string>> FindImageUrlsAsync(string query, int maxCount = 5, CancellationToken ct = default)
    {
        var result = new List<string>();
        if (!IsConfigured) return result;

        try
        {
            var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(_apiKey!)}" +
                      $"&cx={Uri.EscapeDataString(_cx!)}&q={Uri.EscapeDataString(query)}&searchType=image&num={Math.Min(maxCount, 10)}";
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("User-Agent", "NavalArchive/1.0");
            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return result;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.TryGetProperty("items", out var arr) ? arr : default;
            if (items.ValueKind != JsonValueKind.Array) return result;

            for (var i = 0; i < items.GetArrayLength() && result.Count < maxCount; i++)
            {
                var item = items[i];
                var link = item.TryGetProperty("link", out var l) ? l.GetString() : null;
                if (!string.IsNullOrWhiteSpace(link) && (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    result.Add(link);
            }
            return result;
        }
        catch
        {
            return result;
        }
    }
}
