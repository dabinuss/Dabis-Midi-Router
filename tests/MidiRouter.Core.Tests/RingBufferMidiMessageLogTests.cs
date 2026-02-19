using MidiRouter.Core.Monitoring;
using MidiRouter.Core.Routing;

namespace MidiRouter.Core.Tests;

public class RingBufferMidiMessageLogTests
{
    [Fact]
    public void Add_RespectsConfiguredCapacity()
    {
        var log = new RingBufferMidiMessageLog();
        log.ConfigureCapacity(2);

        log.Add(new MidiMessageLogEntry(DateTimeOffset.UtcNow, "A", 1, MidiMessageType.NoteOn, "1"));
        log.Add(new MidiMessageLogEntry(DateTimeOffset.UtcNow, "A", 1, MidiMessageType.NoteOn, "2"));
        log.Add(new MidiMessageLogEntry(DateTimeOffset.UtcNow, "A", 1, MidiMessageType.NoteOn, "3"));

        var entries = log.GetEntries();

        Assert.Equal(2, entries.Count);
        Assert.Equal("2", entries[0].Detail);
        Assert.Equal("3", entries[1].Detail);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var log = new RingBufferMidiMessageLog();
        log.Add(new MidiMessageLogEntry(DateTimeOffset.UtcNow, "A", 1, MidiMessageType.NoteOn, "x"));

        log.Clear();

        Assert.Empty(log.GetEntries());
    }
}
