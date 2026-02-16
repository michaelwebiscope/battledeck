namespace NavalArchive.Api.Models;

public class ShipClass
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Battleship, Carrier, etc.
    public string Country { get; set; } = string.Empty;
    public ICollection<Ship> Ships { get; set; } = new List<Ship>();
}
