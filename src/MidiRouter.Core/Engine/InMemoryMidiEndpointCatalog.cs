using System.Collections.ObjectModel;

namespace MidiRouter.Core.Engine;

public sealed class InMemoryMidiEndpointCatalog : IMidiEndpointCatalog
{
    private readonly object _syncRoot = new();
    private readonly List<MidiEndpointDescriptor> _endpoints =
    [
        new("hw:usb-keys", "USB Keys", MidiEndpointKind.Hardware, SupportsInput: true, SupportsOutput: true, IsUserManaged: false),
        new("loop:1", "Loopback 1", MidiEndpointKind.Loopback, SupportsInput: true, SupportsOutput: true, IsUserManaged: true),
        new("loop:2", "Loopback 2", MidiEndpointKind.Loopback, SupportsInput: true, SupportsOutput: true, IsUserManaged: true)
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

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<MidiEndpointDescriptor> CreateLoopbackEndpointAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var safeName = string.IsNullOrWhiteSpace(name)
            ? $"Loopback {DateTime.Now:HHmmss}"
            : name.Trim();

        var descriptor = new MidiEndpointDescriptor(
            $"loop:{Guid.NewGuid():N}",
            safeName,
            MidiEndpointKind.Loopback,
            SupportsInput: true,
            SupportsOutput: true,
            IsUserManaged: true);

        lock (_syncRoot)
        {
            _endpoints.Add(descriptor);
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(descriptor);
    }

    public Task<bool> DeleteLoopbackEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return Task.FromResult(false);
        }

        bool removed;

        lock (_syncRoot)
        {
            var removedCount = _endpoints.RemoveAll(endpoint =>
                endpoint.Kind == MidiEndpointKind.Loopback &&
                string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase));

            removed = removedCount > 0;
        }

        if (removed)
        {
            EndpointsChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(removed);
    }

    public Task<bool> RenameLoopbackEndpointAsync(string endpointId, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(endpointId) || string.IsNullOrWhiteSpace(newName))
        {
            return Task.FromResult(false);
        }

        bool renamed = false;
        var safeName = newName.Trim();

        lock (_syncRoot)
        {
            for (var index = 0; index < _endpoints.Count; index++)
            {
                var endpoint = _endpoints[index];
                if (endpoint.Kind != MidiEndpointKind.Loopback ||
                    !string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _endpoints[index] = endpoint with
                {
                    Name = safeName
                };

                renamed = true;
                break;
            }
        }

        if (renamed)
        {
            EndpointsChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(renamed);
    }
}

