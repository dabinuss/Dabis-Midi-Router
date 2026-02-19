#if WINDOWS
using System.Text.Json;
using MidiRouter.Core.Config;
using NAudio.Midi;
using System.Collections.ObjectModel;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;

namespace MidiRouter.Core.Engine;

public sealed class WinRtMidiEndpointCatalog : IMidiEndpointCatalog, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, HardwareEndpointState> _hardwareEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MidiEndpointDescriptor> _loopbackEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _loopbackStorePath;

    private DeviceWatcher? _inputWatcher;
    private DeviceWatcher? _outputWatcher;
    private bool _watchersStarted;

    public WinRtMidiEndpointCatalog(ConfigStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configDirectory = Path.GetDirectoryName(options.ConfigPath);
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            configDirectory = AppContext.BaseDirectory;
        }

        _loopbackStorePath = Path.Combine(configDirectory, "loopback-endpoints.json");
        LoadPersistedLoopbackEndpoints();
    }

    public event EventHandler? EndpointsChanged;

    public IReadOnlyList<MidiEndpointDescriptor> GetEndpoints()
    {
        lock (_syncRoot)
        {
            var endpoints = _hardwareEndpoints
                .Select(pair => new MidiEndpointDescriptor(
                    pair.Key,
                    pair.Value.Name,
                    MidiEndpointKind.Hardware,
                    pair.Value.SupportsInput,
                    pair.Value.SupportsOutput,
                    pair.Value.IsOnline,
                    IsUserManaged: false))
                .Concat(_loopbackEndpoints.Values)
                .OrderBy(endpoint => endpoint.Kind)
                .ThenBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ReadOnlyCollection<MidiEndpointDescriptor>(endpoints);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var inputDevices = await DeviceInformation.FindAllAsync(MidiInPort.GetDeviceSelector());
            var outputDevices = await DeviceInformation.FindAllAsync(MidiOutPort.GetDeviceSelector());

            var snapshot = new Dictionary<string, HardwareEndpointState>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in inputDevices)
            {
                var name = SafeDeviceName(device);
                snapshot[device.Id] = new HardwareEndpointState(name, SupportsInput: true, SupportsOutput: false, IsOnline: true);
            }

            foreach (var device in outputDevices)
            {
                if (!snapshot.TryGetValue(device.Id, out var existing))
                {
                    snapshot[device.Id] = new HardwareEndpointState(SafeDeviceName(device), SupportsInput: false, SupportsOutput: true, IsOnline: true);
                    continue;
                }

                snapshot[device.Id] = existing with
                {
                    SupportsOutput = true,
                    Name = string.IsNullOrWhiteSpace(existing.Name) ? SafeDeviceName(device) : existing.Name
                };
            }

            AppendWinMmEndpoints(snapshot);

            lock (_syncRoot)
            {
                _hardwareEndpoints.Clear();
                foreach (var pair in snapshot)
                {
                    _hardwareEndpoints[pair.Key] = pair.Value;
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        // Start watchers after a full snapshot is available.
        // This avoids early partial updates during startup that can make
        // some endpoints appear a few seconds later in the UI.
        EnsureWatchersStarted();
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<MidiEndpointDescriptor> CreateLoopbackEndpointAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = CreateLoopbackDescriptor(
            $"loop:{Guid.NewGuid():N}",
            string.IsNullOrWhiteSpace(name) ? $"Loopback {DateTime.Now:HHmmss}" : name.Trim());

        List<PersistedLoopbackEndpoint> persistedSnapshot;
        lock (_syncRoot)
        {
            _loopbackEndpoints[endpoint.Id] = endpoint;
            persistedSnapshot = SnapshotLoopbackEndpointsUnsafe();
        }

        try
        {
            PersistLoopbackEndpoints(persistedSnapshot);
        }
        catch
        {
            lock (_syncRoot)
            {
                _loopbackEndpoints.Remove(endpoint.Id);
            }

            throw;
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(endpoint);
    }

    public Task<bool> DeleteLoopbackEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return Task.FromResult(false);
        }

        bool removed;
        MidiEndpointDescriptor? removedEndpoint = null;
        List<PersistedLoopbackEndpoint> persistedSnapshot = [];
        lock (_syncRoot)
        {
            removed = _loopbackEndpoints.Remove(endpointId, out removedEndpoint);
            if (removed)
            {
                persistedSnapshot = SnapshotLoopbackEndpointsUnsafe();
            }
        }

        if (removed)
        {
            try
            {
                PersistLoopbackEndpoints(persistedSnapshot);
            }
            catch
            {
                lock (_syncRoot)
                {
                    if (removedEndpoint is not null)
                    {
                        _loopbackEndpoints[removedEndpoint.Id] = removedEndpoint;
                    }
                }

                throw;
            }

            EndpointsChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(removed);
    }

    public Task<bool> RenameLoopbackEndpointAsync(string endpointId, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(endpointId) || string.IsNullOrWhiteSpace(newName))
        {
            return Task.FromResult(false);
        }

        bool renamed;
        var safeName = newName.Trim();
        MidiEndpointDescriptor? previousEndpoint = null;
        List<PersistedLoopbackEndpoint> persistedSnapshot = [];

        lock (_syncRoot)
        {
            if (!_loopbackEndpoints.TryGetValue(endpointId, out var existing))
            {
                return Task.FromResult(false);
            }

            previousEndpoint = existing;
            _loopbackEndpoints[endpointId] = existing with
            {
                Name = safeName
            };

            renamed = true;
            persistedSnapshot = SnapshotLoopbackEndpointsUnsafe();
        }

        if (renamed)
        {
            try
            {
                PersistLoopbackEndpoints(persistedSnapshot);
            }
            catch
            {
                lock (_syncRoot)
                {
                    if (previousEndpoint is not null)
                    {
                        _loopbackEndpoints[endpointId] = previousEndpoint;
                    }
                }

                throw;
            }

            EndpointsChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(renamed);
    }

    public void Dispose()
    {
        StopWatchers();
        _refreshLock.Dispose();
    }

    private void EnsureWatchersStarted()
    {
        lock (_syncRoot)
        {
            if (_watchersStarted)
            {
                return;
            }

            _inputWatcher = DeviceInformation.CreateWatcher(MidiInPort.GetDeviceSelector());
            _outputWatcher = DeviceInformation.CreateWatcher(MidiOutPort.GetDeviceSelector());

            _inputWatcher.Added += OnInputAdded;
            _inputWatcher.Removed += OnInputRemoved;
            _inputWatcher.Updated += OnWatcherUpdated;

            _outputWatcher.Added += OnOutputAdded;
            _outputWatcher.Removed += OnOutputRemoved;
            _outputWatcher.Updated += OnWatcherUpdated;

            _inputWatcher.Start();
            _outputWatcher.Start();
            _watchersStarted = true;
        }
    }

    private void StopWatchers()
    {
        lock (_syncRoot)
        {
            if (!_watchersStarted)
            {
                return;
            }

            if (_inputWatcher is not null)
            {
                _inputWatcher.Added -= OnInputAdded;
                _inputWatcher.Removed -= OnInputRemoved;
                _inputWatcher.Updated -= OnWatcherUpdated;
                SafeStopWatcher(_inputWatcher);
                _inputWatcher = null;
            }

            if (_outputWatcher is not null)
            {
                _outputWatcher.Added -= OnOutputAdded;
                _outputWatcher.Removed -= OnOutputRemoved;
                _outputWatcher.Updated -= OnWatcherUpdated;
                SafeStopWatcher(_outputWatcher);
                _outputWatcher = null;
            }

            _watchersStarted = false;
        }
    }

    private void OnInputAdded(DeviceWatcher sender, DeviceInformation info)
    {
        ApplyHardwareEndpoint(info.Id, SafeDeviceName(info), SupportsInput: true, SupportsOutput: false);
    }

    private void OnOutputAdded(DeviceWatcher sender, DeviceInformation info)
    {
        ApplyHardwareEndpoint(info.Id, SafeDeviceName(info), SupportsInput: false, SupportsOutput: true);
    }

    private void OnInputRemoved(DeviceWatcher sender, DeviceInformationUpdate info)
    {
        RemoveHardwareDirection(info.Id, SupportsInput: true);
    }

    private void OnOutputRemoved(DeviceWatcher sender, DeviceInformationUpdate info)
    {
        RemoveHardwareDirection(info.Id, SupportsInput: false);
    }

    private void OnWatcherUpdated(DeviceWatcher sender, DeviceInformationUpdate info)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    private void ApplyHardwareEndpoint(string endpointId, string name, bool SupportsInput, bool SupportsOutput)
    {
        lock (_syncRoot)
        {
            if (!_hardwareEndpoints.TryGetValue(endpointId, out var existing))
            {
                _hardwareEndpoints[endpointId] = new HardwareEndpointState(name, SupportsInput, SupportsOutput, IsOnline: true);
            }
            else
            {
                _hardwareEndpoints[endpointId] = existing with
                {
                    Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name,
                    SupportsInput = existing.SupportsInput || SupportsInput,
                    SupportsOutput = existing.SupportsOutput || SupportsOutput,
                    IsOnline = true
                };
            }
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveHardwareDirection(string endpointId, bool SupportsInput)
    {
        lock (_syncRoot)
        {
            if (!_hardwareEndpoints.TryGetValue(endpointId, out var existing))
            {
                return;
            }

            var updated = existing with
            {
                SupportsInput = SupportsInput ? false : existing.SupportsInput,
                SupportsOutput = SupportsInput ? existing.SupportsOutput : false,
                IsOnline = true
            };

            if (!updated.SupportsInput && !updated.SupportsOutput)
            {
                _hardwareEndpoints.Remove(endpointId);
            }
            else
            {
                _hardwareEndpoints[endpointId] = updated;
            }
        }

        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SafeStopWatcher(DeviceWatcher watcher)
    {
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch
        {
        }
    }

    private static string SafeDeviceName(DeviceInformation device)
    {
        return string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name;
    }

    private void LoadPersistedLoopbackEndpoints()
    {
        if (!File.Exists(_loopbackStorePath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(_loopbackStorePath);
            var persisted = JsonSerializer.Deserialize<List<PersistedLoopbackEndpoint>>(raw, SerializerOptions) ?? [];

            lock (_syncRoot)
            {
                _loopbackEndpoints.Clear();
                foreach (var item in persisted)
                {
                    if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
                    {
                        continue;
                    }

                    _loopbackEndpoints[item.Id] = CreateLoopbackDescriptor(item.Id, item.Name);
                }
            }
        }
        catch
        {
            // Ignore malformed persistence file and continue with runtime-only loopbacks.
        }
    }

    private List<PersistedLoopbackEndpoint> SnapshotLoopbackEndpointsUnsafe()
    {
        return _loopbackEndpoints.Values
            .OrderBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .Select(endpoint => new PersistedLoopbackEndpoint(endpoint.Id, endpoint.Name))
            .ToList();
    }

    private void PersistLoopbackEndpoints(IReadOnlyCollection<PersistedLoopbackEndpoint> endpoints)
    {
        var directory = Path.GetDirectoryName(_loopbackStorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_loopbackStorePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(endpoints, SerializerOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _loopbackStorePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static MidiEndpointDescriptor CreateLoopbackDescriptor(string endpointId, string name)
    {
        return new MidiEndpointDescriptor(
            endpointId,
            name,
            MidiEndpointKind.Loopback,
            SupportsInput: true,
            SupportsOutput: true,
            IsOnline: true,
            IsUserManaged: true);
    }

    private static void AppendWinMmEndpoints(IDictionary<string, HardwareEndpointState> snapshot)
    {
        for (var index = 0; index < MidiIn.NumberOfDevices; index++)
        {
            var capabilities = MidiIn.DeviceInfo(index);
            var endpointId = $"winmm-in:{index}";
            var name = $"[System] {capabilities.ProductName}";
            snapshot[endpointId] = new HardwareEndpointState(name, SupportsInput: true, SupportsOutput: false, IsOnline: true);
        }

        for (var index = 0; index < MidiOut.NumberOfDevices; index++)
        {
            var capabilities = MidiOut.DeviceInfo(index);
            var endpointId = $"winmm-out:{index}";
            var name = $"[System] {capabilities.ProductName}";
            snapshot[endpointId] = new HardwareEndpointState(name, SupportsInput: false, SupportsOutput: true, IsOnline: true);
        }
    }

    private sealed record HardwareEndpointState(
        string Name,
        bool SupportsInput,
        bool SupportsOutput,
        bool IsOnline);

    private sealed record PersistedLoopbackEndpoint(string Id, string Name);
}
#endif

