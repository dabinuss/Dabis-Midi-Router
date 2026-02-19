using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Config;
using MidiRouter.Core.Engine;
using MidiRouter.Core.Monitoring;
using MidiRouter.Core.Routing;
using System.Windows;

namespace MidiRouter.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IConfigStore _configStore;
    private readonly RouteMatrix _routeMatrix;
    private readonly IMidiEndpointCatalog _endpointCatalog;
    private readonly IMidiSession _midiSession;
    private readonly IMidiMessageLog _messageLog;

    [ObservableProperty]
    private string _statusText = "Bereit";

    [ObservableProperty]
    private string _activeProfileName = "Default";

    public MainViewModel(
        RoutingViewModel routing,
        MonitorViewModel monitor,
        SettingsViewModel settings,
        IConfigStore configStore,
        RouteMatrix routeMatrix,
        IMidiEndpointCatalog endpointCatalog,
        IMidiSession midiSession,
        IMidiMessageLog messageLog)
    {
        Routing = routing;
        Monitor = monitor;
        Settings = settings;
        _configStore = configStore;
        _routeMatrix = routeMatrix;
        _endpointCatalog = endpointCatalog;
        _midiSession = midiSession;
        _messageLog = messageLog;

        _midiSession.StateChanged += OnMidiSessionStateChanged;
    }

    public RoutingViewModel Routing { get; }

    public MonitorViewModel Monitor { get; }

    public SettingsViewModel Settings { get; }

    public async Task InitializeAsync()
    {
        try
        {
            await _endpointCatalog.RefreshAsync();
            await ReloadConfig();
            StatusText = $"MIDI-Session: {GetStateText(_midiSession.State)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Initialisierung fehlgeschlagen: {ex.Message}";
        }
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
            ActiveProfileName = string.IsNullOrWhiteSpace(ActiveProfileName) ? "Default" : ActiveProfileName.Trim(),
            LogBufferSize = Math.Clamp(Settings.LogBufferSize, 1, 200_000),
            StartMinimized = Settings.StartMinimized,
            Profiles =
            [
                new RoutingProfile
                {
                    Name = string.IsNullOrWhiteSpace(ActiveProfileName) ? "Default" : ActiveProfileName.Trim(),
                    Routes = routeConfigs
                }
            ]
        };

        await _configStore.SaveAsync(config);
        StatusText = $"Konfiguration gespeichert ({DateTime.Now:T}) | Session: {GetStateText(_midiSession.State)}";
    }

    [RelayCommand]
    private async Task ReloadConfig()
    {
        var config = await _configStore.LoadAsync();

        ActiveProfileName = config.ActiveProfileName;
        Settings.LogBufferSize = config.LogBufferSize;
        Settings.StartMinimized = config.StartMinimized;
        _messageLog.ConfigureCapacity(Settings.LogBufferSize);

        var profile = config.Profiles.FirstOrDefault(x => x.Name == config.ActiveProfileName)
            ?? config.Profiles.FirstOrDefault()
            ?? new RoutingProfile();

        var routes = profile.Routes
            .Where(route => !string.IsNullOrWhiteSpace(route.SourceEndpointId) && !string.IsNullOrWhiteSpace(route.TargetEndpointId))
            .Select(route =>
                new RouteDefinition(
                    route.Id,
                    route.SourceEndpointId,
                    route.TargetEndpointId,
                    route.Enabled,
                    new RouteFilter(route.Channels, route.MessageTypes)));

        _routeMatrix.ReplaceRoutes(routes);
        StatusText = $"Konfiguration geladen ({DateTime.Now:T}) | Session: {GetStateText(_midiSession.State)}";
    }

    private void OnMidiSessionStateChanged(object? sender, MidiSessionStateChangedEventArgs args)
    {
        RunOnUiThread(() =>
        {
            StatusText = string.IsNullOrWhiteSpace(args.Detail)
                ? $"MIDI-Session: {GetStateText(args.State)}"
                : $"MIDI-Session: {GetStateText(args.State)} ({args.Detail})";
        });
    }

    private static string GetStateText(MidiSessionState state)
    {
        return state switch
        {
            MidiSessionState.Stopped => "Gestoppt",
            MidiSessionState.Starting => "Startet",
            MidiSessionState.Running => "Aktiv",
            MidiSessionState.Faulted => "Fehler",
            _ => "Unbekannt"
        };
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }
}
