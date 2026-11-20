namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// A small, bounded cache of in-flight and finished loads.
/// </summary>
/// <remarks>
/// Storing the task rather than its result coalesces concurrent identical loads: two people
/// opening the same month at once cost Sonarr one request. A load that fails is dropped at
/// once, so the next caller tries again instead of being served the failure.
/// </remarks>
/// <typeparam name="TValue">The cached value.</typeparam>
internal sealed class ExpiringCache<TValue>
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExpiringCache{TValue}"/> class.
    /// </summary>
    /// <param name="capacity">Most entries kept; the soonest to expire go first beyond it.</param>
    /// <param name="time">The clock.</param>
    public ExpiringCache(int capacity, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Gets the number of entries, expired or not, for tests.
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Gets a live entry, or starts the load and caches it.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="ttl">How long a load stays fresh, counted from when it started.</param>
    /// <param name="load">Starts the load. Called at most once per miss.</param>
    /// <param name="ttlFor">
    /// Optionally, a lifetime chosen from the result once it is known, for example a shorter one
    /// for "nothing there" so that something added upstream shows up soon.
    /// </param>
    /// <returns>The load.</returns>
    public Task<TValue> GetOrAdd(string key, TimeSpan ttl, Func<Task<TValue>> load, Func<TValue, TimeSpan>? ttlFor = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);

        Task<TValue> task;
        DateTimeOffset started;
        lock (_lock)
        {
            started = _time.GetUtcNow();
            if (_entries.TryGetValue(key, out var hit) && hit.Expires > started)
            {
                return hit.Task;
            }

            task = load();
            _entries[key] = new Entry(task, started + ttl);
            Trim(started);
        }

        if (task.IsCompleted)
        {
            Settle(key, task, started, ttlFor);
        }
        else
        {
            _ = task.ContinueWith(
                finished => Settle(key, finished, started, ttlFor),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return task;
    }

    /// <summary>
    /// Drops everything, for example after the settings change.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private void Settle(string key, Task<TValue> finished, DateTimeOffset started, Func<TValue, TimeSpan>? ttlFor)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current.Task, finished))
            {
                return;
            }

            if (!finished.IsCompletedSuccessfully)
            {
                _entries.Remove(key);
            }
            else if (ttlFor is not null)
            {
                _entries[key] = current with { Expires = started + ttlFor(finished.Result) };
            }
        }
    }

    private void Trim(DateTimeOffset now)
    {
        if (_entries.Count <= _capacity)
        {
            return;
        }

        foreach (var expired in _entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(expired);
        }

        while (_entries.Count > _capacity)
        {
            var soonest = _entries.MinBy(pair => pair.Value.Expires).Key;
            _entries.Remove(soonest);
        }
    }

    private readonly record struct Entry(Task<TValue> Task, DateTimeOffset Expires);
}
