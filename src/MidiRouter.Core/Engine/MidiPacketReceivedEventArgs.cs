namespace MidiRouter.Core.Engine;

public sealed class MidiPacketReceivedEventArgs(MidiPacket packet) : EventArgs
{
    public MidiPacket Packet { get; } = packet ?? throw new ArgumentNullException(nameof(packet));
}
