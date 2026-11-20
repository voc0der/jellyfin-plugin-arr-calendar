using System.Globalization;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.ArrCalendar.Configuration;
using Jellyfin.Plugin.ArrCalendar.Models;
using Jellyfin.Plugin.ArrCalendar.Posters;
using Jellyfin.Plugin.ArrCalendar.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// Builds the calendar from Sonarr and Radarr, and fetches their artwork.
/// </summary>
/// <remarks>
/// Both services are asked at once, and one failing never hides the other: each reports its
/// own state. Answers are cached briefly and identical requests in flight are shared, so a
/// household paging through the same month costs Sonarr one request a minute.
/// </remarks>
public sealed class CalendarService
{
    /// <summary>Longest range the calendar answers for, in days. A month view is at most 42.</summary>
    public const int MaxRangeDays = 62;

    /// <summary>Artwork types and the sizes Sonarr and Radarr pre-render for each.</summary>
    internal static readonly IReadOnlyDictionary<string, int[]> ImageSizes = new Dictionary<string, int[]>(StringComparer.Ordinal)
    {
        ["poster"] = [250, 500],
        ["fanart"] = [180, 360]
    };

    internal static readonly TimeSpan CalendarTtl = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan QueueTtl = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ImageTtl = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan MissingImageTtl = TimeSpan.FromMinutes(5);

    private readonly IArrClient _client;
    private readonly ArrSecretSource _secrets;
    private readonly ImageSigner _signer;
    private readonly ArrCalendarRateLimiter _rateLimiter;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly ILogger<CalendarService> _logger;
    private readonly ExpiringCache<JsonElement> _calendarCache;
    private readonly ExpiringCache<JsonElement> _queueCache;
    private readonly ExpiringCache<ArrImage?> _imageCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="CalendarService"/> class.
    /// </summary>
    /// <param name="client">Reads from Sonarr and Radarr.</param>
    /// <param name="secrets">Supplies the API keys.</param>
    /// <param name="signer">Signs artwork URLs.</param>
    /// <param name="rateLimiter">Bounds upstream artwork fetches.</param>
    /// <param name="logger">Logger.</param>
    public CalendarService(IArrClient client, ArrSecretSource secrets, ImageSigner signer, ArrCalendarRateLimiter rateLimiter, ILogger<CalendarService> logger)
        : this(client, secrets, signer, rateLimiter, () => Plugin.Instance?.Configuration ?? new PluginConfiguration(), TimeProvider.System, logger)
    {
    }

    internal CalendarService(
        IArrClient client,
        ArrSecretSource secrets,
        ImageSigner signer,
        ArrCalendarRateLimiter rateLimiter,
        Func<PluginConfiguration> configuration,
        TimeProvider time,
        ILogger<CalendarService> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _calendarCache = new ExpiringCache<JsonElement>(64, time);
        _queueCache = new ExpiringCache<JsonElement>(8, time);
        _imageCache = new ExpiringCache<ArrImage?>(400, time);
    }

    /// <summary>
    /// Gets everything happening between two days, inclusive.
    /// </summary>
    /// <param name="start">The first day in view.</param>
    /// <param name="end">The last day in view.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The calendar.</returns>
    public async Task<CalendarResponse> GetCalendarAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (end < start || end.DayNumber - start.DayNumber + 1 > MaxRangeDays)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "The range must run forwards and span at most " + MaxRangeDays + " days.");
        }

        var configuration = _configuration();
        var results = await Task.WhenAll(
            LoadAsync(ArrKind.Sonarr, configuration, start, end, cancellationToken),
            LoadAsync(ArrKind.Radarr, configuration, start, end, cancellationToken)).ConfigureAwait(false);

        return new CalendarResponse
        {
            Start = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            End = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Sources = results.Select(result => result.Status).ToList(),
            Events = CalendarMapper.Sort(results.SelectMany(result => result.Events)),
            DontSpecifyMethod = configuration.DontSpecifyMethod
        };
    }

    /// <summary>
    /// Gets one pre-rendered piece of artwork, or <c>null</c> when the service has none.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <param name="id">The series or movie id.</param>
    /// <param name="type"><c>poster</c> or <c>fanart</c>.</param>
    /// <param name="size">One of that type's sizes in <see cref="ImageSizes"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image, or <c>null</c>.</returns>
    /// <exception cref="ArrApiException">With 429, when the upstream fetch budget is spent.</exception>
    public async Task<ArrImage?> GetImageAsync(ArrKind kind, int id, string type, int size, CancellationToken cancellationToken)
    {
        if (id <= 0 || !ImageSizes.TryGetValue(type, out var sizes) || !sizes.Contains(size))
        {
            return null;
        }

        var configuration = _configuration();
        if (!_secrets.IsConfigured(kind, configuration))
        {
            return null;
        }

        var connection = ArrConnection.Create(kind, configuration.GetUrl(kind), _secrets.ResolveApiKey(kind, configuration));
        var file = type + "-" + size.ToString(CultureInfo.InvariantCulture) + ".jpg";
        var path = "mediacover/" + id.ToString(CultureInfo.InvariantCulture) + "/" + file;
        var timeout = configuration.GetRequestTimeout();
        var load = _imageCache.GetOrAdd(
            connection.Fingerprint + "|" + path,
            ImageTtl,
            () =>
            {
                // Only misses reach the service, so only misses spend the budget. A refusal is a
                // failed load, which the cache drops instead of remembering as "no image".
                if (!_rateLimiter.TryAcquireImageFetch(out _))
                {
                    return Task.FromException<ArrImage?>(new ArrApiException(HttpStatusCode.TooManyRequests, "Too many artwork requests."));
                }

                return _client.GetImageAsync(connection, path, timeout, CancellationToken.None);
            },
            image => image is null ? MissingImageTtl : ImageTtl);

        return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SourceStatus Failure(ArrKind kind, string error)
    {
        // Every signed-in user sees these, so they say what is wrong without saying where
        // anything lives. Administrators get the detail from the log and from Test.
        var name = kind.DisplayName();
        return new SourceStatus
        {
            Source = kind.Id(),
            State = "error",
            Error = error,
            Message = error switch
            {
                "unreachable" => name + " can't be reached right now.",
                "unauthorized" => name + " refused the connection. The server admin needs to check its API key.",
                "misconfigured" => name + " isn't set up correctly. The server admin needs to check its address.",
                _ => name + " had a problem answering."
            }
        };
    }

    private async Task<SourceResult> LoadAsync(ArrKind kind, PluginConfiguration configuration, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (!_secrets.IsConfigured(kind, configuration))
        {
            return new SourceResult(new SourceStatus { Source = kind.Id(), State = "unconfigured" }, []);
        }

        try
        {
            var connection = ArrConnection.Create(kind, configuration.GetUrl(kind), _secrets.ResolveApiKey(kind, configuration));
            var timeout = configuration.GetRequestTimeout();

            // Widened by a day before and two after: the services compare in UTC, while browsers
            // from UTC-12 to UTC+14 ask for their own local days. An episode at 23:30 local on
            // the last day in view is the next UTC day for anyone west of Greenwich.
            var from = start.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";
            var to = end.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";
            var unmonitored = configuration.IncludeUnmonitored ? "true" : "false";
            var query = new List<KeyValuePair<string, string>>
            {
                new("start", from),
                new("end", to),
                new("unmonitored", unmonitored)
            };
            if (kind == ArrKind.Sonarr)
            {
                query.Add(new("includeSeries", "true"));
                query.Add(new("includeEpisodeFile", "false"));
                query.Add(new("includeEpisodeImages", "false"));
            }

            var queueQuery = kind == ArrKind.Sonarr
                ? new List<KeyValuePair<string, string>> { new("includeSeries", "false"), new("includeEpisode", "false") }
                : new List<KeyValuePair<string, string>> { new("includeMovie", "false") };

            // Loads run without the caller's token: they are shared with whoever asks next, and
            // the client's own timeout bounds them.
            var calendarLoad = _calendarCache.GetOrAdd(
                connection.Fingerprint + "|calendar|" + from + "|" + to + "|" + unmonitored,
                CalendarTtl,
                () => _client.GetJsonAsync(connection, "calendar", query, timeout, CancellationToken.None));
            var queueLoad = _queueCache.GetOrAdd(
                connection.Fingerprint + "|queue",
                QueueTtl,
                () => _client.GetJsonAsync(connection, "queue/details", queueQuery, timeout, CancellationToken.None));

            var calendar = await calendarLoad.WaitAsync(cancellationToken).ConfigureAwait(false);
            JsonElement? queue = null;
            try
            {
                queue = await queueLoad.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArrApiException or ArrConnectionException or ArrConfigurationException)
            {
                // The queue only adds download progress; the calendar is worth showing without it.
                _logger.LogDebug(ex, "{Service} queue could not be read", kind.DisplayName());
            }

            var events = kind == ArrKind.Sonarr
                ? CalendarMapper.MapSonarr(calendar, queue, _signer)
                : CalendarMapper.MapRadarr(calendar, queue, _signer, start, end);
            return new SourceResult(new SourceStatus { Source = kind.Id(), State = "ok" }, events);
        }
        catch (ArrConfigurationException ex)
        {
            _logger.LogWarning("{Service} is misconfigured: {Reason}", kind.DisplayName(), LogSanitizer.ForLog(ex.Message));
            return new SourceResult(Failure(kind, "misconfigured"), []);
        }
        catch (ArrConnectionException ex)
        {
            _logger.LogWarning(ex, "{Service} could not be reached: {Reason}", kind.DisplayName(), LogSanitizer.ForLog(ex.Message));
            return new SourceResult(Failure(kind, "unreachable"), []);
        }
        catch (ArrApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("{Service} rejected the API key", kind.DisplayName());
            return new SourceResult(Failure(kind, "unauthorized"), []);
        }
        catch (ArrApiException ex)
        {
            _logger.LogWarning("{Service} calendar failed: {Reason}", kind.DisplayName(), LogSanitizer.ForLog(ex.Message));
            return new SourceResult(Failure(kind, "failed"), []);
        }
    }

    private sealed record SourceResult(SourceStatus Status, List<CalendarEvent> Events);
}
