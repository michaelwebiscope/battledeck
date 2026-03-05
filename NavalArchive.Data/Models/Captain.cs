namespace NavalArchive.Data.Models;

public class Captain
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public int ServiceYears { get; set; }
    public string? ImageUrl { get; set; }
    /// <summary>Cached image bytes from ImageUrl; null if not yet fetched.</summary>
    public byte[]? ImageData { get; set; }
    /// <summary>Content-Type of ImageData (e.g. image/jpeg).</summary>
    public string? ImageContentType { get; set; }
    /// <summary>Incremented when image changes; use in URLs for cache busting.</summary>
    public long ImageVersion { get; set; }
    /// <summary>True when image was manually selected; sync will not overwrite until deleted.</summary>
    public bool ImageManuallySet { get; set; }
    public ICollection<Ship> Ships { get; set; } = new List<Ship>();
}
