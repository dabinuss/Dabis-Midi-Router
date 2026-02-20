namespace MidiRouter.Core.Engine;

public sealed record MidiEndpointDescriptor(
    string Id,
    string Name,
    MidiEndpointKind Kind,
    bool SupportsInput = true,
    bool SupportsOutput = true,
    bool IsOnline = true,
    bool IsUserManaged = false,
    string? LogicalPortId = null);
