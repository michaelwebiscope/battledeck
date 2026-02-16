namespace NavalArchive.Api.Models;

public class CaptainLog
{
    public int Id { get; set; }
    public string ShipName { get; set; } = string.Empty;
    public string LogDate { get; set; } = string.Empty;  // e.g. "1942-06-04"
    public string Entry { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;  // e.g. "USS Enterprise War Diary"
}
