using Microsoft.Extensions.DependencyInjection;
using MidiRouter.Core.Config;
using MidiRouter.Core.Engine;
using MidiRouter.Core.Monitoring;
using MidiRouter.Core.Routing;

namespace MidiRouter.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddMidiRouterCore(
        this IServiceCollection services,
        Action<ConfigStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ConfigStoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<RouteMatrix>();
        services.AddSingleton<TrafficAnalyzer>();
        services.AddSingleton<IMidiMessageLog, RingBufferMidiMessageLog>();

#if WINDOWS
        services.AddSingleton<IMidiEndpointCatalog, WinRtMidiEndpointCatalog>();
        services.AddSingleton<IMidiSession, WinRtMidiSession>();
#else
        services.AddSingleton<IMidiEndpointCatalog, InMemoryMidiEndpointCatalog>();
        services.AddSingleton<IMidiSession, InMemoryMidiSession>();
#endif

        services.AddSingleton<MidiRoutingWorker>();
        services.AddHostedService<MidiRuntimeHostedService>();

        return services;
    }
}
