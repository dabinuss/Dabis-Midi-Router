using MidiRouter.Core.Routing;

namespace MidiRouter.Core.Config;

public sealed class RouteConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string SourceEndpointId { get; set; } = string.Empty;

    public string TargetEndpointId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public List<int> Channels { get; set; } = [];

    public List<MidiMessageType> MessageTypes { get; set; } = [];
}
