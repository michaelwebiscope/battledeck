using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using NavalArchive.Api.Data;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    private static readonly Regex _catastrophicRegex = new("(a+)+$", RegexOptions.Compiled);
    private readonly LogsDbContext _logsDb;
    private readonly LogsDataService _logsData;

    public LogsController(LogsDbContext logsDb, LogsDataService logsData)
    {
        _logsDb = logsDb;
        _logsData = logsData;
    }

    /// <summary>
    /// Normal search: finds matches in database. Catastrophic regex on "aaa...aX" hangs CPU.
    /// </summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] LogSearchRequest request)
    {
        if (string.IsNullOrEmpty(request?.Query))
            return Ok(new { matches = 0, message = "No query provided", excerpts = Array.Empty<string>() });

        var query = request.Query.Trim();

        // CPU SPIKE: Catastrophic backtracking - if query looks like attack pattern, use regex
        if (query.Length > 15 && query.Contains('a') && query.EndsWith('X'))
        {
            var _ = _catastrophicRegex.Matches(query);
            return Ok(new { matches = _.Count, message = "Search completed", excerpts = Array.Empty<string>() });
        }

        // Search database for genuine log entries (EF Contains -> SQL LIKE, case-insensitive in SQLite)
        var count = await _logsDb.CaptainLogs
            .Where(l => l.Entry.Contains(query))
            .CountAsync();

        var rawLogs = await _logsDb.CaptainLogs
            .Where(l => l.Entry.Contains(query))
            .Take(50)
            .Select(l => new { l.Id, l.ShipName, l.LogDate, l.Entry, l.Source })
            .ToListAsync();

        const int maxExcerptLength = 500;
        var excerptItems = rawLogs
            .Where(l => IsReadableLogEntry(l.Entry))
            .Take(20)
            .Select(l => new
            {
                excerpt = l.Entry.Length > maxExcerptLength ? l.Entry[..maxExcerptLength] + "…" : l.Entry,
                shipName = l.ShipName,
                logDate = l.LogDate,
                source = l.Source,
                id = l.Id
            })
            .ToList();

        if (excerptItems.Count == 0 && rawLogs.Count > 0)
            count = 0;

        if (excerptItems.Count == 0)
        {
            var fallback = _logsData.GetLogContent();
            var idx = fallback.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var lineStart = fallback.LastIndexOf('\n', Math.Max(0, idx - 1)) + 1;
                var lineEnd = fallback.IndexOf('\n', idx);
                var line = lineEnd >= 0 ? fallback[lineStart..lineEnd] : fallback[lineStart..];
                var trimmed = line.Trim();
                if (trimmed.Length > maxExcerptLength) trimmed = trimmed[..maxExcerptLength] + "…";
                excerptItems.Add(new { excerpt = trimmed, shipName = "", logDate = "", source = "", id = 0 });
            }
        }
        return Ok(new
        {
            matches = count,
            message = count > 0 ? $"Found {count} entries in the Captain's Logs." : "No matching entries found.",
            excerpts = excerptItems
        });
    }

    /// <summary>
    /// Get full day's log entries for a ship on a given date.
    /// </summary>
    [HttpGet("day")]
    public async Task<IActionResult> GetDayLog([FromQuery] string shipName, [FromQuery] string logDate)
    {
        if (string.IsNullOrEmpty(shipName) || string.IsNullOrEmpty(logDate))
            return BadRequest(new { error = "shipName and logDate are required" });

        var entries = await _logsDb.CaptainLogs
            .Where(l => l.ShipName == shipName && l.LogDate == logDate)
            .OrderBy(l => l.Id)
            .Select(l => new { l.Entry, l.Source })
            .ToListAsync();

        if (entries.Count == 0)
            return NotFound(new { error = "No log entries found for this ship and date" });

        return Ok(new
        {
            shipName,
            logDate,
            source = entries.First().Source,
            entries = entries.Select(e => e.Entry)
        });
    }

    private static bool IsReadableLogEntry(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 100_000) return false;
        var letters = text.Count(c => char.IsLetter(c));
        var spaces = text.Count(c => c == ' ');
        var symbolHeavy = text.Count(c => "<>[]{}|\\^~`@#$%&*+=_".Contains(c));
        if (text.Length == 0) return false;
        if ((double)letters / text.Length < 0.5) return false;
        if ((double)symbolHeavy / text.Length > 0.08) return false;
        if (text.Length > 100 && spaces < 3) return false;
        return true;
    }
}

public class LogSearchRequest
{
    public string Query { get; set; } = string.Empty;
}
