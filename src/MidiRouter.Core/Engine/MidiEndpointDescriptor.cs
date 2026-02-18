namespace MidiRouter.Core.Engine;

public sealed record MidiEndpointDescriptor(
    string Id,
    string Name,
    MidiEndpointKind Kind,
    bool IsOnline = true);
