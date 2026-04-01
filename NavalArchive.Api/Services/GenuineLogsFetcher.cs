using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Data;
using NavalArchive.Data.Models;

namespace NavalArchive.Api.Services;

public class GenuineLogsFetcher
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly (string Url, string ShipName, string Source)[] Sources =
    {
        ("https://midway1942.com/docs/usn_doc_21.shtml", "USS Enterprise", "USS Enterprise War Diary June 1942"),
        ("https://midway1942.com/docs/usn_doc_23.shtml", "USS Hornet", "USS Hornet Deck Log June 4-6 1942"),
        ("https://midway1942.com/docs/usn_doc_12.shtml", "NAS Midway", "NAS Midway Island War Diary May 1942"),
        ("https://midway1942.com/docs/usn_doc_20.shtml", "NAS Midway", "NAS Midway Island War Diary Battle"),
        ("https://midway1942.com/docs/usn_doc_04.shtml", "USS Yorktown", "USS Yorktown Action Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_05.shtml", "USS Enterprise", "USS Enterprise Action Report June 8 1942"),
        ("https://midway1942.com/docs/usn_doc_06.shtml", "USS Enterprise", "USS Enterprise Action Report June 13 1942"),
        ("https://midway1942.com/docs/usn_doc_07.shtml", "USS Hornet", "USS Hornet Action Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_02.shtml", "Task Force 16", "Task Force 16 Action Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_03.shtml", "Task Force 17", "Task Force 17 Action Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_13.shtml", "NAS Midway", "NAS Midway CO Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_17.shtml", "VMF-221", "Marine Fighting Squadron 221 Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_18.shtml", "VMSB-241", "Marine Scout-Bombing 241 Report June 1942"),
        // Additional Midway documents
        ("https://midway1942.com/docs/usn_doc_01.shtml", "USS Yorktown", "USS Yorktown Deck Log June 1942"),
        ("https://midway1942.com/docs/usn_doc_08.shtml", "USS Yorktown", "USS Yorktown Air Group Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_09.shtml", "USS Enterprise", "USS Enterprise Air Group Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_10.shtml", "USS Hornet", "USS Hornet Air Group Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_11.shtml", "Task Force 17", "Task Force 17 Screening Ships Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_14.shtml", "NAS Midway", "NAS Midway Patrol Wing Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_15.shtml", "VMF-221", "VMF-221 Pilot Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_16.shtml", "VMSB-241", "VMSB-241 Pilot Report June 1942"),
        ("https://midway1942.com/docs/usn_doc_19.shtml", "NAS Midway", "NAS Midway Signal Log June 1942"),
        ("https://midway1942.com/docs/usn_doc_22.shtml", "USS Hornet", "USS Hornet Bombing Squadron Report June 1942"),
    };

    // Wikipedia articles mapped to ship/unit names for log attribution
    private static readonly (string Title, string ShipName, string LogDate)[] WikiSources =
    {
        ("Battle_of_Midway",              "USS Enterprise",   "1942-06-04"),
        ("Battle_of_Leyte_Gulf",          "USS Enterprise",   "1944-10-23"),
        ("Battle_of_the_Coral_Sea",       "USS Yorktown",     "1942-05-07"),
        ("Battle_of_the_Atlantic",        "Task Force 16",    "1942-06-01"),
        ("German_battleship_Bismarck",    "Bismarck",         "1941-05-24"),
        ("Attack_on_Pearl_Harbor",        "USS Enterprise",   "1941-12-07"),
        ("Battle_of_Guadalcanal",         "USS Hornet",       "1942-10-26"),
        ("HMS_Hood_(51)",                 "HMS Hood",         "1941-05-24"),
        ("Japanese_battleship_Yamato",    "Yamato",           "1945-04-07"),
        ("Battle_of_the_Denmark_Strait",  "HMS Hood",         "1941-05-24"),
        ("Operation_Ten-Go",              "Yamato",           "1945-04-07"),
        ("USS_Indianapolis_(CA-35)",      "USS Indianapolis", "1945-07-30"),
        ("Battle_of_Samar",               "Task Force 17",    "1944-10-25"),
        ("Battle_of_the_Philippine_Sea",  "USS Enterprise",   "1944-06-19"),
        ("Battle_of_Cape_Matapan",        "HMS Warspite",     "1941-03-28"),
        ("Sinking_of_the_Bismarck",       "Bismarck",         "1941-05-27"),
        ("Battle_of_the_Java_Sea",        "Task Force 16",    "1942-02-27"),
        ("HMS_Prince_of_Wales_(53)",      "HMS Prince of Wales", "1941-12-10"),
        ("USS_Wasp_(CV-7)",               "USS Wasp",         "1942-09-15"),
        ("Battle_of_North_Cape",          "HMS Duke of York", "1943-12-26"),
    };

    public async Task FetchAndSaveAsync(LogsDbContext db, CancellationToken ct = default)
    {
        var allEntries = new List<CaptainLog>();
        var entryId = 1;

        // Primary source documents from midway1942.com
        foreach (var (url, shipName, source) in Sources)
        {
            try
            {
                var html = await _http.GetStringAsync(url, ct);
                var entries = ExtractLogEntries(html, shipName, source);
                foreach (var entry in entries)
                {
                    allEntries.Add(new CaptainLog
                    {
                        Id = entryId++,
                        ShipName = shipName,
                        LogDate = entry.Date,
                        Entry = entry.Text,
                        Source = source
                    });
                }
                await Task.Delay(300, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Logs fetch failed {url}: {ex.Message}");
            }
        }

        // Wikipedia: full article text split into log-style entries
        var wikiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        wikiHttp.DefaultRequestHeaders.Add("User-Agent", "NavalArchive/1.0 (demo app)");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in allEntries) seen.Add(entry.Entry);

        for (var i = 0; i < WikiSources.Length; i += 5)
        {
            var batch = WikiSources.Skip(i).Take(5).ToArray();
            var titles = string.Join("|", batch.Select(b => b.Title));
            var url = $"https://en.wikipedia.org/w/api.php?action=query&titles={Uri.EscapeDataString(titles)}&prop=extracts&explaintext&exchars=8000&format=json";
            try
            {
                var json = await wikiHttp.GetStringAsync(url, ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
                foreach (var page in pages.EnumerateObject())
                {
                    if (page.Name == "-1") continue;
                    var title = page.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var extract = page.Value.TryGetProperty("extract", out var e) ? e.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(extract)) continue;
                    var meta = batch.FirstOrDefault(b => b.Title.Replace('_', ' ').Equals(title, StringComparison.OrdinalIgnoreCase));
                    var shipName = meta.ShipName ?? title;
                    var logDate = meta.LogDate ?? "1942-06-04";
                    var source = $"Wikipedia: {title.Replace('_', ' ')}";
                    var sentences = System.Text.RegularExpressions.Regex.Split(extract, @"(?<=[.!?])\s+");
                    foreach (var sent in sentences)
                    {
                        var trimmed = sent.Trim();
                        if (IsValidLogEntry(trimmed) && seen.Add(trimmed))
                        {
                            allEntries.Add(new CaptainLog { Id = entryId++, ShipName = shipName, LogDate = logDate, Entry = trimmed, Source = source });
                        }
                    }
                }
                await Task.Delay(800, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Wikipedia logs fetch failed: {ex.Message}");
            }
        }

        if (allEntries.Count > 0)
        {
            await db.Database.EnsureCreatedAsync(ct);
            await db.CaptainLogs.ExecuteDeleteAsync(ct);
            await db.CaptainLogs.AddRangeAsync(allEntries, ct);
            await db.SaveChangesAsync(ct);
            Console.WriteLine($"Saved {allEntries.Count} genuine log entries to database.");
        }
    }

    private static List<(string Date, string Text)> ExtractLogEntries(string html, string shipName, string source)
    {
        var entries = new List<(string Date, string Text)>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var currentDate = InferDateFromSource(source);
        var timeEntryRegex = new Regex(@"(\d{4})\s+\d{4}\s*\|\s*(.+)", RegexOptions.Singleline);
        var dateRegex = new Regex(@"(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+(\d{1,2}),?\s*19(?:42|43|44|45)");

        foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
        {
            var text = node.InnerText?.Trim() ?? "";
            if (text.Length < 10) continue;

            if ((node.Name == "h3" || node.Name == "p") && dateRegex.Match(text).Success)
            {
                var m = dateRegex.Match(text);
                var monthStr = text.ToLowerInvariant();
                var month = monthStr.Contains("jan") ? "01" : monthStr.Contains("feb") ? "02" : monthStr.Contains("mar") ? "03" : monthStr.Contains("apr") ? "04" : monthStr.Contains("may") ? "05" : monthStr.Contains("jun") ? "06" : monthStr.Contains("jul") ? "07" : monthStr.Contains("aug") ? "08" : monthStr.Contains("sep") ? "09" : monthStr.Contains("oct") ? "10" : monthStr.Contains("nov") ? "11" : monthStr.Contains("dec") ? "12" : "06";
                var year = Regex.Match(text, @"19(42|43|44|45)").Groups[1].Value;
                var day = m.Groups[1].Value.PadLeft(2, '0');
                currentDate = $"19{year}-{month}-{day}";
            }

            if (node.Name == "td")
            {
                var m = timeEntryRegex.Match(text);
                if (m.Success)
                {
                    var remarks = HtmlEntity.DeEntitize(m.Groups[2].Value.Trim());
                    remarks = Regex.Replace(remarks, @"\s+", " ").Trim();
                    if (IsValidLogEntry(remarks))
                        entries.Add((currentDate, remarks));
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        entries = entries.Where(e => seen.Add(e.Text)).ToList();

        if (entries.Count == 0)
        {
            var fullText = doc.DocumentNode.InnerText;
            fullText = Regex.Replace(fullText, @"\s+", " ");
            var fallbackRegex = new Regex(@"(\d{4})\s+\d{4}\s*\|\s*([^|]+?)(?=\s*\d{4}\s+\d{4}\s*\||\z)", RegexOptions.Singleline);
            foreach (Match m in fallbackRegex.Matches(fullText))
            {
                var t = m.Groups[2].Value.Trim();
                if (t.Length > 15 && IsValidLogEntry(t)) entries.Add((currentDate, t));
            }
            var altRegex = new Regex(@"(?:At\s+)?(\d{3,4})\s*(?:hours?|\.|:|\-)\s*([^.]+\.)", RegexOptions.Singleline);
            foreach (Match m in altRegex.Matches(fullText))
            {
                var t = m.Groups[2].Value.Trim();
                if (t.Length > 20 && t.Length < 400 && IsValidLogEntry(t)) entries.Add((currentDate, t));
            }
        }

        return entries.Take(500).ToList();
    }

    private static bool IsValidLogEntry(string text)
    {
        if (text.Length < 20 || text.Length > 600) return false;

        // Reject HTML entity remnants
        if (text.Contains('&') && Regex.IsMatch(text, @"&[a-zA-Z]+;|&#\d+;")) return false;

        // Reject non-printable / non-ASCII
        var invalidChars = text.Count(c => c > 127 || (c < 32 && c != '\t'));
        if ((double)invalidChars / text.Length > 0.02) return false;

        // Must be mostly letters (prose)
        var letters = text.Count(char.IsLetter);
        if ((double)letters / text.Length < 0.50) return false;

        // Must have at least 5 words
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 5) return false;

        // Reject navigation pipe menus ("documents | order of battle | ...")
        if (text.Contains(" | ")) return false;

        // Reject mostly-uppercase headers/boilerplate
        var upperLetters = text.Count(char.IsUpper);
        if (letters > 0 && (double)upperLetters / letters > 0.55) return false;

        // Reject lines that are just a date + attribution (e.g. "June 6, 1942 CO Marine Scout-Bombing 241.")
        if (Regex.IsMatch(text, @"^(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\S*\s+\d")) return false;

        // Reject document header lines
        if (Regex.IsMatch(text, @"^Documents?\s*:", RegexOptions.IgnoreCase)) return false;

        // Reject known boilerplate
        if (text.Contains("Approved:") || text.Contains("forwarding of reports") ||
            text.StartsWith("SECRET") || text.StartsWith("CONFIDENTIAL") ||
            text.Contains("page ") || text.Contains("CinC,")) return false;

        return true;
    }

    private static string InferDateFromSource(string source)
    {
        if (source.Contains("May")) return "1942-05-15";
        if (source.Contains("June 4") || source.Contains("June 4-6")) return "1942-06-04";
        if (source.Contains("June")) return "1942-06-01";
        return "1942-06-04";
    }
}
