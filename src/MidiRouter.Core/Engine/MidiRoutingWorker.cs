using System.Collections.Concurrent;
using System.Threading.Channels;
using MidiRouter.Core.Monitoring;
using MidiRouter.Core.Routing;

namespace MidiRouter.Core.Engine;

public sealed class MidiRoutingWorker(
    RouteMatrix routeMatrix,
    IMidiSession midiSession,
    IMidiEndpointCatalog endpointCatalog,
    TrafficAnalyzer trafficAnalyzer,
    IMidiMessageLog messageLog) : IAsyncDisposable
{
    private readonly object _syncRoot = new();
    private Channel<MidiPacket>? _channel;
    private CancellationTokenSource? _shutdown;
    private Task? _workerTask;
    private bool _isStarted;
    private bool _routeEventsSubscribed;
    private bool _endpointEventsSubscribed;
    private volatile RouteIndex _routeIndex = RouteIndex.Empty;
    private readonly ConcurrentDictionary<string, string> _endpointNameCache = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<RouteForwardedEventArgs>? RouteForwarded;

    public void Start()
    {
        lock (_syncRoot)
        {
            if (_isStarted)
            {
                return;
            }

            _isStarted = true;
            _shutdown = new CancellationTokenSource();
            _channel = Channel.CreateUnbounded<MidiPacket>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

            if (!_routeEventsSubscribed)
            {
                routeMatrix.RoutesChanged += OnRoutesChanged;
                _routeEventsSubscribed = true;
            }

            if (!_endpointEventsSubscribed)
            {
                endpointCatalog.EndpointsChanged += OnEndpointsChanged;
                _endpointEventsSubscribed = true;
            }

            RebuildRouteIndex();
            _endpointNameCache.Clear();
            midiSession.PacketReceived += OnPacketReceived;
            _workerTask = Task.Run(() => ProcessQueueAsync(_channel, _shutdown.Token));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? workerTask;
        CancellationTokenSource? shutdown;
        Channel<MidiPacket>? channel;

        lock (_syncRoot)
        {
            if (!_isStarted)
            {
                return;
            }

            _isStarted = false;
            midiSession.PacketReceived -= OnPacketReceived;
            if (_routeEventsSubscribed)
            {
                routeMatrix.RoutesChanged -= OnRoutesChanged;
                _routeEventsSubscribed = false;
            }

            if (_endpointEventsSubscribed)
            {
                endpointCatalog.EndpointsChanged -= OnEndpointsChanged;
                _endpointEventsSubscribed = false;
            }

            workerTask = _workerTask;
            shutdown = _shutdown;
            channel = _channel;

            _workerTask = null;
            _shutdown = null;
            _channel = null;
        }

        channel?.Writer.TryComplete();
        if (shutdown is not null)
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
        }

        if (workerTask is not null)
        {
            try
            {
                await workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChannelClosedException)
            {
            }
        }

        shutdown?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private void OnPacketReceived(object? sender, MidiPacketReceivedEventArgs args)
    {
        var packet = args.Packet;
        trafficAnalyzer.RegisterMessage(packet.SourceEndpointId, packet.Data.Length, packet.Channel);
        messageLog.Add(CreateLogEntry(packet.SourceEndpointId, packet, "IN"));

        var channel = _channel;
        if (channel is not null)
        {
            channel.Writer.TryWrite(packet);
        }
    }

    private async Task ProcessQueueAsync(Channel<MidiPacket> channel, CancellationToken cancellationToken)
    {
        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var packet))
            {
                var routeIndex = _routeIndex;
                if (!routeIndex.RoutesBySourceEndpoint.TryGetValue(packet.SourceEndpointId, out var routes))
                {
                    continue;
                }

                foreach (var route in routes)
                {
                    if (!route.Enabled)
                    {
                        continue;
                    }

                    if (!route.Filter.Allows(packet.Channel, packet.MessageType))
                    {
                        continue;
                    }

                    try
                    {
                        await midiSession
                            .SendAsync(route.TargetEndpointId, packet, cancellationToken)
                            .ConfigureAwait(false);

                        trafficAnalyzer.RegisterMessage(route.TargetEndpointId, packet.Data.Length, packet.Channel);
                        messageLog.Add(CreateLogEntry(route.TargetEndpointId, packet, $"Routed from {ResolveEndpointName(route.SourceEndpointId)}"));
                        RouteForwarded?.Invoke(this, new RouteForwardedEventArgs(
                            route.Id,
                            route.SourceEndpointId,
                            route.TargetEndpointId,
                            DateTimeOffset.UtcNow));
                    }
                    catch (Exception ex)
                    {
                        messageLog.Add(new MidiMessageLogEntry(
                            DateTimeOffset.UtcNow,
                            ResolveEndpointName(route.TargetEndpointId),
                            packet.Channel,
                            packet.MessageType,
                            $"ERROR {ex.Message}"));
                    }
                }
            }
        }
    }

    private MidiMessageLogEntry CreateLogEntry(string endpointId, MidiPacket packet, string detailPrefix)
    {
        var endpointName = ResolveEndpointName(endpointId);
        var detail = $"{detailPrefix} {FormatPacketDetail(packet)}";
        return new MidiMessageLogEntry(packet.TimestampUtc, endpointName, packet.Channel, packet.MessageType, detail);
    }

    private string ResolveEndpointName(string endpointId)
    {
        return _endpointNameCache.GetOrAdd(endpointId, ResolveEndpointNameUncached);
    }

    private string ResolveEndpointNameUncached(string endpointId)
    {
        return endpointCatalog
            .GetEndpoints()
            .FirstOrDefault(endpoint => string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? endpointId;
    }

    private static string FormatPacketDetail(MidiPacket packet)
    {
        var data = packet.Data;
        if (data.Length == 0)
        {
            return "Empty";
        }

        return packet.MessageType switch
        {
            MidiMessageType.NoteOn or MidiMessageType.NoteOff when data.Length >= 3 =>
                $"{packet.MessageType} {ToNoteName(data[1])} Vel:{data[2]}",
            MidiMessageType.ControlChange when data.Length >= 3 =>
                $"CC#{data[1]} Val:{data[2]}",
            MidiMessageType.ProgramChange when data.Length >= 2 =>
                $"Program {data[1]}",
            MidiMessageType.PitchBend when data.Length >= 3 =>
                $"Pitch {(data[1] | (data[2] << 7)) - 8192}",
            MidiMessageType.SysEx =>
                $"SysEx {data.Length} bytes",
            MidiMessageType.Clock =>
                $"Clock {data[0]:X2}",
            _ => $"{packet.MessageType} [{Convert.ToHexString(data)}]"
        };
    }

    private static string ToNoteName(byte note)
    {
        string[] noteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        var name = noteNames[note % 12];
        var octave = (note / 12) - 1;
        return $"{name}{octave}";
    }

    private void OnRoutesChanged(object? sender, EventArgs args)
    {
        RebuildRouteIndex();
    }

    private void OnEndpointsChanged(object? sender, EventArgs args)
    {
        _endpointNameCache.Clear();
    }

    private void RebuildRouteIndex()
    {
        var bySource = new Dictionary<string, List<RouteDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routeMatrix.GetRoutes())
        {
            if (!bySource.TryGetValue(route.SourceEndpointId, out var list))
            {
                list = [];
                bySource[route.SourceEndpointId] = list;
            }

            list.Add(route);
        }

        _routeIndex = RouteIndex.Create(bySource);
    }

    private sealed class RouteIndex
    {
        public static RouteIndex Empty { get; } = new(new Dictionary<string, RouteDefinition[]>(StringComparer.OrdinalIgnoreCase));

        private RouteIndex(Dictionary<string, RouteDefinition[]> routesBySourceEndpoint)
        {
            RoutesBySourceEndpoint = routesBySourceEndpoint;
        }

        public Dictionary<string, RouteDefinition[]> RoutesBySourceEndpoint { get; }

        public static RouteIndex Create(Dictionary<string, List<RouteDefinition>> routesBySourceEndpoint)
        {
            var materialized = new Dictionary<string, RouteDefinition[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in routesBySourceEndpoint)
            {
                materialized[pair.Key] = pair.Value.ToArray();
            }

            return new RouteIndex(materialized);
        }
    }
}
