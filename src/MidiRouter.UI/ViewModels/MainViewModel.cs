using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Config;
using MidiRouter.Core.Routing;

namespace MidiRouter.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IConfigStore _configStore;
    private readonly RouteMatrix _routeMatrix;

    [ObservableProperty]
    private string _statusText = "Bereit";

    [ObservableProperty]
    private string _activeProfileName = "Default";

    public MainViewModel(
        RoutingViewModel routing,
        MonitorViewModel monitor,
        SettingsViewModel settings,
        IConfigStore configStore,
        RouteMatrix routeMatrix)
    {
        Routing = routing;
        Monitor = monitor;
        Settings = settings;
        _configStore = configStore;
        _routeMatrix = routeMatrix;
    }

    public RoutingViewModel Routing { get; }

    public MonitorViewModel Monitor { get; }

    public SettingsViewModel Settings { get; }

    public async Task InitializeAsync()
    {
        await ReloadConfig();
    }

    [RelayCommand]
    private async Task SaveConfig()
    {
        var routeConfigs = _routeMatrix
            .GetRoutes()
            .Select(route => new RouteConfig
            {
                Id = route.Id,
                SourceEndpointId = route.SourceEndpointId,
                TargetEndpointId = route.TargetEndpointId,
                Enabled = route.Enabled,
                Channels = route.Filter.Channels.ToList(),
                MessageTypes = route.Filter.MessageTypes.ToList()
            })
            .ToList();

        var config = new AppConfig
        {
            ActiveProfileName = ActiveProfileName,
            LogBufferSize = Settings.LogBufferSize,
            StartMinimized = Settings.StartMinimized,
            Profiles =
            [
                new RoutingProfile
                {
                    Name = ActiveProfileName,
                    Routes = routeConfigs
                }
            ]
        };

        await _configStore.SaveAsync(config);
        StatusText = $"Konfiguration gespeichert ({DateTime.Now:T})";
    }

    [RelayCommand]
    private async Task ReloadConfig()
    {
        var config = await _configStore.LoadAsync();

        ActiveProfileName = config.ActiveProfileName;
        Settings.LogBufferSize = config.LogBufferSize;
        Settings.StartMinimized = config.StartMinimized;

        var profile = config.Profiles.FirstOrDefault(x => x.Name == config.ActiveProfileName)
            ?? config.Profiles.FirstOrDefault()
            ?? new RoutingProfile();

        var routes = profile.Routes.Select(route =>
            new RouteDefinition(
                route.Id,
                route.SourceEndpointId,
                route.TargetEndpointId,
                route.Enabled,
                new RouteFilter(route.Channels, route.MessageTypes)));

        _routeMatrix.ReplaceRoutes(routes);
        StatusText = $"Konfiguration geladen ({DateTime.Now:T})";
    }
}
