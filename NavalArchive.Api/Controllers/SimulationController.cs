using Microsoft.AspNetCore.Mvc;

namespace NavalArchive.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SimulationController : ControllerBase
{
    // CRITICAL: Not thread-safe. No lock. Race condition on concurrent Add.
    private static readonly List<string> _activeUsers = new();

    /// <summary>
    /// RACE CONDITION: Read count, sleep 100ms, then Add. Concurrent requests throw InvalidOperationException.
    /// </summary>
    [HttpPost("join")]
    public IActionResult Join([FromBody] JoinRequest request)
    {
        var userName = request?.UserName ?? "Anonymous";
        var count = _activeUsers.Count;
        Thread.Sleep(100);
        _activeUsers.Add(userName);
        return Ok(new { message = $"Welcome, {userName}. Total participants: {_activeUsers.Count}" });
    }
}

public class JoinRequest
{
    public string UserName { get; set; } = "Anonymous";
}
