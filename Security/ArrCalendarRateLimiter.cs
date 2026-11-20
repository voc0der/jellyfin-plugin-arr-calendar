using System.Threading.RateLimiting;

namespace Jellyfin.Plugin.ArrCalendar.Security;

/// <summary>
/// Bounds how often this plugin does work, however well-credentialed the caller is.
/// </summary>
/// <remarks>
/// Every calendar request can reach Sonarr and Radarr, so without a bound one looping client
/// could turn the plugin into an amplifier pointed at them. Windows are process-local and reset
/// when Jellyfin restarts: abuse bounds, not quotas. A plugin cannot add middleware to
/// Jellyfin's pipeline, so this uses <see cref="System.Threading.RateLimiting"/> directly.
/// </remarks>
public sealed class ArrCalendarRateLimiter : IDisposable
{
    /// <summary>Calendar requests per user per minute. Paging through months is one each.</summary>
    public const int DefaultCalendarPermitLimit = 60;

    /// <summary>Library lookups per user per minute. One per opened entry.</summary>
    public const int DefaultLibraryPermitLimit = 60;

    /// <summary>
    /// Artwork fetches from Sonarr and Radarr per minute, process-wide. Only cache misses count,
    /// and the endpoint is anonymous, so the bound is on the upstream work, not on callers.
    /// </summary>
    public const int DefaultImageFetchPermitLimit = 600;

    /// <summary>Administrator requests per minute, process-wide: someone pressing Test.</summary>
    public const int DefaultAdminPermitLimit = 30;

    private readonly PartitionedRateLimiter<string> _calendar;
    private readonly PartitionedRateLimiter<string> _library;
    private readonly FixedWindowRateLimiter _imageFetch;
    private readonly FixedWindowRateLimiter _admin;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrCalendarRateLimiter"/> class with the
    /// default per-minute limits.
    /// </summary>
    public ArrCalendarRateLimiter()
        : this(DefaultCalendarPermitLimit, DefaultLibraryPermitLimit, DefaultImageFetchPermitLimit, DefaultAdminPermitLimit, TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrCalendarRateLimiter"/> class.
    /// </summary>
    /// <param name="calendarLimit">Calendar requests per user per window.</param>
    /// <param name="libraryLimit">Library lookups per user per window.</param>
    /// <param name="imageFetchLimit">Upstream artwork fetches per window.</param>
    /// <param name="adminLimit">Administrator requests per window.</param>
    /// <param name="window">The window.</param>
    public ArrCalendarRateLimiter(int calendarLimit, int libraryLimit, int imageFetchLimit, int adminLimit, TimeSpan window)
    {
        _calendar = PerUser(calendarLimit, window);
        _library = PerUser(libraryLimit, window);
        _imageFetch = new FixedWindowRateLimiter(Options(imageFetchLimit, window));
        _admin = new FixedWindowRateLimiter(Options(adminLimit, window));
    }

    /// <summary>Takes a permit for one calendar request.</summary>
    /// <param name="userKey">The caller's partition: their user id, or the API key's empty id.</param>
    /// <param name="retryAfter">How long to wait, when refused.</param>
    /// <returns><c>true</c> if the request may proceed.</returns>
    public bool TryAcquireCalendar(string userKey, out TimeSpan retryAfter) => Evaluate(_calendar.AttemptAcquire(userKey), out retryAfter);

    /// <summary>Takes a permit for one library lookup.</summary>
    /// <param name="userKey">The caller's user id.</param>
    /// <param name="retryAfter">How long to wait, when refused.</param>
    /// <returns><c>true</c> if the lookup may proceed.</returns>
    public bool TryAcquireLibrary(string userKey, out TimeSpan retryAfter) => Evaluate(_library.AttemptAcquire(userKey), out retryAfter);

    /// <summary>Takes a permit for one upstream artwork fetch.</summary>
    /// <param name="retryAfter">How long to wait, when refused.</param>
    /// <returns><c>true</c> if the fetch may proceed.</returns>
    public bool TryAcquireImageFetch(out TimeSpan retryAfter) => Evaluate(_imageFetch.AttemptAcquire(), out retryAfter);

    /// <summary>Takes a permit for one administrator request.</summary>
    /// <param name="retryAfter">How long to wait, when refused.</param>
    /// <returns><c>true</c> if the request may proceed.</returns>
    public bool TryAcquireAdmin(out TimeSpan retryAfter) => Evaluate(_admin.AttemptAcquire(), out retryAfter);

    /// <inheritdoc />
    public void Dispose()
    {
        _calendar.Dispose();
        _library.Dispose();
        _imageFetch.Dispose();
        _admin.Dispose();
    }

    private static FixedWindowRateLimiterOptions Options(int limit, TimeSpan window) => new()
    {
        PermitLimit = limit,
        Window = window,
        QueueLimit = 0,
        AutoReplenishment = true
    };

    private static PartitionedRateLimiter<string> PerUser(int limit, TimeSpan window)
    {
        return PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => Options(limit, window)));
    }

    private static bool Evaluate(RateLimitLease lease, out TimeSpan retryAfter)
    {
        using (lease)
        {
            if (lease.IsAcquired)
            {
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan metadata) ? metadata : TimeSpan.FromSeconds(1);
            return false;
        }
    }
}
