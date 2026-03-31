using System.Collections.Concurrent;
using System.Threading;

namespace NavalArchive.Api.Services;

public sealed class DynamicListDiagnosticsService
{
    private long _requestCount;
    private long _dbSearchCount;
    private long _dbFilterCount;
    private long _fallbackCount;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly ConcurrentDictionary<string, long> _fallbackReasons = new(StringComparer.OrdinalIgnoreCase);

    public void RecordRequest(bool usedDatabaseSearch, bool usedDatabaseFilter, string? fallbackReason)
    {
        Interlocked.Increment(ref _requestCount);
        if (usedDatabaseSearch) Interlocked.Increment(ref _dbSearchCount);
        if (usedDatabaseFilter) Interlocked.Increment(ref _dbFilterCount);
        if (!string.IsNullOrWhiteSpace(fallbackReason))
        {
            Interlocked.Increment(ref _fallbackCount);
            _fallbackReasons.AddOrUpdate(fallbackReason, 1, (_, oldValue) => oldValue + 1);
        }
    }

    public object Snapshot(bool dbQueryModeEnabled, bool redisEnabled, string databaseProvider)
    {
        var requests = Interlocked.Read(ref _requestCount);
        var dbSearch = Interlocked.Read(ref _dbSearchCount);
        var dbFilter = Interlocked.Read(ref _dbFilterCount);
        var fallback = Interlocked.Read(ref _fallbackCount);
        var fallbackRate = requests > 0 ? Math.Round((double)fallback / requests, 4) : 0.0;
        var dbCoverageRate = requests > 0 ? Math.Round((double)dbSearch / requests, 4) : 0.0;
        var fallbackByReason = _fallbackReasons
            .OrderByDescending(kv => kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return new
        {
            startedAtUtc = _startedAtUtc,
            uptimeSeconds = (long)Math.Max(0, (DateTime.UtcNow - _startedAtUtc).TotalSeconds),
            mode = new
            {
                dynamicListsDbQueryMode = dbQueryModeEnabled,
                redisEnabled,
                databaseProvider
            },
            counters = new
            {
                requests,
                dbSearch,
                dbFilter,
                fallback
            },
            rates = new
            {
                fallbackRate,
                dbCoverageRate
            },
            fallbackByReason
        };
    }
}
