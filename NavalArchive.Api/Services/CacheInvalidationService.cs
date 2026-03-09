using Microsoft.Extensions.Caching.Memory;

namespace NavalArchive.Api.Services;

/// <summary>Bumps cache versions to invalidate dependent entity caches. IMemoryCache has no prefix invalidation.</summary>
public class CacheInvalidationService
{
    private readonly IMemoryCache _cache;
    private const string ShipsVersionKey = "cache:ships:version";
    private const string ClassesVersionKey = "cache:classes:version";
    private const string CaptainsVersionKey = "cache:captains:version";
    private const string StatsVersionKey = "cache:stats:version";

    public CacheInvalidationService(IMemoryCache cache) => _cache = cache;

    public int GetShipsVersion() => _cache.GetOrCreate(ShipsVersionKey, _ => 1);
    public int GetClassesVersion() => _cache.GetOrCreate(ClassesVersionKey, _ => 1);
    public int GetCaptainsVersion() => _cache.GetOrCreate(CaptainsVersionKey, _ => 1);
    public int GetStatsVersion() => _cache.GetOrCreate(StatsVersionKey, _ => 1);

    /// <summary>Invalidate ships cache (e.g. when class or captain changes).</summary>
    public void InvalidateShips()
    {
        var v = GetShipsVersion();
        _cache.Set(ShipsVersionKey, v + 1);
    }

    /// <summary>Invalidate classes cache (e.g. when ship changes).</summary>
    public void InvalidateClasses()
    {
        var v = GetClassesVersion();
        _cache.Set(ClassesVersionKey, v + 1);
    }

    /// <summary>Invalidate captains cache (e.g. when ship changes).</summary>
    public void InvalidateCaptains()
    {
        var v = GetCaptainsVersion();
        _cache.Set(CaptainsVersionKey, v + 1);
    }

    /// <summary>Invalidate stats cache.</summary>
    public void InvalidateStats()
    {
        var v = GetStatsVersion();
        _cache.Set(StatsVersionKey, v + 1);
    }

    /// <summary>When class is created or updated: invalidate class + ships (ships display class).</summary>
    public void OnClassUpdated()
    {
        var v = GetClassesVersion();
        _cache.Set(ClassesVersionKey, v + 1);
        InvalidateShips();
        InvalidateStats();
    }

    /// <summary>When class is created: invalidate classes list + ships.</summary>
    public void OnClassCreated()
    {
        OnClassUpdated();
    }

    /// <summary>When captain is created: invalidate captains list.</summary>
    public void OnCaptainCreated()
    {
        var v = GetCaptainsVersion();
        _cache.Set(CaptainsVersionKey, v + 1);
    }

    /// <summary>When captain is updated: invalidate captain + ships (ships display captain).</summary>
    public void OnCaptainUpdated()
    {
        var v = GetCaptainsVersion();
        _cache.Set(CaptainsVersionKey, v + 1);
        InvalidateShips();
        InvalidateStats();
    }

    /// <summary>When ship is updated: invalidate ship + classes + captains (they show ship counts).</summary>
    public void OnShipUpdated()
    {
        InvalidateShips();
        InvalidateClasses();
        InvalidateCaptains();
        InvalidateStats();
    }
}
