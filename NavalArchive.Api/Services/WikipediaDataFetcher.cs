using System.Net.Http.Json;
using System.Text.Json;
using NavalArchive.Data.Models;

namespace NavalArchive.Api.Services;

public class WikipediaDataFetcher
{
    private readonly HttpClient _http;
    private const int MaxRetries = 4;
    private const int BaseDelayMs = 2000;

    public WikipediaDataFetcher()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", "NavalArchive/1.0 (https://github.com/navalarchive; contact@example.com)");
    }

    private const string ApiBase = "https://en.wikipedia.org/w/api.php";

    /// <summary>Fetch URL with retry on 429 (rate limit). Exponential backoff: 2s, 4s, 8s, 16s.</summary>
    private async Task<string> FetchWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var res = await _http.GetAsync(url, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var delay = BaseDelayMs * (1 << attempt);
                Console.WriteLine($"Wikipedia 429 rate limit. Waiting {delay}ms before retry {attempt + 1}/{MaxRetries}...");
                await Task.Delay(delay, ct);
                continue;
            }
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(ct);
        }
        throw new HttpRequestException("Wikipedia rate limit (429) - too many retries.");
    }

    // Ship name -> Wikipedia page title
    private static readonly Dictionary<string, string> ShipTitles = new()
    {
        ["Bismarck"] = "German_battleship_Bismarck",
        ["Tirpitz"] = "German_battleship_Tirpitz",
        ["Yamato"] = "Japanese_battleship_Yamato",
        ["Musashi"] = "Japanese_battleship_Musashi",
        ["USS Iowa"] = "USS_Iowa_(BB-61)",
        ["USS New Jersey"] = "USS_New_Jersey_(BB-62)",
        ["USS Missouri"] = "USS_Missouri_(BB-63)",
        ["USS Wisconsin"] = "USS_Wisconsin_(BB-64)",
        ["USS Enterprise"] = "USS_Enterprise_(CV-6)",
        ["USS Yorktown"] = "USS_Yorktown_(CV-5)",
        ["USS Hornet"] = "USS_Hornet_(CV-8)",
        ["USS Lexington"] = "USS_Lexington_(CV-2)",
        ["USS Saratoga"] = "USS_Saratoga_(CV-3)",
        ["HMS Hood"] = "HMS_Hood_(51)",
        ["HMS Illustrious"] = "HMS_Illustrious_(87)",
        ["HMS Ark Royal"] = "HMS_Ark_Royal_(91)",
        ["HMS Victorious"] = "HMS_Victorious_(R38)",
        ["HMS Formidable"] = "HMS_Formidable_(67)",
        ["Shokaku"] = "Japanese_aircraft_carrier_Shokaku",
        ["Zuikaku"] = "Japanese_aircraft_carrier_Zuikaku",
        ["HMS Exeter"] = "HMS_Exeter_(68)",
        ["USS Indianapolis"] = "USS_Indianapolis_(CA-35)",
        ["USS Baltimore"] = "USS_Baltimore_(CA-68)",
        ["USS Fletcher"] = "USS_Fletcher_(DD-445)",
        ["USS Johnston"] = "USS_Johnston_(DD-557)",
        ["USS Hoel"] = "USS_Hoel_(DD-533)",
        ["HMS Cossack"] = "HMS_Cossack_(F03)",
        ["HMS Warspite"] = "HMS_Warspite_(03)",
        ["HMS Prince of Wales"] = "HMS_Prince_of_Wales_(53)",
        ["HMS Repulse"] = "HMS_Repulse_(1916)",
        ["USS Wasp"] = "USS_Wasp_(CV-7)",
        ["USS Ranger"] = "USS_Ranger_(CV-4)",
        ["Akagi"] = "Japanese_aircraft_carrier_Akagi",
        ["Kaga"] = "Japanese_aircraft_carrier_Kaga",
        ["Soryu"] = "Japanese_aircraft_carrier_Soryu",
        ["Hiryu"] = "Japanese_aircraft_carrier_Hiryu",
        ["Scharnhorst"] = "German_battleship_Scharnhorst",
        ["Gneisenau"] = "German_battleship_Gneisenau",
        ["Admiral Graf Spee"] = "German_cruiser_Admiral_Graf_Spee",
        ["USS San Francisco"] = "USS_San_Francisco_(CA-38)",
        ["USS Helena"] = "USS_Helena_(CL-50)",
        ["HMS Sheffield"] = "HMS_Sheffield_(C24)",
        ["USS Washington"] = "USS_Washington_(BB-56)",
        ["USS South Dakota"] = "USS_South_Dakota_(BB-57)",
        ["USS North Carolina"] = "USS_North_Carolina_(BB-55)",
        ["HMS King George V"] = "HMS_King_George_V_(41)",
        ["HMS Rodney"] = "HMS_Rodney_(29)",
        ["USS Essex"] = "USS_Essex_(CV-9)",
        ["USS Intrepid"] = "USS_Intrepid_(CV-11)",
        ["USS Franklin"] = "USS_Franklin_(CV-13)",
        ["USS Gambier Bay"] = "USS_Gambier_Bay_(CVE-73)",
        ["HMS Hermes"] = "HMS_Hermes_(95)",
        ["USS Quincy"] = "USS_Quincy_(CA-39)",
        ["USS Vincennes"] = "USS_Vincennes_(CA-44)",
        ["HMS Ajax"] = "HMS_Ajax_(22)",
        ["HMS Achilles"] = "HMS_Achilles_(70)"
    };

    // Captain name -> Wikipedia page title (for portrait images)
    private static readonly Dictionary<string, string> CaptainTitles = new()
    {
        ["Ernst Lindemann"] = "Ernst_Lindemann",
        ["Karl Topp"] = "Karl_Topp",
        ["Kosaku Aruga"] = "Kosaku_Aruga",
        ["Toshihira Inoguchi"] = "Toshihira_Inoguchi",
        ["John McCrea"] = "John_L._McCrea",
        ["Charles F. Adams"] = "Charles_F._Adams_(admiral)",
        ["William Callaghan"] = "William_M._Callaghan",
        ["Glenn Davis"] = "Glenn_B._Davis",
        ["George Murray"] = "George_D._Murray",
        ["Elliott Buckmaster"] = "Elliott_Buckmaster",
        ["Marc Mitscher"] = "Marc_Mitscher",
        ["Frederick Sherman"] = "Frederick_C._Sherman",
        ["Dewey B. Bronson"] = "Dewey_B._Bronson",
        ["Ralph Kerr"] = "Ralph_Kerr",
        ["Denis Boyd"] = "Denis_Boyd_(Royal_Navy_officer)",
        ["Arthur Power"] = "Arthur_Power",
        ["Henry Bovell"] = "Henry_Bovell",
        ["Philip Vian"] = "Philip_Vian",
        ["Takatsugu Jojima"] = "Takatsugu_Jojima",
        ["Tamon Yamaguchi"] = "Tamon_Yamaguchi",
        ["Frederick Bell"] = "Frederick_Bell_(Royal_Navy_officer)",
        ["Charles McVay"] = "Charles_B._McVay_III",
        ["Walter Deakins"] = "Walter_Deakins",
        ["William Cole"] = "William_Cole",
        ["Ernest Evans"] = "Ernest_Evans"
    };

    public async Task<List<FetchedShipData>> FetchAllAsync(CancellationToken ct = default)
    {
        var results = new List<FetchedShipData>();
        var batchSize = 5; // Wikipedia allows batch requests
        var titles = ShipTitles.ToList();

        for (var i = 0; i < titles.Count; i += batchSize)
        {
            var batch = titles.Skip(i).Take(batchSize).ToList();
            var titleParam = string.Join("|", batch.Select(x => x.Value));

            var url = $"{ApiBase}?action=query&titles={Uri.EscapeDataString(titleParam)}" +
                      "&prop=extracts|pageimages&exintro&explaintext&exchars=500" +
                      "&pithumbsize=800&format=json";

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
                    var imageUrl = "";
                    if (page.Value.TryGetProperty("thumbnail", out var thumb) && thumb.TryGetProperty("source", out var src))
                        imageUrl = src.GetString() ?? "";

                    var shipName = batch.FirstOrDefault(x =>
                        x.Value.Replace("_", " ") == title || x.Value == title).Key ?? title;
                    results.Add(new FetchedShipData(shipName, extract, imageUrl));
                }

                await Task.Delay(800, ct); // Be nice to Wikipedia (avoid 429)
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Wikipedia fetch batch failed: {ex.Message}");
            }
        }

        return results;
    }

    public async Task<List<FetchedCaptainData>> FetchCaptainsAsync(CancellationToken ct = default)
    {
        var results = new List<FetchedCaptainData>();
        var batchSize = 5;
        var titles = CaptainTitles.ToList();

        for (var i = 0; i < titles.Count; i += batchSize)
        {
            var batch = titles.Skip(i).Take(batchSize).ToList();
            var titleParam = string.Join("|", batch.Select(x => x.Value));

            var url = $"{ApiBase}?action=query&titles={Uri.EscapeDataString(titleParam)}" +
                      "&prop=pageimages|images&pithumbsize=400&imlimit=500&format=json";

            try
            {
                var json = await FetchWithRetryAsync(url, ct);
                var doc = JsonDocument.Parse(json);
                var pages = doc.RootElement.GetProperty("query").GetProperty("pages");

                foreach (var page in pages.EnumerateObject())
                {
                    if (page.Name == "-1") continue;

                    var title = page.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var imageUrl = "";
                    if (page.Value.TryGetProperty("thumbnail", out var thumb) && thumb.TryGetProperty("source", out var src))
                        imageUrl = src.GetString() ?? "";

                    if (string.IsNullOrEmpty(imageUrl) && page.Value.TryGetProperty("images", out var images))
                    {
                        var portraitFile = GetFirstPortraitImage(images);
                        if (!string.IsNullOrEmpty(portraitFile))
                        {
                            await Task.Delay(400, ct); // Extra delay before secondary fetch
                            imageUrl = await FetchImageUrlFromFileAsync(portraitFile, ct) ?? "";
                        }
                    }

                    var captainName = batch.FirstOrDefault(x =>
                        x.Value.Replace("_", " ") == title || x.Value == title).Key ?? title;
                    results.Add(new FetchedCaptainData(captainName, imageUrl));
                }

                await Task.Delay(800, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Wikipedia captain fetch batch failed: {ex.Message}");
            }
        }

        return results;
    }

    private static string? GetFirstPortraitImage(JsonElement images)
    {
        foreach (var img in images.EnumerateArray())
        {
            var fileTitle = img.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(fileTitle) || !fileTitle.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
                continue;
            var lower = fileTitle.ToLowerInvariant();
            if (lower.Contains("flag") || lower.Contains("ensign") || lower.Contains("icon") ||
                lower.Contains(".svg") || lower.Contains("oojs") || lower.Contains("edit-"))
                continue;
            if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".png") || lower.EndsWith(".webp"))
                return fileTitle;
        }
        return null;
    }

    /// <summary>Search Wikipedia by query. Returns page titles and snippets.</summary>
    public async Task<List<WikipediaSearchResult>> SearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        var results = new List<WikipediaSearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        try
        {
            var url = $"{ApiBase}?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srlimit={Math.Min(limit, 50)}&format=json";
            var json = await FetchWithRetryAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("query", out var queryEl) ||
                !queryEl.TryGetProperty("search", out var searchEl))
                return results;
            foreach (var item in searchEl.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(title))
                    results.Add(new WikipediaSearchResult(title, snippet));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Wikipedia search failed: {ex.Message}");
        }
        return results;
    }

    /// <summary>Fetch image URL from a Wikipedia page by title.</summary>
    public async Task<string?> FetchImageFromPageAsync(string pageTitle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pageTitle)) return null;
        try
        {
            var url = $"{ApiBase}?action=query&titles={Uri.EscapeDataString(pageTitle)}" +
                      "&prop=pageimages|images&pithumbsize=400&imlimit=50&format=json";
            var json = await FetchWithRetryAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            foreach (var page in pages.EnumerateObject())
            {
                if (page.Name == "-1") continue;
                var imageUrl = "";
                if (page.Value.TryGetProperty("thumbnail", out var thumb) && thumb.TryGetProperty("source", out var src))
                    imageUrl = src.GetString() ?? "";
                if (string.IsNullOrEmpty(imageUrl) && page.Value.TryGetProperty("images", out var images))
                {
                    var portraitFile = GetFirstPortraitImage(images);
                    if (!string.IsNullOrEmpty(portraitFile))
                    {
                        await Task.Delay(400, ct);
                        imageUrl = await FetchImageUrlFromFileAsync(portraitFile, ct) ?? "";
                    }
                }
                if (!string.IsNullOrEmpty(imageUrl)) return imageUrl;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Wikipedia image fetch failed for {pageTitle}: {ex.Message}");
        }
        return null;
    }

    private async Task<string?> FetchImageUrlFromFileAsync(string fileTitle, CancellationToken ct)
    {
        try
        {
            var url = $"{ApiBase}?action=query&titles={Uri.EscapeDataString(fileTitle)}" +
                      "&prop=imageinfo&iiprop=url&iiurlwidth=400&format=json";
            var json = await FetchWithRetryAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            foreach (var page in pages.EnumerateObject())
            {
                if (page.Name == "-1") continue;
                if (page.Value.TryGetProperty("imageinfo", out var info) && info.GetArrayLength() > 0)
                {
                    var first = info[0];
                    if (first.TryGetProperty("thumburl", out var thumb))
                        return thumb.GetString();
                    if (first.TryGetProperty("url", out var u))
                        return u.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Wikipedia image URL fetch failed for {fileTitle}: {ex.Message}");
        }
        return null;
    }
}

public record FetchedShipData(string Name, string Description, string ImageUrl);
public record FetchedCaptainData(string Name, string ImageUrl);
public record WikipediaSearchResult(string Title, string Snippet);
