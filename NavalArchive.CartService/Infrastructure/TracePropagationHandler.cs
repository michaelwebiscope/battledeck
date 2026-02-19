using System.Diagnostics;

namespace NavalArchive.CartService.Infrastructure;

public class TracePropagationHandler : DelegatingHandler
{
    private const string TraceParent = "traceparent";
    private const string TraceState = "tracestate";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            var traceId = activity.TraceId.ToString();
            var spanId = activity.SpanId.ToString();
            var flags = activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00";
            request.Headers.TryAddWithoutValidation(TraceParent, $"00-{traceId}-{spanId}-{flags}");
            if (!string.IsNullOrEmpty(activity.TraceStateString))
                request.Headers.TryAddWithoutValidation(TraceState, activity.TraceStateString);
        }
        return await base.SendAsync(request, ct);
    }
}
