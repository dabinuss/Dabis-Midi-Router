using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Config;
using MidiRouter.Core.Engine;
using MidiRouter.Core.Monitoring;
using MidiRouter.Core.Routing;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace MidiRouter.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IConfigStore _configStore;
    private readonly RouteMatrix _routeMatrix;
    private readonly IMidiSession _midiSession;
    private readonly IMidiMessageLog _messageLog;
    private readonly DispatcherTimer _autoSaveTimer;
    private bool _suppressAutoSave;
    private bool _hasPendingChanges;
    private bool _isSaving;
    private bool _saveRequestedWhileSaving;

    [ObservableProperty]
    private string _statusText = "Bereit";

    [ObservableProperty]
    private string _activeProfileName = "Default";

    [ObservableProperty]
    private string _saveStateText = "Gespeichert";

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    public MainViewModel(
        RoutingViewModel routing,
        MonitorViewModel monitor,
        SettingsViewModel settings,
        IConfigStore configStore,
        RouteMatrix routeMatrix,
        IMidiSession midiSession,
        IMidiMessageLog messageLog)
    {
        Routing = routing;
        Monitor = monitor;
        Settings = settings;
        _configStore = configStore;
        _routeMatrix = routeMatrix;
        _midiSession = midiSession;
        _messageLog = messageLog;

        _midiSession.StateChanged += OnMidiSessionStateChanged;
        _routeMatrix.RoutesChanged += OnRoutesChanged;
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        _autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _autoSaveTimer.Tick += OnAutoSaveTimerTick;
    }

    public RoutingViewModel Routing { get; }

    public MonitorViewModel Monitor { get; }

    public SettingsViewModel Settings { get; }

    public async Task InitializeAsync()
    {
        try
        {
            await Routing.InitializeAsync();
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
        _autoSaveTimer.Stop();
        _hasPendingChanges = true;
        HasUnsavedChanges = true;
        SaveStateText = "Aenderungen ausstehend";
        await SaveConfigCoreAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ReloadConfig()
    {
        _autoSaveTimer.Stop();
        _suppressAutoSave = true;

        try
        {
            var config = await _configStore.LoadAsync().ConfigureAwait(false);

            RunOnUiThread(() =>
            {
                ActiveProfileName = config.ActiveProfileName;
                Settings.LogBufferSize = config.LogBufferSize;
                Settings.StartMinimized = config.StartMinimized;
                Settings.HostingMode = config.HostingMode;
                Settings.AutoStartHost = config.AutoStartHost;
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
                _hasPendingChanges = false;
                HasUnsavedChanges = false;
                SaveStateText = $"Gespeichert ({DateTime.Now:T})";
                StatusText = $"Konfiguration geladen ({DateTime.Now:T}) | Session: {GetStateText(_midiSession.State)}";
            });
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    public async Task FlushPendingSaveAsync()
    {
        _autoSaveTimer.Stop();

        if (_isSaving)
        {
            _saveRequestedWhileSaving = true;
            while (_isSaving)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        if (_hasPendingChanges || _saveRequestedWhileSaving)
        {
            await SaveConfigCoreAsync().ConfigureAwait(false);
        }
    }

    partial void OnActiveProfileNameChanged(string value)
    {
        MarkDirty();
    }

    private void OnRoutesChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(MarkDirty);
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.LogBufferSize))
        {
            _messageLog.ConfigureCapacity(Settings.LogBufferSize);
        }

        if (e.PropertyName is nameof(SettingsViewModel.LogBufferSize) or
            nameof(SettingsViewModel.StartMinimized) or
            nameof(SettingsViewModel.RunInBackground) or
            nameof(SettingsViewModel.AutoStartHost))
        {
            RunOnUiThread(MarkDirty);
        }
    }

    private async void OnAutoSaveTimerTick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        await SaveConfigCoreAsync().ConfigureAwait(false);
    }

    private void MarkDirty()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _hasPendingChanges = true;
        HasUnsavedChanges = true;
        SaveStateText = "Aenderungen ausstehend";
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async Task SaveConfigCoreAsync()
    {
        if (_suppressAutoSave || !_hasPendingChanges)
        {
            return;
        }

        if (_isSaving)
        {
            _saveRequestedWhileSaving = true;
            return;
        }

        _isSaving = true;
        _saveRequestedWhileSaving = false;

        RunOnUiThread(() => SaveStateText = "Speichert...");

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
            HostingMode = Settings.HostingMode,
            AutoStartHost = Settings.AutoStartHost,
            Profiles =
            [
                new RoutingProfile
                {
                    Name = string.IsNullOrWhiteSpace(ActiveProfileName) ? "Default" : ActiveProfileName.Trim(),
                    Routes = routeConfigs
                }
            ]
        };

        try
        {
            await _configStore.SaveAsync(config).ConfigureAwait(false);
            RunOnUiThread(() =>
            {
                _hasPendingChanges = false;
                HasUnsavedChanges = false;
                SaveStateText = $"Gespeichert ({DateTime.Now:T})";
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                _hasPendingChanges = true;
                HasUnsavedChanges = true;
                SaveStateText = "Aenderungen ausstehend (Speicherfehler)";
                StatusText = $"Speichern fehlgeschlagen: {ex.Message}";
            });
        }
        finally
        {
            _isSaving = false;
        }

        if (_saveRequestedWhileSaving)
        {
            _saveRequestedWhileSaving = false;
            MarkDirty();
        }
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
