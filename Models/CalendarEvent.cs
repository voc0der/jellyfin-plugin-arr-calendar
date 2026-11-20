using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ArrCalendar.Models;

/// <summary>
/// One episode airing, or one release of a movie, on the calendar.
/// </summary>
/// <remarks>
/// Facts only. Whether something is on air, missing or upcoming depends on the time the page
/// is looked at, so the page works that out itself and keeps it current while it stays open.
/// </remarks>
public sealed class CalendarEvent
{
    /// <summary>Gets or sets a key unique within a response, such as <c>sonarr:episode:12</c>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>episode</c> or <c>movie</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the series title for an episode, the movie title for a movie.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets Sonarr's series id, which groups episodes that drop together.</summary>
    [JsonPropertyName("seriesId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SeriesId { get; set; }

    /// <summary>Gets or sets the episode's title.</summary>
    [JsonPropertyName("episodeTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EpisodeTitle { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("season")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number within the season.</summary>
    [JsonPropertyName("episode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Episode { get; set; }

    /// <summary>Gets or sets the absolute episode number, for anime.</summary>
    [JsonPropertyName("absolute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Absolute { get; set; }

    /// <summary>Gets or sets when the episode airs, as an ISO 8601 UTC instant.</summary>
    [JsonPropertyName("airDateUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AirDateUtc { get; set; }

    /// <summary>Gets or sets the day it airs in its network's own time zone, as yyyy-MM-dd.</summary>
    [JsonPropertyName("airDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AirDate { get; set; }

    /// <summary>Gets or sets <c>series</c>, <c>season</c> or <c>midseason</c> for a finale.</summary>
    [JsonPropertyName("finale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Finale { get; set; }

    /// <summary>Gets or sets the network.</summary>
    [JsonPropertyName("network")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Network { get; set; }

    /// <summary>Gets or sets Radarr's movie id.</summary>
    [JsonPropertyName("movieId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MovieId { get; set; }

    /// <summary>Gets or sets the movie's year.</summary>
    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Year { get; set; }

    /// <summary>Gets or sets which release this is: <c>cinema</c>, <c>digital</c> or <c>physical</c>.</summary>
    [JsonPropertyName("release")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Release { get; set; }

    /// <summary>Gets or sets the release day, as yyyy-MM-dd. Release dates have no time of day.</summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Date { get; set; }

    /// <summary>Gets or sets every known release day of the movie, keyed by release.</summary>
    [JsonPropertyName("releases")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Releases { get; set; }

    /// <summary>Gets or sets the studio.</summary>
    [JsonPropertyName("studio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Studio { get; set; }

    /// <summary>Gets or sets the certification, such as PG-13 or TV-14.</summary>
    [JsonPropertyName("certification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Certification { get; set; }

    /// <summary>Gets or sets whether Radarr considers the movie available to download.</summary>
    [JsonPropertyName("available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Available { get; set; }

    /// <summary>Gets or sets the running time in minutes.</summary>
    [JsonPropertyName("runtime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Runtime { get; set; }

    /// <summary>Gets or sets whether the file is downloaded.</summary>
    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    /// <summary>Gets or sets whether the service is watching for it.</summary>
    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    /// <summary>Gets or sets the overview, trimmed.</summary>
    [JsonPropertyName("overview")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="Overview"/> describes the series,
    /// because the episode has none yet. Absent otherwise.
    /// </summary>
    [JsonPropertyName("overviewIsSeries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OverviewIsSeries { get; set; }

    /// <summary>Gets or sets up to three genres.</summary>
    [JsonPropertyName("genres")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Genres { get; set; }

    /// <summary>Gets or sets the artwork path, such as <c>sonarr/12</c>.</summary>
    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Image { get; set; }

    /// <summary>Gets or sets the signature that lets the browser load <see cref="Image"/>.</summary>
    [JsonPropertyName("imageSig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageSig { get; set; }

    /// <summary>Gets or sets the download in progress, when there is one.</summary>
    [JsonPropertyName("download")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DownloadInfo? Download { get; set; }

    /// <summary>Gets or sets the series' or movie's TVDB id.</summary>
    [JsonPropertyName("tvdbId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the series' or movie's TMDB id.</summary>
    [JsonPropertyName("tmdbId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the series' or movie's IMDb id.</summary>
    [JsonPropertyName("imdbId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImdbId { get; set; }
}

/// <summary>
/// A download in progress, from the service's queue.
/// </summary>
public sealed class DownloadInfo
{
    /// <summary>Gets or sets how much is done, 0 to 100, or <c>null</c> before the size is known.</summary>
    [JsonPropertyName("progress")]
    public int? Progress { get; set; }

    /// <summary>
    /// Gets or sets <c>downloading</c>, <c>queued</c>, <c>paused</c>, <c>importing</c> or
    /// <c>warning</c>.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "downloading";

    /// <summary>Gets or sets when the download client expects to finish, as an ISO 8601 UTC instant.</summary>
    [JsonPropertyName("eta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Eta { get; set; }
}
