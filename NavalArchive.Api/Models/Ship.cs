namespace NavalArchive.Api.Models;

public class Ship
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ClassId { get; set; }
    public int CaptainId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public int YearCommissioned { get; set; }

    public ShipClass? Class { get; set; }
    public Captain? Captain { get; set; }
}
