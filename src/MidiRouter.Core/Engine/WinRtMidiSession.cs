#if WINDOWS
using System.Collections.Concurrent;
using MidiRouter.Core.Routing;
using NAudio.Midi;
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
    private readonly ConcurrentDictionary<string, WinMmInputState> _winMmInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MidiOut> _winMmOutputs = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _isStarted;
    private int _reconcileScheduled;
    private CancellationTokenSource? _reconcileDebounceCts;

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
        lock (_syncRoot)
        {
            _reconcileDebounceCts?.Cancel();
            _reconcileDebounceCts?.Dispose();
            _reconcileDebounceCts = null;
        }

        await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var pair in _inputPorts)
            {
                pair.Value.MessageReceived -= OnMessageReceived;
                pair.Value.Dispose();
            }

            foreach (var pair in _outputPorts)
            {
                pair.Value.Dispose();
            }

            foreach (var pair in _winMmInputs)
            {
                pair.Value.Port.MessageReceived -= pair.Value.Handler;
                pair.Value.Port.Stop();
                pair.Value.Port.Dispose();
            }

            foreach (var pair in _winMmOutputs)
            {
                pair.Value.Dispose();
            }

            _inputPorts.Clear();
            _outputPorts.Clear();
            _winMmInputs.Clear();
            _winMmOutputs.Clear();
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

        if (_winMmOutputs.TryGetValue(endpointId, out var winMmOut))
        {
            SendViaWinMm(winMmOut, packet.Data);
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
        if (!_isStarted)
        {
            return;
        }

        CancellationTokenSource debounceCts;
        lock (_syncRoot)
        {
            _reconcileDebounceCts?.Cancel();
            _reconcileDebounceCts?.Dispose();
            _reconcileDebounceCts = new CancellationTokenSource();
            debounceCts = _reconcileDebounceCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, debounceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Interlocked.Exchange(ref _reconcileScheduled, 1) == 1)
            {
                return;
            }

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
        }, CancellationToken.None);
    }

    private async Task ReconcilePortsAsync(CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoints = endpointCatalog.GetEndpoints()
                .Where(endpoint =>
                    endpoint.IsOnline &&
                    endpoint.Kind is MidiEndpointKind.Hardware or MidiEndpointKind.Loopback)
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

                if (_inputPorts.TryRemove(existingInputId, out var inputPort))
                {
                    inputPort.MessageReceived -= OnMessageReceived;
                    inputPort.Dispose();
                }
            }

            foreach (var existingInputId in _winMmInputs.Keys.ToList())
            {
                if (desiredInputIds.Contains(existingInputId))
                {
                    continue;
                }

                if (_winMmInputs.TryRemove(existingInputId, out var state))
                {
                    state.Port.MessageReceived -= state.Handler;
                    state.Port.Stop();
                    state.Port.Dispose();
                }
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

            foreach (var existingOutputId in _winMmOutputs.Keys.ToList())
            {
                if (desiredOutputIds.Contains(existingOutputId))
                {
                    continue;
                }

                if (_winMmOutputs.TryRemove(existingOutputId, out var outputPort))
                {
                    outputPort.Dispose();
                }
            }

            foreach (var inputId in desiredInputIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_inputPorts.ContainsKey(inputId) || _winMmInputs.ContainsKey(inputId))
                {
                    continue;
                }

                if (TryCreateWinMmIn(inputId, out var winMmInputState))
                {
                    if (!_winMmInputs.TryAdd(inputId, winMmInputState))
                    {
                        winMmInputState.Port.MessageReceived -= winMmInputState.Handler;
                        winMmInputState.Port.Stop();
                        winMmInputState.Port.Dispose();
                    }

                    continue;
                }

                if (IsWinMmInputId(inputId))
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

                if (_outputPorts.ContainsKey(outputId) || _winMmOutputs.ContainsKey(outputId))
                {
                    continue;
                }

                if (TryCreateWinMmOut(outputId, out var winMmOut))
                {
                    if (!_winMmOutputs.TryAdd(outputId, winMmOut))
                    {
                        winMmOut.Dispose();
                    }

                    continue;
                }

                if (IsWinMmOutputId(outputId))
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

    private bool TryCreateWinMmIn(string endpointId, out WinMmInputState state)
    {
        state = default!;

        if (!TryParseWinMmIndex(endpointId, "winmm-in:", out var deviceIndex) || deviceIndex >= MidiIn.NumberOfDevices)
        {
            return false;
        }

        try
        {
            var midiIn = new MidiIn(deviceIndex);
            EventHandler<MidiInMessageEventArgs> handler = (_, args) =>
            {
                var data = DecodeWinMmMessage(args.RawMessage);
                if (data.Length == 0)
                {
                    return;
                }

                var packet = new MidiPacket(
                    endpointId,
                    data,
                    DecodeChannel(data),
                    DecodeMessageType(data),
                    DateTimeOffset.UtcNow);

                PacketReceived?.Invoke(this, new MidiPacketReceivedEventArgs(packet));
            };

            midiIn.MessageReceived += handler;
            midiIn.Start();
            state = new WinMmInputState(midiIn, handler);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateWinMmOut(string endpointId, out MidiOut midiOut)
    {
        midiOut = null!;

        if (!TryParseWinMmIndex(endpointId, "winmm-out:", out var deviceIndex) || deviceIndex >= MidiOut.NumberOfDevices)
        {
            return false;
        }

        try
        {
            midiOut = new MidiOut(deviceIndex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseWinMmIndex(string endpointId, string prefix, out int index)
    {
        index = -1;
        if (!endpointId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(endpointId[prefix.Length..], out index) && index >= 0;
    }

    private static bool IsWinMmInputId(string endpointId) =>
        endpointId.StartsWith("winmm-in:", StringComparison.OrdinalIgnoreCase);

    private static bool IsWinMmOutputId(string endpointId) =>
        endpointId.StartsWith("winmm-out:", StringComparison.OrdinalIgnoreCase);

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

    private static byte[] DecodeWinMmMessage(int rawMessage)
    {
        var status = (byte)(rawMessage & 0xFF);
        if (status == 0)
        {
            return [];
        }

        var data1 = (byte)((rawMessage >> 8) & 0xFF);
        var data2 = (byte)((rawMessage >> 16) & 0xFF);
        var length = GetMessageLength(status);

        return length switch
        {
            1 => [status],
            2 => [status, data1],
            _ => [status, data1, data2]
        };
    }

    private static int GetMessageLength(byte status)
    {
        var command = status & 0xF0;
        return command switch
        {
            0xC0 or 0xD0 => 2,
            0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0 => 3,
            _ => 1
        };
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

    private static MidiRouter.Core.Routing.MidiMessageType DecodeMessageType(byte[] data)
    {
        if (data.Length == 0)
        {
            return MidiRouter.Core.Routing.MidiMessageType.Unknown;
        }

        var status = data[0];
        var command = status & 0xF0;
        return command switch
        {
            0x80 => MidiRouter.Core.Routing.MidiMessageType.NoteOff,
            0x90 => MidiRouter.Core.Routing.MidiMessageType.NoteOn,
            0xB0 => MidiRouter.Core.Routing.MidiMessageType.ControlChange,
            0xC0 => MidiRouter.Core.Routing.MidiMessageType.ProgramChange,
            0xE0 => MidiRouter.Core.Routing.MidiMessageType.PitchBend,
            _ when status is 0xF0 or 0xF7 => MidiRouter.Core.Routing.MidiMessageType.SysEx,
            _ when status is 0xF8 or 0xFA or 0xFB or 0xFC => MidiRouter.Core.Routing.MidiMessageType.Clock,
            _ => MidiRouter.Core.Routing.MidiMessageType.Unknown
        };
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

    private static void SendViaWinMm(MidiOut midiOut, byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (data[0] == 0xF0)
        {
            midiOut.SendBuffer(data);
            return;
        }

        var status = data[0];
        var data1 = data.Length > 1 ? data[1] : 0;
        var data2 = data.Length > 2 ? data[2] : 0;
        var rawMessage = status | (data1 << 8) | (data2 << 16);
        midiOut.Send(rawMessage);
    }

    private sealed record WinMmInputState(MidiIn Port, EventHandler<MidiInMessageEventArgs> Handler);
}
#endif
