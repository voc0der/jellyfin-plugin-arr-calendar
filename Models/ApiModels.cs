using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ArrCalendar.Models;

/// <summary>
/// JSON error answer from this plugin's endpoints.
/// </summary>
/// <param name="statusCode">The HTTP status.</param>
/// <param name="error">A stable identifier the page maps to its own wording.</param>
/// <param name="message">A displayable message.</param>
public sealed class ErrorResponse(int statusCode, string error, string message)
{
    /// <summary>Gets the HTTP status.</summary>
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; } = statusCode;

    /// <summary>Gets a stable identifier.</summary>
    [JsonPropertyName("error")]
    public string Error { get; } = error;

    /// <summary>Gets a displayable message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; } = message;
}

/// <summary>
/// The item in the signed-in user's Jellyfin library that matches a calendar entry.
/// </summary>
public sealed class LibraryItemResponse
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what matched: <c>episode</c>, <c>series</c> when the episode itself is not in
    /// the library yet, or <c>movie</c>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;
}

/// <summary>
/// A connection test from the settings page, with the values as typed, saved or not.
/// </summary>
public sealed class TestConnectionRequest
{
    /// <summary>Gets or sets <c>sonarr</c> or <c>radarr</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Gets or sets the address to test.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the API key to test. Left empty, the saved key is used, but only for the
    /// saved address.
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

/// <summary>
/// The result of a connection test.
/// </summary>
public sealed class TestConnectionResponse
{
    /// <summary>Gets or sets a value indicating whether the service answered as itself and accepted the key.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>Gets or sets the version the service reported.</summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>Gets or sets a message for the administrator.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Whether the Calendar is reaching Jellyfin Web, as the settings page shows it.
/// </summary>
public sealed class RegistrationStatusResponse
{
    /// <summary>Gets or sets a value indicating whether the Calendar is switched on.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the outcome of the last JavaScript Injector registration.</summary>
    [JsonPropertyName("loaderState")]
    public string LoaderState { get; set; } = string.Empty;

    /// <summary>Gets or sets why the last registration failed, when it did.</summary>
    [JsonPropertyName("loaderDetail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LoaderDetail { get; set; }

    /// <summary>Gets or sets the plugin version browsers load.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Seerr Integration is loaded and active.</summary>
    [JsonPropertyName("seerrIntegrationActive")]
    public bool SeerrIntegrationActive { get; set; }

    /// <summary>
    /// Gets or sets whether Seerr Integration's Requests page is switched on, when it is active and
    /// its settings could be read.
    /// </summary>
    [JsonPropertyName("seerrIntegrationRequestsEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SeerrIntegrationRequestsEnabled { get; set; }

    /// <summary>Gets or sets Sonarr's configuration state.</summary>
    [JsonPropertyName("sonarr")]
    public SourceConfigurationStatus Sonarr { get; set; } = new();

    /// <summary>Gets or sets Radarr's configuration state.</summary>
    [JsonPropertyName("radarr")]
    public SourceConfigurationStatus Radarr { get; set; } = new();
}

/// <summary>
/// One service's configuration state, without contacting it.
/// </summary>
public sealed class SourceConfigurationStatus
{
    /// <summary>Gets or sets a value indicating whether an address and a key are both set.</summary>
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    /// <summary>Gets or sets where the key comes from: <c>environment</c>, <c>configuration</c> or <c>none</c>.</summary>
    [JsonPropertyName("apiKeySource")]
    public string ApiKeySource { get; set; } = "none";

    /// <summary>Gets or sets why the saved address or key cannot be used, when it cannot.</summary>
    [JsonPropertyName("problem")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Problem { get; set; }
}
