using Microsoft.Extensions.DependencyInjection;
using MidiRouter.UI.ViewModels;

namespace MidiRouter.UI.DependencyInjection;

public static class UiServiceCollectionExtensions
{
    public static IServiceCollection AddMidiRouterUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<RoutingViewModel>();
        services.AddSingleton<MonitorViewModel>();
        services.AddSingleton<SettingsViewModel>();

        return services;
    }
}
