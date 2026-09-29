namespace ProjectFileHub.Core.Services;

public sealed record CachedResource<T>(T Value, long EstimatedBytes);

public readonly record struct ResourceCacheState(int Entries, long EstimatedBytes, int Pending, int Running);

/// <summary>
/// A bounded LRU and single-flight queue. Factories resume on the caller's context,
/// so UI resources can be created on their owning thread. A timed-out native call
/// keeps its concurrency slot until it actually finishes; cancellation must never
/// turn an unresponsive decoder into an unbounded stream of replacement decoders.
/// </summary>
public sealed class BoundedAsyncCache<T>
{
    private readonly object _sync = new();
    private readonly long _maximumBytes;
    private readonly int _maximumEntries;
    private readonly int _maximumPending;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _slots;
    private readonly Dictionary<string, LinkedListNode<(string Key, CachedResource<T> Resource)>> _cached = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, CachedResource<T> Resource)> _lru = new();
    private readonly Dictionary<(long Generation, string Key), Work> _pending = new();
    private long _bytes;
    private long _generation;
    private int _running;

    public BoundedAsyncCache(long maximumBytes, int maximumEntries, int concurrency, int maximumPending, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPending, concurrency);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _maximumBytes = maximumBytes;
        _maximumEntries = maximumEntries;
        _maximumPending = maximumPending;
        _timeout = timeout;
        _slots = new SemaphoreSlim(concurrency, concurrency);
    }

    public ResourceCacheState State
    {
        get { lock (_sync) return new(_cached.Count, _bytes, _pending.Count, _running); }
    }

    public async Task<T> GetAsync(string key, Func<CancellationToken, Task<CachedResource<T>>> load, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(load);
        cancellationToken.ThrowIfCancellationRequested();
        Work work;
        var start = false;
        lock (_sync)
        {
            if (_cached.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Resource.Value;
            }

            var identity = (_generation, key.ToUpperInvariant());
            if (!_pending.TryGetValue(identity, out work!))
            {
                if (_pending.Count >= _maximumPending)
                    throw new InvalidOperationException("图片加载队列已满，请稍后重试。");
                work = new Work(identity, new CancellationTokenSource(_timeout));
                _pending.Add(identity, work);
                start = true;
            }
        }

        if (start) _ = RunAsync(work, load);
        // Stop waiting promptly even if a Windows decoder ignores cancellation.
        return await work.Completion.Task.WaitAsync(_timeout, cancellationToken);
    }

    public void CancelPending(bool clearCache = false)
    {
        Work[] pending;
        lock (_sync)
        {
            _generation++;
            pending = _pending.Values.ToArray();
            if (clearCache)
            {
                ClearCacheUnsafe();
            }
        }

        // Cancellation callbacks are external code. Never invoke them while the
        // cache lock is held because they may synchronously re-enter this cache.
        foreach (var work in pending) work.Invalidate();
    }

    private async Task RunAsync(Work work, Func<CancellationToken, Task<CachedResource<T>>> load)
    {
        var entered = false;
        try
        {
            await _slots.WaitAsync(work.Cancellation.Token);
            entered = true;
            lock (_sync) _running++;
            work.Cancellation.Token.ThrowIfCancellationRequested();
            var resource = await load(work.Cancellation.Token);
            work.Cancellation.Token.ThrowIfCancellationRequested();
            if (resource.EstimatedBytes < 0)
                throw new InvalidOperationException("缓存资源的估算字节数不能为负数。");
            lock (_sync)
            {
                if (work.Identity.Generation != _generation)
                    throw new OperationCanceledException(work.Cancellation.Token);
                if (resource.EstimatedBytes > 0 && resource.EstimatedBytes <= _maximumBytes)
                {
                    while (_lru.Last is { } tail && (_bytes + resource.EstimatedBytes > _maximumBytes || _cached.Count >= _maximumEntries))
                    {
                        _bytes -= tail.Value.Resource.EstimatedBytes;
                        _cached.Remove(tail.Value.Key);
                        _lru.RemoveLast();
                    }
                    var node = _lru.AddFirst((work.Identity.Key, resource));
                    _cached.Add(work.Identity.Key, node);
                    _bytes += resource.EstimatedBytes;
                }
                work.Completion.TrySetResult(resource.Value);
            }
        }
        catch (OperationCanceledException)
        {
            bool invalidated;
            lock (_sync)
            {
                invalidated = work.IsInvalidated || work.Identity.Generation != _generation;
            }

            if (invalidated)
            {
                work.Completion.TrySetCanceled();
            }
            else
            {
                work.Completion.TrySetException(
                    new TimeoutException($"资源加载在 {_timeout.TotalSeconds:0.#} 秒内未完成。"));
                _ = work.Completion.Task.Exception;
            }
        }
        catch (Exception exception)
        {
            work.Completion.TrySetException(exception);
            _ = work.Completion.Task.Exception; // Observe faults even after every UI waiter has left.
        }
        finally
        {
            lock (_sync)
            {
                _pending.Remove(work.Identity);
                if (entered) _running--;
                work.Cancellation.Dispose();
            }
            if (entered) _slots.Release();
        }
    }

    private void ClearCacheUnsafe()
    {
        _cached.Clear();
        _lru.Clear();
        _bytes = 0;
    }

    private sealed record Work((long Generation, string Key) Identity, CancellationTokenSource Cancellation)
    {
        private int _invalidated;

        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;

        public void Invalidate()
        {
            Interlocked.Exchange(ref _invalidated, 1);
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation completed between the snapshot and cancellation.
            }
        }
    }
}
