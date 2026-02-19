using System.Threading;

namespace MidiRouter.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _instanceName;
    private readonly string _activationEventName;
    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _ownsMutex;

    public SingleInstanceCoordinator(string instanceName, string activationEventName)
    {
        _instanceName = instanceName;
        _activationEventName = activationEventName;
    }

    public bool TryAcquirePrimary(Action onActivationRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivationRequested);

        _mutex = new Mutex(initiallyOwned: false, _instanceName);
        _ownsMutex = _mutex.WaitOne(0, false);
        if (!_ownsMutex)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _activationEventName);

        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, _) =>
            {
                if (state is Action callback)
                {
                    callback();
                }
            },
            onActivationRequested,
            Timeout.Infinite,
            executeOnlyOnce: false);

        return true;
    }

    public static void SignalPrimaryInstance(string activationEventName)
    {
        using var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationEventName);
        _ = activationEvent.Set();
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
        _registeredWait = null;

        _activationEvent?.Dispose();
        _activationEvent = null;

        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex?.Dispose();
        _mutex = null;
    }
}
