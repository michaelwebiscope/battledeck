using System.Diagnostics;

namespace NavalArchive.Messaging;

/// <summary>Propagates W3C Trace Context (traceparent, tracestate) on outbound HTTP calls.</summary>
public sealed class TracePropagationHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            var flags = activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00";
            request.Headers.TryAddWithoutValidation(
                TracePropagation.TraceParentHeader,
                $"00-{activity.TraceId}-{activity.SpanId}-{flags}");
            if (!string.IsNullOrEmpty(activity.TraceStateString))
                request.Headers.TryAddWithoutValidation(TracePropagation.TraceStateHeader, activity.TraceStateString);
        }

        return await base.SendAsync(request, ct);
    }
}

public static class TracePropagation
{
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    public static ActivityContext? ContextFromHeaders(string? traceParent, string? traceState = null)
    {
        if (string.IsNullOrWhiteSpace(traceParent))
            return null;

        return ActivityContext.TryParse(traceParent, traceState, out var ctx) ? ctx : null;
    }
}
