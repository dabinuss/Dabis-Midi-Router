using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Engine;
using MidiRouter.Core.Routing;

namespace MidiRouter.UI.ViewModels;

public partial class RoutingViewModel : ObservableObject
{
    private const string PortOwnerAll = "Alle Eigentümer";
    private const string PortOwnerSystem = "System";
    private const string PortOwnerApp = "App";
    private const string PortDirectionAll = "Alle Richtungen";
    private const string PortDirectionInOut = "In/Out";
    private const string PortDirectionInput = "Input";
    private const string PortDirectionOutput = "Output";

    private readonly RouteMatrix _routeMatrix;
    private readonly IMidiEndpointCatalog _endpointCatalog;
    private readonly Dictionary<string, long> _createdPortPriority = new(StringComparer.OrdinalIgnoreCase);
    private long _createdPortCounter;

    [ObservableProperty]
    private string _serviceHealthStatus = "Port-Check ausstehend";

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private string _newPortName = "Neuer Port";

    [ObservableProperty]
    private string _editPortName = string.Empty;

    [ObservableProperty]
    private PortRow? _selectedPort;

    [ObservableProperty]
    private bool _hideSystemPorts;

    [ObservableProperty]
    private string _portSearchText = string.Empty;

    [ObservableProperty]
    private string _selectedPortOwnerFilter = PortOwnerAll;

    [ObservableProperty]
    private string _selectedPortDirectionFilter = PortDirectionAll;

    [ObservableProperty]
    private string? _selectedSourceEndpointId;

    [ObservableProperty]
    private string? _selectedTargetEndpointId;

    [ObservableProperty]
    private string _routeChannelsText = "alle";

    [ObservableProperty]
    private string _routeTypesText = "alle";

    [ObservableProperty]
    private bool _routeEnabled = true;

    [ObservableProperty]
    private Guid? _selectedRouteId;

    public RoutingViewModel(RouteMatrix routeMatrix, IMidiEndpointCatalog endpointCatalog)
    {
        _routeMatrix = routeMatrix;
        _endpointCatalog = endpointCatalog;

        _routeMatrix.RoutesChanged += (_, _) => RunOnUiThread(RefreshRoutes);
        _endpointCatalog.EndpointsChanged += (_, _) => RunOnUiThread(RefreshPorts);

        RefreshPorts();
        RefreshRoutes();
    }

    public ObservableCollection<PortRow> Ports { get; } = [];

    public ObservableCollection<MidiEndpointDescriptor> SourceEndpoints { get; } = [];

    public ObservableCollection<MidiEndpointDescriptor> TargetEndpoints { get; } = [];

    public ObservableCollection<RouteRow> Routes { get; } = [];

    public event EventHandler? EditPortFocusRequested;

    public ObservableCollection<string> PortOwnerFilters { get; } =
    [
        PortOwnerAll,
        PortOwnerSystem,
        PortOwnerApp
    ];

    public ObservableCollection<string> PortDirectionFilters { get; } =
    [
        PortDirectionAll,
        PortDirectionInOut,
        PortDirectionInput,
        PortDirectionOutput
    ];

    public async Task InitializeAsync()
    {
        await RefreshEndpointsAsync();
    }

    [RelayCommand]
    private async Task RefreshEndpointsAsync()
    {
        try
        {
            ValidationMessage = string.Empty;
            await _endpointCatalog.RefreshAsync();
            RefreshPorts();
            ServiceHealthStatus = $"System-Ports geladen: {Ports.Count}";
        }
        catch (Exception ex)
        {
            ServiceHealthStatus = $"Port-Service nicht verfügbar";
            ValidationMessage = $"Port-Refresh fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreatePortAsync()
    {
        try
        {
            ValidationMessage = string.Empty;
            var created = await _endpointCatalog.CreateLoopbackEndpointAsync(NewPortName);
            _createdPortPriority[created.Id] = ++_createdPortCounter;
            RefreshPorts();
            SelectedPort = Ports.FirstOrDefault(port => string.Equals(port.Id, created.Id, StringComparison.OrdinalIgnoreCase));
            EditPortFocusRequested?.Invoke(this, EventArgs.Empty);
            ValidationMessage = $"Port '{created.Name}' erstellt.";
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Port konnte nicht erstellt werden: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedAppPort))]
    private async Task RenamePortAsync()
    {
        if (SelectedPort is null)
        {
            return;
        }

        try
        {
            ValidationMessage = string.Empty;
            var renamed = await _endpointCatalog.RenameLoopbackEndpointAsync(SelectedPort.Id, EditPortName);
            if (!renamed)
            {
                ValidationMessage = "Port konnte nicht umbenannt werden.";
                return;
            }

            RefreshPorts();
            ValidationMessage = "Port wurde umbenannt.";
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Port umbenennen fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedAppPort))]
    private async Task DeletePortAsync()
    {
        if (SelectedPort is null)
        {
            return;
        }

        try
        {
            ValidationMessage = string.Empty;
            var removed = await _endpointCatalog.DeleteLoopbackEndpointAsync(SelectedPort.Id);
            if (!removed)
            {
                ValidationMessage = "Port konnte nicht gelöscht werden.";
                return;
            }

            var routesToRemove = _routeMatrix
                .GetRoutes()
                .Where(route =>
                    string.Equals(route.SourceEndpointId, SelectedPort.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(route.TargetEndpointId, SelectedPort.Id, StringComparison.OrdinalIgnoreCase))
                .Select(route => route.Id)
                .ToList();

            foreach (var routeId in routesToRemove)
            {
                _routeMatrix.RemoveRoute(routeId);
            }

            RefreshPorts();
            ValidationMessage = "Port wurde gelöscht.";
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Port löschen fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveRoute))]
    private void SaveRoute()
    {
        try
        {
            ValidationMessage = string.Empty;

            var route = new RouteDefinition(
                SelectedRouteId ?? Guid.NewGuid(),
                SelectedSourceEndpointId!,
                SelectedTargetEndpointId!,
                RouteEnabled,
                new RouteFilter(ParseChannels(RouteChannelsText), ParseMessageTypes(RouteTypesText)));

            _routeMatrix.AddOrUpdateRoute(route);
            SelectedRouteId = route.Id;
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteRoute))]
    private void DeleteRoute()
    {
        if (!SelectedRouteId.HasValue)
        {
            return;
        }

        _routeMatrix.RemoveRoute(SelectedRouteId.Value);
        NewRoute();
    }

    [RelayCommand]
    private void NewRoute()
    {
        SelectedRouteId = null;
        RouteEnabled = true;
        RouteChannelsText = "alle";
        RouteTypesText = "alle";

        if (SourceEndpoints.Count > 0)
        {
            SelectedSourceEndpointId = SourceEndpoints[0].Id;
        }

        if (TargetEndpoints.Count > 0)
        {
            var preferred = TargetEndpoints.FirstOrDefault(endpoint =>
                !string.Equals(endpoint.Id, SelectedSourceEndpointId, StringComparison.OrdinalIgnoreCase)) ?? TargetEndpoints[0];
            SelectedTargetEndpointId = preferred.Id;
        }

        SaveRouteCommand.NotifyCanExecuteChanged();
        DeleteRouteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectRoute(Guid routeId)
    {
        var route = _routeMatrix.GetRoutes().FirstOrDefault(candidate => candidate.Id == routeId);
        if (route is null)
        {
            return;
        }

        SelectedRouteId = route.Id;
        SelectedSourceEndpointId = route.SourceEndpointId;
        SelectedTargetEndpointId = route.TargetEndpointId;
        RouteEnabled = route.Enabled;
        RouteChannelsText = route.Filter.Channels.Count == 0 ? "alle" : string.Join(", ", route.Filter.Channels.OrderBy(channel => channel));
        RouteTypesText = route.Filter.MessageTypes.Count == 0 ? "alle" : string.Join(", ", route.Filter.MessageTypes.OrderBy(type => type.ToString()));

        SaveRouteCommand.NotifyCanExecuteChanged();
        DeleteRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPortChanged(PortRow? value)
    {
        EditPortName = value?.Name ?? string.Empty;
        RenamePortCommand.NotifyCanExecuteChanged();
        DeletePortCommand.NotifyCanExecuteChanged();
    }

    partial void OnEditPortNameChanged(string value)
    {
        RenamePortCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSourceEndpointIdChanged(string? value)
    {
        SaveRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnHideSystemPortsChanged(bool value)
    {
        RefreshPorts();
    }

    partial void OnPortSearchTextChanged(string value)
    {
        RefreshPorts();
    }

    partial void OnSelectedPortOwnerFilterChanged(string value)
    {
        RefreshPorts();
    }

    partial void OnSelectedPortDirectionFilterChanged(string value)
    {
        RefreshPorts();
    }

    partial void OnSelectedTargetEndpointIdChanged(string? value)
    {
        SaveRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRouteIdChanged(Guid? value)
    {
        if (value.HasValue)
        {
            SelectRoute(value.Value);
        }

        DeleteRouteCommand.NotifyCanExecuteChanged();
    }

    private bool CanEditSelectedAppPort()
    {
        return SelectedPort is { IsUserManaged: true };
    }

    private bool CanSaveRoute()
    {
        return !string.IsNullOrWhiteSpace(SelectedSourceEndpointId) &&
               !string.IsNullOrWhiteSpace(SelectedTargetEndpointId) &&
               !string.Equals(SelectedSourceEndpointId, SelectedTargetEndpointId, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanDeleteRoute()
    {
        return SelectedRouteId.HasValue;
    }

    private void RefreshPorts()
    {
        var endpoints = _endpointCatalog.GetEndpoints().ToList();

        // Keep priority map in sync when ports were removed.
        var knownIds = endpoints
            .Select(endpoint => endpoint.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleId in _createdPortPriority.Keys.Where(id => !knownIds.Contains(id)).ToList())
        {
            _createdPortPriority.Remove(staleId);
        }

        var portSnapshot = endpoints
            .Where(ShouldIncludeInPortList)
            .OrderByDescending(endpoint =>
                _createdPortPriority.TryGetValue(endpoint.Id, out var priority) ? priority : 0)
            .ThenBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var routingSnapshot = endpoints
            .OrderBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selectedPortId = SelectedPort?.Id;

        Ports.Clear();
        SourceEndpoints.Clear();
        TargetEndpoints.Clear();

        foreach (var endpoint in portSnapshot)
        {
            Ports.Add(new PortRow(
                endpoint.Id,
                FormatPortDisplayName(endpoint),
                endpoint.Kind == MidiEndpointKind.Loopback ? "Loopback" : "System",
                GetDirection(endpoint),
                endpoint.IsOnline ? "Online" : "Offline",
                endpoint.IsUserManaged ? "App" : "System",
                endpoint.IsUserManaged));
        }

        foreach (var endpoint in routingSnapshot)
        {
            if (endpoint.SupportsInput)
            {
                SourceEndpoints.Add(endpoint);
            }

            if (endpoint.SupportsOutput)
            {
                TargetEndpoints.Add(endpoint);
            }
        }

        SelectedPort = selectedPortId is null
            ? Ports.FirstOrDefault()
            : Ports.FirstOrDefault(port => string.Equals(port.Id, selectedPortId, StringComparison.OrdinalIgnoreCase))
              ?? Ports.FirstOrDefault();

        if (SelectedSourceEndpointId is null || SourceEndpoints.All(endpoint => endpoint.Id != SelectedSourceEndpointId))
        {
            SelectedSourceEndpointId = SourceEndpoints.FirstOrDefault()?.Id;
        }

        if (SelectedTargetEndpointId is null || TargetEndpoints.All(endpoint => endpoint.Id != SelectedTargetEndpointId))
        {
            var preferred = TargetEndpoints.FirstOrDefault(endpoint =>
                !string.Equals(endpoint.Id, SelectedSourceEndpointId, StringComparison.OrdinalIgnoreCase));
            SelectedTargetEndpointId = preferred?.Id ?? TargetEndpoints.FirstOrDefault()?.Id;
        }

        SaveRouteCommand.NotifyCanExecuteChanged();
    }

    private bool ShouldIncludeInPortList(MidiEndpointDescriptor endpoint)
    {
        if (HideSystemPorts && !endpoint.IsUserManaged)
        {
            return false;
        }

        if (!MatchesOwnerFilter(endpoint) || !MatchesDirectionFilter(endpoint))
        {
            return false;
        }

        return MatchesSearch(endpoint);
    }

    private bool MatchesOwnerFilter(MidiEndpointDescriptor endpoint)
    {
        return SelectedPortOwnerFilter switch
        {
            PortOwnerSystem => !endpoint.IsUserManaged,
            PortOwnerApp => endpoint.IsUserManaged,
            _ => true
        };
    }

    private bool MatchesDirectionFilter(MidiEndpointDescriptor endpoint)
    {
        return SelectedPortDirectionFilter switch
        {
            PortDirectionInOut => endpoint.SupportsInput && endpoint.SupportsOutput,
            PortDirectionInput => endpoint.SupportsInput && !endpoint.SupportsOutput,
            PortDirectionOutput => endpoint.SupportsOutput && !endpoint.SupportsInput,
            _ => true
        };
    }

    private bool MatchesSearch(MidiEndpointDescriptor endpoint)
    {
        if (string.IsNullOrWhiteSpace(PortSearchText))
        {
            return true;
        }

        var search = PortSearchText.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        if (endpoint.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var owner = endpoint.IsUserManaged ? PortOwnerApp : PortOwnerSystem;
        var type = endpoint.Kind == MidiEndpointKind.Loopback ? "Loopback" : "System";
        var direction = GetDirection(endpoint);

        return owner.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               type.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               direction.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshRoutes()
    {
        var endpointLookup = _endpointCatalog.GetEndpoints()
            .ToDictionary(endpoint => endpoint.Id, endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase);

        var selectedRouteId = SelectedRouteId;
        Routes.Clear();

        foreach (var route in _routeMatrix.GetRoutes())
        {
            Routes.Add(new RouteRow(
                route.Id,
                endpointLookup.TryGetValue(route.SourceEndpointId, out var sourceName) ? sourceName : route.SourceEndpointId,
                endpointLookup.TryGetValue(route.TargetEndpointId, out var targetName) ? targetName : route.TargetEndpointId,
                route.Enabled,
                route.Filter.Channels.Count == 0 ? "alle" : string.Join(", ", route.Filter.Channels.OrderBy(channel => channel)),
                route.Filter.MessageTypes.Count == 0 ? "alle" : string.Join(", ", route.Filter.MessageTypes.OrderBy(type => type.ToString()))));
        }

        if (selectedRouteId.HasValue && Routes.Any(route => route.Id == selectedRouteId.Value))
        {
            SelectRoute(selectedRouteId.Value);
        }
    }

    private static string GetDirection(MidiEndpointDescriptor endpoint)
    {
        return endpoint switch
        {
            { SupportsInput: true, SupportsOutput: true } => "In/Out",
            { SupportsInput: true } => "Input",
            { SupportsOutput: true } => "Output",
            _ => "-"
        };
    }

    private static string FormatPortDisplayName(MidiEndpointDescriptor endpoint)
    {
        if (endpoint.IsUserManaged)
        {
            return endpoint.Name;
        }

        return endpoint.Name.StartsWith("[System]", StringComparison.OrdinalIgnoreCase)
            ? endpoint.Name
            : $"[System] {endpoint.Name}";
    }

    private static IReadOnlyCollection<int> ParseChannels(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAllowAll(text))
        {
            return [];
        }

        var channels = new HashSet<int>();
        foreach (var token in SplitTokens(text))
        {
            if (!int.TryParse(token, out var channel) || channel is < 1 or > 16)
            {
                throw new FormatException("Kanaele muessen 1 bis 16 sein.");
            }

            channels.Add(channel);
        }

        return channels;
    }

    private static IReadOnlyCollection<MidiMessageType> ParseMessageTypes(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAllowAll(text))
        {
            return [];
        }

        var types = new HashSet<MidiMessageType>();
        foreach (var token in SplitTokens(text))
        {
            var normalized = token.ToLowerInvariant();
            var parsed = normalized switch
            {
                "noteon" or "note-on" => MidiMessageType.NoteOn,
                "noteoff" or "note-off" => MidiMessageType.NoteOff,
                "cc" or "controlchange" => MidiMessageType.ControlChange,
                "pc" or "programchange" => MidiMessageType.ProgramChange,
                "pitchbend" => MidiMessageType.PitchBend,
                "sysex" => MidiMessageType.SysEx,
                "clock" => MidiMessageType.Clock,
                _ => throw new FormatException($"Unbekannter Typ: {token}")
            };

            types.Add(parsed);
        }

        return types;
    }

    private static IEnumerable<string> SplitTokens(string text)
    {
        return text.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsAllowAll(string text)
    {
        var value = text.Trim();
        return string.Equals(value, "alle", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) ||
               value == "*";
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

    public sealed record PortRow(
        string Id,
        string Name,
        string Type,
        string Direction,
        string Status,
        string Owner,
        bool IsUserManaged);

    public sealed record RouteRow(
        Guid Id,
        string SourceName,
        string TargetName,
        bool Enabled,
        string Channels,
        string MessageTypes);
}
