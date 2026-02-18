using System.Collections.ObjectModel;
using System.Collections.Concurrent;

namespace MidiRouter.Core.Routing;

public sealed class RouteMatrix
{
    private readonly ConcurrentDictionary<Guid, RouteDefinition> _routes = new();

    public event EventHandler? RoutesChanged;

    public IReadOnlyCollection<RouteDefinition> GetRoutes()
    {
        return new ReadOnlyCollection<RouteDefinition>(_routes.Values.OrderBy(route => route.SourceEndpointId).ThenBy(route => route.TargetEndpointId).ToList());
    }

    public void ReplaceRoutes(IEnumerable<RouteDefinition> routes)
    {
        if (routes is null)
        {
            throw new ArgumentNullException(nameof(routes));
        }

        _routes.Clear();

        foreach (var route in routes)
        {
            _routes[route.Id] = route;
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    public RouteDefinition AddOrUpdateRoute(RouteDefinition route)
    {
        if (route is null)
        {
            throw new ArgumentNullException(nameof(route));
        }

        _routes[route.Id] = route;
        RoutesChanged?.Invoke(this, EventArgs.Empty);
        return route;
    }

    public bool RemoveRoute(Guid routeId)
    {
        var removed = _routes.TryRemove(routeId, out _);
        if (removed)
        {
            RoutesChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public bool ShouldRoute(Guid routeId, int channel, MidiMessageType messageType)
    {
        if (!_routes.TryGetValue(routeId, out var route) || !route.Enabled)
        {
            return false;
        }

        return route.Filter.Allows(channel, messageType);
    }
}
