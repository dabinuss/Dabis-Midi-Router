namespace MidiRouter.Core.Monitoring;

public interface IMidiMessageLog
{
    event EventHandler<MidiMessageLogEntry>? EntryAdded;

    event EventHandler? Cleared;

    int Capacity { get; }

    IReadOnlyList<MidiMessageLogEntry> GetEntries();

    void ConfigureCapacity(int capacity);

    void Add(MidiMessageLogEntry entry);

    void Clear();
}
