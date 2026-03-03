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
        if (!IsConfigured) return null;

        try
        {
            var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(_apiKey!)}" +
                      $"&cx={Uri.EscapeDataString(_cx!)}&q={Uri.EscapeDataString(query)}&searchType=image&num=5";
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("User-Agent", "NavalArchive/1.0");
            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.TryGetProperty("items", out var arr) ? arr : default;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;

            var first = items[0];
            if (first.TryGetProperty("link", out var link))
                return link.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }
}
