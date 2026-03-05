using Microsoft.AspNetCore.Mvc;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WikipediaController : ControllerBase
{
    private readonly WikipediaDataFetcher _fetcher = new();

    /// <summary>Search Wikipedia. Returns page titles and snippets.</summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<object>>> Search([FromQuery] string q, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var results = await _fetcher.SearchAsync(q ?? "", limit, ct);
        return Ok(results.Select(r => new { title = r.Title, snippet = r.Snippet }).ToList());
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
