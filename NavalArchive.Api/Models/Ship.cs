namespace NavalArchive.Api.Models;

public class Ship
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ClassId { get; set; }
    public int CaptainId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    /// <summary>Cached image bytes from ImageUrl; null if not yet fetched.</summary>
    public byte[]? ImageData { get; set; }
    /// <summary>Content-Type of ImageData (e.g. image/jpeg).</summary>
    public string? ImageContentType { get; set; }
    public string? VideoUrl { get; set; }
    public int YearCommissioned { get; set; }

    public ShipClass? Class { get; set; }
    public Captain? Captain { get; set; }
}
