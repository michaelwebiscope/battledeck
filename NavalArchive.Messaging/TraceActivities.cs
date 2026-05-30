using System.Diagnostics;

namespace NavalArchive.Messaging;

/// <summary>
/// Shared ActivitySource for custom spans. Register via
/// OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES=NavalArchive
/// </summary>
public static class TraceActivities
{
    public const string SourceName = "NavalArchive";

    public static readonly ActivitySource Source = new(SourceName);
}
