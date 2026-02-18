namespace MidiRouter.Core.Engine;

public interface IMidiEndpointCatalog
{
    event EventHandler? EndpointsChanged;

    IReadOnlyList<MidiEndpointDescriptor> GetEndpoints();
}
