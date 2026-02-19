#if WINDOWS
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Windows.Devices.Midi2;
using Microsoft.Windows.Devices.Midi2.Endpoints.Loopback;
using Microsoft.Windows.Devices.Midi2.Initialization;
using Microsoft.Windows.Devices.Midi2.ServiceConfig;
using MidiRouter.Core.Config;
using NAudio.Midi;
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
    private readonly Dictionary<Guid, ManagedLoopbackEndpointState> _managedLoopbacks = new();
    private readonly Dictionary<string, Guid> _managedPortAssociationByEndpointId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _managedUmpAssociationByEndpointId = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _loopbackStorePath;

    private MidiDesktopAppSdkInitializer? _midiInitializer;
    private volatile bool _midiServicesEnabled;
    private string _midiServicesStatus = "Windows MIDI Services Runtime nicht initialisiert.";
    private Task? _midiServicesInitializationTask;
    private int _midiServicesInitializationState;

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

        LoadPersistedLoopbackDefinitions();
        EnsureMidiServicesInitializationStarted();
    }

    public event EventHandler? EndpointsChanged;

    public IReadOnlyList<MidiEndpointDescriptor> GetEndpoints()
    {
        lock (_syncRoot)
        {
            var endpoints = _hardwareEndpoints
                .Select(pair =>
                {
                    var isManaged = _managedPortAssociationByEndpointId.ContainsKey(pair.Key);
                    return new MidiEndpointDescriptor(
                        pair.Key,
                        pair.Value.Name,
                        isManaged ? MidiEndpointKind.Loopback : MidiEndpointKind.Hardware,
                        pair.Value.SupportsInput,
                        pair.Value.SupportsOutput,
                        pair.Value.IsOnline,
                        IsUserManaged: isManaged);
                })
                .OrderBy(endpoint => endpoint.Kind)
                .ThenBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ReadOnlyCollection<MidiEndpointDescriptor>(endpoints);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMidiServicesInitializationStarted();

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_midiServicesEnabled)
            {
                await EnsureManagedLoopbacksMaterializedAsync(cancellationToken).ConfigureAwait(false);
                await RefreshManagedLoopbackPortMappingsAsync(cancellationToken).ConfigureAwait(false);
            }

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

                RebuildManagedAssociationMapUnsafe(snapshot);
            }
        }
        finally
        {
            _refreshLock.Release();
        }

        EnsureWatchersStarted();
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<MidiEndpointDescriptor> CreateLoopbackEndpointAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureMidiServicesReadyAsync(cancellationToken).ConfigureAwait(false);

        var baseName = NormalizeLoopbackBaseName(name);
        var loopback = new ManagedLoopbackEndpointState(
            associationId: Guid.NewGuid(),
            baseName: baseName,
            uniqueIdA: BuildUniqueId("A"),
            uniqueIdB: BuildUniqueId("B"));

        await CreateRuntimeLoopbackPairAsync(loopback, cancellationToken).ConfigureAwait(false);
        await ResolveManagedPortIdsWithRetryAsync(loopback, cancellationToken).ConfigureAwait(false);

        lock (_syncRoot)
        {
            _managedLoopbacks[loopback.AssociationId] = loopback;
            RegisterLoopbackMappingsUnsafe(loopback);
        }

        PersistLoopbackDefinitions();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        var createdDescriptor = FindDescriptorForLoopback(loopback.AssociationId);
        if (createdDescriptor is not null)
        {
            return createdDescriptor;
        }

        return new MidiEndpointDescriptor(
            loopback.EndpointDeviceIdA,
            BuildEndpointName(baseName, 'A'),
            MidiEndpointKind.Loopback,
            SupportsInput: true,
            SupportsOutput: true,
            IsOnline: true,
            IsUserManaged: true);
    }

    public async Task<bool> DeleteLoopbackEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return false;
        }

        await EnsureMidiServicesReadyAsync(cancellationToken).ConfigureAwait(false);

        ManagedLoopbackEndpointState? loopback;
        lock (_syncRoot)
        {
            if (!TryFindLoopbackByEndpointIdUnsafe(endpointId, out loopback) || loopback is null)
            {
                return false;
            }
        }

        if (_midiServicesEnabled)
        {
            try
            {
                var removed = MidiLoopbackEndpointManager.RemoveTransientLoopbackEndpoints(
                    new MidiLoopbackEndpointRemovalConfig(loopback.AssociationId));

                if (!removed)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        lock (_syncRoot)
        {
            _managedLoopbacks.Remove(loopback.AssociationId);
            RemoveLoopbackMappingsUnsafe(loopback.AssociationId);
        }

        PersistLoopbackDefinitions();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RenameLoopbackEndpointAsync(string endpointId, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(endpointId) || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        await EnsureMidiServicesReadyAsync(cancellationToken).ConfigureAwait(false);

        ManagedLoopbackEndpointState? loopback;
        lock (_syncRoot)
        {
            if (!TryFindLoopbackByEndpointIdUnsafe(endpointId, out loopback) || loopback is null)
            {
                return false;
            }
        }

        var renamedBaseName = NormalizeLoopbackBaseName(newName);

        if (_midiServicesEnabled)
        {
            if (!TryRenameRuntimeLoopbackEndpoint(loopback.EndpointDeviceIdA, BuildEndpointName(renamedBaseName, 'A')))
            {
                return false;
            }

            if (!TryRenameRuntimeLoopbackEndpoint(loopback.EndpointDeviceIdB, BuildEndpointName(renamedBaseName, 'B')))
            {
                return false;
            }
        }

        lock (_syncRoot)
        {
            if (_managedLoopbacks.TryGetValue(loopback.AssociationId, out var current))
            {
                current.BaseName = renamedBaseName;
            }
        }

        PersistLoopbackDefinitions();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
        StopWatchers();
        _midiInitializer?.Dispose();
        _midiInitializer = null;
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

            RebuildManagedAssociationMapUnsafe(_hardwareEndpoints);
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

            RebuildManagedAssociationMapUnsafe(_hardwareEndpoints);
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

    private void EnsureMidiServicesInitializationStarted()
    {
        if (Interlocked.CompareExchange(ref _midiServicesInitializationState, 1, comparand: 0) != 0)
        {
            return;
        }

        _midiServicesInitializationTask = Task.Run(() =>
        {
            InitializeMidiServicesRuntimeCore();
        });

        _ = _midiServicesInitializationTask.ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _midiServicesInitializationState, 2);
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
        }, TaskScheduler.Default);
    }

    private void InitializeMidiServicesRuntimeCore()
    {
        try
        {
            _midiInitializer = MidiDesktopAppSdkInitializer.Create();
            if (_midiInitializer is null)
            {
                _midiServicesEnabled = false;
                _midiServicesStatus = "Windows MIDI Services Runtime nicht installiert. Bitte installieren: Microsoft.WindowsMIDIServicesSDK.";
                return;
            }

            if (!_midiInitializer.InitializeSdkRuntime())
            {
                _midiServicesEnabled = false;
                _midiServicesStatus = "Windows MIDI Services SDK Runtime konnte nicht initialisiert werden.";
                return;
            }

            if (!_midiInitializer.EnsureServiceAvailable())
            {
                _midiServicesEnabled = false;
                _midiServicesStatus = "Windows MIDI Service ist nicht verfugbar.";
                return;
            }

            if (!MidiLoopbackEndpointManager.IsTransportAvailable)
            {
                _midiServicesEnabled = false;
                _midiServicesStatus = "Loopback-Transport von Windows MIDI Services ist nicht verfugbar.";
                return;
            }

            _midiServicesEnabled = true;
            _midiServicesStatus = "Windows MIDI Services aktiv.";
        }
        catch (Exception ex)
        {
            _midiServicesEnabled = false;
            _midiServicesStatus = $"Windows MIDI Services Initialisierung fehlgeschlagen: {ex.Message}";
        }
    }

    private async Task EnsureManagedLoopbacksMaterializedAsync(CancellationToken cancellationToken)
    {
        List<ManagedLoopbackEndpointState> snapshot;
        lock (_syncRoot)
        {
            snapshot = _managedLoopbacks.Values
                .Select(loopback => loopback.Clone())
                .ToList();
        }

        var changed = false;
        foreach (var loopback in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolvedA = FindLoopbackEndpointIdByUniqueId(loopback.UniqueIdA);
            var resolvedB = FindLoopbackEndpointIdByUniqueId(loopback.UniqueIdB);

            if (!string.IsNullOrWhiteSpace(resolvedA) && !string.IsNullOrWhiteSpace(resolvedB))
            {
                loopback.EndpointDeviceIdA = resolvedA;
                loopback.EndpointDeviceIdB = resolvedB;
            }
            else
            {
                await CreateRuntimeLoopbackPairAsync(loopback, cancellationToken).ConfigureAwait(false);
            }

            lock (_syncRoot)
            {
                if (_managedLoopbacks.TryGetValue(loopback.AssociationId, out var current))
                {
                    current.EndpointDeviceIdA = loopback.EndpointDeviceIdA;
                    current.EndpointDeviceIdB = loopback.EndpointDeviceIdB;
                    _managedUmpAssociationByEndpointId[current.EndpointDeviceIdA] = current.AssociationId;
                    _managedUmpAssociationByEndpointId[current.EndpointDeviceIdB] = current.AssociationId;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            PersistLoopbackDefinitions();
        }
    }

    private async Task RefreshManagedLoopbackPortMappingsAsync(CancellationToken cancellationToken)
    {
        List<ManagedLoopbackEndpointState> snapshot;
        lock (_syncRoot)
        {
            snapshot = _managedLoopbacks.Values
                .Select(loopback => loopback.Clone())
                .ToList();
        }

        var updated = false;
        foreach (var loopback in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolvedPortIds = ResolveAssociatedMidi1PortDeviceIds(loopback.EndpointDeviceIdA)
                .Union(ResolveAssociatedMidi1PortDeviceIds(loopback.EndpointDeviceIdB), StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (resolvedPortIds.Count == 0)
            {
                continue;
            }

            lock (_syncRoot)
            {
                if (_managedLoopbacks.TryGetValue(loopback.AssociationId, out var current) &&
                    !current.PortDeviceIds.SetEquals(resolvedPortIds))
                {
                    current.PortDeviceIds = resolvedPortIds;
                    updated = true;
                }
            }
        }

        if (updated)
        {
            PersistLoopbackDefinitions();
        }

        await Task.CompletedTask;
    }

    private async Task CreateRuntimeLoopbackPairAsync(ManagedLoopbackEndpointState loopback, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var endpointA = new MidiLoopbackEndpointDefinition
        {
            Name = BuildEndpointName(loopback.BaseName, 'A'),
            UniqueId = loopback.UniqueIdA,
            Description = "Dabis Midi Router - virtueller Loopback-Port (A)"
        };

        var endpointB = new MidiLoopbackEndpointDefinition
        {
            Name = BuildEndpointName(loopback.BaseName, 'B'),
            UniqueId = loopback.UniqueIdB,
            Description = "Dabis Midi Router - virtueller Loopback-Port (B)"
        };

        var creationConfig = new MidiLoopbackEndpointCreationConfig(loopback.AssociationId, endpointA, endpointB);
        var creationResult = MidiLoopbackEndpointManager.CreateTransientLoopbackEndpoints(creationConfig);

        if (!creationResult.Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(creationResult.ErrorInformation)
                ? "Loopback-Endpoint konnte nicht erstellt werden."
                : creationResult.ErrorInformation);
        }

        loopback.EndpointDeviceIdA = creationResult.EndpointDeviceIdA;
        loopback.EndpointDeviceIdB = creationResult.EndpointDeviceIdB;

        await Task.CompletedTask;
    }

    private async Task ResolveManagedPortIdsWithRetryAsync(ManagedLoopbackEndpointState loopback, CancellationToken cancellationToken)
    {
        const int maxAttempts = 15;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolvedIds = ResolveAssociatedMidi1PortDeviceIds(loopback.EndpointDeviceIdA)
                .Union(ResolveAssociatedMidi1PortDeviceIds(loopback.EndpointDeviceIdB), StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (resolvedIds.Count > 0)
            {
                loopback.PortDeviceIds = resolvedIds;
                return;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HashSet<string> ResolveAssociatedMidi1PortDeviceIds(string endpointDeviceId)
    {
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(endpointDeviceId))
        {
            return resolved;
        }

        try
        {
            var endpoint = MidiEndpointDeviceInformation.CreateFromEndpointDeviceId(endpointDeviceId);
            if (endpoint is null)
            {
                return resolved;
            }

            AddAssociatedPortIds(endpoint.FindAllAssociatedMidi1PortsForThisEndpoint(Midi1PortFlow.MidiMessageSource), resolved);
            AddAssociatedPortIds(endpoint.FindAllAssociatedMidi1PortsForThisEndpoint(Midi1PortFlow.MidiMessageDestination), resolved);
        }
        catch
        {
        }

        return resolved;
    }

    private static void AddAssociatedPortIds(IEnumerable<MidiEndpointAssociatedPortDeviceInformation> ports, ISet<string> target)
    {
        foreach (var port in ports)
        {
            if (!string.IsNullOrWhiteSpace(port.PortDeviceId))
            {
                target.Add(port.PortDeviceId);
            }
        }
    }

    private bool TryRenameRuntimeLoopbackEndpoint(string endpointDeviceId, string endpointName)
    {
        try
        {
            var updateConfig = new MidiServiceEndpointCustomizationConfig(MidiLoopbackEndpointManager.TransportId)
            {
                Name = endpointName,
                Description = "Dabis Midi Router - virtueller Loopback-Port"
            };

            updateConfig.MatchCriteria.EndpointDeviceId = endpointDeviceId;
            var response = MidiServiceConfig.UpdateTransportPluginConfig(updateConfig);
            return response.Status == MidiServiceConfigResponseStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureMidiServicesReadyAsync(CancellationToken cancellationToken)
    {
        EnsureMidiServicesInitializationStarted();

        if (_midiServicesInitializationTask is not null)
        {
            await _midiServicesInitializationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_midiServicesEnabled)
        {
            return;
        }

        throw new InvalidOperationException(_midiServicesStatus);
    }

    private MidiEndpointDescriptor? FindDescriptorForLoopback(Guid associationId)
    {
        lock (_syncRoot)
        {
            var endpointIds = _managedPortAssociationByEndpointId
                .Where(pair => pair.Value == associationId)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return GetEndpoints()
                .FirstOrDefault(endpoint => endpointIds.Contains(endpoint.Id));
        }
    }

    private bool TryFindLoopbackByEndpointIdUnsafe(string endpointId, out ManagedLoopbackEndpointState? loopback)
    {
        loopback = null;

        if (_managedPortAssociationByEndpointId.TryGetValue(endpointId, out var associationId) &&
            _managedLoopbacks.TryGetValue(associationId, out loopback))
        {
            return true;
        }

        if (_managedUmpAssociationByEndpointId.TryGetValue(endpointId, out associationId) &&
            _managedLoopbacks.TryGetValue(associationId, out loopback))
        {
            return true;
        }

        return false;
    }

    private void RegisterLoopbackMappingsUnsafe(ManagedLoopbackEndpointState loopback)
    {
        if (!string.IsNullOrWhiteSpace(loopback.EndpointDeviceIdA))
        {
            _managedUmpAssociationByEndpointId[loopback.EndpointDeviceIdA] = loopback.AssociationId;
        }

        if (!string.IsNullOrWhiteSpace(loopback.EndpointDeviceIdB))
        {
            _managedUmpAssociationByEndpointId[loopback.EndpointDeviceIdB] = loopback.AssociationId;
        }

        foreach (var portId in loopback.PortDeviceIds)
        {
            _managedPortAssociationByEndpointId[portId] = loopback.AssociationId;
        }
    }

    private void RemoveLoopbackMappingsUnsafe(Guid associationId)
    {
        foreach (var key in _managedPortAssociationByEndpointId
                     .Where(pair => pair.Value == associationId)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _managedPortAssociationByEndpointId.Remove(key);
        }

        foreach (var key in _managedUmpAssociationByEndpointId
                     .Where(pair => pair.Value == associationId)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _managedUmpAssociationByEndpointId.Remove(key);
        }
    }

    private void RebuildManagedAssociationMapUnsafe(IReadOnlyDictionary<string, HardwareEndpointState> snapshot)
    {
        _managedPortAssociationByEndpointId.Clear();

        foreach (var loopback in _managedLoopbacks.Values)
        {
            foreach (var portId in loopback.PortDeviceIds)
            {
                if (snapshot.ContainsKey(portId))
                {
                    _managedPortAssociationByEndpointId[portId] = loopback.AssociationId;
                }
            }

            foreach (var candidate in snapshot)
            {
                if (_managedPortAssociationByEndpointId.ContainsKey(candidate.Key))
                {
                    continue;
                }

                var normalizedName = NormalizeEndpointName(candidate.Value.Name);
                var endpointNameA = NormalizeEndpointName(BuildEndpointName(loopback.BaseName, 'A'));
                var endpointNameB = NormalizeEndpointName(BuildEndpointName(loopback.BaseName, 'B'));

                if (string.Equals(normalizedName, endpointNameA, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalizedName, endpointNameB, StringComparison.OrdinalIgnoreCase))
                {
                    _managedPortAssociationByEndpointId[candidate.Key] = loopback.AssociationId;
                    loopback.PortDeviceIds.Add(candidate.Key);
                }
            }

            if (!string.IsNullOrWhiteSpace(loopback.EndpointDeviceIdA))
            {
                _managedUmpAssociationByEndpointId[loopback.EndpointDeviceIdA] = loopback.AssociationId;
            }

            if (!string.IsNullOrWhiteSpace(loopback.EndpointDeviceIdB))
            {
                _managedUmpAssociationByEndpointId[loopback.EndpointDeviceIdB] = loopback.AssociationId;
            }
        }
    }

    private void LoadPersistedLoopbackDefinitions()
    {
        if (!File.Exists(_loopbackStorePath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(_loopbackStorePath);

            var currentFormat = JsonSerializer.Deserialize<List<PersistedLoopbackEndpointDefinition>>(raw, SerializerOptions);
            if (currentFormat is { Count: > 0 })
            {
                lock (_syncRoot)
                {
                    _managedLoopbacks.Clear();
                    _managedPortAssociationByEndpointId.Clear();
                    _managedUmpAssociationByEndpointId.Clear();

                    foreach (var entry in currentFormat)
                    {
                        if (entry.AssociationId == Guid.Empty ||
                            string.IsNullOrWhiteSpace(entry.BaseName) ||
                            string.IsNullOrWhiteSpace(entry.UniqueIdA) ||
                            string.IsNullOrWhiteSpace(entry.UniqueIdB))
                        {
                            continue;
                        }

                        _managedLoopbacks[entry.AssociationId] = new ManagedLoopbackEndpointState(
                            entry.AssociationId,
                            entry.BaseName,
                            entry.UniqueIdA,
                            entry.UniqueIdB,
                            entry.EndpointDeviceIdA ?? string.Empty,
                            entry.EndpointDeviceIdB ?? string.Empty,
                            (entry.PortDeviceIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));
                    }
                }

                return;
            }

            var legacyFormat = JsonSerializer.Deserialize<List<PersistedLoopbackEndpointLegacy>>(raw, SerializerOptions);
            if (legacyFormat is not { Count: > 0 })
            {
                return;
            }

            lock (_syncRoot)
            {
                _managedLoopbacks.Clear();
                _managedPortAssociationByEndpointId.Clear();
                _managedUmpAssociationByEndpointId.Clear();

                foreach (var legacy in legacyFormat)
                {
                    if (string.IsNullOrWhiteSpace(legacy.Name))
                    {
                        continue;
                    }

                    var associationId = Guid.NewGuid();
                    _managedLoopbacks[associationId] = new ManagedLoopbackEndpointState(
                        associationId,
                        NormalizeLoopbackBaseName(legacy.Name),
                        BuildUniqueId("A"),
                        BuildUniqueId("B"));
                }
            }

            PersistLoopbackDefinitions();
        }
        catch
        {
        }
    }

    private void PersistLoopbackDefinitions()
    {
        List<PersistedLoopbackEndpointDefinition> snapshot;
        lock (_syncRoot)
        {
            snapshot = _managedLoopbacks.Values
                .OrderBy(loopback => loopback.BaseName, StringComparer.OrdinalIgnoreCase)
                .Select(loopback => new PersistedLoopbackEndpointDefinition(
                    loopback.AssociationId,
                    loopback.BaseName,
                    loopback.UniqueIdA,
                    loopback.UniqueIdB,
                    loopback.EndpointDeviceIdA,
                    loopback.EndpointDeviceIdB,
                    loopback.PortDeviceIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();
        }

        var directory = Path.GetDirectoryName(_loopbackStorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_loopbackStorePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
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

    private static string BuildEndpointName(string baseName, char side)
    {
        return $"{baseName} ({side})";
    }

    private static string NormalizeLoopbackBaseName(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? $"Loopback {DateTime.Now:HHmmss}" : value.Trim();

        if (candidate.EndsWith("(A)", StringComparison.OrdinalIgnoreCase) ||
            candidate.EndsWith("(B)", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^3].TrimEnd();
        }

        return candidate;
    }

    private static string NormalizeEndpointName(string value)
    {
        if (value.StartsWith("[System]", StringComparison.OrdinalIgnoreCase))
        {
            return value[8..].TrimStart();
        }

        return value;
    }

    private static string BuildUniqueId(string suffix)
    {
        return "DMR" + Guid.NewGuid().ToString("N")[..18] + suffix;
    }

    private string? FindLoopbackEndpointIdByUniqueId(string uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            return null;
        }

        try
        {
            foreach (var endpoint in MidiEndpointDeviceInformation.FindAll())
            {
                if (endpoint.GetTransportSuppliedInfo().TransportId != MidiLoopbackEndpointManager.TransportId)
                {
                    continue;
                }

                if (string.Equals(endpoint.GetTransportSuppliedInfo().SerialNumber, uniqueId, StringComparison.OrdinalIgnoreCase))
                {
                    return endpoint.EndpointDeviceId;
                }
            }
        }
        catch
        {
        }

        return null;
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

    private sealed class ManagedLoopbackEndpointState
    {
        public ManagedLoopbackEndpointState(
            Guid associationId,
            string baseName,
            string uniqueIdA,
            string uniqueIdB,
            string endpointDeviceIdA = "",
            string endpointDeviceIdB = "",
            HashSet<string>? portDeviceIds = null)
        {
            AssociationId = associationId;
            BaseName = baseName;
            UniqueIdA = uniqueIdA;
            UniqueIdB = uniqueIdB;
            EndpointDeviceIdA = endpointDeviceIdA;
            EndpointDeviceIdB = endpointDeviceIdB;
            PortDeviceIds = portDeviceIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public Guid AssociationId { get; set; }

        public string BaseName { get; set; }

        public string UniqueIdA { get; }

        public string UniqueIdB { get; }

        public string EndpointDeviceIdA { get; set; }

        public string EndpointDeviceIdB { get; set; }

        public HashSet<string> PortDeviceIds { get; set; }

        public ManagedLoopbackEndpointState Clone()
        {
            return new ManagedLoopbackEndpointState(
                AssociationId,
                BaseName,
                UniqueIdA,
                UniqueIdB,
                EndpointDeviceIdA,
                EndpointDeviceIdB,
                PortDeviceIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed record PersistedLoopbackEndpointDefinition(
        Guid AssociationId,
        string BaseName,
        string UniqueIdA,
        string UniqueIdB,
        string? EndpointDeviceIdA,
        string? EndpointDeviceIdB,
        IReadOnlyList<string>? PortDeviceIds);

    private sealed record PersistedLoopbackEndpointLegacy(string Id, string Name);
}
#endif
