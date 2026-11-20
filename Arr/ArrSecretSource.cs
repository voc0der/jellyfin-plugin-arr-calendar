using Jellyfin.Plugin.ArrCalendar.Configuration;

namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// Supplies the Sonarr and Radarr API keys, preferring the deployment environment.
/// </summary>
/// <remarks>
/// Both keys are full administrator credentials for their service. One stored in plugin
/// configuration is written in plaintext to Jellyfin's configuration XML and is readable back
/// through <c>GET /Plugins/{id}/Configuration</c>. An operator who sets the environment
/// variables instead keeps the key out of both. Each key may be given directly or as a path to
/// a file holding it; the file form wins, and only its path ever reaches a log.
/// <para>
/// The names are deliberately not prefixed <c>JELLYFIN_</c>: Jellyfin logs every environment
/// variable starting with JELLYFIN_, DOTNET_ or ASPNETCORE_, with its value, at startup.
/// </para>
/// <para>
/// Values are read on each call, so rotating a file takes effect without any plugin action.
/// </para>
/// </remarks>
public sealed class ArrSecretSource
{
    /// <summary>Environment variable holding Sonarr's API key.</summary>
    public const string SonarrApiKeyVariable = "ARR_CALENDAR_SONARR_API_KEY";

    /// <summary>Environment variable holding a path to a file containing Sonarr's API key.</summary>
    public const string SonarrApiKeyFileVariable = "ARR_CALENDAR_SONARR_API_KEY_FILE";

    /// <summary>Environment variable holding Radarr's API key.</summary>
    public const string RadarrApiKeyVariable = "ARR_CALENDAR_RADARR_API_KEY";

    /// <summary>Environment variable holding a path to a file containing Radarr's API key.</summary>
    public const string RadarrApiKeyFileVariable = "ARR_CALENDAR_RADARR_API_KEY_FILE";

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, string> _readAllText;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrSecretSource"/> class.
    /// </summary>
    public ArrSecretSource()
        : this(Environment.GetEnvironmentVariable, File.ReadAllText)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrSecretSource"/> class with explicit
    /// environment and file accessors, for testing.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <param name="readAllText">Reads the full contents of a file.</param>
    public ArrSecretSource(Func<string, string?> getEnvironmentVariable, Func<string, string> readAllText)
    {
        _getEnvironmentVariable = getEnvironmentVariable ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
        _readAllText = readAllText ?? throw new ArgumentNullException(nameof(readAllText));
    }

    /// <summary>
    /// Gets a process-wide instance for callers that cannot take one by injection.
    /// </summary>
    /// <remarks>
    /// Only <see cref="PluginConfiguration"/> needs this, because Jellyfin constructs
    /// configuration objects itself. The source is stateless, so two instances behave the same.
    /// </remarks>
    public static ArrSecretSource Default { get; } = new();

    /// <summary>
    /// Gets the API key the environment supplies for a service, or <c>null</c>.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <returns>The key, or <c>null</c> when the environment supplies none.</returns>
    public string? GetEnvironmentApiKey(ArrKind kind)
    {
        return kind == ArrKind.Sonarr
            ? Read(SonarrApiKeyFileVariable, SonarrApiKeyVariable)
            : Read(RadarrApiKeyFileVariable, RadarrApiKeyVariable);
    }

    /// <summary>
    /// Resolves the API key to present to a service, preferring the environment.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <param name="configuration">The configuration to fall back to.</param>
    /// <returns>The key, or <c>null</c> when neither source supplies one.</returns>
    public string? ResolveApiKey(ArrKind kind, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var fromEnvironment = GetEnvironmentApiKey(kind);
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var stored = configuration.GetStoredApiKey(kind).Trim();
        return stored.Length == 0 ? null : stored;
    }

    /// <summary>
    /// Gets a value indicating whether a service has both an address and a key.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns><c>true</c> when the service can be asked anything.</returns>
    public bool IsConfigured(ArrKind kind, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return !string.IsNullOrWhiteSpace(configuration.GetUrl(kind)) && ResolveApiKey(kind, configuration) is not null;
    }

    private string? Read(string fileVariable, string valueVariable)
    {
        var path = _getEnvironmentVariable(fileVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var fromFile = _readAllText(path).Trim();
                return fromFile.Length == 0 ? null : fromFile;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        var fromEnvironment = _getEnvironmentVariable(valueVariable)?.Trim();
        return string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
    }
}
