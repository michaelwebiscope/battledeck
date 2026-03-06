using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NavalArchive.Data;
using NavalArchive.Data.Models;
using NavalArchive.Api.Models;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/image-sources")]
public class ImageSourcesController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;

    public ImageSourcesController(NavalArchiveDbContext db)
    {
        _db = db;
    }

    /// <summary>List all image sources (entity), ordered by SortOrder.</summary>
    [HttpGet]
    public async Task<ActionResult<List<ImageSourceConfig>>> GetAll(CancellationToken ct = default)
    {
        var sources = await _db.ImageSources
            .AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .ToListAsync(ct);
        var configs = sources.Select(ToConfig).ToList();
        return Ok(configs);
    }

    /// <summary>Replace all sources (upsert by SourceId).</summary>
    [HttpPut]
    public async Task<ActionResult<List<ImageSourceConfig>>> ReplaceAll([FromBody] List<ImageSourceConfig> configs, CancellationToken ct = default)
    {
        if (configs == null) return BadRequest();
        var existing = await _db.ImageSources.ToListAsync(ct);
        _db.ImageSources.RemoveRange(existing);
        for (var i = 0; i < configs.Count; i++)
        {
            var c = configs[i];
            var id = string.IsNullOrWhiteSpace(c.Id) ? ("custom-" + Guid.NewGuid().ToString("N")[..8]) : c.Id.Trim();
            var entity = new ImageSource
            {
                SourceId = id,
                Name = c.Name ?? id,
                ProviderType = c.ProviderType ?? "Custom",
                RetryCount = Math.Clamp(c.RetryCount, 1, 5),
                SortOrder = i,
                Enabled = c.Enabled,
                AuthKeyRef = string.IsNullOrWhiteSpace(c.AuthKeyRef) ? null : c.AuthKeyRef,
                CustomConfigJson = c.CustomConfig != null ? JsonSerializer.Serialize(c.CustomConfig) : null
            };
            _db.ImageSources.Add(entity);
        }
        await _db.SaveChangesAsync(ct);
        var result = await _db.ImageSources.OrderBy(s => s.SortOrder).ToListAsync(ct);
        return Ok(result.Select(ToConfig).ToList());
    }

    /// <summary>Delete a source by id (SourceId string).</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        var entity = await _db.ImageSources.FirstOrDefaultAsync(s => s.SourceId == id, ct);
        if (entity == null) return NotFound();
        _db.ImageSources.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public static ImageSourceConfig ToConfig(ImageSource s)
    {
        CustomApiConfig? custom = null;
        if (!string.IsNullOrWhiteSpace(s.CustomConfigJson))
        {
            try
            {
                custom = JsonSerializer.Deserialize<CustomApiConfig>(s.CustomConfigJson);
            }
            catch { }
        }
        return new ImageSourceConfig
        {
            Id = s.SourceId,
            Name = s.Name,
            ProviderType = s.ProviderType,
            RetryCount = s.RetryCount,
            SortOrder = s.SortOrder,
            Enabled = s.Enabled,
            AuthKeyRef = s.AuthKeyRef,
            CustomConfig = custom
        };
    }
}
