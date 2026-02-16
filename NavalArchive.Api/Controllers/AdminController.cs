using Microsoft.AspNetCore.Mvc;
using NavalArchive.Api.Data;
using NavalArchive.Api.Services;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    private readonly NavalArchiveDbContext _db;
    private readonly LogsDbContext _logsDb;
    private readonly DataSyncService _sync;
    private readonly LogsDataService _logsData;
    private readonly GenuineLogsFetcher _genuineLogs;

    public AdminController(NavalArchiveDbContext db, LogsDbContext logsDb, DataSyncService sync, LogsDataService logsData, GenuineLogsFetcher genuineLogs)
    {
        _db = db;
        _logsDb = logsDb;
        _sync = sync;
        _logsData = logsData;
        _genuineLogs = genuineLogs;
    }

    /// <summary>
    /// Trigger refresh of ship data, Wikipedia logs, and genuine war diaries from midway1942.com.
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> SyncFromWikipedia()
    {
        try
        {
            await _sync.SyncFromWikipediaAsync(_db);
            await _logsData.RefreshFromWikipediaAsync();
            await _genuineLogs.FetchAndSaveAsync(_logsDb);
            return Ok(new { message = "Sync completed. Ships, Wikipedia logs, and genuine war diaries updated." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
