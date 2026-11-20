using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ArrCalendar.Models;

/// <summary>
/// What the calendar page shows for a range of days.
/// </summary>
/// <remarks>
/// Built field by field from Sonarr's and Radarr's answers, never passed through: their
/// answers also carry server paths, quality profiles and tags, none of which reach a browser.
/// Names are fixed with <see cref="JsonPropertyNameAttribute"/>, so the page's contract does not
/// depend on which case profile Jellyfin's formatter would apply.
/// </remarks>
public sealed class CalendarResponse
{
    /// <summary>Gets or sets the first day asked for, as yyyy-MM-dd.</summary>
    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    /// <summary>Gets or sets the last day asked for, as yyyy-MM-dd.</summary>
    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;

    /// <summary>Gets or sets how each service fared.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<SourceStatus> Sources { get; set; } = [];

    /// <summary>Gets or sets the episodes and releases, in time order.</summary>
    [JsonPropertyName("events")]
    public IReadOnlyList<CalendarEvent> Events { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the page says "Ready" and "Pending" instead of
    /// "Downloaded" and "Downloading". Sent with every answer, so the page never shows one
    /// wording and then the other.
    /// </summary>
    [JsonPropertyName("dontSpecifyMethod")]
    public bool DontSpecifyMethod { get; set; }
}

/// <summary>
/// How one service fared for a calendar request.
/// </summary>
public sealed class SourceStatus
{
    /// <summary>Gets or sets the service, <c>sonarr</c> or <c>radarr</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>ok</c>, <c>unconfigured</c> or <c>error</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets why it failed: <c>unreachable</c>, <c>unauthorized</c>, <c>misconfigured</c>
    /// or <c>failed</c>.
    /// </summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>Gets or sets a message for an administrator. Never holds a key or an upstream body.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
