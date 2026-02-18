using MidiRouter.Core.Routing;

namespace MidiRouter.Core.Monitoring;

public sealed record MidiMessageLogEntry(
    DateTimeOffset Timestamp,
    string EndpointName,
    int Channel,
    MidiMessageType MessageType,
    string Detail);
