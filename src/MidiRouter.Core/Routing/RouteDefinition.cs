namespace MidiRouter.Core.Routing;

public sealed record RouteDefinition
{
    public RouteDefinition(
        Guid id,
        string sourceEndpointId,
        string targetEndpointId,
        bool enabled,
        RouteFilter? filter = null)
    {
        if (string.IsNullOrWhiteSpace(sourceEndpointId))
        {
            throw new ArgumentException("Source endpoint is required.", nameof(sourceEndpointId));
        }

        if (string.IsNullOrWhiteSpace(targetEndpointId))
        {
            throw new ArgumentException("Target endpoint is required.", nameof(targetEndpointId));
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        SourceEndpointId = sourceEndpointId;
        TargetEndpointId = targetEndpointId;
        Enabled = enabled;
        Filter = filter ?? RouteFilter.AllowAll;
    }

    public Guid Id { get; }

    public string SourceEndpointId { get; }

    public string TargetEndpointId { get; }

    public bool Enabled { get; }

    public RouteFilter Filter { get; }
}
