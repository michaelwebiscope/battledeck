using Microsoft.Extensions.DependencyInjection;

namespace NavalArchive.Messaging;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers default HttpClient with W3C trace header propagation for the trace chain.</summary>
    public static IServiceCollection AddTraceChainHttpClient(this IServiceCollection services)
    {
        services.AddTransient<TracePropagationHandler>();
        services.AddHttpClient(string.Empty).AddHttpMessageHandler<TracePropagationHandler>();
        return services;
    }
}
