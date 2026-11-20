namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// The services the calendar reads.
/// </summary>
public enum ArrKind
{
    /// <summary>Sonarr, for episodes.</summary>
    Sonarr,

    /// <summary>Radarr, for movies.</summary>
    Radarr
}

/// <summary>
/// Names for <see cref="ArrKind"/> as they appear in URLs, JSON and messages.
/// </summary>
public static class ArrKindNames
{
    /// <summary>
    /// Gets the lowercase identifier used in URLs and JSON.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <returns><c>sonarr</c> or <c>radarr</c>.</returns>
    public static string Id(this ArrKind kind) => kind == ArrKind.Sonarr ? "sonarr" : "radarr";

    /// <summary>
    /// Gets the display name, which is also the <c>appName</c> the service reports.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <returns><c>Sonarr</c> or <c>Radarr</c>.</returns>
    public static string DisplayName(this ArrKind kind) => kind == ArrKind.Sonarr ? "Sonarr" : "Radarr";

    /// <summary>
    /// Parses a lowercase identifier, exactly.
    /// </summary>
    /// <param name="value">The identifier.</param>
    /// <param name="kind">The service, when recognised.</param>
    /// <returns><c>true</c> when the value names a service.</returns>
    public static bool TryParse(string? value, out ArrKind kind)
    {
        switch (value)
        {
            case "sonarr":
                kind = ArrKind.Sonarr;
                return true;
            case "radarr":
                kind = ArrKind.Radarr;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
