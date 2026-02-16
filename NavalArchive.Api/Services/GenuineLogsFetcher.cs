using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Api.Data;
using NavalArchive.Api.Models;

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
        ("https://midway1942.com/docs/usn_doc_18.shtml", "VMSB-241", "Marine Scout-Bombing 241 Report June 1942")
    };

    public async Task FetchAndSaveAsync(LogsDbContext db, CancellationToken ct = default)
    {
        var allEntries = new List<CaptainLog>();
        var entryId = 1;

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
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Logs fetch failed {url}: {ex.Message}");
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
                    var remarks = Regex.Replace(m.Groups[2].Value.Trim(), @"\s+", " ");
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
        if (text.Length < 12 || text.Length > 600) return false;
        if (text.Contains("Approved:") || text.Contains("page ")) return false;
        var letters = text.Count(c => char.IsLetter(c));
        var total = text.Length;
        if (total == 0) return false;
        if ((double)letters / total < 0.4) return false;
        var invalidChars = text.Count(c => c > 127 || (c < 32 && c != '\t'));
        if ((double)invalidChars / total > 0.05) return false;
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
