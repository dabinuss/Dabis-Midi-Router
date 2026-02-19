#if WINDOWS
using System.Collections.Concurrent;
using MidiRouter.Core.Routing;
using Windows.Devices.Midi;
using Windows.Storage.Streams;
using WinRtMidiMessageType = Windows.Devices.Midi.MidiMessageType;

namespace MidiRouter.Core.Engine;

public sealed class WinRtMidiSession(IMidiEndpointCatalog endpointCatalog) : IMidiSession
{
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly ConcurrentDictionary<string, MidiInPort> _inputPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IMidiOutPort> _outputPorts = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _isStarted;
    private int _reconcileScheduled;

    public event EventHandler<MidiPacketReceivedEventArgs>? PacketReceived;

    public event EventHandler<MidiSessionStateChangedEventArgs>? StateChanged;

    public MidiSessionState State { get; private set; } = MidiSessionState.Stopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isStarted)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _reconcileScheduled, 0);
            SetState(MidiSessionState.Starting);
            await endpointCatalog.RefreshAsync(cancellationToken).ConfigureAwait(false);

            endpointCatalog.EndpointsChanged += OnEndpointsChanged;
            _isStarted = true;

            await ReconcilePortsAsync(cancellationToken).ConfigureAwait(false);
            SetState(MidiSessionState.Running);
        }
        catch (Exception ex)
        {
            SetState(MidiSessionState.Faulted, ex.Message);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isStarted && State == MidiSessionState.Stopped)
        {
            return;
        }

        endpointCatalog.EndpointsChanged -= OnEndpointsChanged;
        _isStarted = false;
        Interlocked.Exchange(ref _reconcileScheduled, 0);

        await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var pair in _inputPorts)
            {
                var inputPort = pair.Value;
                inputPort.MessageReceived -= OnMessageReceived;
                inputPort.Dispose();
            }

            foreach (var pair in _outputPorts)
            {
                var outputPort = pair.Value;
                outputPort.Dispose();
            }

            _inputPorts.Clear();
            _outputPorts.Clear();
        }
        finally
        {
            _reconcileLock.Release();
        }

        SetState(MidiSessionState.Stopped);
    }

    public Task SendAsync(string endpointId, MidiPacket packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(packet);

        if (IsLoopbackEndpoint(endpointId))
        {
            var mirroredPacket = packet with
            {
                SourceEndpointId = endpointId,
                TimestampUtc = DateTimeOffset.UtcNow
            };

            PacketReceived?.Invoke(this, new MidiPacketReceivedEventArgs(mirroredPacket));
            return Task.CompletedTask;
        }

        if (!_outputPorts.TryGetValue(endpointId, out var outPort))
        {
            return Task.CompletedTask;
        }

        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(packet.Data);
            var buffer = writer.DetachBuffer();
            outPort.SendBuffer(buffer);
        }
        catch (ObjectDisposedException)
        {
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _reconcileLock.Dispose();
    }

    private void OnEndpointsChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _reconcileScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (_isStarted)
                {
                    Interlocked.Exchange(ref _reconcileScheduled, 0);
                    await ReconcilePortsAsync(CancellationToken.None).ConfigureAwait(false);

                    if (State != MidiSessionState.Running)
                    {
                        SetState(MidiSessionState.Running);
                    }

                    if (Volatile.Read(ref _reconcileScheduled) == 0)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                SetState(MidiSessionState.Faulted, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _reconcileScheduled, 0);
            }
        });
    }

    private async Task ReconcilePortsAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoints = endpointCatalog.GetEndpoints()
                .Where(endpoint => endpoint.Kind == MidiEndpointKind.Hardware && endpoint.IsOnline)
                .ToList();

            var desiredInputIds = endpoints
                .Where(endpoint => endpoint.SupportsInput)
                .Select(endpoint => endpoint.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var desiredOutputIds = endpoints
                .Where(endpoint => endpoint.SupportsOutput)
                .Select(endpoint => endpoint.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var existingInputId in _inputPorts.Keys.ToList())
            {
                if (desiredInputIds.Contains(existingInputId))
                {
                    continue;
                }

                if (!_inputPorts.TryRemove(existingInputId, out var inputPort))
                {
                    continue;
                }

                inputPort.MessageReceived -= OnMessageReceived;
                inputPort.Dispose();
            }

            foreach (var existingOutputId in _outputPorts.Keys.ToList())
            {
                if (desiredOutputIds.Contains(existingOutputId))
                {
                    continue;
                }

                if (_outputPorts.TryRemove(existingOutputId, out var outputPort))
                {
                    outputPort.Dispose();
                }
            }

            foreach (var inputId in desiredInputIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_inputPorts.ContainsKey(inputId))
                {
                    continue;
                }

                var inputPort = await MidiInPort.FromIdAsync(inputId);
                if (inputPort is null)
                {
                    continue;
                }

                inputPort.MessageReceived += OnMessageReceived;
                if (!_inputPorts.TryAdd(inputId, inputPort))
                {
                    inputPort.MessageReceived -= OnMessageReceived;
                    inputPort.Dispose();
                }
            }

            foreach (var outputId in desiredOutputIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_outputPorts.ContainsKey(outputId))
                {
                    continue;
                }

                var outputPort = await MidiOutPort.FromIdAsync(outputId);
                if (outputPort is null)
                {
                    continue;
                }

                if (!_outputPorts.TryAdd(outputId, outputPort))
                {
                    outputPort.Dispose();
                }
            }
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void OnMessageReceived(MidiInPort sender, MidiMessageReceivedEventArgs args)
    {
        var data = CopyBuffer(args.Message.RawData);
        if (data.Length == 0)
        {
            return;
        }

        var packet = new MidiPacket(
            sender.DeviceId,
            data,
            DecodeChannel(data),
            MapMessageType(args.Message.Type),
            DateTimeOffset.UtcNow);

        PacketReceived?.Invoke(this, new MidiPacketReceivedEventArgs(packet));
    }

    private bool IsLoopbackEndpoint(string endpointId)
    {
        return endpointCatalog
            .GetEndpoints()
            .Any(endpoint =>
                endpoint.Kind == MidiEndpointKind.Loopback &&
                string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase));
    }

    private void SetState(MidiSessionState state, string? detail = null)
    {
        lock (_syncRoot)
        {
            if (State == state)
            {
                return;
            }

            State = state;
        }

        StateChanged?.Invoke(this, new MidiSessionStateChangedEventArgs(state, detail));
    }

    private static byte[] CopyBuffer(IBuffer buffer)
    {
        if (buffer.Length == 0)
        {
            return [];
        }

        using var reader = DataReader.FromBuffer(buffer);
        var data = new byte[buffer.Length];
        reader.ReadBytes(data);
        return data;
    }

    private static int DecodeChannel(byte[] data)
    {
        var status = data[0];
        var statusHighNibble = status & 0xF0;
        if (statusHighNibble is >= 0x80 and <= 0xE0)
        {
            return (status & 0x0F) + 1;
        }

        return 0;
    }

    private static MidiRouter.Core.Routing.MidiMessageType MapMessageType(WinRtMidiMessageType winRtMessageType)
    {
        return winRtMessageType switch
        {
            WinRtMidiMessageType.NoteOn => MidiRouter.Core.Routing.MidiMessageType.NoteOn,
            WinRtMidiMessageType.NoteOff => MidiRouter.Core.Routing.MidiMessageType.NoteOff,
            WinRtMidiMessageType.ControlChange => MidiRouter.Core.Routing.MidiMessageType.ControlChange,
            WinRtMidiMessageType.ProgramChange => MidiRouter.Core.Routing.MidiMessageType.ProgramChange,
            WinRtMidiMessageType.PitchBendChange => MidiRouter.Core.Routing.MidiMessageType.PitchBend,
            WinRtMidiMessageType.SystemExclusive or WinRtMidiMessageType.EndSystemExclusive => MidiRouter.Core.Routing.MidiMessageType.SysEx,
            WinRtMidiMessageType.TimingClock or WinRtMidiMessageType.Start or WinRtMidiMessageType.Continue or WinRtMidiMessageType.Stop => MidiRouter.Core.Routing.MidiMessageType.Clock,
            _ => MidiRouter.Core.Routing.MidiMessageType.Unknown
        };
    }
}
#endif
