using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Engine;
using MidiRouter.Core.Routing;

namespace MidiRouter.UI.ViewModels;

public partial class RoutingViewModel : ObservableObject
{
    private const string PortOwnerAll = "Alle Eigentumer";
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

    [ObservableProperty]
    private bool _showOnlySelectedPortRoutes;

    [ObservableProperty]
    private string? _selectedRoutePortId;

    [ObservableProperty]
    private string _selectedRouteSummary = "Keine Route ausgewahlt.";

    public RoutingViewModel(RouteMatrix routeMatrix, IMidiEndpointCatalog endpointCatalog)
    {
        _routeMatrix = routeMatrix;
        _endpointCatalog = endpointCatalog;

        _routeMatrix.RoutesChanged += (_, _) => RunOnUiThread(RefreshRoutes);
        _endpointCatalog.EndpointsChanged += (_, _) => RunOnUiThread(() =>
        {
            RefreshPorts();
            RefreshRoutes();
        });

        RefreshPorts();
        RefreshRoutes();
    }

    public ObservableCollection<PortRow> Ports { get; } = [];

    public ObservableCollection<RouteEndpointRow> LeftRoutePorts { get; } = [];

    public ObservableCollection<RouteEndpointRow> RightRoutePorts { get; } = [];

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
            RefreshRoutes();
            ServiceHealthStatus = $"Ports geladen: L {LeftRoutePorts.Count} / R {RightRoutePorts.Count}";
        }
        catch (Exception ex)
        {
            ServiceHealthStatus = "Port-Service nicht verfugbar";
            ValidationMessage = $"Port-Refresh fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreatePortAsync()
    {
        try
        {
            ValidationMessage = string.Empty;
            var knownManagedIds = _endpointCatalog
                .GetEndpoints()
                .Where(endpoint => endpoint.IsUserManaged)
                .Select(endpoint => endpoint.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var created = await _endpointCatalog.CreateLoopbackEndpointAsync(NewPortName);

            var newlyCreatedManagedEndpoints = _endpointCatalog
                .GetEndpoints()
                .Where(endpoint => endpoint.IsUserManaged && !knownManagedIds.Contains(endpoint.Id))
                .OrderBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (newlyCreatedManagedEndpoints.Count == 0)
            {
                _createdPortPriority[created.Id] = ++_createdPortCounter;
            }
            else
            {
                foreach (var endpoint in newlyCreatedManagedEndpoints)
                {
                    _createdPortPriority[endpoint.Id] = ++_createdPortCounter;
                }
            }

            RefreshPorts();

            var selectedPortId = Ports.Any(port => string.Equals(port.Id, created.Id, StringComparison.OrdinalIgnoreCase))
                ? created.Id
                : newlyCreatedManagedEndpoints
                    .Select(endpoint => endpoint.Id)
                    .FirstOrDefault(id => Ports.Any(port => string.Equals(port.Id, id, StringComparison.OrdinalIgnoreCase)));

            SelectedPort = selectedPortId is null
                ? Ports.FirstOrDefault()
                : Ports.FirstOrDefault(port => string.Equals(port.Id, selectedPortId, StringComparison.OrdinalIgnoreCase));

            if (created.SupportsInput)
            {
                SelectedSourceEndpointId = selectedPortId ?? created.Id;
            }

            if (created.SupportsOutput)
            {
                SelectedTargetEndpointId = selectedPortId ?? created.Id;
            }

            SelectRoutePort(selectedPortId ?? created.Id);
            EditPortFocusRequested?.Invoke(this, EventArgs.Empty);
            ValidationMessage = $"Interner Port '{created.Name}' erstellt.";
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
            RefreshRoutes();
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
                ValidationMessage = "Port konnte nicht geloscht werden.";
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
            RefreshRoutes();
            ValidationMessage = "Port wurde geloscht.";
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Port loschen fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveRoute))]
    private void SaveRoute()
    {
        if (string.IsNullOrWhiteSpace(SelectedSourceEndpointId) || string.IsNullOrWhiteSpace(SelectedTargetEndpointId))
        {
            ValidationMessage = "Quelle und Ziel mussen ausgewahlt sein.";
            return;
        }

        try
        {
            ValidationMessage = string.Empty;
            var routeId = SelectedRouteId ?? Guid.NewGuid();

            if (!ValidateRouteEndpoints(SelectedSourceEndpointId, SelectedTargetEndpointId, routeId, out var validationError))
            {
                ValidationMessage = validationError;
                return;
            }

            var route = new RouteDefinition(
                routeId,
                SelectedSourceEndpointId,
                SelectedTargetEndpointId,
                RouteEnabled,
                new RouteFilter(ParseChannels(RouteChannelsText), ParseMessageTypes(RouteTypesText)));

            _routeMatrix.AddOrUpdateRoute(route);
            SelectedRouteId = route.Id;
            SelectedRouteSummary = BuildRouteSummary(route.SourceEndpointId, route.TargetEndpointId, route.Enabled);
            ValidationMessage = "Route gespeichert.";
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

        var removed = _routeMatrix.RemoveRoute(SelectedRouteId.Value);
        if (!removed)
        {
            ValidationMessage = "Route konnte nicht entfernt werden.";
            return;
        }

        var nextRoute = _routeMatrix.GetRoutes().FirstOrDefault();
        if (nextRoute is null)
        {
            NewRoute();
        }
        else
        {
            SelectedRouteId = nextRoute.Id;
            SelectRoute(nextRoute.Id);
        }

        ValidationMessage = "Route entfernt.";
    }

    [RelayCommand]
    private void NewRoute()
    {
        SelectedRouteId = null;
        RouteEnabled = true;
        RouteChannelsText = "alle";
        RouteTypesText = "alle";
        SelectedRouteSummary = "Neue Route: ziehe einen Connector oder wahle Quelle/Ziel.";

        if (LeftRoutePorts.Count > 0)
        {
            SelectedSourceEndpointId = LeftRoutePorts[0].Id;
        }

        if (RightRoutePorts.Count > 0)
        {
            var preferred = RightRoutePorts.FirstOrDefault(endpoint =>
                !string.Equals(endpoint.Id, SelectedSourceEndpointId, StringComparison.OrdinalIgnoreCase)) ?? RightRoutePorts[0];
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
        SelectedRouteSummary = BuildRouteSummary(route.SourceEndpointId, route.TargetEndpointId, route.Enabled);

        SelectRoutePort(route.SourceEndpointId);

        SaveRouteCommand.NotifyCanExecuteChanged();
        DeleteRouteCommand.NotifyCanExecuteChanged();
    }

    public void SelectRouteFromCanvas(Guid routeId)
    {
        SelectRoute(routeId);
    }

    public bool DeleteSelectedRouteFromCanvas()
    {
        if (!CanDeleteRoute())
        {
            return false;
        }

        DeleteRoute();
        return true;
    }

    public bool TryDeleteRouteForPort(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            ValidationMessage = "Kein Port ausgewahlt.";
            return false;
        }

        if (SelectedRouteId.HasValue)
        {
            var selectedRoute = _routeMatrix.GetRoutes().FirstOrDefault(route => route.Id == SelectedRouteId.Value);
            if (selectedRoute is not null &&
                (string.Equals(selectedRoute.SourceEndpointId, endpointId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(selectedRoute.TargetEndpointId, endpointId, StringComparison.OrdinalIgnoreCase)))
            {
                DeleteRoute();
                return true;
            }
        }

        var matchingRoutes = _routeMatrix.GetRoutes()
            .Where(route =>
                string.Equals(route.SourceEndpointId, endpointId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(route.TargetEndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingRoutes.Count == 0)
        {
            ValidationMessage = "Keine Verbindung fur diesen Port gefunden.";
            return false;
        }

        if (matchingRoutes.Count > 1)
        {
            var suggested = matchingRoutes.FirstOrDefault(route => route.Enabled) ?? matchingRoutes[0];
            SelectRoute(suggested.Id);
            ValidationMessage = "Mehrere Verbindungen vorhanden. Bitte zuerst die gewunschte Linie anklicken und dann erneut loschen.";
            return false;
        }

        SelectRoute(matchingRoutes[0].Id);
        DeleteRoute();
        return true;
    }

    public void SelectRoutePort(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return;
        }

        SelectedRoutePortId = endpointId;
    }

    public bool CanCreateRoute(string sourceEndpointId, string targetEndpointId, out string error)
    {
        return ValidateRouteEndpoints(sourceEndpointId, targetEndpointId, ignoreRouteId: null, out error);
    }

    public bool TryCreateRouteByDrag(string sourceEndpointId, string targetEndpointId)
    {
        if (!ValidateRouteEndpoints(sourceEndpointId, targetEndpointId, ignoreRouteId: null, out var error))
        {
            ValidationMessage = error;
            return false;
        }

        var route = new RouteDefinition(
            Guid.NewGuid(),
            sourceEndpointId,
            targetEndpointId,
            enabled: true,
            filter: RouteFilter.AllowAll);

        _routeMatrix.AddOrUpdateRoute(route);
        SelectedRouteId = route.Id;
        SelectRoute(route.Id);
        ValidationMessage = $"Route erstellt: {ResolveEndpointName(sourceEndpointId)} -> {ResolveEndpointName(targetEndpointId)}";
        return true;
    }

    public IReadOnlyList<RouteRow> GetRenderableRoutes()
    {
        if (!ShowOnlySelectedPortRoutes || string.IsNullOrWhiteSpace(SelectedRoutePortId))
        {
            return Routes.ToList();
        }

        return Routes
            .Where(route =>
                string.Equals(route.SourceEndpointId, SelectedRoutePortId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(route.TargetEndpointId, SelectedRoutePortId, StringComparison.OrdinalIgnoreCase))
            .ToList();
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

    partial void OnHideSystemPortsChanged(bool value)
    {
        RefreshPorts();
        RefreshRoutes();
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

    partial void OnSelectedSourceEndpointIdChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            SyncSelectedPortFromEndpointId(value);
            SelectRoutePort(value);
        }

        SaveRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetEndpointIdChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            SyncSelectedPortFromEndpointId(value);
            SelectRoutePort(value);
        }

        SaveRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRouteIdChanged(Guid? value)
    {
        if (value.HasValue)
        {
            SelectRoute(value.Value);
        }
        else if (!CanDeleteRoute())
        {
            SelectedRouteSummary = "Keine Route ausgewahlt.";
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
        var allEndpoints = _endpointCatalog.GetEndpoints().ToList();

        var knownIds = allEndpoints
            .Select(endpoint => endpoint.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleId in _createdPortPriority.Keys.Where(id => !knownIds.Contains(id)).ToList())
        {
            _createdPortPriority.Remove(staleId);
        }

        var filteredEndpoints = allEndpoints
            .Where(ShouldIncludeEndpointInUi)
            .OrderByDescending(endpoint =>
                _createdPortPriority.TryGetValue(endpoint.Id, out var priority) ? priority : 0)
            .ThenBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selectedPortId = SelectedPort?.Id;

        Ports.Clear();
        LeftRoutePorts.Clear();
        RightRoutePorts.Clear();

        foreach (var endpoint in filteredEndpoints)
        {
            var displayName = FormatPortDisplayName(endpoint);
            var direction = GetDirection(endpoint);
            var owner = endpoint.IsUserManaged ? PortOwnerApp : PortOwnerSystem;
            var status = endpoint.IsOnline ? "Online" : "Offline";

            var port = new PortRow(
                endpoint.Id,
                displayName,
                endpoint.Kind == MidiEndpointKind.Loopback ? "Loopback" : "System",
                direction,
                status,
                owner,
                endpoint.IsUserManaged);

            Ports.Add(port);

            if (endpoint.SupportsInput)
            {
                LeftRoutePorts.Add(new RouteEndpointRow(
                    endpoint.Id,
                    displayName,
                    direction,
                    owner,
                    status,
                    BuildEndpointMeta(endpoint)));
            }

            if (endpoint.SupportsOutput)
            {
                RightRoutePorts.Add(new RouteEndpointRow(
                    endpoint.Id,
                    displayName,
                    direction,
                    owner,
                    status,
                    BuildEndpointMeta(endpoint)));
            }
        }

        SelectedPort = selectedPortId is null
            ? Ports.FirstOrDefault()
            : Ports.FirstOrDefault(port => string.Equals(port.Id, selectedPortId, StringComparison.OrdinalIgnoreCase))
              ?? Ports.FirstOrDefault();

        if (SelectedSourceEndpointId is null || LeftRoutePorts.All(endpoint => endpoint.Id != SelectedSourceEndpointId))
        {
            SelectedSourceEndpointId = LeftRoutePorts.FirstOrDefault()?.Id;
        }

        if (SelectedTargetEndpointId is null || RightRoutePorts.All(endpoint => endpoint.Id != SelectedTargetEndpointId))
        {
            var preferred = RightRoutePorts.FirstOrDefault(endpoint =>
                !string.Equals(endpoint.Id, SelectedSourceEndpointId, StringComparison.OrdinalIgnoreCase));
            SelectedTargetEndpointId = preferred?.Id ?? RightRoutePorts.FirstOrDefault()?.Id;
        }

        if (SelectedRoutePortId is null ||
            (LeftRoutePorts.All(endpoint => endpoint.Id != SelectedRoutePortId) &&
             RightRoutePorts.All(endpoint => endpoint.Id != SelectedRoutePortId)))
        {
            SelectedRoutePortId = SelectedSourceEndpointId ?? SelectedTargetEndpointId;
        }

        SaveRouteCommand.NotifyCanExecuteChanged();
    }

    private bool ShouldIncludeEndpointInUi(MidiEndpointDescriptor endpoint)
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
            var sourceName = endpointLookup.TryGetValue(route.SourceEndpointId, out var resolvedSourceName)
                ? resolvedSourceName
                : route.SourceEndpointId;

            var targetName = endpointLookup.TryGetValue(route.TargetEndpointId, out var resolvedTargetName)
                ? resolvedTargetName
                : route.TargetEndpointId;

            Routes.Add(new RouteRow(
                route.Id,
                route.SourceEndpointId,
                sourceName,
                route.TargetEndpointId,
                targetName,
                route.Enabled,
                route.Filter.Channels.Count == 0 ? "alle" : string.Join(", ", route.Filter.Channels.OrderBy(channel => channel)),
                route.Filter.MessageTypes.Count == 0 ? "alle" : string.Join(", ", route.Filter.MessageTypes.OrderBy(type => type.ToString()))));
        }

        if (selectedRouteId.HasValue && Routes.Any(route => route.Id == selectedRouteId.Value))
        {
            SelectRoute(selectedRouteId.Value);
        }
        else if (Routes.Count == 0)
        {
            SelectedRouteId = null;
            SelectedRouteSummary = "Keine Route ausgewahlt.";
        }

        DeleteRouteCommand.NotifyCanExecuteChanged();
    }

    private bool ValidateRouteEndpoints(string sourceEndpointId, string targetEndpointId, Guid? ignoreRouteId, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceEndpointId) || string.IsNullOrWhiteSpace(targetEndpointId))
        {
            error = "Quelle und Ziel mussen gesetzt sein.";
            return false;
        }

        if (string.Equals(sourceEndpointId, targetEndpointId, StringComparison.OrdinalIgnoreCase))
        {
            error = "Quelle und Ziel durfen nicht identisch sein.";
            return false;
        }

        var endpoints = _endpointCatalog.GetEndpoints();
        var source = endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Id, sourceEndpointId, StringComparison.OrdinalIgnoreCase));
        var target = endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.Id, targetEndpointId, StringComparison.OrdinalIgnoreCase));

        if (source is null || !source.SupportsInput)
        {
            error = "Die gewahlte Quelle unterstutzt keinen Input.";
            return false;
        }

        if (target is null || !target.SupportsOutput)
        {
            error = "Das gewahlte Ziel unterstutzt keinen Output.";
            return false;
        }

        var duplicate = _routeMatrix
            .GetRoutes()
            .FirstOrDefault(route =>
                route.Id != ignoreRouteId &&
                string.Equals(route.SourceEndpointId, sourceEndpointId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(route.TargetEndpointId, targetEndpointId, StringComparison.OrdinalIgnoreCase));

        if (duplicate is not null)
        {
            SelectedRouteId = duplicate.Id;
            error = "Die Route existiert bereits.";
            return false;
        }

        return true;
    }

    private void SyncSelectedPortFromEndpointId(string endpointId)
    {
        var selected = Ports.FirstOrDefault(port => string.Equals(port.Id, endpointId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            SelectedPort = selected;
        }
    }

    private string BuildRouteSummary(string sourceEndpointId, string targetEndpointId, bool enabled)
    {
        var sourceName = ResolveEndpointName(sourceEndpointId);
        var targetName = ResolveEndpointName(targetEndpointId);
        var status = enabled ? "Aktiv" : "Inaktiv";
        return $"{sourceName} -> {targetName} ({status})";
    }

    private string ResolveEndpointName(string endpointId)
    {
        return _endpointCatalog.GetEndpoints()
            .FirstOrDefault(endpoint => string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? endpointId;
    }

    private static string BuildEndpointMeta(MidiEndpointDescriptor endpoint)
    {
        var type = endpoint.Kind == MidiEndpointKind.Loopback ? "Loopback" : "System";
        var status = endpoint.IsOnline ? "Online" : "Offline";
        return $"{type} | {status}";
    }

    private static string GetDirection(MidiEndpointDescriptor endpoint)
    {
        return endpoint switch
        {
            { SupportsInput: true, SupportsOutput: true } => PortDirectionInOut,
            { SupportsInput: true } => PortDirectionInput,
            { SupportsOutput: true } => PortDirectionOutput,
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
                throw new FormatException("Kanaele mussen zwischen 1 und 16 liegen.");
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

    public sealed record RouteEndpointRow(
        string Id,
        string Name,
        string Direction,
        string Owner,
        string Status,
        string Meta);

    public sealed record RouteRow(
        Guid Id,
        string SourceEndpointId,
        string SourceName,
        string TargetEndpointId,
        string TargetName,
        bool Enabled,
        string Channels,
        string MessageTypes);
}
