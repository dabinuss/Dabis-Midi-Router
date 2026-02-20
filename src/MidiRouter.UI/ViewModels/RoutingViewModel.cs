using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MidiRouter.Core.Engine;
using MidiRouter.Core.Monitoring;
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
    private readonly TrafficAnalyzer _trafficAnalyzer;
    private readonly DispatcherTimer _trafficRefreshTimer;
    private readonly DispatcherTimer _portRefreshTimer;
    private readonly Dictionary<string, long> _createdPortPriority = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _portListPrimaryByEndpointId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _portListMemberIdsByPrimaryId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _leftRoutePrimaryByEndpointId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rightRoutePrimaryByEndpointId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _leftRouteMemberIdsByPrimaryId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _rightRouteMemberIdsByPrimaryId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _latestBytesPerSecondByEndpointId = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MidiEndpointDescriptor> _cachedEndpoints = [];
    private bool _refreshRoutesAfterPortRefresh;
    private long _createdPortCounter;
    private bool _isSynchronizingPortSelection;

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
    private PortRow? _selectedAppPort;

    [ObservableProperty]
    private PortRow? _selectedSystemPort;

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

    public RoutingViewModel(RouteMatrix routeMatrix, IMidiEndpointCatalog endpointCatalog, TrafficAnalyzer trafficAnalyzer)
    {
        _routeMatrix = routeMatrix;
        _endpointCatalog = endpointCatalog;
        _trafficAnalyzer = trafficAnalyzer;

        _routeMatrix.RoutesChanged += (_, _) => RunOnUiThread(RefreshRoutes);
        _endpointCatalog.EndpointsChanged += (_, _) => RunOnUiThread(HandleEndpointsChanged);

        _trafficRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _trafficRefreshTimer.Tick += (_, _) => PollTrafficRatesAndRefreshColumns();
        _trafficRefreshTimer.Start();

        _portRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _portRefreshTimer.Tick += OnPortRefreshTimerTick;

        UpdateEndpointSnapshot();
        RefreshPorts();
        RefreshRoutes();
    }

    public ObservableCollection<PortRow> Ports { get; } = [];

    public ObservableCollection<PortRow> AppPorts { get; } = [];

    public ObservableCollection<PortRow> SystemPorts { get; } = [];

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
            UpdateEndpointSnapshot();
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
            var knownManagedIds = GetCachedEndpoints()
                .Where(endpoint => endpoint.IsUserManaged)
                .Select(endpoint => endpoint.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var created = await _endpointCatalog.CreateLoopbackEndpointAsync(NewPortName);
            UpdateEndpointSnapshot();

            var newlyCreatedManagedEndpoints = GetCachedEndpoints()
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

            var candidateEndpointIds = newlyCreatedManagedEndpoints
                .Select(endpoint => endpoint.Id)
                .Prepend(created.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selectedPortId = candidateEndpointIds
                .Select(endpointId =>
                    _portListPrimaryByEndpointId.TryGetValue(endpointId, out var primaryId)
                        ? primaryId
                        : endpointId)
                .FirstOrDefault(primaryId => Ports.Any(port => string.Equals(port.Id, primaryId, StringComparison.OrdinalIgnoreCase)));

            SelectedPort = selectedPortId is null
                ? Ports.FirstOrDefault()
                : Ports.FirstOrDefault(port => string.Equals(port.Id, selectedPortId, StringComparison.OrdinalIgnoreCase));

            var selectedSourceEndpointId = candidateEndpointIds
                .Select(MapRouteEndpointIdForSource)
                .FirstOrDefault(endpointId =>
                    LeftRoutePorts.Any(port => string.Equals(port.Id, endpointId, StringComparison.OrdinalIgnoreCase)));

            var selectedTargetEndpointId = candidateEndpointIds
                .Select(MapRouteEndpointIdForTarget)
                .FirstOrDefault(endpointId =>
                    RightRoutePorts.Any(port => string.Equals(port.Id, endpointId, StringComparison.OrdinalIgnoreCase)));

            if (created.SupportsInput)
            {
                SelectedSourceEndpointId = selectedSourceEndpointId ?? LeftRoutePorts.FirstOrDefault()?.Id;
            }

            if (created.SupportsOutput)
            {
                SelectedTargetEndpointId = selectedTargetEndpointId ?? RightRoutePorts.FirstOrDefault()?.Id;
            }

            var selectedRouteEndpointId = selectedSourceEndpointId ??
                                          selectedTargetEndpointId ??
                                          candidateEndpointIds
                                              .Select(NormalizeRoutePortSelectionId)
                                              .FirstOrDefault(endpointId =>
                                                  LeftRoutePorts.Any(port => string.Equals(port.Id, endpointId, StringComparison.OrdinalIgnoreCase)) ||
                                                  RightRoutePorts.Any(port => string.Equals(port.Id, endpointId, StringComparison.OrdinalIgnoreCase))) ??
                                          NormalizeRoutePortSelectionId(created.Id);

            SelectRoutePort(selectedRouteEndpointId);
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

            UpdateEndpointSnapshot();
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

            var routeEndpointIds = _portListMemberIdsByPrimaryId.TryGetValue(SelectedPort.Id, out var memberIds)
                ? memberIds
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SelectedPort.Id };

            var routesToRemove = _routeMatrix
                .GetRoutes()
                .Where(route =>
                    routeEndpointIds.Contains(route.SourceEndpointId) ||
                    routeEndpointIds.Contains(route.TargetEndpointId))
                .Select(route => route.Id)
                .ToList();

            foreach (var routeId in routesToRemove)
            {
                _routeMatrix.RemoveRoute(routeId);
            }

            UpdateEndpointSnapshot();
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
        SelectedSourceEndpointId = MapRouteEndpointIdForSource(route.SourceEndpointId);
        SelectedTargetEndpointId = MapRouteEndpointIdForTarget(route.TargetEndpointId);
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

        var candidateEndpointIds = ResolveEquivalentEndpointIds(endpointId);
        if (SelectedRouteId.HasValue)
        {
            var selectedRoute = _routeMatrix.GetRoutes().FirstOrDefault(route => route.Id == SelectedRouteId.Value);
            if (selectedRoute is not null &&
                (candidateEndpointIds.Contains(selectedRoute.SourceEndpointId) ||
                 candidateEndpointIds.Contains(selectedRoute.TargetEndpointId)))
            {
                DeleteRoute();
                return true;
            }
        }

        var matchingRoutes = _routeMatrix.GetRoutes()
            .Where(route =>
                candidateEndpointIds.Contains(route.SourceEndpointId) ||
                candidateEndpointIds.Contains(route.TargetEndpointId))
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

        SelectedRoutePortId = NormalizeRoutePortSelectionId(endpointId);
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

        var selectedRouteEndpointIds = ResolveEquivalentEndpointIds(SelectedRoutePortId);
        return Routes
            .Where(route =>
                selectedRouteEndpointIds.Contains(route.SourceEndpointId) ||
                selectedRouteEndpointIds.Contains(route.TargetEndpointId))
            .ToList();
    }

    partial void OnSelectedPortChanged(PortRow? value)
    {
        SyncGridSelectionsFromSelectedPort(value);
        EditPortName = value?.Name ?? string.Empty;
        RenamePortCommand.NotifyCanExecuteChanged();
        DeletePortCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAppPortChanged(PortRow? value)
    {
        if (value is null)
        {
            TryRestoreGridSelectionFromSelectedPort(isUserManagedSelection: true);
            return;
        }

        SelectPortFromGrid(value, isUserManagedSelection: true);
    }

    partial void OnSelectedSystemPortChanged(PortRow? value)
    {
        if (value is null)
        {
            TryRestoreGridSelectionFromSelectedPort(isUserManagedSelection: false);
            return;
        }

        SelectPortFromGrid(value, isUserManagedSelection: false);
    }

    partial void OnEditPortNameChanged(string value)
    {
        RenamePortCommand.NotifyCanExecuteChanged();
    }

    partial void OnHideSystemPortsChanged(bool value)
    {
        SchedulePortRefresh(includeRoutes: true);
    }

    partial void OnPortSearchTextChanged(string value)
    {
        SchedulePortRefresh(includeRoutes: false);
    }

    partial void OnSelectedPortOwnerFilterChanged(string value)
    {
        SchedulePortRefresh(includeRoutes: false);
    }

    partial void OnSelectedPortDirectionFilterChanged(string value)
    {
        SchedulePortRefresh(includeRoutes: false);
    }

    partial void OnSelectedSourceEndpointIdChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            SelectRoutePort(value);
        }

        SaveRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetEndpointIdChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
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

    private void SelectPortFromGrid(PortRow? value, bool isUserManagedSelection)
    {
        if (_isSynchronizingPortSelection || value is null)
        {
            return;
        }

        if (value.IsUserManaged != isUserManagedSelection)
        {
            return;
        }

        try
        {
            _isSynchronizingPortSelection = true;
            SelectedPort = value;

            if (isUserManagedSelection)
            {
                SelectedSystemPort = null;
            }
            else
            {
                SelectedAppPort = null;
            }
        }
        finally
        {
            _isSynchronizingPortSelection = false;
        }
    }

    private void SyncGridSelectionsFromSelectedPort(PortRow? value)
    {
        if (_isSynchronizingPortSelection)
        {
            return;
        }

        try
        {
            _isSynchronizingPortSelection = true;

            if (value is null)
            {
                SelectedAppPort = null;
                SelectedSystemPort = null;
                return;
            }

            if (value.IsUserManaged)
            {
                if (!ReferenceEquals(SelectedAppPort, value))
                {
                    SelectedAppPort = value;
                }

                SelectedSystemPort = null;
                return;
            }

            if (!ReferenceEquals(SelectedSystemPort, value))
            {
                SelectedSystemPort = value;
            }

            SelectedAppPort = null;
        }
        finally
        {
            _isSynchronizingPortSelection = false;
        }
    }

    private void TryRestoreGridSelectionFromSelectedPort(bool isUserManagedSelection)
    {
        if (_isSynchronizingPortSelection || SelectedPort is null || SelectedPort.IsUserManaged != isUserManagedSelection)
        {
            return;
        }

        var restored = (isUserManagedSelection ? AppPorts : SystemPorts)
            .FirstOrDefault(port => string.Equals(port.Id, SelectedPort.Id, StringComparison.OrdinalIgnoreCase));
        if (restored is null)
        {
            return;
        }

        try
        {
            _isSynchronizingPortSelection = true;
            if (isUserManagedSelection)
            {
                if (!ReferenceEquals(SelectedAppPort, restored))
                {
                    SelectedAppPort = restored;
                }
            }
            else
            {
                if (!ReferenceEquals(SelectedSystemPort, restored))
                {
                    SelectedSystemPort = restored;
                }
            }
        }
        finally
        {
            _isSynchronizingPortSelection = false;
        }
    }

    private void HandleEndpointsChanged()
    {
        UpdateEndpointSnapshot();
        RefreshPorts();
        RefreshRoutes();
    }

    private void OnPortRefreshTimerTick(object? sender, EventArgs e)
    {
        _portRefreshTimer.Stop();
        RefreshPorts();

        if (!_refreshRoutesAfterPortRefresh)
        {
            return;
        }

        _refreshRoutesAfterPortRefresh = false;
        RefreshRoutes();
    }

    private void SchedulePortRefresh(bool includeRoutes)
    {
        _refreshRoutesAfterPortRefresh |= includeRoutes;
        _portRefreshTimer.Stop();
        _portRefreshTimer.Start();
    }

    private void UpdateEndpointSnapshot()
    {
        _cachedEndpoints = _endpointCatalog.GetEndpoints().ToList();
    }

    private IReadOnlyList<MidiEndpointDescriptor> GetCachedEndpoints()
    {
        if (_cachedEndpoints.Count == 0)
        {
            UpdateEndpointSnapshot();
        }

        return _cachedEndpoints;
    }

    private void RefreshPorts()
    {
        var allEndpoints = GetCachedEndpoints();

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
        var bytesPerSecondByEndpointId = _latestBytesPerSecondByEndpointId;

        Ports.Clear();
        AppPorts.Clear();
        SystemPorts.Clear();
        LeftRoutePorts.Clear();
        RightRoutePorts.Clear();

        _portListPrimaryByEndpointId.Clear();
        _portListMemberIdsByPrimaryId.Clear();
        _leftRoutePrimaryByEndpointId.Clear();
        _rightRoutePrimaryByEndpointId.Clear();
        _leftRouteMemberIdsByPrimaryId.Clear();
        _rightRouteMemberIdsByPrimaryId.Clear();

        var portRows = new Dictionary<string, PortAggregationState>(StringComparer.OrdinalIgnoreCase);
        var portOrder = new List<string>();
        var leftRouteRows = new Dictionary<string, RouteEndpointAggregationState>(StringComparer.OrdinalIgnoreCase);
        var leftRouteOrder = new List<string>();
        var rightRouteRows = new Dictionary<string, RouteEndpointAggregationState>(StringComparer.OrdinalIgnoreCase);
        var rightRouteOrder = new List<string>();

        foreach (var endpoint in filteredEndpoints)
        {
            var displayName = FormatPortDisplayName(endpoint);
            var listDisplayName = GetPortListDisplayName(endpoint, displayName);
            var owner = endpoint.IsUserManaged ? PortOwnerApp : PortOwnerSystem;
            var type = endpoint.Kind == MidiEndpointKind.Loopback ? "Loopback" : "System";

            var groupKey = BuildPortListGroupKey(endpoint, listDisplayName, type, owner);
            if (!portRows.TryGetValue(groupKey, out var aggregate))
            {
                aggregate = new PortAggregationState(
                    endpoint.Id,
                    listDisplayName,
                    endpoint.IsUserManaged,
                    endpoint.SupportsInput,
                    endpoint.SupportsOutput,
                    endpoint.IsOnline,
                    endpoint.IsOnline,
                    ResolveBytesPerSecond(endpoint.Id, bytesPerSecondByEndpointId));
                portRows[groupKey] = aggregate;
                portOrder.Add(groupKey);
            }
            else
            {
                aggregate.SupportsInput |= endpoint.SupportsInput;
                aggregate.SupportsOutput |= endpoint.SupportsOutput;
                aggregate.AnyOnline |= endpoint.IsOnline;
                aggregate.AllOnline &= endpoint.IsOnline;
                aggregate.BytesPerSecond += ResolveBytesPerSecond(endpoint.Id, bytesPerSecondByEndpointId);
            }

            _portListPrimaryByEndpointId[endpoint.Id] = aggregate.PrimaryId;
            if (!_portListMemberIdsByPrimaryId.TryGetValue(aggregate.PrimaryId, out var memberIds))
            {
                memberIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _portListMemberIdsByPrimaryId[aggregate.PrimaryId] = memberIds;
            }

            memberIds.Add(endpoint.Id);

            if (endpoint.SupportsInput)
            {
                var routeGroupKey = BuildRouteVisualGroupKey(endpoint, listDisplayName);
                if (!leftRouteRows.TryGetValue(routeGroupKey, out var leftAggregate))
                {
                    leftAggregate = new RouteEndpointAggregationState(
                        endpoint.Id,
                        listDisplayName,
                        owner,
                        endpoint.Kind == MidiEndpointKind.Loopback,
                        endpoint.IsOnline,
                        endpoint.SupportsInput,
                        endpoint.SupportsOutput);
                    leftRouteRows[routeGroupKey] = leftAggregate;
                    leftRouteOrder.Add(routeGroupKey);
                }
                else
                {
                    leftAggregate.IsLoopback |= endpoint.Kind == MidiEndpointKind.Loopback;
                    leftAggregate.IsOnline |= endpoint.IsOnline;
                    leftAggregate.SupportsInput |= endpoint.SupportsInput;
                    leftAggregate.SupportsOutput |= endpoint.SupportsOutput;
                }

                _leftRoutePrimaryByEndpointId[endpoint.Id] = leftAggregate.PrimaryId;
                if (!_leftRouteMemberIdsByPrimaryId.TryGetValue(leftAggregate.PrimaryId, out var leftMemberIds))
                {
                    leftMemberIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _leftRouteMemberIdsByPrimaryId[leftAggregate.PrimaryId] = leftMemberIds;
                }

                leftMemberIds.Add(endpoint.Id);
            }

            if (endpoint.SupportsOutput)
            {
                var routeGroupKey = BuildRouteVisualGroupKey(endpoint, listDisplayName);
                if (!rightRouteRows.TryGetValue(routeGroupKey, out var rightAggregate))
                {
                    rightAggregate = new RouteEndpointAggregationState(
                        endpoint.Id,
                        listDisplayName,
                        owner,
                        endpoint.Kind == MidiEndpointKind.Loopback,
                        endpoint.IsOnline,
                        endpoint.SupportsInput,
                        endpoint.SupportsOutput);
                    rightRouteRows[routeGroupKey] = rightAggregate;
                    rightRouteOrder.Add(routeGroupKey);
                }
                else
                {
                    rightAggregate.IsLoopback |= endpoint.Kind == MidiEndpointKind.Loopback;
                    rightAggregate.IsOnline |= endpoint.IsOnline;
                    rightAggregate.SupportsInput |= endpoint.SupportsInput;
                    rightAggregate.SupportsOutput |= endpoint.SupportsOutput;
                }

                _rightRoutePrimaryByEndpointId[endpoint.Id] = rightAggregate.PrimaryId;
                if (!_rightRouteMemberIdsByPrimaryId.TryGetValue(rightAggregate.PrimaryId, out var rightMemberIds))
                {
                    rightMemberIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _rightRouteMemberIdsByPrimaryId[rightAggregate.PrimaryId] = rightMemberIds;
                }

                rightMemberIds.Add(endpoint.Id);
            }
        }

        foreach (var routeGroupKey in leftRouteOrder)
        {
            var aggregate = leftRouteRows[routeGroupKey];
            var routeStatus = aggregate.IsOnline ? "Online" : "Offline";
            LeftRoutePorts.Add(new RouteEndpointRow(
                aggregate.PrimaryId,
                aggregate.Name,
                GetDirection(aggregate.SupportsInput, aggregate.SupportsOutput),
                aggregate.Owner,
                routeStatus,
                BuildEndpointMeta(aggregate.IsLoopback, aggregate.IsOnline)));
        }

        foreach (var routeGroupKey in rightRouteOrder)
        {
            var aggregate = rightRouteRows[routeGroupKey];
            var routeStatus = aggregate.IsOnline ? "Online" : "Offline";
            RightRoutePorts.Add(new RouteEndpointRow(
                aggregate.PrimaryId,
                aggregate.Name,
                GetDirection(aggregate.SupportsInput, aggregate.SupportsOutput),
                aggregate.Owner,
                routeStatus,
                BuildEndpointMeta(aggregate.IsLoopback, aggregate.IsOnline)));
        }

        foreach (var groupKey in portOrder)
        {
            var aggregate = portRows[groupKey];
            var row = new PortRow(
                aggregate.PrimaryId,
                aggregate.Name,
                GetDirection(aggregate.SupportsInput, aggregate.SupportsOutput),
                aggregate.IsUserManaged ? FormatByteRate(aggregate.BytesPerSecond) : "-",
                aggregate.IsUserManaged);

            Ports.Add(row);
            if (row.IsUserManaged)
            {
                AppPorts.Add(row);
            }
            else
            {
                SystemPorts.Add(row);
            }
        }

        var mappedSelectedPortId = selectedPortId is null
            ? null
            : _portListPrimaryByEndpointId.TryGetValue(selectedPortId, out var primaryId)
                ? primaryId
                : selectedPortId;

        SelectedPort = mappedSelectedPortId is null
            ? Ports.FirstOrDefault()
            : Ports.FirstOrDefault(port => string.Equals(port.Id, mappedSelectedPortId, StringComparison.OrdinalIgnoreCase))
              ?? Ports.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(SelectedSourceEndpointId))
        {
            SelectedSourceEndpointId = MapRouteEndpointIdForSource(SelectedSourceEndpointId);
        }

        if (SelectedSourceEndpointId is null || LeftRoutePorts.All(endpoint => endpoint.Id != SelectedSourceEndpointId))
        {
            SelectedSourceEndpointId = LeftRoutePorts.FirstOrDefault()?.Id;
        }

        if (!string.IsNullOrWhiteSpace(SelectedTargetEndpointId))
        {
            SelectedTargetEndpointId = MapRouteEndpointIdForTarget(SelectedTargetEndpointId);
        }

        if (SelectedTargetEndpointId is null || RightRoutePorts.All(endpoint => endpoint.Id != SelectedTargetEndpointId))
        {
            var preferred = RightRoutePorts.FirstOrDefault(endpoint =>
                !string.Equals(endpoint.Id, SelectedSourceEndpointId, StringComparison.OrdinalIgnoreCase));
            SelectedTargetEndpointId = preferred?.Id ?? RightRoutePorts.FirstOrDefault()?.Id;
        }

        if (!string.IsNullOrWhiteSpace(SelectedRoutePortId))
        {
            SelectedRoutePortId = NormalizeRoutePortSelectionId(SelectedRoutePortId);
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
        var endpointLookup = GetCachedEndpoints()
            .ToDictionary(
                endpoint => endpoint.Id,
                endpoint => GetPortListDisplayName(endpoint, FormatPortDisplayName(endpoint)),
                StringComparer.OrdinalIgnoreCase);

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

        var endpoints = GetCachedEndpoints();
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

    private string BuildRouteSummary(string sourceEndpointId, string targetEndpointId, bool enabled)
    {
        var sourceName = ResolveEndpointName(sourceEndpointId);
        var targetName = ResolveEndpointName(targetEndpointId);
        var status = enabled ? "Aktiv" : "Inaktiv";
        return $"{sourceName} -> {targetName} ({status})";
    }

    private string ResolveEndpointName(string endpointId)
    {
        var endpoint = GetCachedEndpoints()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, endpointId, StringComparison.OrdinalIgnoreCase));

        return endpoint is null
            ? endpointId
            : GetPortListDisplayName(endpoint, FormatPortDisplayName(endpoint));
    }

    private static string BuildEndpointMeta(MidiEndpointDescriptor endpoint)
    {
        return BuildEndpointMeta(endpoint.Kind == MidiEndpointKind.Loopback, endpoint.IsOnline);
    }

    private static string BuildEndpointMeta(bool isLoopback, bool isOnline)
    {
        var type = isLoopback ? "Loopback" : "System";
        var status = isOnline ? "Online" : "Offline";
        return $"{type} | {status}";
    }

    private static string GetDirection(MidiEndpointDescriptor endpoint)
    {
        return GetDirection(endpoint.SupportsInput, endpoint.SupportsOutput);
    }

    private static string GetDirection(bool supportsInput, bool supportsOutput)
    {
        return (supportsInput, supportsOutput) switch
        {
            (true, true) => PortDirectionInOut,
            (true, false) => PortDirectionInput,
            (false, true) => PortDirectionOutput,
            _ => "-"
        };
    }

    private static string BuildPortListGroupKey(MidiEndpointDescriptor endpoint, string displayName, string type, string owner)
    {
        if (endpoint.IsUserManaged && !string.IsNullOrWhiteSpace(endpoint.LogicalPortId))
        {
            return $"managed|{endpoint.LogicalPortId}";
        }

        return $"{(endpoint.IsUserManaged ? "managed" : "system")}|{type}|{owner}|{displayName}";
    }

    private static string BuildRouteVisualGroupKey(MidiEndpointDescriptor endpoint, string displayName)
    {
        if (endpoint.IsUserManaged && !string.IsNullOrWhiteSpace(endpoint.LogicalPortId))
        {
            return $"managed|{endpoint.LogicalPortId}";
        }

        return $"endpoint|{endpoint.Id}|{displayName}";
    }

    private string MapRouteEndpointIdForSource(string endpointId)
    {
        return MapRouteEndpointId(endpointId, _leftRoutePrimaryByEndpointId);
    }

    private string MapRouteEndpointIdForTarget(string endpointId)
    {
        return MapRouteEndpointId(endpointId, _rightRoutePrimaryByEndpointId);
    }

    private static string MapRouteEndpointId(string endpointId, IReadOnlyDictionary<string, string> primaryByEndpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return endpointId;
        }

        return primaryByEndpointId.TryGetValue(endpointId, out var primaryId) ? primaryId : endpointId;
    }

    private string NormalizeRoutePortSelectionId(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return endpointId;
        }

        var leftMapped = MapRouteEndpointIdForSource(endpointId);
        var rightMapped = MapRouteEndpointIdForTarget(endpointId);
        if (LeftRoutePorts.Any(port => string.Equals(port.Id, leftMapped, StringComparison.OrdinalIgnoreCase)))
        {
            return leftMapped;
        }

        if (RightRoutePorts.Any(port => string.Equals(port.Id, rightMapped, StringComparison.OrdinalIgnoreCase)))
        {
            return rightMapped;
        }

        return endpointId;
    }

    private HashSet<string> ResolveEquivalentEndpointIds(string endpointId)
    {
        var endpointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { endpointId };

        if (_portListPrimaryByEndpointId.TryGetValue(endpointId, out var portPrimaryId) &&
            _portListMemberIdsByPrimaryId.TryGetValue(portPrimaryId, out var portMemberIds))
        {
            endpointIds.UnionWith(portMemberIds);
        }

        if (_leftRouteMemberIdsByPrimaryId.TryGetValue(endpointId, out var leftMemberIds))
        {
            endpointIds.UnionWith(leftMemberIds);
        }

        if (_rightRouteMemberIdsByPrimaryId.TryGetValue(endpointId, out var rightMemberIds))
        {
            endpointIds.UnionWith(rightMemberIds);
        }

        return endpointIds;
    }

    public string ResolveLeftRouteEndpointId(string endpointId)
    {
        return MapRouteEndpointIdForSource(endpointId);
    }

    public string ResolveRightRouteEndpointId(string endpointId)
    {
        return MapRouteEndpointIdForTarget(endpointId);
    }

    private void RefreshTrafficColumns()
    {
        var selectedPortId = SelectedPort?.Id;
        var bytesPerSecondByEndpointId = _latestBytesPerSecondByEndpointId;

        RefreshTrafficColumn(Ports, bytesPerSecondByEndpointId);
        RefreshTrafficColumn(AppPorts, bytesPerSecondByEndpointId);
        RefreshTrafficColumn(SystemPorts, bytesPerSecondByEndpointId);
        RestoreSelectedPortAfterTrafficRefresh(selectedPortId);
    }

    private void PollTrafficRatesAndRefreshColumns()
    {
        var snapshots = _trafficAnalyzer.GetAllSnapshots();

        _latestBytesPerSecondByEndpointId.Clear();
        foreach (var snapshot in snapshots)
        {
            _latestBytesPerSecondByEndpointId[snapshot.EndpointId] = Math.Max(0, snapshot.BytesPerSecond);
        }

        RefreshTrafficColumns();
    }

    private void RefreshTrafficColumn(ObservableCollection<PortRow> rows, IReadOnlyDictionary<string, double> bytesPerSecondByEndpointId)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var formatted = row.IsUserManaged
                ? FormatByteRate(GetBytesPerSecondForPortRow(row.Id, bytesPerSecondByEndpointId))
                : "-";
            row.UpdateSentData(formatted);
        }
    }

    private double GetBytesPerSecondForPortRow(string primaryPortId, IReadOnlyDictionary<string, double> bytesPerSecondByEndpointId)
    {
        if (!_portListMemberIdsByPrimaryId.TryGetValue(primaryPortId, out var memberIds))
        {
            return ResolveBytesPerSecond(primaryPortId, bytesPerSecondByEndpointId);
        }

        var total = 0d;
        foreach (var memberId in memberIds)
        {
            total += ResolveBytesPerSecond(memberId, bytesPerSecondByEndpointId);
        }

        return total;
    }

    private static double ResolveBytesPerSecond(string endpointId, IReadOnlyDictionary<string, double> bytesPerSecondByEndpointId)
    {
        return bytesPerSecondByEndpointId.TryGetValue(endpointId, out var bytesPerSecond)
            ? Math.Max(0, bytesPerSecond)
            : 0d;
    }

    private static string FormatByteRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024d)
        {
            return $"{bytesPerSecond:0.0} B/s";
        }

        if (bytesPerSecond < 1024d * 1024d)
        {
            return $"{bytesPerSecond / 1024d:0.00} KB/s";
        }

        return $"{bytesPerSecond / (1024d * 1024d):0.00} MB/s";
    }

    private void RestoreSelectedPortAfterTrafficRefresh(string? selectedPortId)
    {
        if (string.IsNullOrWhiteSpace(selectedPortId))
        {
            return;
        }

        var mappedSelectedPortId = _portListPrimaryByEndpointId.TryGetValue(selectedPortId, out var primaryId)
            ? primaryId
            : selectedPortId;

        var selected = AppPorts.FirstOrDefault(port => string.Equals(port.Id, mappedSelectedPortId, StringComparison.OrdinalIgnoreCase)) ??
                       SystemPorts.FirstOrDefault(port => string.Equals(port.Id, mappedSelectedPortId, StringComparison.OrdinalIgnoreCase)) ??
                       Ports.FirstOrDefault(port => string.Equals(port.Id, mappedSelectedPortId, StringComparison.OrdinalIgnoreCase));

        if (selected is not null && !ReferenceEquals(SelectedPort, selected))
        {
            SelectedPort = selected;
        }
    }

    private static string GetPortListDisplayName(MidiEndpointDescriptor endpoint, string displayName)
    {
        if (!endpoint.IsUserManaged)
        {
            return displayName;
        }

        if (displayName.EndsWith(" (A)", StringComparison.OrdinalIgnoreCase) ||
            displayName.EndsWith(" (B)", StringComparison.OrdinalIgnoreCase))
        {
            return displayName[..^4].TrimEnd();
        }

        return displayName;
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

    public sealed class PortRow : ObservableObject
    {
        private string _sentData;

        public PortRow(
            string id,
            string name,
            string direction,
            string sentData,
            bool isUserManaged)
        {
            Id = id;
            Name = name;
            Direction = direction;
            _sentData = sentData;
            IsUserManaged = isUserManaged;
        }

        public string Id { get; }

        public string Name { get; }

        public string Direction { get; }

        public string SentData
        {
            get => _sentData;
            private set => SetProperty(ref _sentData, value);
        }

        public bool IsUserManaged { get; }

        public void UpdateSentData(string value)
        {
            if (!string.Equals(_sentData, value, StringComparison.Ordinal))
            {
                SentData = value;
            }
        }
    }

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

    private sealed class PortAggregationState
    {
        public PortAggregationState(
            string primaryId,
            string name,
            bool isUserManaged,
            bool supportsInput,
            bool supportsOutput,
            bool anyOnline,
            bool allOnline,
            double bytesPerSecond)
        {
            PrimaryId = primaryId;
            Name = name;
            IsUserManaged = isUserManaged;
            SupportsInput = supportsInput;
            SupportsOutput = supportsOutput;
            AnyOnline = anyOnline;
            AllOnline = allOnline;
            BytesPerSecond = bytesPerSecond;
        }

        public string PrimaryId { get; }

        public string Name { get; }

        public bool IsUserManaged { get; }

        public bool SupportsInput { get; set; }

        public bool SupportsOutput { get; set; }

        public bool AnyOnline { get; set; }

        public bool AllOnline { get; set; }

        public double BytesPerSecond { get; set; }
    }

    private sealed class RouteEndpointAggregationState
    {
        public RouteEndpointAggregationState(
            string primaryId,
            string name,
            string owner,
            bool isLoopback,
            bool isOnline,
            bool supportsInput,
            bool supportsOutput)
        {
            PrimaryId = primaryId;
            Name = name;
            Owner = owner;
            IsLoopback = isLoopback;
            IsOnline = isOnline;
            SupportsInput = supportsInput;
            SupportsOutput = supportsOutput;
        }

        public string PrimaryId { get; }

        public string Name { get; }

        public string Owner { get; }

        public bool IsLoopback { get; set; }

        public bool IsOnline { get; set; }

        public bool SupportsInput { get; set; }

        public bool SupportsOutput { get; set; }
    }
}
