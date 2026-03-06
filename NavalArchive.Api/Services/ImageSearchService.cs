using System.Text.Json;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Services;

/// <summary>Optional API keys for image search (passed from UI, not persisted).</summary>
public record ImageSearchKeys(string? PexelsApiKey, string? PixabayApiKey, string? UnsplashAccessKey, string? GoogleApiKey, string? GoogleCseId, IReadOnlyDictionary<string, string>? CustomKeys = null);

/// <summary>
/// Unified image search with fallback chain: Pexels (best) → Pixabay → Unsplash → Google (last).
/// All free except Google (100/day). Set API keys via env, appsettings, or pass per-request.
/// </summary>
public class ImageSearchService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly WikipediaDataFetcher _wikipedia;

    public ImageSearchService(IHttpClientFactory http, IConfiguration config, WikipediaDataFetcher wikipedia)
    {
        _http = http;
        _config = config;
        _wikipedia = wikipedia;
    }

    private string? GetKey(string name, ImageSearchKeys? keys)
    {
        if (keys?.CustomKeys != null && keys.CustomKeys.TryGetValue(name, out var customVal) && !string.IsNullOrWhiteSpace(customVal))
            return customVal;
        return name switch
        {
            "PEXELS_API_KEY" => keys?.PexelsApiKey ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            "PIXABAY_API_KEY" => keys?.PixabayApiKey ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            "UNSPLASH_ACCESS_KEY" => keys?.UnsplashAccessKey ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            "GOOGLE_API_KEY" => keys?.GoogleApiKey ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            "GOOGLE_CSE_ID" => keys?.GoogleCseId ?? _config[name] ?? Environment.GetEnvironmentVariable(name),
            _ => keys?.CustomKeys != null && keys.CustomKeys.TryGetValue(name, out var v) ? v : _config[name] ?? Environment.GetEnvironmentVariable(name)
        };
    }

    private bool IsConfiguredWith(ImageSearchKeys? keys) =>
        !string.IsNullOrWhiteSpace(GetKey("PEXELS_API_KEY", keys)) ||
        !string.IsNullOrWhiteSpace(GetKey("PIXABAY_API_KEY", keys)) ||
        !string.IsNullOrWhiteSpace(GetKey("UNSPLASH_ACCESS_KEY", keys)) ||
        (!string.IsNullOrWhiteSpace(GetKey("GOOGLE_API_KEY", keys)) && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_CSE_ID", keys)));

    public bool IsConfigured => IsConfiguredWith(null);

    /// <summary>Search images. Uses sources config if provided (with retry); otherwise fallback chain: Pexels → Pixabay → Unsplash → Google.</summary>
    public async Task<List<string>> FindImageUrlsAsync(string query, int maxCount = 5, CancellationToken ct = default, ImageSearchKeys? keys = null, Action<string>? onProgress = null, string? provider = null, IReadOnlyList<ImageSourceConfig>? sources = null)
    {
        if (sources != null && sources.Count > 0)
            return await FindImageUrlsWithSourcesAsync(query, maxCount, ct, keys, onProgress, provider, sources);

        var providers = string.IsNullOrWhiteSpace(provider)
            ? new[] { "Wikipedia", "Pexels", "Pixabay", "Unsplash", "Google" }
            : new[] { provider.Trim() };

        var needsKeys = providers.Any(p => !string.Equals(p, "Wikipedia", StringComparison.OrdinalIgnoreCase));
        if (needsKeys && !IsConfiguredWith(keys)) return new List<string>();

        foreach (var p in providers)
        {
            var urls = await TryProviderAsync(p, query, maxCount, 1, ct, keys, onProgress);
            if (urls.Count > 0) return urls;
        }

        return new List<string>();
    }

    /// <summary>Search using configurable sources with per-source retry count.</summary>
    private async Task<List<string>> FindImageUrlsWithSourcesAsync(string query, int maxCount, CancellationToken ct, ImageSearchKeys? keys, Action<string>? onProgress, string? providerFilter, IReadOnlyList<ImageSourceConfig> sources)
    {
        var ordered = sources.Where(s => s.Enabled).OrderBy(s => s.SortOrder).ToList();
        if (ordered.Count == 0) return new List<string>();

        foreach (var src in ordered)
        {
            if (!string.IsNullOrWhiteSpace(providerFilter) && !string.Equals(src.ProviderType, providerFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(src.ProviderType, "Wikipedia", StringComparison.OrdinalIgnoreCase))
            {
                // Wikipedia needs no API key
            }
            else
            {
                var hasKey = string.IsNullOrWhiteSpace(src.AuthKeyRef) || !string.IsNullOrWhiteSpace(GetKey(src.AuthKeyRef, keys));
                if (src.ProviderType == "Google" && hasKey && !string.IsNullOrWhiteSpace(src.AuthKeyRef))
                    hasKey = !string.IsNullOrWhiteSpace(GetKey("GOOGLE_CSE_ID", keys));
                if (!hasKey) { onProgress?.Invoke($"[{src.Name}] nokey"); continue; }
            }

            var retries = Math.Max(1, Math.Min(src.RetryCount, 5));
            for (var attempt = 0; attempt < retries; attempt++)
            {
                onProgress?.Invoke(retries > 1 ? $"Trying {src.Name} (attempt {attempt + 1}/{retries})..." : $"Trying {src.Name}...");
                var urls = await TryProviderAsync(src.ProviderType, query, maxCount, 1, ct, keys, onProgress, src);
                if (urls.Count > 0) { onProgress?.Invoke($"{src.Name}: {urls.Count} results"); return urls; }
                onProgress?.Invoke($"{src.Name}: no results");
                if (attempt < retries - 1) await Task.Delay(500, ct);
            }
        }

        return new List<string>();
    }

    private async Task<List<string>> TryProviderAsync(string providerType, string query, int maxCount, int retries, CancellationToken ct, ImageSearchKeys? keys, Action<string>? onProgress, ImageSourceConfig? config = null)
    {
        var p = providerType.ToLowerInvariant();
        if (p == "wikipedia")
            return await TryWikipediaAsync(query, maxCount, ct);
        if (p == "pexels" && !string.IsNullOrWhiteSpace(GetKey("PEXELS_API_KEY", keys)))
            return await TryPexelsAsync(query, maxCount, ct, keys);
        if (p == "pixabay" && !string.IsNullOrWhiteSpace(GetKey("PIXABAY_API_KEY", keys)))
            return await TryPixabayAsync(query, maxCount, ct, keys);
        if (p == "unsplash" && !string.IsNullOrWhiteSpace(GetKey("UNSPLASH_ACCESS_KEY", keys)))
            return await TryUnsplashAsync(query, maxCount, ct, keys);
        if (p == "google" && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_API_KEY", keys)) && !string.IsNullOrWhiteSpace(GetKey("GOOGLE_CSE_ID", keys)))
            return await TryGoogleAsync(query, maxCount, ct, keys);
        if (p == "custom" && config?.CustomConfig != null)
            return await TryCustomApiAsync(query, maxCount, ct, keys, config);
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

    private async Task<List<string>> TryWikipediaAsync(string query, int maxCount, CancellationToken ct)
    {
        var result = new List<string>();
        try
        {
            var searchResults = await _wikipedia.SearchAsync(query, Math.Min(maxCount, 10), ct);
            foreach (var r in searchResults)
            {
                if (result.Count >= maxCount) break;
                var imageUrl = await _wikipedia.FetchImageFromPageAsync(r.Title, ct);
                if (!string.IsNullOrEmpty(imageUrl) && IsValidUrl(imageUrl))
                    result.Add(imageUrl);
                await Task.Delay(150, ct); // Be nice to Wikipedia
            }
        }
        catch { }
        return result;
    }

    private async Task<List<string>> TryCustomApiAsync(string query, int maxCount, CancellationToken ct, ImageSearchKeys? keys, ImageSourceConfig config)
    {
        var c = config.CustomConfig!;
        var result = new List<string>();
        try
        {
            var url = c.BaseUrl.TrimEnd('?', '&');
            var sep = url.Contains('?') ? '&' : '?';
            url += $"{sep}{Uri.EscapeDataString(c.QueryParam)}={Uri.EscapeDataString(query)}";
            if (c.AuthType.Equals("query", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.AuthQueryParam) && !string.IsNullOrWhiteSpace(c.AuthValueFromKey))
            {
                var authVal = GetKey(c.AuthValueFromKey, keys);
                if (!string.IsNullOrWhiteSpace(authVal)) url += $"&{Uri.EscapeDataString(c.AuthQueryParam)}={Uri.EscapeDataString(authVal)}";
            }

            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            if (c.AuthType.Equals("header", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.AuthHeaderName) && !string.IsNullOrWhiteSpace(c.AuthValueFromKey))
            {
                var authVal = GetKey(c.AuthValueFromKey, keys);
                if (!string.IsNullOrWhiteSpace(authVal)) client.DefaultRequestHeaders.Add(c.AuthHeaderName.Trim(), authVal);
            }

            var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return result;

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement;
            foreach (var seg in (c.ResponsePath ?? "results").Split('.'))
            {
                if (items.ValueKind == JsonValueKind.Null || items.ValueKind == JsonValueKind.Undefined) return result;
                if (!items.TryGetProperty(seg, out items)) return result;
            }
            if (items.ValueKind != JsonValueKind.Array) return result;

            for (var i = 0; i < items.GetArrayLength() && result.Count < maxCount; i++)
            {
                var item = items[i];
                var link = GetNestedString(item, c.ImageUrlPath);
                if (IsValidUrl(link)) result.Add(link!);
            }
        }
        catch { }
        return result;
    }

    private static string? GetNestedString(JsonElement el, string path)
    {
        foreach (var seg in path.Split('.'))
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(seg, out el))
                return null;
        }
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
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
