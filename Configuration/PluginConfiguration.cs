using System.Xml.Serialization;
using Jellyfin.Plugin.ArrCalendar.Arr;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArrCalendar.Configuration;

/// <summary>
/// Arr Calendar plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Timeout used when none, or a nonsensical one, is configured.
    /// </summary>
    public const int DefaultTimeoutSeconds = 15;

    /// <summary>
    /// Longest timeout the plugin will wait on Sonarr or Radarr.
    /// </summary>
    public const int MaximumTimeoutSeconds = 120;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Enabled = true;
        SonarrUrl = string.Empty;
        SonarrApiKey = string.Empty;
        RadarrUrl = string.Empty;
        RadarrApiKey = string.Empty;
        IncludeUnmonitored = false;
        RequestTimeoutSeconds = DefaultTimeoutSeconds;
    }

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin Web shows the Calendar.
    /// </summary>
    /// <remarks>
    /// Switching it off removes the page's loader from JavaScript Injector, so browsers stop
    /// loading the page on their next refresh, and the calendar endpoint refuses to answer.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets Sonarr's address as Jellyfin reaches it, including any URL base.
    /// </summary>
    public string SonarrUrl { get; set; }

    /// <summary>
    /// Gets or sets Sonarr's API key.
    /// </summary>
    /// <remarks>
    /// Only used when the deployment does not supply the key through the environment. A key
    /// stored here is readable by any caller that can reach Jellyfin's plugin configuration
    /// endpoint; see <c>docs/DESIGN.md</c> for the environment-supplied alternative.
    /// </remarks>
    public string SonarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets Radarr's address as Jellyfin reaches it, including any URL base.
    /// </summary>
    public string RadarrUrl { get; set; }

    /// <summary>
    /// Gets or sets Radarr's API key.
    /// </summary>
    /// <remarks>
    /// Same storage caveat as <see cref="SonarrApiKey"/>.
    /// </remarks>
    public string RadarrApiKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether unmonitored episodes and movies are shown too.
    /// </summary>
    public bool IncludeUnmonitored { get; set; }

    /// <summary>
    /// Gets or sets how long to wait for Sonarr or Radarr, in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets a value indicating whether Sonarr's API key comes from the environment.
    /// </summary>
    /// <remarks>
    /// Purely informational, for the settings page, which makes the field inert when this is
    /// <c>true</c>. Excluded from XML so it never round-trips to disk.
    /// </remarks>
    [XmlIgnore]
    public bool SonarrApiKeyFromEnvironment => ArrSecretSource.Default.GetEnvironmentApiKey(ArrKind.Sonarr) is not null;

    /// <summary>
    /// Gets a value indicating whether Radarr's API key comes from the environment.
    /// </summary>
    [XmlIgnore]
    public bool RadarrApiKeyFromEnvironment => ArrSecretSource.Default.GetEnvironmentApiKey(ArrKind.Radarr) is not null;

    /// <summary>
    /// Gets the configured address for one service.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <returns>The address, possibly empty.</returns>
    public string GetUrl(ArrKind kind) => (kind == ArrKind.Sonarr ? SonarrUrl : RadarrUrl) ?? string.Empty;

    /// <summary>
    /// Gets the API key stored in this configuration for one service.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <returns>The stored key, possibly empty.</returns>
    public string GetStoredApiKey(ArrKind kind) => (kind == ArrKind.Sonarr ? SonarrApiKey : RadarrApiKey) ?? string.Empty;

    /// <summary>
    /// Gets the configured timeout clamped to a safe range.
    /// </summary>
    /// <returns>The clamped timeout.</returns>
    public TimeSpan GetRequestTimeout()
    {
        var seconds = RequestTimeoutSeconds <= 0 ? DefaultTimeoutSeconds : RequestTimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Min(seconds, MaximumTimeoutSeconds));
    }
}
