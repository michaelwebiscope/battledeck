using System.ComponentModel.DataAnnotations.Schema;

namespace NavalArchive.Data.Models;

/// <summary>Image search source entity: Wikipedia, Pexels, Pixabay, Unsplash, Google, or custom API.</summary>
public class ImageSource
{
    public int Id { get; set; }
    public string SourceId { get; set; } = "";  // e.g. "wikipedia", "pexels", "custom-123"
    public string Name { get; set; } = "";
    public string ProviderType { get; set; } = "";  // Wikipedia | Pexels | Pixabay | Unsplash | Google | Custom
    public int RetryCount { get; set; } = 2;
    public int SortOrder { get; set; }
    public bool Enabled { get; set; } = true;
    public string? AuthKeyRef { get; set; }
    /// <summary>JSON for custom API config (BaseUrl, QueryParam, AuthType, etc.)</summary>
    [Column(TypeName = "TEXT")]
    public string? CustomConfigJson { get; set; }
}
