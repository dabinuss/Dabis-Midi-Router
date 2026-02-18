namespace MidiRouter.Core.Routing;

public enum MidiMessageType
{
    NoteOn = 0,
    NoteOff = 1,
    ControlChange = 2,
    ProgramChange = 3,
    PitchBend = 4,
    SysEx = 5,
    Clock = 6,
    Unknown = 7
}
