namespace MidiRouter.Core.Engine;

public interface IMidiEndpointCatalog
{
    event EventHandler? EndpointsChanged;

    IReadOnlyList<MidiEndpointDescriptor> GetEndpoints();

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<MidiEndpointDescriptor> CreateLoopbackEndpointAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> DeleteLoopbackEndpointAsync(string endpointId, CancellationToken cancellationToken = default);

    Task<bool> RenameLoopbackEndpointAsync(string endpointId, string newName, CancellationToken cancellationToken = default);
}
