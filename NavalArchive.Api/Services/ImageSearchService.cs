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

    /// <summary>Search images. Tries Pexels (best) → Pixabay → Unsplash → Google (worst), or only the specified provider if provider is set.</summary>
    public async Task<List<string>> FindImageUrlsAsync(string query, int maxCount = 5, CancellationToken ct = default, ImageSearchKeys? keys = null, Action<string>? onProgress = null, string? provider = null)
    {
        if (!IsConfiguredWith(keys)) return new List<string>();

        var providers = string.IsNullOrWhiteSpace(provider)
            ? new[] { "Pexels", "Pixabay", "Unsplash", "Google" }
            : new[] { provider.Trim() };

        foreach (var p in providers)
        {
            var pLower = p.ToLowerInvariant();
            if (pLower == "pexels" && !string.IsNullOrWhiteSpace(GetKey("PEXELS_API_KEY", keys)))
            {
                onProgress?.Invoke("Trying Pexels...");
                var urls = await TryPexelsAsync(query, maxCount, ct, keys);
                if (urls.Count > 0) { onProgress?.Invoke($"Pexels: {urls.Count} results"); return urls; }
                onProgress?.Invoke("Pexels: no results");
            }
            else if (pLower == "pixabay" && !string.IsNullOrWhiteSpace(GetKey("PIXABAY_API_KEY", keys)))
            {
                onProgress?.Invoke("Trying Pixabay...");
                var urls = await TryPixabayAsync(query, maxCount, ct, keys);
                if (urls.Count > 0) { onProgress?.Invoke($"Pixabay: {urls.Count} results"); return urls; }
                onProgress?.Invoke("Pixabay: no results");
            }
            else if (pLower == "unsplash" && !string.IsNullOrWhiteSpace(GetKey("UNSPLASH_ACCESS_KEY", keys)))
            {
                onProgress?.Invoke("Trying Unsplash...");
                var urls = await TryUnsplashAsync(query, maxCount, ct, keys);
                if (urls.Count > 0) { onProgress?.Invoke($"Unsplash: {urls.Count} results"); return urls; }
                onProgress?.Invoke("Unsplash: no results");
            }
            else if (pLower == "google" && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_API_KEY", keys)) && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_CSE_ID", keys)))
            {
                onProgress?.Invoke("Trying Google...");
                var urls = await TryGoogleAsync(query, maxCount, ct, keys);
                if (urls.Count > 0) { onProgress?.Invoke($"Google: {urls.Count} results"); return urls; }
                onProgress?.Invoke("Google: no results");
            }
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

    /// <summary>Test each configured API key. Returns per-provider success/failure with error details.</summary>
    public async Task<List<KeyTestResult>> TestKeysAsync(ImageSearchKeys? keys, CancellationToken ct = default)
    {
        var results = new List<KeyTestResult>();
        var query = "battleship";

        if (!string.IsNullOrWhiteSpace(GetKey("PEXELS_API_KEY", keys)))
        {
            try
            {
                var url = $"https://api.pexels.com/v1/search?query={Uri.EscapeDataString(query)}&per_page=1";
                var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("Authorization", GetKey("PEXELS_API_KEY", keys)!);
                var res = await client.GetAsync(url, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                results.Add(new KeyTestResult("Pexels", res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "OK" : $"HTTP {(int)res.StatusCode}: {(body.Length > 200 ? body.Substring(0, 200) + "..." : body)}"));
            }
            catch (Exception ex) { results.Add(new KeyTestResult("Pexels", false, ex.Message)); }
        }

        if (!string.IsNullOrWhiteSpace(GetKey("PIXABAY_API_KEY", keys)))
        {
            try
            {
                var url = $"https://pixabay.com/api/?key={Uri.EscapeDataString(GetKey("PIXABAY_API_KEY", keys)!)}&q={Uri.EscapeDataString(query)}&per_page=1";
                var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var res = await client.GetAsync(url, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                results.Add(new KeyTestResult("Pixabay", res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "OK" : $"HTTP {(int)res.StatusCode}: {(body.Length > 200 ? body.Substring(0, 200) + "..." : body)}"));
            }
            catch (Exception ex) { results.Add(new KeyTestResult("Pixabay", false, ex.Message)); }
        }

        if (!string.IsNullOrWhiteSpace(GetKey("UNSPLASH_ACCESS_KEY", keys)))
        {
            try
            {
                var url = $"https://api.unsplash.com/search/photos?query={Uri.EscapeDataString(query)}&per_page=1";
                var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("Authorization", $"Client-ID {GetKey("UNSPLASH_ACCESS_KEY", keys)}");
                var res = await client.GetAsync(url, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                results.Add(new KeyTestResult("Unsplash", res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "OK" : $"HTTP {(int)res.StatusCode}: {(body.Length > 200 ? body.Substring(0, 200) + "..." : body)}"));
            }
            catch (Exception ex) { results.Add(new KeyTestResult("Unsplash", false, ex.Message)); }
        }

        if (!string.IsNullOrWhiteSpace(GetKey("GOOGLE_API_KEY", keys)) && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_CSE_ID", keys)))
        {
            try
            {
                var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(GetKey("GOOGLE_API_KEY", keys)!)}&cx={Uri.EscapeDataString(GetKey("GOOGLE_CSE_ID", keys)!)}&q={Uri.EscapeDataString(query)}&searchType=image&num=1";
                var client = _http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var res = await client.GetAsync(url, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                results.Add(new KeyTestResult("Google", res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "OK" : $"HTTP {(int)res.StatusCode}: {(body.Length > 200 ? body.Substring(0, 200) + "..." : body)}"));
            }
            catch (Exception ex) { results.Add(new KeyTestResult("Google", false, ex.Message)); }
        }

        if (results.Count == 0)
            results.Add(new KeyTestResult("(none)", false, "No API keys provided. Enter keys in the optional section above."));

        return results;
    }

    /// <summary>Test all 4 providers; for each without a key, returns Provider with Message "nokey". Used by populate to show key status.</summary>
    public async Task<List<KeyTestResult>> TestKeysForPopulateAsync(ImageSearchKeys? keys, CancellationToken ct = default)
    {
        var results = new List<KeyTestResult>();
        var providers = new[] { "Pexels", "Pixabay", "Unsplash", "Google" };
        var keyNames = new[] { "PEXELS_API_KEY", "PIXABAY_API_KEY", "UNSPLASH_ACCESS_KEY", "GOOGLE_API_KEY" };

        for (var i = 0; i < 4; i++)
        {
            var hasKey = !string.IsNullOrWhiteSpace(GetKey(keyNames[i], keys));
            if (i == 3) hasKey = hasKey && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_CSE_ID", keys));

            if (!hasKey)
            {
                results.Add(new KeyTestResult(providers[i], false, "nokey"));
                continue;
            }

            if (i == 0)
            {
                try
                {
                    var url = $"https://api.pexels.com/v1/search?query=battleship&per_page=1";
                    var client = _http.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.Add("Authorization", GetKey("PEXELS_API_KEY", keys)!);
                    var res = await client.GetAsync(url, ct);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    results.Add(new KeyTestResult("Pexels", res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "OK" : $"HTTP {(int)res.StatusCode}: {(body.Length > 150 ? body.Substring(0, 150) + "..." : body)}"));
                }
                catch (Exception ex) { results.Add(new KeyTestResult("Pexels", false, ex.Message)); }
            }
            else if (i == 1)
            {
                try
                {
                    var url = $"https://pixabay.com/api/?key={Uri.EscapeDataString(GetKey("PIXABAY_API_KEY", keys)!)}&q=battleship&per_page=1";
                    var client = _http.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var res = await client.GetAsync(url, ct);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    results.Add(new KeyTestResult("Pixabay", res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "OK" : $"HTTP {(int)res.StatusCode}: {(body.Length > 150 ? body.Substring(0, 150) + "..." : body)}"));
                }
                catch (Exception ex) { results.Add(new KeyTestResult("Pixabay", false, ex.Message)); }
            }
            else if (i == 2)
            {
                try
                {
                    var url = $"https://api.unsplash.com/search/photos?query=battleship&per_page=1";
                    var client = _http.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.Add("Authorization", $"Client-ID {GetKey("UNSPLASH_ACCESS_KEY", keys)}");
                    var res = await client.GetAsync(url, ct);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    results.Add(new KeyTestResult("Unsplash", res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "OK" : $"HTTP {(int)res.StatusCode}: {(body.Length > 150 ? body.Substring(0, 150) + "..." : body)}"));
                }
                catch (Exception ex) { results.Add(new KeyTestResult("Unsplash", false, ex.Message)); }
            }
            else
            {
                try
                {
                    var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(GetKey("GOOGLE_API_KEY", keys)!)}&cx={Uri.EscapeDataString(GetKey("GOOGLE_CSE_ID", keys)!)}&q=battleship&searchType=image&num=1";
                    var client = _http.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var res = await client.GetAsync(url, ct);
                    var body = await res.Content.ReadAsStringAsync(ct);
                    results.Add(new KeyTestResult("Google", res.IsSuccessStatusCode, res.IsSuccessStatusCode ? "OK" : $"HTTP {(int)res.StatusCode}: {(body.Length > 150 ? body.Substring(0, 150) + "..." : body)}"));
                }
                catch (Exception ex) { results.Add(new KeyTestResult("Google", false, ex.Message)); }
            }
        }

        return results;
    }
}

public record KeyTestResult(string Provider, bool Ok, string Message);
