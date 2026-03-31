using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NavalArchive.Api.Contracts;
using NavalArchive.Data;

namespace NavalArchive.Api.Services;

public sealed class DynamicListService
{
    private readonly NavalArchiveDbContext _db;
    private readonly LogsDbContext _logsDb;
    private readonly IMemoryCache _cache;
    private readonly DynamicListDiagnosticsService _diagnostics;
    private readonly TimeSpan _rowsCacheTtl;
    private readonly TimeSpan _filterConfigCacheTtl;
    private readonly bool _useDatabaseQueryMode;

    private const int StringRadioMaxUnique = 10;
    private const int StringNameMaxUnique = 100;

    public DynamicListService(
        NavalArchiveDbContext db,
        LogsDbContext logsDb,
        IMemoryCache cache,
        DynamicListDiagnosticsService diagnostics,
        IConfiguration config
    )
    {
        _db = db;
        _logsDb = logsDb;
        _cache = cache;
        _diagnostics = diagnostics;
        var fallbackMinutes = Math.Max(1, config.GetValue<int?>("Cache:ExpirationMinutes") ?? 10);
        _rowsCacheTtl = TimeSpan.FromSeconds(
            Math.Max(10, config.GetValue<int?>("Cache:DynamicLists:RowsSeconds") ?? fallbackMinutes * 60)
        );
        _filterConfigCacheTtl = TimeSpan.FromSeconds(
            Math.Max(10, config.GetValue<int?>("Cache:DynamicLists:FilterConfigSeconds") ?? fallbackMinutes * 60)
        );
        _useDatabaseQueryMode = config.GetValue<bool>("DynamicLists:UseDatabaseQueryMode");
    }

    public async Task<DynamicListResponseDto> GetListAsync(string entity, DynamicListQueryDto query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 100 : query.PageSize, 1, 500);
        var normalizedEntity = entity.Trim().ToLowerInvariant();
        var profile = string.IsNullOrWhiteSpace(query.Profile) ? "" : query.Profile.Trim().ToLowerInvariant();
        var rowsCacheKey = $"dynamic-list:rows:{normalizedEntity}:{profile}";
        var filterConfigCacheKey = $"dynamic-list:filter-config:{normalizedEntity}:{profile}";
        var rowsCacheHit = _cache.TryGetValue(rowsCacheKey, out List<Dictionary<string, object?>>? cachedRows) && cachedRows is not null;
        List<Dictionary<string, object?>> rows;
        if (rowsCacheHit)
        {
            rows = cachedRows!;
        }
        else
        {
            rows = await LoadRowsAsync(normalizedEntity, profile, ct);
            _cache.Set(rowsCacheKey, rows, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _rowsCacheTtl,
                Priority = CacheItemPriority.High
            });
        }

        var filterConfigCacheHit = _cache.TryGetValue(filterConfigCacheKey, out List<DynamicListFilterConfigDto>? cachedFilterConfig) && cachedFilterConfig is not null;
        List<DynamicListFilterConfigDto> filterConfigAll;
        if (filterConfigCacheHit)
        {
            filterConfigAll = cachedFilterConfig!;
        }
        else
        {
            // Build filter metadata from full cached rows so options stay stable across searches/pages.
            filterConfigAll = BuildFilterConfig(rows);
            _cache.Set(filterConfigCacheKey, filterConfigAll, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _filterConfigCacheTtl,
                Priority = CacheItemPriority.High
            });
        }
        var cfgByKey = filterConfigAll.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        var active = ParseActiveFilters(query);
        active = SanitizeActiveFilters(active, cfgByKey.Keys);
        active = StripNoopRangeFilters(active, cfgByKey);
        var usedDatabaseSearch = false;
        var usedDatabaseFilter = false;
        string? dbFallbackReason = null;
        List<Dictionary<string, object?>> pageItems;
        int total;

        if (_useDatabaseQueryMode)
        {
            PagedRowsResult? dbPage = null;
            try
            {
                dbPage = await TryLoadDbPagedRowsAsync(normalizedEntity, profile, query.Q, active, page, pageSize, ct);
            }
            catch (InvalidOperationException)
            {
                dbFallbackReason = "db_query_translation_error";
            }
            if (dbPage is not null)
            {
                usedDatabaseSearch = true;
                usedDatabaseFilter = active.Count > 0;
                pageItems = dbPage.Items;
                total = dbPage.Total;
            }
            else
            {
                dbFallbackReason ??= "unsupported_filter_key_or_shape";
                var searchedRows = ApplySearch(rows, query.Q);
                var filteredRows = ApplyFilters(searchedRows, active, cfgByKey);
                total = filteredRows.Count;
                pageItems = filteredRows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            }
        }
        else
        {
            var searchedRows = ApplySearch(rows, query.Q);
            var filteredRows = ApplyFilters(searchedRows, active, cfgByKey);
            total = filteredRows.Count;
            pageItems = filteredRows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        }

        _diagnostics.RecordRequest(usedDatabaseSearch, usedDatabaseFilter, dbFallbackReason);

        return new DynamicListResponseDto
        {
            Items = pageItems,
            Total = total,
            Page = page,
            PageSize = pageSize,
            FilterConfig = filterConfigAll,
            ActiveFilters = active,
            RuntimeHints = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["entity"] = entity,
                ["profile"] = profile,
                ["rowsCacheHit"] = rowsCacheHit,
                ["filterConfigCacheHit"] = filterConfigCacheHit,
                ["usedDatabaseSearch"] = usedDatabaseSearch,
                ["usedDatabaseFilter"] = usedDatabaseFilter,
                ["dbFallbackReason"] = dbFallbackReason
            }
        };
    }

    private sealed class PagedRowsResult
    {
        public List<Dictionary<string, object?>> Items { get; init; } = [];
        public int Total { get; init; }
    }

    private async Task<PagedRowsResult?> TryLoadDbPagedRowsAsync(
        string entity,
        string profile,
        string? q,
        JsonObject active,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        return entity switch
        {
            "ships" or "ship" => await TryLoadShipDbPageAsync(profile, q, active, page, pageSize, ct),
            "classes" or "class" => await TryLoadClassDbPageAsync(q, active, page, pageSize, ct),
            "captains" or "captain" => await TryLoadCaptainDbPageAsync(q, active, page, pageSize, ct),
            "logs" or "log" => await TryLoadLogDbPageAsync(q, active, page, pageSize, ct),
            _ => null
        };
    }

    private static bool TryGetStringFilter(JsonNode? node, out string value)
    {
        value = JsonAsString(node)?.Trim() ?? "";
        return value.Length > 0;
    }

    private static bool TryGetRangeFilter(JsonNode? node, out double? min, out double? max)
    {
        min = null;
        max = null;
        if (node is not JsonObject range) return false;
        if (TryJsonDouble(range["min"], out var minVal)) min = minVal;
        if (TryJsonDouble(range["max"], out var maxVal)) max = maxVal;
        return min.HasValue || max.HasValue;
    }

    private static bool TryGetIntFilter(JsonNode? node, out int? exact, out int? min, out int? max)
    {
        exact = null;
        min = null;
        max = null;
        if (node is JsonObject)
        {
            if (!TryGetRangeFilter(node, out var dmin, out var dmax)) return false;
            if (dmin.HasValue) min = (int)Math.Floor(dmin.Value);
            if (dmax.HasValue) max = (int)Math.Ceiling(dmax.Value);
            return min.HasValue || max.HasValue;
        }

        if (!TryGetStringFilter(node, out var s)) return false;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return false;
        exact = id;
        return true;
    }

    private static bool TryGetDateRangeFilter(JsonNode? node, out DateTime? from, out DateTime? to)
    {
        from = null;
        to = null;
        if (node is not JsonObject range) return false;
        var fromRaw = JsonAsString(range["from"]);
        var toRaw = JsonAsString(range["to"]);
        if (!string.IsNullOrWhiteSpace(fromRaw) &&
            DateTime.TryParse(fromRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fromDt))
            from = fromDt;
        if (!string.IsNullOrWhiteSpace(toRaw) &&
            DateTime.TryParse(toRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var toDt))
            to = toDt;
        return from.HasValue || to.HasValue;
    }

    private static string LikePattern(string value) => $"%{value.Trim().ToLowerInvariant()}%";

    private async Task<PagedRowsResult?> TryLoadShipDbPageAsync(
        string profile,
        string? q,
        JsonObject active,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        var query = _db.Ships
            .Include(s => s.Class)
            .Include(s => s.Captain)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = LikePattern(q);
            query = query.Where(s =>
                EF.Functions.Like((s.Name ?? "").ToLower(), like) ||
                EF.Functions.Like((s.Description ?? "").ToLower(), like) ||
                (s.Class != null && (
                    EF.Functions.Like((s.Class.Name ?? "").ToLower(), like) ||
                    EF.Functions.Like((s.Class.Type ?? "").ToLower(), like) ||
                    EF.Functions.Like((s.Class.Country ?? "").ToLower(), like))) ||
                (s.Captain != null && (
                    EF.Functions.Like((s.Captain.Name ?? "").ToLower(), like) ||
                    EF.Functions.Like((s.Captain.Rank ?? "").ToLower(), like))));
        }

        foreach (var kv in active)
        {
            var key = kv.Key.ToLowerInvariant();
            switch (key)
            {
                case "name":
                    if (!TryGetStringFilter(kv.Value, out var shipName)) return null;
                    query = query.Where(s => EF.Functions.Like((s.Name ?? "").ToLower(), LikePattern(shipName)));
                    break;
                case "id":
                    if (!TryGetIntFilter(kv.Value, out var shipId, out var shipIdMin, out var shipIdMax)) return null;
                    if (shipId.HasValue) query = query.Where(s => s.Id == shipId.Value);
                    if (shipIdMin.HasValue) query = query.Where(s => s.Id >= shipIdMin.Value);
                    if (shipIdMax.HasValue) query = query.Where(s => s.Id <= shipIdMax.Value);
                    break;
                case "description":
                    if (!TryGetStringFilter(kv.Value, out var desc)) return null;
                    query = query.Where(s => EF.Functions.Like((s.Description ?? "").ToLower(), LikePattern(desc)));
                    break;
                case "class.id":
                    if (!TryGetIntFilter(kv.Value, out var classId, out var classIdMin, out var classIdMax)) return null;
                    if (classId.HasValue) query = query.Where(s => s.ClassId == classId.Value);
                    if (classIdMin.HasValue) query = query.Where(s => s.ClassId >= classIdMin.Value);
                    if (classIdMax.HasValue) query = query.Where(s => s.ClassId <= classIdMax.Value);
                    break;
                case "class.name":
                    if (!TryGetStringFilter(kv.Value, out var className)) return null;
                    query = query.Where(s => s.Class != null && EF.Functions.Like((s.Class.Name ?? "").ToLower(), LikePattern(className)));
                    break;
                case "class.type":
                    if (!TryGetStringFilter(kv.Value, out var classType)) return null;
                    query = query.Where(s => s.Class != null && EF.Functions.Like((s.Class.Type ?? "").ToLower(), LikePattern(classType)));
                    break;
                case "class.country":
                    if (!TryGetStringFilter(kv.Value, out var classCountry)) return null;
                    query = query.Where(s => s.Class != null && EF.Functions.Like((s.Class.Country ?? "").ToLower(), LikePattern(classCountry)));
                    break;
                case "captain.name":
                    if (!TryGetStringFilter(kv.Value, out var captainName)) return null;
                    query = query.Where(s => s.Captain != null && EF.Functions.Like((s.Captain.Name ?? "").ToLower(), LikePattern(captainName)));
                    break;
                case "captain.id":
                    if (!TryGetIntFilter(kv.Value, out var captainId, out var captainIdMin, out var captainIdMax)) return null;
                    if (captainId.HasValue) query = query.Where(s => s.CaptainId == captainId.Value);
                    if (captainIdMin.HasValue) query = query.Where(s => s.CaptainId >= captainIdMin.Value);
                    if (captainIdMax.HasValue) query = query.Where(s => s.CaptainId <= captainIdMax.Value);
                    break;
                case "captain.rank":
                    if (!TryGetStringFilter(kv.Value, out var captainRank)) return null;
                    query = query.Where(s => s.Captain != null && EF.Functions.Like((s.Captain.Rank ?? "").ToLower(), LikePattern(captainRank)));
                    break;
                case "yearcommissioned":
                    if (!TryGetRangeFilter(kv.Value, out var minYear, out var maxYear)) return null;
                    if (minYear.HasValue) query = query.Where(s => s.YearCommissioned >= (int)Math.Floor(minYear.Value));
                    if (maxYear.HasValue) query = query.Where(s => s.YearCommissioned <= (int)Math.Ceiling(maxYear.Value));
                    break;
                default:
                    return null;
            }
        }

        var total = await query.CountAsync(ct);
        var ships = await query
            .OrderBy(s => s.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var rows = new List<Dictionary<string, object?>>(ships.Count);
        foreach (var s in ships)
        {
            rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = s.Id,
                ["name"] = s.Name,
                ["description"] = s.Description,
                ["imageUrl"] = s.ImageUrl,
                ["imageVersion"] = s.ImageVersion,
                ["yearCommissioned"] = s.YearCommissioned,
                ["class"] = s.Class == null
                    ? null
                    : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = s.Class.Id,
                        ["name"] = s.Class.Name,
                        ["type"] = s.Class.Type,
                        ["country"] = s.Class.Country
                    },
                ["captain"] = s.Captain == null
                    ? null
                    : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = s.Captain.Id,
                        ["name"] = s.Captain.Name,
                        ["rank"] = s.Captain.Rank,
                        ["serviceYears"] = s.Captain.ServiceYears,
                        ["imageUrl"] = s.Captain.ImageUrl,
                        ["imageVersion"] = s.Captain.ImageVersion
                    }
            });
        }

        _ = profile;
        return new PagedRowsResult { Items = rows, Total = total };
    }

    private async Task<PagedRowsResult?> TryLoadClassDbPageAsync(
        string? q,
        JsonObject active,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        var query = _db.ShipClasses.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = LikePattern(q);
            query = query.Where(c =>
                EF.Functions.Like((c.Name ?? "").ToLower(), like) ||
                EF.Functions.Like((c.Type ?? "").ToLower(), like) ||
                EF.Functions.Like((c.Country ?? "").ToLower(), like));
        }

        foreach (var kv in active)
        {
            var key = kv.Key.ToLowerInvariant();
            switch (key)
            {
                case "name":
                    if (!TryGetStringFilter(kv.Value, out var name)) return null;
                    query = query.Where(c => EF.Functions.Like((c.Name ?? "").ToLower(), LikePattern(name)));
                    break;
                case "id":
                    if (!TryGetIntFilter(kv.Value, out var id, out var idMin, out var idMax)) return null;
                    if (id.HasValue) query = query.Where(c => c.Id == id.Value);
                    if (idMin.HasValue) query = query.Where(c => c.Id >= idMin.Value);
                    if (idMax.HasValue) query = query.Where(c => c.Id <= idMax.Value);
                    break;
                case "type":
                    if (!TryGetStringFilter(kv.Value, out var type)) return null;
                    query = query.Where(c => EF.Functions.Like((c.Type ?? "").ToLower(), LikePattern(type)));
                    break;
                case "country":
                    if (!TryGetStringFilter(kv.Value, out var country)) return null;
                    query = query.Where(c => EF.Functions.Like((c.Country ?? "").ToLower(), LikePattern(country)));
                    break;
                case "shipcount":
                    if (!TryGetRangeFilter(kv.Value, out var min, out var max)) return null;
                    if (min.HasValue) query = query.Where(c => c.Ships.Count >= (int)Math.Floor(min.Value));
                    if (max.HasValue) query = query.Where(c => c.Ships.Count <= (int)Math.Ceiling(max.Value));
                    break;
                default:
                    return null;
            }
        }

        var total = await query.CountAsync(ct);
        var classes = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Type,
                c.Country,
                ShipCount = c.Ships.Count
            })
            .ToListAsync(ct);

        var rows = classes.Select(c => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = c.Id,
            ["name"] = c.Name,
            ["type"] = c.Type,
            ["country"] = c.Country,
            ["shipCount"] = c.ShipCount
        }).ToList();

        return new PagedRowsResult { Items = rows, Total = total };
    }

    private async Task<PagedRowsResult?> TryLoadCaptainDbPageAsync(
        string? q,
        JsonObject active,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        var query = _db.Captains.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = LikePattern(q);
            query = query.Where(c =>
                EF.Functions.Like((c.Name ?? "").ToLower(), like) ||
                EF.Functions.Like((c.Rank ?? "").ToLower(), like));
        }

        foreach (var kv in active)
        {
            var key = kv.Key.ToLowerInvariant();
            switch (key)
            {
                case "name":
                    if (!TryGetStringFilter(kv.Value, out var name)) return null;
                    query = query.Where(c => EF.Functions.Like((c.Name ?? "").ToLower(), LikePattern(name)));
                    break;
                case "id":
                    if (!TryGetIntFilter(kv.Value, out var id, out var idMin, out var idMax)) return null;
                    if (id.HasValue) query = query.Where(c => c.Id == id.Value);
                    if (idMin.HasValue) query = query.Where(c => c.Id >= idMin.Value);
                    if (idMax.HasValue) query = query.Where(c => c.Id <= idMax.Value);
                    break;
                case "rank":
                    if (!TryGetStringFilter(kv.Value, out var rank)) return null;
                    query = query.Where(c => EF.Functions.Like((c.Rank ?? "").ToLower(), LikePattern(rank)));
                    break;
                case "serviceyears":
                    if (!TryGetRangeFilter(kv.Value, out var minYears, out var maxYears)) return null;
                    if (minYears.HasValue) query = query.Where(c => c.ServiceYears >= (int)Math.Floor(minYears.Value));
                    if (maxYears.HasValue) query = query.Where(c => c.ServiceYears <= (int)Math.Ceiling(maxYears.Value));
                    break;
                case "shipcount":
                    if (!TryGetRangeFilter(kv.Value, out var minShips, out var maxShips)) return null;
                    if (minShips.HasValue) query = query.Where(c => c.Ships.Count >= (int)Math.Floor(minShips.Value));
                    if (maxShips.HasValue) query = query.Where(c => c.Ships.Count <= (int)Math.Ceiling(maxShips.Value));
                    break;
                default:
                    return null;
            }
        }

        var total = await query.CountAsync(ct);
        var captains = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Rank,
                c.ServiceYears,
                c.ImageUrl,
                c.ImageVersion,
                ShipCount = c.Ships.Count
            })
            .ToListAsync(ct);

        var rows = captains.Select(c => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = c.Id,
            ["name"] = c.Name,
            ["rank"] = c.Rank,
            ["serviceYears"] = c.ServiceYears,
            ["imageUrl"] = c.ImageUrl,
            ["imageVersion"] = c.ImageVersion,
            ["shipCount"] = c.ShipCount
        }).ToList();

        return new PagedRowsResult { Items = rows, Total = total };
    }

    private async Task<PagedRowsResult?> TryLoadLogDbPageAsync(
        string? q,
        JsonObject active,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        var query = _logsDb.CaptainLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = LikePattern(q);
            query = query.Where(l =>
                EF.Functions.Like((l.ShipName ?? "").ToLower(), like) ||
                EF.Functions.Like((l.Source ?? "").ToLower(), like) ||
                EF.Functions.Like((l.Entry ?? "").ToLower(), like));
        }

        foreach (var kv in active)
        {
            var key = kv.Key.ToLowerInvariant();
            switch (key)
            {
                case "shipname":
                    if (!TryGetStringFilter(kv.Value, out var shipName)) return null;
                    query = query.Where(l => EF.Functions.Like((l.ShipName ?? "").ToLower(), LikePattern(shipName)));
                    break;
                case "id":
                    if (!TryGetIntFilter(kv.Value, out var id, out var idMin, out var idMax)) return null;
                    if (id.HasValue) query = query.Where(l => l.Id == id.Value);
                    if (idMin.HasValue) query = query.Where(l => l.Id >= idMin.Value);
                    if (idMax.HasValue) query = query.Where(l => l.Id <= idMax.Value);
                    break;
                case "source":
                    if (!TryGetStringFilter(kv.Value, out var source)) return null;
                    query = query.Where(l => EF.Functions.Like((l.Source ?? "").ToLower(), LikePattern(source)));
                    break;
                case "entry":
                case "excerpt":
                    if (!TryGetStringFilter(kv.Value, out var entry)) return null;
                    query = query.Where(l => EF.Functions.Like((l.Entry ?? "").ToLower(), LikePattern(entry)));
                    break;
                case "logdate":
                    if (!TryGetDateRangeFilter(kv.Value, out var from, out var to)) return null;
                    if (from.HasValue)
                    {
                        var fromDate = from.Value.ToString("yyyy-MM-dd");
                        query = query.Where(l => string.Compare(l.LogDate, fromDate, StringComparison.Ordinal) >= 0);
                    }
                    if (to.HasValue)
                    {
                        var toDate = to.Value.ToString("yyyy-MM-dd");
                        query = query.Where(l => string.Compare(l.LogDate, toDate, StringComparison.Ordinal) <= 0);
                    }
                    break;
                default:
                    return null;
            }
        }

        var total = await query.CountAsync(ct);
        var logs = await query
            .OrderByDescending(l => l.LogDate)
            .ThenBy(l => l.ShipName)
            .ThenBy(l => l.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        const int maxExcerptLength = 500;
        var rows = logs.Select(l => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = l.Id,
            ["shipName"] = l.ShipName,
            ["logDate"] = l.LogDate,
            ["source"] = l.Source,
            ["entry"] = l.Entry,
            ["excerpt"] = string.IsNullOrEmpty(l.Entry)
                ? ""
                : l.Entry.Length > maxExcerptLength
                    ? l.Entry[..maxExcerptLength] + "…"
                    : l.Entry
        }).ToList();

        return new PagedRowsResult { Items = rows, Total = total };
    }

    private async Task<List<Dictionary<string, object?>>> LoadRowsAsync(string entity, string profile, CancellationToken ct)
    {
        return entity switch
        {
            "ships" or "ship" => await LoadShipRowsAsync(profile, ct),
            "classes" or "class" => await LoadClassRowsAsync(ct),
            "captains" or "captain" => await LoadCaptainRowsAsync(ct),
            "logs" or "log" => await LoadLogRowsAsync(ct),
            _ => throw new ArgumentException($"Unsupported list entity: {entity}", nameof(entity))
        };
    }

    private async Task<List<Dictionary<string, object?>>> LoadShipRowsAsync(string profile, CancellationToken ct)
    {
        var ships = await _db.Ships
            .Include(s => s.Class)
            .Include(s => s.Captain)
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        var rows = new List<Dictionary<string, object?>>(ships.Count);
        foreach (var s in ships)
        {
            rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = s.Id,
                ["name"] = s.Name,
                ["description"] = s.Description,
                ["imageUrl"] = s.ImageUrl,
                ["imageVersion"] = s.ImageVersion,
                ["yearCommissioned"] = s.YearCommissioned,
                ["class"] = s.Class == null
                    ? null
                    : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = s.Class.Id,
                        ["name"] = s.Class.Name,
                        ["type"] = s.Class.Type,
                        ["country"] = s.Class.Country
                    },
                ["captain"] = s.Captain == null
                    ? null
                    : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = s.Captain.Id,
                        ["name"] = s.Captain.Name,
                        ["rank"] = s.Captain.Rank,
                        ["serviceYears"] = s.Captain.ServiceYears,
                        ["imageUrl"] = s.Captain.ImageUrl,
                        ["imageVersion"] = s.Captain.ImageVersion
                    }
            });
        }

        // Profile-specific shaping can evolve over time; current gallery profile uses same fields.
        _ = profile;
        return rows;
    }

    private async Task<List<Dictionary<string, object?>>> LoadClassRowsAsync(CancellationToken ct)
    {
        var classes = await _db.ShipClasses
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.Type, c.Country, ShipCount = c.Ships.Count })
            .ToListAsync(ct);

        return classes
            .Select(c => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["type"] = c.Type,
                ["country"] = c.Country,
                ["shipCount"] = c.ShipCount
            })
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> LoadCaptainRowsAsync(CancellationToken ct)
    {
        var captains = await _db.Captains
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Rank,
                c.ServiceYears,
                c.ImageUrl,
                c.ImageVersion,
                ShipCount = c.Ships.Count
            })
            .ToListAsync(ct);

        return captains
            .Select(c => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["rank"] = c.Rank,
                ["serviceYears"] = c.ServiceYears,
                ["imageUrl"] = c.ImageUrl,
                ["imageVersion"] = c.ImageVersion,
                ["shipCount"] = c.ShipCount
            })
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> LoadLogRowsAsync(CancellationToken ct)
    {
        const int maxExcerptLength = 500;
        var logs = await _logsDb.CaptainLogs
            .AsNoTracking()
            .OrderByDescending(l => l.LogDate)
            .ThenBy(l => l.ShipName)
            .ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.ShipName, l.LogDate, l.Entry, l.Source })
            .ToListAsync(ct);

        return logs
            .Select(l => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = l.Id,
                ["shipName"] = l.ShipName,
                ["logDate"] = l.LogDate,
                ["source"] = l.Source,
                ["entry"] = l.Entry,
                ["excerpt"] = string.IsNullOrEmpty(l.Entry)
                    ? ""
                    : l.Entry.Length > maxExcerptLength
                        ? l.Entry[..maxExcerptLength] + "…"
                        : l.Entry
            })
            .ToList();
    }

    private static List<Dictionary<string, object?>> ApplySearch(List<Dictionary<string, object?>> rows, string? q)
    {
        var qq = string.IsNullOrWhiteSpace(q) ? "" : q.Trim().ToLowerInvariant();
        if (qq.Length == 0) return rows;
        return rows.Where(r =>
        {
            var flat = FlattenRow(r);
            foreach (var value in flat.Values)
            {
                if (value == null) continue;
                if (Convert.ToString(value, CultureInfo.InvariantCulture)?.ToLowerInvariant().Contains(qq) == true)
                    return true;
            }

            return false;
        }).ToList();
    }

    private static List<DynamicListFilterConfigDto> BuildFilterConfig(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return [];
        var byKey = new Dictionary<string, List<object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var flat = FlattenRow(row);
            foreach (var kv in flat)
            {
                if (ShouldSkipKey(kv.Key)) continue;
                if (!byKey.TryGetValue(kv.Key, out var list))
                {
                    list = [];
                    byKey[kv.Key] = list;
                }

                list.Add(kv.Value);
            }
        }

        var outCfg = new List<DynamicListFilterConfigDto>();
        foreach (var key in byKey.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var samples = byKey[key].Where(v => v is not null && Convert.ToString(v, CultureInfo.InvariantCulture) != "").ToList();
            if (samples.Count == 0) continue;

            var cfg = InferFieldConfig(key, samples, rows.Count);
            cfg.Key = key;
            cfg.Label = KeyToLabel(key);
            outCfg.Add(cfg);
        }

        return outCfg;
    }

    private static DynamicListFilterConfigDto InferFieldConfig(string key, List<object?> samples, int entityCount)
    {
        if (samples.All(v => v is bool))
        {
            return new DynamicListFilterConfigDto { Type = "radio", Options = ["true", "false"] };
        }

        var strValues = samples.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Where(s => s != "").ToList();
        if (strValues.Count == 0)
            return new DynamicListFilterConfigDto { Type = "text" };

        if (strValues.All(IsIsoDateString))
        {
            var dates = strValues.Select(s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)).ToList();
            return new DynamicListFilterConfigDto
            {
                Type = "range",
                RangeMode = "date",
                DateMin = dates.Min().ToString("yyyy-MM-dd"),
                DateMax = dates.Max().ToString("yyyy-MM-dd")
            };
        }

        if (strValues.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var nums = strValues
                .Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToList();
            var rangeSamples = NumericSamplesForRangeBounds(nums);
            return new DynamicListFilterConfigDto
            {
                Type = "range",
                RangeMode = "number",
                Min = rangeSamples.Min(),
                Max = rangeSamples.Max()
            };
        }

        var uniq = strValues.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var nameLike = IsLikelyNameEnumKey(key);
        var maxEnum = nameLike ? StringNameMaxUnique : StringRadioMaxUnique;
        if (uniq.Count > maxEnum) return new DynamicListFilterConfigDto { Type = "text" };
        if (uniq.Count == entityCount && entityCount > maxEnum) return new DynamicListFilterConfigDto { Type = "text" };

        return new DynamicListFilterConfigDto
        {
            Type = nameLike ? "dropdown" : "radio",
            Options = uniq
        };
    }

    private static List<double> NumericSamplesForRangeBounds(List<double> nums)
    {
        if (!LooksLikeCalendarYearColumn(nums)) return nums;
        var positives = nums.Where(x => x > 0).ToList();
        return positives.Count > 0 ? positives : nums;
    }

    private static bool LooksLikeCalendarYearColumn(List<double> nums)
    {
        if (nums.Count == 0) return false;
        if (!nums.Any(x => x == 0)) return false;
        var positives = nums.Where(x => x > 0).ToList();
        if (positives.Count == 0) return false;
        var mn = positives.Min();
        var mx = positives.Max();
        return mn >= 500 && mx <= 4000 && mn <= mx;
    }

    private static JsonObject ParseActiveFilters(DynamicListQueryDto query)
    {
        JsonObject outObj = [];
        if (!string.IsNullOrWhiteSpace(query.Df))
        {
            try
            {
                var node = JsonNode.Parse(query.Df);
                if (node is JsonObject jo)
                {
                    outObj = jo;
                }
            }
            catch
            {
                // ignore invalid JSON and leave filters empty
            }
        }

        return outObj;
    }

    private static JsonObject SanitizeActiveFilters(JsonObject active, IEnumerable<string> allowedKeys)
    {
        var allow = new HashSet<string>(allowedKeys, StringComparer.OrdinalIgnoreCase);
        JsonObject outObj = [];
        foreach (var kv in active)
        {
            if (allow.Contains(kv.Key)) outObj[kv.Key] = kv.Value?.DeepClone();
        }

        return outObj;
    }

    private static JsonObject StripNoopRangeFilters(
        JsonObject active,
        IReadOnlyDictionary<string, DynamicListFilterConfigDto> cfgByKey
    )
    {
        JsonObject outObj = [];
        foreach (var kv in active)
        {
            if (!cfgByKey.TryGetValue(kv.Key, out var cfg))
            {
                outObj[kv.Key] = kv.Value?.DeepClone();
                continue;
            }

            if (cfg.Type != "range")
            {
                outObj[kv.Key] = kv.Value?.DeepClone();
                continue;
            }

            if (kv.Value is not JsonObject o)
            {
                outObj[kv.Key] = kv.Value?.DeepClone();
                continue;
            }

            if (cfg.RangeMode == "number")
            {
                var hasMin = TryJsonDouble(o["min"], out var minVal);
                var hasMax = TryJsonDouble(o["max"], out var maxVal);
                var sameMin = hasMin && cfg.Min.HasValue && Math.Abs(minVal - cfg.Min.Value) < 0.0001;
                var sameMax = hasMax && cfg.Max.HasValue && Math.Abs(maxVal - cfg.Max.Value) < 0.0001;
                if ((!hasMin || sameMin) && (!hasMax || sameMax)) continue;
                outObj[kv.Key] = o.DeepClone();
                continue;
            }

            if (cfg.RangeMode == "date")
            {
                var from = JsonAsString(o["from"]);
                var to = JsonAsString(o["to"]);
                var sameFrom = !string.IsNullOrWhiteSpace(from) &&
                               !string.IsNullOrWhiteSpace(cfg.DateMin) &&
                               string.Equals(from, cfg.DateMin, StringComparison.Ordinal);
                var sameTo = !string.IsNullOrWhiteSpace(to) &&
                             !string.IsNullOrWhiteSpace(cfg.DateMax) &&
                             string.Equals(to, cfg.DateMax, StringComparison.Ordinal);
                if ((string.IsNullOrWhiteSpace(from) || sameFrom) && (string.IsNullOrWhiteSpace(to) || sameTo)) continue;
                outObj[kv.Key] = o.DeepClone();
                continue;
            }

            outObj[kv.Key] = kv.Value?.DeepClone();
        }

        return outObj;
    }

    private static List<Dictionary<string, object?>> ApplyFilters(
        List<Dictionary<string, object?>> rows,
        JsonObject active,
        IReadOnlyDictionary<string, DynamicListFilterConfigDto> cfgByKey
    )
    {
        if (active.Count == 0) return rows;
        return rows.Where(row =>
        {
            var flat = FlattenRow(row);
            foreach (var kv in active)
            {
                if (!cfgByKey.TryGetValue(kv.Key, out var cfg)) continue;
                flat.TryGetValue(kv.Key, out var raw);
                if (!MatchesFilter(raw, kv.Value, cfg)) return false;
            }

            return true;
        }).ToList();
    }

    private static bool MatchesFilter(object? rowValue, JsonNode? filterNode, DynamicListFilterConfigDto cfg)
    {
        if (filterNode is null) return true;
        if (cfg.Type == "range" && filterNode is JsonObject range)
        {
            if (cfg.RangeMode == "number")
            {
                if (!TryObjectDouble(rowValue, out var rowNum)) return false;
                var hasMin = TryJsonDouble(range["min"], out var minVal);
                var hasMax = TryJsonDouble(range["max"], out var maxVal);
                if (hasMin && rowNum < minVal) return false;
                if (hasMax && rowNum > maxVal) return false;
                return true;
            }

            if (cfg.RangeMode == "date")
            {
                var rowDateStr = Convert.ToString(rowValue, CultureInfo.InvariantCulture);
                if (!DateTime.TryParse(rowDateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rowDate))
                    return false;
                var from = JsonAsString(range["from"]);
                var to = JsonAsString(range["to"]);
                if (!string.IsNullOrWhiteSpace(from) &&
                    DateTime.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fromDt) &&
                    rowDate < fromDt)
                    return false;
                if (!string.IsNullOrWhiteSpace(to) &&
                    DateTime.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var toDt) &&
                    rowDate > toDt)
                    return false;
                return true;
            }
        }

        var needle = JsonAsString(filterNode)?.Trim() ?? "";
        if (needle.Length == 0) return true;
        var rowStr = Convert.ToString(rowValue, CultureInfo.InvariantCulture) ?? "";
        if (cfg.Type == "text")
        {
            return rowStr.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(rowStr, needle, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> FlattenRow(Dictionary<string, object?> row)
    {
        var outMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        FlattenInto(row, "", outMap);
        return outMap;
    }

    private static void FlattenInto(object? value, string prefix, Dictionary<string, object?> outMap)
    {
        if (value is null) return;
        if (value is IDictionary<string, object?> dict)
        {
            foreach (var kv in dict)
            {
                var next = string.IsNullOrEmpty(prefix) ? kv.Key : $"{prefix}.{kv.Key}";
                FlattenInto(kv.Value, next, outMap);
            }

            return;
        }

        if (value is JsonObject jo)
        {
            foreach (var kv in jo)
            {
                var next = string.IsNullOrEmpty(prefix) ? kv.Key : $"{prefix}.{kv.Key}";
                FlattenInto(kv.Value, next, outMap);
            }

            return;
        }

        if (value is JsonValue jv)
        {
            outMap[prefix] = jv.ToString();
            return;
        }

        if (value is IEnumerable<object?> && value is not string)
        {
            return; // skip arrays/collections
        }

        outMap[prefix] = value;
    }

    private static string KeyToLabel(string key)
    {
        var parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var labels = parts.Select(seg =>
        {
            if (seg.Length == 0) return seg;
            var withSpaces = System.Text.RegularExpressions.Regex.Replace(seg, "([A-Z])", " $1").Trim();
            return withSpaces.Length == 0
                ? seg
                : char.ToUpperInvariant(withSpaces[0]) + withSpaces[1..];
        });
        return string.Join(" · ", labels);
    }

    private static bool ShouldSkipKey(string key)
    {
        var l = key.ToLowerInvariant();
        return l.Contains("imagedata") || l.Contains("imageurl") || l.Contains("imageversion") || l.Contains("videourl");
    }

    private static bool IsLikelyNameEnumKey(string key)
    {
        var k = key.ToLowerInvariant();
        return (k.Contains("captain") && k.Contains("name")) || k.EndsWith(".name", StringComparison.Ordinal) || k == "name" || k.Contains(".name.");
    }

    private static bool IsIsoDateString(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length < 10) return false;
        if (!char.IsDigit(s[0]) || !char.IsDigit(s[1]) || !char.IsDigit(s[2]) || !char.IsDigit(s[3])) return false;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
    }

    private static bool TryObjectDouble(object? value, out double d)
    {
        if (value is null)
        {
            d = 0;
            return false;
        }
        var s = Convert.ToString(value, CultureInfo.InvariantCulture);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    private static bool TryJsonDouble(JsonNode? value, out double d)
    {
        var s = JsonAsString(value);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    private static string? JsonAsString(JsonNode? value)
    {
        if (value is null) return null;
        if (value is JsonValue jv) return jv.ToString();
        return value.ToJsonString();
    }
}
