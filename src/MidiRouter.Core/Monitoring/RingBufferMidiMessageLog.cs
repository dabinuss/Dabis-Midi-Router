using System.Collections.ObjectModel;

namespace MidiRouter.Core.Monitoring;

public sealed class RingBufferMidiMessageLog : IMidiMessageLog
{
    private readonly object _syncRoot = new();
    private readonly Queue<MidiMessageLogEntry> _entries = new();

    private int _capacity = 5000;

    public event EventHandler<MidiMessageLogEntry>? EntryAdded;

    public event EventHandler? Cleared;

    public int Capacity
    {
        get
        {
            lock (_syncRoot)
            {
                return _capacity;
            }
        }
    }

    public IReadOnlyList<MidiMessageLogEntry> GetEntries()
    {
        lock (_syncRoot)
        {
            return new ReadOnlyCollection<MidiMessageLogEntry>(_entries.ToList());
        }
    }

    public void ConfigureCapacity(int capacity)
    {
        var safeCapacity = Math.Clamp(capacity, 1, 200_000);

        lock (_syncRoot)
        {
            _capacity = safeCapacity;

            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public void Add(MidiMessageLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_syncRoot)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _entries.Clear();
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
