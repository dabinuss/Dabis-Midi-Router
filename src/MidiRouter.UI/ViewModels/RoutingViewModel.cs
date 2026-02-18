using System.Collections.ObjectModel;
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

    public RoutingViewModel(RouteMatrix routeMatrix, IMidiEndpointCatalog endpointCatalog)
    {
        _routeMatrix = routeMatrix;
        _endpointCatalog = endpointCatalog;

        _routeMatrix.RoutesChanged += (_, _) => RefreshRoutes();
        _endpointCatalog.EndpointsChanged += (_, _) => RefreshEndpoints();

        RefreshEndpoints();
        RefreshRoutes();
    }

    public ObservableCollection<MidiEndpointDescriptor> Endpoints { get; } = [];

    public ObservableCollection<RouteDefinition> Routes { get; } = [];

    [RelayCommand(CanExecute = nameof(CanAddRoute))]
    private void AddRoute()
    {
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
        _routeMatrix.RemoveRoute(routeId);
    }

    private bool CanAddRoute()
    {
        return !string.IsNullOrWhiteSpace(SelectedSourceEndpointId)
            && !string.IsNullOrWhiteSpace(SelectedTargetEndpointId)
            && !string.Equals(SelectedSourceEndpointId, SelectedTargetEndpointId, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSelectedSourceEndpointIdChanged(string? value)
    {
        AddRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetEndpointIdChanged(string? value)
    {
        AddRouteCommand.NotifyCanExecuteChanged();
    }

    private void RefreshEndpoints()
    {
        Endpoints.Clear();

        foreach (var endpoint in _endpointCatalog.GetEndpoints())
        {
            Endpoints.Add(endpoint);
        }

        if (Endpoints.Count > 0)
        {
            SelectedSourceEndpointId ??= Endpoints[0].Id;
            SelectedTargetEndpointId ??= Endpoints[Math.Min(1, Endpoints.Count - 1)].Id;
        }
    }

    private void RefreshRoutes()
    {
        Routes.Clear();

        foreach (var route in _routeMatrix.GetRoutes())
        {
            Routes.Add(route);
        }
    }
}
