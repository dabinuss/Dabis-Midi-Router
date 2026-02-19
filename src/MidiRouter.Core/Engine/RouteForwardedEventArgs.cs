namespace MidiRouter.Core.Engine;

public sealed class RouteForwardedEventArgs(
    Guid routeId,
    string sourceEndpointId,
    string targetEndpointId,
    DateTimeOffset timestampUtc) : EventArgs
{
    public Guid RouteId { get; } = routeId;

    public string SourceEndpointId { get; } = sourceEndpointId;

    public string TargetEndpointId { get; } = targetEndpointId;

    public DateTimeOffset TimestampUtc { get; } = timestampUtc;
}
