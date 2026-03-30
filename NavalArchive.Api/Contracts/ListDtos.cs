using System.Text.Json.Nodes;

namespace NavalArchive.Api.Contracts;

public sealed class DynamicListQueryDto
{
    public string? Q { get; set; }
    public string? Df { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;
    public string? Profile { get; set; }
}

public sealed class DynamicListFilterConfigDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public string? RangeMode { get; set; }
    public List<string>? Options { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string? DateMin { get; set; }
    public string? DateMax { get; set; }
}

public sealed class DynamicListResponseDto
{
    public List<Dictionary<string, object?>> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;
    public List<DynamicListFilterConfigDto> FilterConfig { get; set; } = [];
    public JsonObject ActiveFilters { get; set; } = [];
    public Dictionary<string, object?> RuntimeHints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
