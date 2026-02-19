using System.Collections.ObjectModel;

namespace MidiRouter.Core.Engine;

public sealed class InMemoryMidiSession(IMidiEndpointCatalog endpointCatalog) : IMidiSession
{
    private readonly object _syncRoot = new();
    private readonly List<SentPacketRecord> _sentPackets = [];

    public event EventHandler<MidiPacketReceivedEventArgs>? PacketReceived;

    public event EventHandler<MidiSessionStateChangedEventArgs>? StateChanged;

    public MidiSessionState State { get; private set; } = MidiSessionState.Stopped;

    public IReadOnlyList<SentPacketRecord> GetSentPackets()
    {
        lock (_syncRoot)
        {
            return new ReadOnlyCollection<SentPacketRecord>(_sentPackets.ToList());
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetState(MidiSessionState.Running);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetState(MidiSessionState.Stopped);
        return Task.CompletedTask;
    }

    public Task SendAsync(string endpointId, MidiPacket packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(packet);

        lock (_syncRoot)
        {
            _sentPackets.Add(new SentPacketRecord(endpointId, packet));
        }

        if (IsLoopbackEndpoint(endpointId))
        {
            var mirroredPacket = packet with
            {
                SourceEndpointId = endpointId,
                TimestampUtc = DateTimeOffset.UtcNow
            };

            PacketReceived?.Invoke(this, new MidiPacketReceivedEventArgs(mirroredPacket));
        }

        return Task.CompletedTask;
    }

    public void InjectIncomingPacket(MidiPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        PacketReceived?.Invoke(this, new MidiPacketReceivedEventArgs(packet));
    }

    public ValueTask DisposeAsync()
    {
        SetState(MidiSessionState.Stopped);
        return ValueTask.CompletedTask;
    }

    private void SetState(MidiSessionState state, string? detail = null)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, new MidiSessionStateChangedEventArgs(state, detail));
    }

    private bool IsLoopbackEndpoint(string endpointId)
    {
        return endpointCatalog
            .GetEndpoints()
            .Any(endpoint =>
                endpoint.Kind == MidiEndpointKind.Loopback &&
                string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record SentPacketRecord(string TargetEndpointId, MidiPacket Packet);
}
