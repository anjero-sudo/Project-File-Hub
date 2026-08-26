namespace ProjectFileHub.App.WindowsIntegration;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\Anjero.ProjectFileHub.SingleInstance";
    private const string ActivationEventName = @"Local\Anjero.ProjectFileHub.Activate";

    private readonly Mutex _mutex = new(false, MutexName);
    private readonly EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (_ownsMutex)
        {
            _activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivationEventName,
                out _);
        }
    }

    public bool IsPrimary => _ownsMutex;

    public void StartListening(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);
        ThrowIfDisposed();
        if (!IsPrimary || _activationEvent is null || _registeredWait is not null)
        {
            return;
        }

        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => activationRequested(),
            state: null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    public void SignalPrimary()
    {
        ThrowIfDisposed();
        if (IsPrimary)
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registeredWait?.Unregister(null);
        _activationEvent?.Dispose();
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
