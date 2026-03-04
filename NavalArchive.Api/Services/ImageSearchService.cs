using System.Text.Json;

namespace NavalArchive.Api.Services;

/// <summary>Optional API keys for image search (passed from UI, not persisted).</summary>
public record ImageSearchKeys(string? PexelsApiKey, string? PixabayApiKey, string? UnsplashAccessKey, string? GoogleApiKey, string? GoogleCseId);

/// <summary>
/// Unified image search with fallback chain: Pexels (best) → Pixabay → Unsplash → Google (last).
/// All free except Google (100/day). Set API keys via env, appsettings, or pass per-request.
/// </summary>
public class ImageSearchService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    public ImageSearchService(IHttpClientFactory http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    private string? GetKey(string name, ImageSearchKeys? keys) =>
        name switch
        {
            "PEXELS_API_KEY" => keys?.PexelsApiKey ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            "PIXABAY_API_KEY" => keys?.PixabayApiKey ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            "UNSPLASH_ACCESS_KEY" => keys?.UnsplashAccessKey ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            "GOOGLE_API_KEY" => keys?.GoogleApiKey ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            "GOOGLE_CSE_ID" => keys?.GoogleCseId ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            _ => _config[name] ?? Environment.GetEnvironmentVariable(name)
        };

    private bool IsConfiguredWith(ImageSearchKeys? keys) =>
        !string.IsNullOrWhiteSpace(GetKey("PEXELS_API_KEY", keys)) ||
        !string.IsNullOrWhiteSpace(GetKey("PIXABAY_API_KEY", keys)) ||
        !string.IsNullOrWhiteSpace(GetKey("UNSPLASH_ACCESS_KEY", keys)) ||
        (!string.IsNullOrWhiteSpace(GetKey("GOOGLE_API_KEY", keys)) && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_CSE_ID", keys)));

    public bool IsConfigured => IsConfiguredWith(null);

    /// <summary>Search images. Tries Pexels (best) → Pixabay → Unsplash → Google (worst). Optional keys override config.</summary>
    public async Task<List<string>> FindImageUrlsAsync(string query, int maxCount = 5, CancellationToken ct = default, ImageSearchKeys? keys = null)
    {
        if (!IsConfiguredWith(keys)) return new List<string>();

        // 1. Pexels (best: 200 req/hr, good stock photos)
        if (!string.IsNullOrWhiteSpace(GetKey("PEXELS_API_KEY", keys)))
        {
            var urls = await TryPexelsAsync(query, maxCount, ct, keys);
            if (urls.Count > 0) return urls;
        }

        // 2. Pixabay (large library)
        if (!string.IsNullOrWhiteSpace(GetKey("PIXABAY_API_KEY", keys)))
        {
            var urls = await TryPixabayAsync(query, maxCount, ct, keys);
            if (urls.Count > 0) return urls;
        }

        // 3. Unsplash (high quality, 50 req/hr demo)
        if (!string.IsNullOrWhiteSpace(GetKey("UNSPLASH_ACCESS_KEY", keys)))
        {
            var urls = await TryUnsplashAsync(query, maxCount, ct, keys);
            if (urls.Count > 0) return urls;
        }

        // 4. Google (last resort, 100/day free)
        if (!string.IsNullOrWhiteSpace(GetKey("GOOGLE_API_KEY", keys)) && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_CSE_ID", keys)))
        {
            var urls = await TryGoogleAsync(query, maxCount, ct, keys);
            if (urls.Count > 0) return urls;
        }

        return new List<string>();
    }

    private async Task<List<string>> TryPexelsAsync(string query, int maxCount, CancellationToken ct, ImageSearchKeys? keys = null)
    {
        var result = new List<string>();
        try
        {
            var url = $"https://api.pexels.com/v1/search?query={Uri.EscapeDataString(query)}&per_page={maxCount}";
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("Authorization", GetKey("PEXELS_API_KEY", keys)!);
            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return result;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Array)
                return result;

            for (var i = 0; i < photos.GetArrayLength() && result.Count < maxCount; i++)
            {
                var photo = photos[i];
                if (photo.TryGetProperty("src", out var src) && src.TryGetProperty("large", out var large))
                {
                    var link = large.GetString();
                    if (IsValidUrl(link)) result.Add(link!);
                }
            }
        }
        catch { }
        return result;
    }

    private async Task<List<string>> TryPixabayAsync(string query, int maxCount, CancellationToken ct, ImageSearchKeys? keys = null)
    {
        var result = new List<string>();
        try
        {
            var url = $"https://pixabay.com/api/?key={Uri.EscapeDataString(GetKey("PIXABAY_API_KEY", keys)!)}&q={Uri.EscapeDataString(query)}&image_type=photo&per_page={maxCount}";
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("User-Agent", "NavalArchive/1.0");
            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return result;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
                return result;

            for (var i = 0; i < hits.GetArrayLength() && result.Count < maxCount; i++)
            {
                var hit = hits[i];
                var link = hit.TryGetProperty("largeImageURL", out var l) ? l.GetString() :
                           hit.TryGetProperty("webformatURL", out var w) ? w.GetString() : null;
                if (IsValidUrl(link)) result.Add(link!);
            }
        }
        catch { }
        return result;
    }

    private async Task<List<string>> TryUnsplashAsync(string query, int maxCount, CancellationToken ct, ImageSearchKeys? keys = null)
    {
        var result = new List<string>();
        try
        {
            var url = $"https://api.unsplash.com/search/photos?query={Uri.EscapeDataString(query)}&per_page={maxCount}";
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("Authorization", $"Client-ID {GetKey("UNSPLASH_ACCESS_KEY", keys)}");
            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return result;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return result;

            for (var i = 0; i < results.GetArrayLength() && result.Count < maxCount; i++)
            {
                var photo = results[i];
                if (photo.TryGetProperty("urls", out var urls))
                {
                    var link = urls.TryGetProperty("regular", out var r) ? r.GetString() :
                               urls.TryGetProperty("full", out var f) ? f.GetString() :
                               urls.TryGetProperty("small", out var s) ? s.GetString() : null;
                    if (IsValidUrl(link)) result.Add(link!);
                }
            }
        }
        catch { }
        return result;
    }

    private async Task<List<string>> TryGoogleAsync(string query, int maxCount, CancellationToken ct, ImageSearchKeys? keys = null)
    {
        var result = new List<string>();
        try
        {
            var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(GetKey("GOOGLE_API_KEY", keys)!)}" +
                      $"&cx={Uri.EscapeDataString(GetKey("GOOGLE_CSE_ID", keys)!)}&q={Uri.EscapeDataString(query)}&searchType=image&num={Math.Min(maxCount, 10)}";
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("User-Agent", "NavalArchive/1.0");
            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return result;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return result;

            for (var i = 0; i < items.GetArrayLength() && result.Count < maxCount; i++)
            {
                var link = items[i].TryGetProperty("link", out var l) ? l.GetString() : null;
                if (IsValidUrl(link)) result.Add(link!);
            }
        }
        catch { }
        return result;
    }

    private static bool IsValidUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
