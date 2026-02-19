namespace MidiRouter.Core.Engine;

public sealed class MidiSessionStateChangedEventArgs(MidiSessionState state, string? detail = null) : EventArgs
{
    public MidiSessionState State { get; } = state;

    public string? Detail { get; } = detail;
}
