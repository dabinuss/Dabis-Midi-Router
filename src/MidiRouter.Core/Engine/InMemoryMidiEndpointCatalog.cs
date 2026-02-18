using System.Collections.ObjectModel;

namespace MidiRouter.Core.Engine;

public sealed class InMemoryMidiEndpointCatalog : IMidiEndpointCatalog
{
    private readonly object _syncRoot = new();
    private readonly List<MidiEndpointDescriptor> _endpoints =
    [
        new("hw:usb-keys", "USB Keys", MidiEndpointKind.Hardware),
        new("loop:1", "Loopback 1", MidiEndpointKind.Loopback),
        new("loop:2", "Loopback 2", MidiEndpointKind.Loopback)
    ];

    public event EventHandler? EndpointsChanged;

    public IReadOnlyList<MidiEndpointDescriptor> GetEndpoints()
    {
        lock (_syncRoot)
        {
            return new ReadOnlyCollection<MidiEndpointDescriptor>(_endpoints.ToList());
        }
    }

    public void ReplaceEndpoints(IEnumerable<MidiEndpointDescriptor> endpoints)
    {
        if (endpoints is null)
        {
            throw new ArgumentNullException(nameof(endpoints));
        }

        lock (_syncRoot)
        {
            _endpoints.Clear();
            _endpoints.AddRange(endpoints);
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }
}
