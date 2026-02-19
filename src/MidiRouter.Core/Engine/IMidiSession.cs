namespace MidiRouter.Core.Engine;

public interface IMidiSession : IAsyncDisposable
{
    event EventHandler<MidiPacketReceivedEventArgs>? PacketReceived;

    event EventHandler<MidiSessionStateChangedEventArgs>? StateChanged;

    MidiSessionState State { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SendAsync(string endpointId, MidiPacket packet, CancellationToken cancellationToken = default);
}
