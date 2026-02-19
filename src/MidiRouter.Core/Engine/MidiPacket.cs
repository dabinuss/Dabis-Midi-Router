using MidiRouter.Core.Routing;

namespace MidiRouter.Core.Engine;

public sealed record MidiPacket(
    string SourceEndpointId,
    byte[] Data,
    int Channel,
    MidiMessageType MessageType,
    DateTimeOffset TimestampUtc);
