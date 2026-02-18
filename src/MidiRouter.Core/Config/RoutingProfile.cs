namespace MidiRouter.Core.Config;

public sealed class RoutingProfile
{
    public string Name { get; set; } = "Default";

    public List<RouteConfig> Routes { get; set; } = [];
}
