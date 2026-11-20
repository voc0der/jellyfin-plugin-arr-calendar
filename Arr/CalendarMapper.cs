using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ArrCalendar.Models;
using Jellyfin.Plugin.ArrCalendar.Posters;

namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// Turns Sonarr's and Radarr's answers into calendar entries.
/// </summary>
/// <remarks>
/// Every field is copied by name onto <see cref="CalendarEvent"/>. Nothing is passed through,
/// because both services embed server paths (<c>path</c>, <c>folderName</c>), quality profiles
/// and tags in the same objects. Pure, so it is tested against answers recorded from real
/// instances.
/// </remarks>
internal static partial class CalendarMapper
{
    internal const int MaxOverviewLength = 1200;
    private const int MaxTextLength = 300;

    private static readonly string[] Finales = ["series", "season", "midseason"];

    /// <summary>
    /// Maps Sonarr's calendar, with each episode's series embedded, and its queue.
    /// </summary>
    /// <param name="calendar">The answer to <c>calendar?includeSeries=true</c>.</param>
    /// <param name="queue">The answer to <c>queue/details</c>, or <c>null</c> when it failed.</param>
    /// <param name="signer">Signs artwork URLs.</param>
    /// <returns>One entry per episode that has a date.</returns>
    public static List<CalendarEvent> MapSonarr(JsonElement calendar, JsonElement? queue, ImageSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        var downloads = Downloads(queue, "episodeId");
        var events = new List<CalendarEvent>();
        foreach (var episode in JsonRead.Items(calendar))
        {
            var id = JsonRead.PositiveInt(episode, "id");
            var airDateUtc = Instant(JsonRead.String(episode, "airDateUtc"));
            var airDate = DateOnlyText(JsonRead.String(episode, "airDate"));
            if (id is null || (airDateUtc is null && airDate is null))
            {
                continue;
            }

            var series = JsonRead.Object(episode, "series") ?? default;
            var seriesId = JsonRead.PositiveInt(episode, "seriesId") ?? JsonRead.PositiveInt(series, "id");
            var episodeOverview = Overview(JsonRead.String(episode, "overview"));
            var finale = JsonRead.String(episode, "finaleType")?.ToLowerInvariant();
            var anime = string.Equals(JsonRead.String(series, "seriesType"), "anime", StringComparison.OrdinalIgnoreCase);

            events.Add(new CalendarEvent
            {
                Key = "sonarr:episode:" + id.Value.ToString(CultureInfo.InvariantCulture),
                Kind = "episode",
                Title = Text(JsonRead.String(series, "title")) ?? "Unknown series",
                SeriesId = seriesId,
                EpisodeTitle = Text(JsonRead.String(episode, "title")),
                Season = JsonRead.Int(episode, "seasonNumber") is >= 0 and var season ? season : null,
                Episode = JsonRead.Int(episode, "episodeNumber") is >= 0 and var number ? number : null,
                Absolute = anime ? JsonRead.PositiveInt(episode, "absoluteEpisodeNumber") : null,
                AirDateUtc = airDateUtc,
                AirDate = airDate,
                Finale = finale is not null && Finales.Contains(finale) ? finale : null,
                Network = Text(JsonRead.String(series, "network")),

                // Sonarr reports 0 for an episode whose length is not known yet; the series'
                // usual length is the better guess for when it is on air.
                Runtime = JsonRead.PositiveInt(episode, "runtime") ?? JsonRead.PositiveInt(series, "runtime"),
                HasFile = JsonRead.Bool(episode, "hasFile"),
                Monitored = JsonRead.Bool(episode, "monitored"),
                Overview = episodeOverview ?? Overview(JsonRead.String(series, "overview")),
                OverviewIsSeries = episodeOverview is null && JsonRead.String(series, "overview") is { Length: > 0 } ? true : null,
                Certification = Text(JsonRead.String(series, "certification")),
                Genres = Genres(series),
                Image = seriesId is null ? null : "sonarr/" + seriesId.Value.ToString(CultureInfo.InvariantCulture),
                ImageSig = seriesId is null ? null : signer.Sign(ArrKind.Sonarr, seriesId.Value),
                Download = downloads.GetValueOrDefault(id.Value),
                TvdbId = JsonRead.PositiveInt(series, "tvdbId"),
                TmdbId = JsonRead.PositiveInt(series, "tmdbId"),
                ImdbId = Imdb(JsonRead.String(series, "imdbId"))
            });
        }

        return events;
    }

    /// <summary>
    /// Maps Radarr's calendar and queue. A movie appears once for each of its releases that
    /// falls inside the range.
    /// </summary>
    /// <param name="calendar">The answer to <c>calendar</c>.</param>
    /// <param name="queue">The answer to <c>queue/details</c>, or <c>null</c> when it failed.</param>
    /// <param name="signer">Signs artwork URLs.</param>
    /// <param name="from">First day of the range, inclusive.</param>
    /// <param name="to">Last day of the range, inclusive.</param>
    /// <returns>One entry per release day in the range.</returns>
    public static List<CalendarEvent> MapRadarr(JsonElement calendar, JsonElement? queue, ImageSigner signer, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(signer);

        var downloads = Downloads(queue, "movieId");
        var events = new List<CalendarEvent>();
        foreach (var movie in JsonRead.Items(calendar))
        {
            var id = JsonRead.PositiveInt(movie, "id");
            if (id is null)
            {
                continue;
            }

            // Release dates are calendar days. Radarr stores them as UTC midnight, so the day is
            // the date part, never converted to another zone (which would move it a day early
            // west of Greenwich).
            var releases = new Dictionary<string, string>(StringComparer.Ordinal);
            AddRelease(releases, "cinema", JsonRead.String(movie, "inCinemas"));
            AddRelease(releases, "digital", JsonRead.String(movie, "digitalRelease"));
            AddRelease(releases, "physical", JsonRead.String(movie, "physicalRelease"));
            if (releases.Count == 0)
            {
                continue;
            }

            foreach (var (release, date) in releases)
            {
                var day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (day < from || day > to)
                {
                    continue;
                }

                events.Add(new CalendarEvent
                {
                    Key = "radarr:movie:" + id.Value.ToString(CultureInfo.InvariantCulture) + ":" + release,
                    Kind = "movie",
                    Title = Text(JsonRead.String(movie, "title")) ?? "Unknown movie",
                    MovieId = id,
                    Year = JsonRead.PositiveInt(movie, "year"),
                    Release = release,
                    Date = date,
                    Releases = releases,
                    Studio = Text(JsonRead.String(movie, "studio")),
                    Certification = Text(JsonRead.String(movie, "certification")),
                    Available = JsonRead.NullableBool(movie, "isAvailable"),
                    Runtime = JsonRead.PositiveInt(movie, "runtime"),
                    HasFile = JsonRead.Bool(movie, "hasFile"),
                    Monitored = JsonRead.Bool(movie, "monitored"),
                    Overview = Overview(JsonRead.String(movie, "overview")),
                    Genres = Genres(movie),
                    Image = "radarr/" + id.Value.ToString(CultureInfo.InvariantCulture),
                    ImageSig = signer.Sign(ArrKind.Radarr, id.Value),
                    Download = downloads.GetValueOrDefault(id.Value),
                    TmdbId = JsonRead.PositiveInt(movie, "tmdbId"),
                    ImdbId = Imdb(JsonRead.String(movie, "imdbId"))
                });
            }
        }

        return events;
    }

    /// <summary>
    /// Orders entries by when they happen, then by title.
    /// </summary>
    /// <param name="events">The entries.</param>
    /// <returns>The same entries, ordered.</returns>
    public static List<CalendarEvent> Sort(IEnumerable<CalendarEvent> events)
    {
        return events
            .OrderBy(SortInstant)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Season ?? 0)
            .ThenBy(item => item.Episode ?? 0)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Combines a service's queue into one download per episode or movie.
    /// </summary>
    /// <param name="queue">The queue answer.</param>
    /// <param name="idField"><c>episodeId</c> or <c>movieId</c>.</param>
    /// <returns>The downloads by id.</returns>
    internal static Dictionary<int, DownloadInfo> Downloads(JsonElement? queue, string idField)
    {
        return JsonRead.Items(queue)
            .Select(item => (Id: JsonRead.PositiveInt(item, idField), Item: item))
            .Where(entry => entry.Id is not null)
            .GroupBy(entry => entry.Id!.Value, entry => entry.Item)
            .ToDictionary(group => group.Key, group => Combine(group.ToList()));
    }

    /// <summary>
    /// What one queue item is doing, in the page's terms.
    /// </summary>
    /// <param name="item">The queue item.</param>
    /// <returns><c>downloading</c>, <c>queued</c>, <c>paused</c>, <c>importing</c> or <c>warning</c>.</returns>
    internal static string StateOf(JsonElement item)
    {
        var status = JsonRead.String(item, "status")?.ToLowerInvariant();
        var tracked = JsonRead.String(item, "trackedDownloadState")?.ToLowerInvariant();
        var trackedStatus = JsonRead.String(item, "trackedDownloadStatus")?.ToLowerInvariant();

        if (tracked is "importblocked" or "failedpending" or "failed" || status is "failed")
        {
            return "warning";
        }

        if (tracked is "importpending" or "importing" || status is "completed")
        {
            return "importing";
        }

        if (status is "paused")
        {
            return "paused";
        }

        if (status is "queued" or "delay" or "downloadclientunavailable" or "fallback")
        {
            return "queued";
        }

        return status is "warning" || trackedStatus is "warning" or "error" ? "warning" : "downloading";
    }

    private static DownloadInfo Combine(List<JsonElement> items)
    {
        double size = 0;
        double left = 0;
        foreach (var item in items)
        {
            var itemSize = JsonRead.Number(item, "size");
            if (itemSize is > 0)
            {
                size += itemSize.Value;
                left += Math.Clamp(JsonRead.Number(item, "sizeleft") ?? itemSize.Value, 0, itemSize.Value);
            }
        }

        // The most hopeful state wins: one episode of a pack still downloading means the whole
        // entry is downloading, whatever the others are doing.
        var states = items.Select(StateOf).ToList();
        var state = new[] { "downloading", "importing", "queued", "paused", "warning" }.First(candidate => states.Contains(candidate));

        var eta = items
            .Select(item => Instant(JsonRead.String(item, "estimatedCompletionTime")))
            .Where(value => value is not null)
            .Max();

        return new DownloadInfo
        {
            Progress = size > 0 ? (int)Math.Round((1 - (left / size)) * 100, MidpointRounding.AwayFromZero) : null,
            State = state,
            Eta = eta
        };
    }

    private static void AddRelease(Dictionary<string, string> releases, string release, string? value)
    {
        var date = DateOnlyText(value);
        if (date is not null)
        {
            releases[release] = date;
        }
    }

    private static DateTimeOffset SortInstant(CalendarEvent item)
    {
        if (item.AirDateUtc is not null)
        {
            return DateTimeOffset.Parse(item.AirDateUtc, CultureInfo.InvariantCulture);
        }

        var day = item.Date ?? item.AirDate;
        return day is null
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    /// <summary>
    /// An instant as ISO 8601 UTC, or <c>null</c> when it does not parse.
    /// </summary>
    /// <param name="value">The upstream value.</param>
    /// <returns>The normalized instant.</returns>
    internal static string? Instant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        {
            return null;
        }

        return instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The calendar day at the start of an upstream date, as yyyy-MM-dd, or <c>null</c>.
    /// </summary>
    /// <param name="value">A date, or a date and time whose date part is the day.</param>
    /// <returns>The day.</returns>
    internal static string? DateOnlyText(string? value)
    {
        if (value is null || value.Length < 10
            || !DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return null;
        }

        return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// An overview trimmed to a whole word under the length limit.
    /// </summary>
    /// <param name="value">The upstream overview.</param>
    /// <returns>The trimmed overview, or <c>null</c> when there is none.</returns>
    internal static string? Overview(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length <= MaxOverviewLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', MaxOverviewLength - 1);
        return text[..(cut > MaxOverviewLength / 2 ? cut : MaxOverviewLength - 1)].TrimEnd() + "…";
    }

    private static string? Text(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    private static string[]? Genres(JsonElement owner)
    {
        var genres = JsonRead.Strings(owner, "genres").Select(genre => genre.Trim()).Take(3).ToArray();
        return genres.Length == 0 ? null : genres;
    }

    private static string? Imdb(string? value) => value is not null && ImdbPattern().IsMatch(value) ? value : null;

    [GeneratedRegex(@"^tt\d{1,10}$")]
    private static partial Regex ImdbPattern();
}
