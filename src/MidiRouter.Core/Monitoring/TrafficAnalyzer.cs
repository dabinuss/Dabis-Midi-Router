using System.Collections.Concurrent;

namespace MidiRouter.Core.Monitoring;

public sealed class TrafficAnalyzer
{
    private readonly ConcurrentDictionary<string, EndpointCounter> _counters = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterMessage(string endpointId, int byteCount, int channel)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new ArgumentException("Endpoint id is required.", nameof(endpointId));
        }

        var counter = _counters.GetOrAdd(endpointId, static _ => new EndpointCounter());
        counter.RegisterMessage(byteCount, channel);
    }

    public TrafficSnapshot GetSnapshot(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new ArgumentException("Endpoint id is required.", nameof(endpointId));
        }

        var counter = _counters.GetOrAdd(endpointId, static _ => new EndpointCounter());
        return counter.CreateSnapshot(endpointId);
    }

    public IReadOnlyList<TrafficSnapshot> GetAllSnapshots()
    {
        return _counters
            .Select(pair => pair.Value.CreateSnapshot(pair.Key))
            .OrderBy(snapshot => snapshot.EndpointId)
            .ToList();
    }

    public IReadOnlyList<TrafficSnapshot> PeekAllSnapshots()
    {
        return _counters
            .Select(pair => pair.Value.PeekSnapshot(pair.Key))
            .OrderBy(snapshot => snapshot.EndpointId)
            .ToList();
    }

    private sealed class EndpointCounter
    {
        private readonly object _syncRoot = new();
        private readonly HashSet<int> _activeChannels = [];

        private long _messageCount;
        private long _byteCount;
        private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;

        public void RegisterMessage(int byteCount, int channel)
        {
            var safeByteCount = Math.Max(0, byteCount);

            lock (_syncRoot)
            {
                _messageCount++;
                _byteCount += safeByteCount;

                if (channel is >= 1 and <= 16)
                {
                    _activeChannels.Add(channel);
                }
            }
        }

        public TrafficSnapshot CreateSnapshot(string endpointId)
        {
            lock (_syncRoot)
            {
                var now = DateTimeOffset.UtcNow;
                var elapsedSeconds = Math.Max((now - _windowStart).TotalSeconds, 0.001);

                var snapshot = new TrafficSnapshot(
                    endpointId,
                    MessagesPerSecond: _messageCount / elapsedSeconds,
                    BytesPerSecond: _byteCount / elapsedSeconds,
                    ActiveChannels: _activeChannels.OrderBy(x => x).ToArray(),
                    CapturedAtUtc: now);

                _messageCount = 0;
                _byteCount = 0;
                _activeChannels.Clear();
                _windowStart = now;

                return snapshot;
            }
        }

        public TrafficSnapshot PeekSnapshot(string endpointId)
        {
            lock (_syncRoot)
            {
                var now = DateTimeOffset.UtcNow;
                var elapsedSeconds = Math.Max((now - _windowStart).TotalSeconds, 0.001);

                return new TrafficSnapshot(
                    endpointId,
                    MessagesPerSecond: _messageCount / elapsedSeconds,
                    BytesPerSecond: _byteCount / elapsedSeconds,
                    ActiveChannels: _activeChannels.OrderBy(x => x).ToArray(),
                    CapturedAtUtc: now);
            }
        }
    }
}
