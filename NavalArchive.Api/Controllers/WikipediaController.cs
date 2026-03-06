using Microsoft.AspNetCore.Mvc;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WikipediaController : ControllerBase
{
    private readonly WikipediaDataFetcher _fetcher = new();

    /// <summary>Search Wikipedia. Returns page titles and snippets. Results are reordered so titles matching the entity name (query minus suffix) appear first.</summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<object>>> Search([FromQuery] string q, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var query = q ?? "";
        var results = await _fetcher.SearchAsync(query, limit, ct);
        var entityPart = ExtractEntityPart(query);
        var ordered = results
            .OrderByDescending(r => ScoreMatch(r.Title, entityPart))
            .Select(r => new { title = r.Title, snippet = r.Snippet })
            .ToList();
        return Ok(ordered);
    }

    /// <summary>Extract entity name from query: "USS Fletcher battleship" -> "USS Fletcher".</summary>
    private static string ExtractEntityPart(string query)
    {
        var parts = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return query.Trim();
        var suffixes = new[] { "battleship", "captain", "destroyer", "carrier", "cruiser", "portrait", "admiral" };
        var last = parts[^1].ToLowerInvariant();
        if (suffixes.Contains(last))
            return string.Join(" ", parts.Take(parts.Length - 1)).Trim();
        return query.Trim();
    }

    /// <summary>Higher score = better match. Exact start wins, then contains, then word match.</summary>
    private static int ScoreMatch(string title, string entityPart)
    {
        if (string.IsNullOrEmpty(entityPart)) return 0;
        var titleNorm = title.Replace("_", " ");
        var entityNorm = entityPart.Trim();
        if (titleNorm.StartsWith(entityNorm, StringComparison.OrdinalIgnoreCase)) return 100;
        if (titleNorm.Contains(entityNorm, StringComparison.OrdinalIgnoreCase)) return 50;
        var entityWords = entityNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matchCount = entityWords.Count(w => titleNorm.Contains(w, StringComparison.OrdinalIgnoreCase));
        return matchCount * 10;
    }

    /// <summary>Fetch image URL from a Wikipedia page by title.</summary>
    [HttpGet("image")]
    public async Task<ActionResult<object>> GetImage([FromQuery] string title, CancellationToken ct = default)
    {
        var imageUrl = await _fetcher.FetchImageFromPageAsync(title ?? "", ct);
        if (string.IsNullOrEmpty(imageUrl)) return NotFound(new { error = "No image found for this page" });
        return Ok(new { imageUrl });
    }
}
