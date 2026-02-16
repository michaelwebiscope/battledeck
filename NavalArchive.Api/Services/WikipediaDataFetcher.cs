using System.Net.Http.Json;
using System.Text.Json;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Services;

public class WikipediaDataFetcher
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string ApiBase = "https://en.wikipedia.org/w/api.php";

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
        ["USS Franklin"] = "USS_Franklin_(CV-13)"
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
                var json = await _http.GetStringAsync(url, ct);
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

                await Task.Delay(200, ct); // Be nice to Wikipedia
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Wikipedia fetch batch failed: {ex.Message}");
            }
        }

        return results;
    }
}

public record FetchedShipData(string Name, string Description, string ImageUrl);
