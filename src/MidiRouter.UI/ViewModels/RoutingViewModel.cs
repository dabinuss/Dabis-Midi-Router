using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Engine;
using MidiRouter.Core.Routing;

namespace MidiRouter.UI.ViewModels;

public partial class RoutingViewModel : ObservableObject
{
    private readonly RouteMatrix _routeMatrix;
    private readonly IMidiEndpointCatalog _endpointCatalog;

    [ObservableProperty]
    private string? _selectedSourceEndpointId;

    [ObservableProperty]
    private string? _selectedTargetEndpointId;

    [ObservableProperty]
    private string _newLoopbackName = "Loopback";

    [ObservableProperty]
    private string? _selectedLoopbackEndpointId;

    [ObservableProperty]
    private Guid? _selectedRouteId;

    [ObservableProperty]
    private bool _selectedRouteEnabled = true;

    [ObservableProperty]
    private string _selectedRouteChannelsText = string.Empty;

    [ObservableProperty]
    private string _selectedRouteMessageTypesText = string.Empty;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public RoutingViewModel(RouteMatrix routeMatrix, IMidiEndpointCatalog endpointCatalog)
    {
        _routeMatrix = routeMatrix;
        _endpointCatalog = endpointCatalog;

        _routeMatrix.RoutesChanged += (_, _) => RunOnUiThread(RefreshRoutes);
        _endpointCatalog.EndpointsChanged += (_, _) => RunOnUiThread(RefreshEndpoints);

        RefreshEndpoints();
        RefreshRoutes();
    }

    public ObservableCollection<MidiEndpointDescriptor> Endpoints { get; } = [];

    public ObservableCollection<MidiEndpointDescriptor> SourceEndpoints { get; } = [];

    public ObservableCollection<MidiEndpointDescriptor> TargetEndpoints { get; } = [];

    public ObservableCollection<MidiEndpointDescriptor> LoopbackEndpoints { get; } = [];

    public ObservableCollection<RouteGridRow> Routes { get; } = [];

    [RelayCommand(CanExecute = nameof(CanAddRoute))]
    private void AddRoute()
    {
        ValidationMessage = string.Empty;

        var route = new RouteDefinition(
            Guid.NewGuid(),
            SelectedSourceEndpointId!,
            SelectedTargetEndpointId!,
            enabled: true,
            filter: RouteFilter.AllowAll);

        _routeMatrix.AddOrUpdateRoute(route);
    }

    [RelayCommand]
    private void RemoveRoute(Guid routeId)
    {
        ValidationMessage = string.Empty;
        _routeMatrix.RemoveRoute(routeId);
    }

    [RelayCommand(CanExecute = nameof(CanApplyRouteSettings))]
    private void ApplyRouteSettings()
    {
        ValidationMessage = string.Empty;

        var currentRoute = _routeMatrix.GetRoutes().FirstOrDefault(route => route.Id == SelectedRouteId);
        if (currentRoute is null)
        {
            return;
        }

        try
        {
            var channels = ParseChannels(SelectedRouteChannelsText);
            var messageTypes = ParseMessageTypes(SelectedRouteMessageTypesText);

            var updatedRoute = new RouteDefinition(
                currentRoute.Id,
                currentRoute.SourceEndpointId,
                currentRoute.TargetEndpointId,
                SelectedRouteEnabled,
                new RouteFilter(channels, messageTypes));

            _routeMatrix.AddOrUpdateRoute(updatedRoute);
        }
        catch (FormatException ex)
        {
            ValidationMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshEndpointsAsync()
    {
        try
        {
            ValidationMessage = string.Empty;
            await _endpointCatalog.RefreshAsync();
            RefreshEndpoints();
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Port-Refresh fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateLoopbackAsync()
    {
        try
        {
            ValidationMessage = string.Empty;
            var endpoint = await _endpointCatalog.CreateLoopbackEndpointAsync(NewLoopbackName);
            await _endpointCatalog.RefreshAsync();

            SelectedLoopbackEndpointId = endpoint.Id;
            SelectedSourceEndpointId = endpoint.Id;
            SelectedTargetEndpointId = endpoint.Id;
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Loopback-Erstellung fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteLoopback))]
    private async Task DeleteLoopbackAsync()
    {
        try
        {
            ValidationMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(SelectedLoopbackEndpointId))
            {
                return;
            }

            var endpointId = SelectedLoopbackEndpointId;
            var removed = await _endpointCatalog.DeleteLoopbackEndpointAsync(endpointId);
            if (!removed)
            {
                ValidationMessage = "Loopback-Port konnte nicht geloescht werden.";
                return;
            }

            var affectedRoutes = _routeMatrix
                .GetRoutes()
                .Where(route =>
                    string.Equals(route.SourceEndpointId, endpointId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(route.TargetEndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                .Select(route => route.Id)
                .ToList();

            foreach (var routeId in affectedRoutes)
            {
                _routeMatrix.RemoveRoute(routeId);
            }

            await _endpointCatalog.RefreshAsync();
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Loopback-Loeschen fehlgeschlagen: {ex.Message}";
        }
    }

    private bool CanAddRoute()
    {
        if (string.IsNullOrWhiteSpace(SelectedSourceEndpointId) || string.IsNullOrWhiteSpace(SelectedTargetEndpointId))
        {
            return false;
        }

        if (string.Equals(SelectedSourceEndpointId, SelectedTargetEndpointId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var source = Endpoints.FirstOrDefault(endpoint =>
            string.Equals(endpoint.Id, SelectedSourceEndpointId, StringComparison.OrdinalIgnoreCase));
        var target = Endpoints.FirstOrDefault(endpoint =>
            string.Equals(endpoint.Id, SelectedTargetEndpointId, StringComparison.OrdinalIgnoreCase));

        return source?.SupportsInput == true && target?.SupportsOutput == true;
    }

    private bool CanDeleteLoopback()
    {
        return !string.IsNullOrWhiteSpace(SelectedLoopbackEndpointId);
    }

    private bool CanApplyRouteSettings()
    {
        return SelectedRouteId.HasValue;
    }

    partial void OnSelectedSourceEndpointIdChanged(string? value)
    {
        AddRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetEndpointIdChanged(string? value)
    {
        AddRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLoopbackEndpointIdChanged(string? value)
    {
        DeleteLoopbackCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRouteIdChanged(Guid? value)
    {
        UpdateSelectedRouteEditorState(value);
        ApplyRouteSettingsCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSelectedRouteEditorState(Guid? routeId)
    {
        var route = _routeMatrix.GetRoutes().FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is null)
        {
            SelectedRouteEnabled = true;
            SelectedRouteChannelsText = string.Empty;
            SelectedRouteMessageTypesText = string.Empty;
            return;
        }

        SelectedRouteEnabled = route.Enabled;
        SelectedRouteChannelsText = route.Filter.Channels.Count == 0
            ? "alle"
            : string.Join(", ", route.Filter.Channels.OrderBy(channel => channel));
        SelectedRouteMessageTypesText = route.Filter.MessageTypes.Count == 0
            ? "alle"
            : string.Join(", ", route.Filter.MessageTypes.OrderBy(type => type.ToString()));
    }

    private void RefreshEndpoints()
    {
        var snapshot = _endpointCatalog.GetEndpoints();

        Endpoints.Clear();
        SourceEndpoints.Clear();
        TargetEndpoints.Clear();
        LoopbackEndpoints.Clear();

        foreach (var endpoint in snapshot)
        {
            Endpoints.Add(endpoint);
            if (endpoint.SupportsInput)
            {
                SourceEndpoints.Add(endpoint);
            }

            if (endpoint.SupportsOutput)
            {
                TargetEndpoints.Add(endpoint);
            }

            if (endpoint.Kind == MidiEndpointKind.Loopback)
            {
                LoopbackEndpoints.Add(endpoint);
            }
        }

        if (SourceEndpoints.Count > 0 && SourceEndpoints.All(endpoint => endpoint.Id != SelectedSourceEndpointId))
        {
            SelectedSourceEndpointId = SourceEndpoints[0].Id;
        }

        if (TargetEndpoints.Count > 0 && TargetEndpoints.All(endpoint => endpoint.Id != SelectedTargetEndpointId))
        {
            var preferredTarget = TargetEndpoints
                .FirstOrDefault(endpoint => !string.Equals(endpoint.Id, SelectedSourceEndpointId, StringComparison.OrdinalIgnoreCase))
                ?? TargetEndpoints[0];
            SelectedTargetEndpointId = preferredTarget.Id;
        }

        if (LoopbackEndpoints.Count == 0)
        {
            SelectedLoopbackEndpointId = null;
        }
        else if (LoopbackEndpoints.All(endpoint => endpoint.Id != SelectedLoopbackEndpointId))
        {
            SelectedLoopbackEndpointId = LoopbackEndpoints[0].Id;
        }

        AddRouteCommand.NotifyCanExecuteChanged();
        DeleteLoopbackCommand.NotifyCanExecuteChanged();
    }

    private void RefreshRoutes()
    {
        var selectedRouteId = SelectedRouteId;
        var routeSnapshot = _routeMatrix.GetRoutes();

        Routes.Clear();
        foreach (var route in routeSnapshot)
        {
            Routes.Add(new RouteGridRow(
                route.Id,
                route.SourceEndpointId,
                ResolveEndpointName(route.SourceEndpointId),
                route.TargetEndpointId,
                ResolveEndpointName(route.TargetEndpointId),
                route.Enabled,
                route.Filter.Channels.Count == 0
                    ? "alle"
                    : string.Join(", ", route.Filter.Channels.OrderBy(channel => channel)),
                route.Filter.MessageTypes.Count == 0
                    ? "alle"
                    : string.Join(", ", route.Filter.MessageTypes.OrderBy(type => type.ToString()))));
        }

        if (selectedRouteId.HasValue && Routes.Any(route => route.Id == selectedRouteId.Value))
        {
            SelectedRouteId = selectedRouteId.Value;
        }
        else
        {
            SelectedRouteId = Routes.FirstOrDefault()?.Id;
        }

        UpdateSelectedRouteEditorState(SelectedRouteId);
    }

    private string ResolveEndpointName(string endpointId)
    {
        return Endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? endpointId;
    }

    private static IReadOnlyList<int> ParseChannels(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAllowAllToken(text))
        {
            return [];
        }

        var channels = new HashSet<int>();
        foreach (var token in Tokenize(text))
        {
            if (!int.TryParse(token, out var channel) || channel is < 1 or > 16)
            {
                throw new FormatException("Kanaele muessen als Zahlen 1-16 angegeben werden.");
            }

            channels.Add(channel);
        }

        return channels.OrderBy(channel => channel).ToList();
    }

    private static IReadOnlyList<MidiMessageType> ParseMessageTypes(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAllowAllToken(text))
        {
            return [];
        }

        var messageTypes = new HashSet<MidiMessageType>();
        foreach (var token in Tokenize(text))
        {
            var messageType = token.ToLowerInvariant() switch
            {
                "noteon" or "note-on" => MidiMessageType.NoteOn,
                "noteoff" or "note-off" => MidiMessageType.NoteOff,
                "cc" or "controlchange" or "control-change" => MidiMessageType.ControlChange,
                "pc" or "programchange" or "program-change" => MidiMessageType.ProgramChange,
                "pitchbend" or "pitch-bend" => MidiMessageType.PitchBend,
                "sysex" or "sys-ex" => MidiMessageType.SysEx,
                "clock" => MidiMessageType.Clock,
                _ => throw new FormatException($"Unbekannter Message-Typ: {token}")
            };

            messageTypes.Add(messageType);
        }

        return messageTypes.OrderBy(type => type.ToString()).ToList();
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        return text.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsAllowAllToken(string text)
    {
        return string.Equals(text.Trim(), "alle", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text.Trim(), "all", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text.Trim(), "*", StringComparison.OrdinalIgnoreCase);
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

    public sealed record RouteGridRow(
        Guid Id,
        string SourceEndpointId,
        string SourceName,
        string TargetEndpointId,
        string TargetName,
        bool Enabled,
        string Channels,
        string MessageTypes);
}
