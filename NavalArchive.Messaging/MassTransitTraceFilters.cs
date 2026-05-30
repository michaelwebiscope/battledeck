using System.Diagnostics;
using MassTransit;

namespace NavalArchive.Messaging;

/// <summary>Injects W3C trace context into MassTransit message headers on publish.</summary>
public sealed class TracePublishFilter<T> : IFilter<PublishContext<T>>
    where T : class
{
    public void Probe(ProbeContext context) => context.CreateFilterScope("tracePublish");

    public Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            var flags = activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00";
            context.Headers.Set(
                TracePropagation.TraceParentHeader,
                $"00-{activity.TraceId}-{activity.SpanId}-{flags}");
            if (!string.IsNullOrEmpty(activity.TraceStateString))
                context.Headers.Set(TracePropagation.TraceStateHeader, activity.TraceStateString);
        }

        return next.Send(context);
    }
}

/// <summary>Restores W3C trace context from MassTransit message headers on consume.</summary>
public sealed class TraceConsumeFilter<T> : IFilter<ConsumeContext<T>>
    where T : class
{
    public void Probe(ProbeContext context) => context.CreateFilterScope("traceConsume");

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        context.Headers.TryGetHeader(TracePropagation.TraceParentHeader, out var traceParent);
        context.Headers.TryGetHeader(TracePropagation.TraceStateHeader, out var traceState);

        var parent = TracePropagation.ContextFromHeaders(traceParent?.ToString(), traceState?.ToString());
        using var activity = parent is { } ctx
            ? TraceActivities.Source.StartActivity("MassTransit.Consume", ActivityKind.Consumer, ctx)
            : TraceActivities.Source.StartActivity("MassTransit.Consume", ActivityKind.Consumer);

        await next.Send(context);
    }
}
