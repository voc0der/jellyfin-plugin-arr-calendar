using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// A validated address and API key for one Sonarr or Radarr.
/// </summary>
/// <remarks>
/// Every URL the plugin requests is composed here, under the service's <c>/api/v3/</c> root,
/// and checked to still be under it. The API key travels only to that root: the HTTP client
/// refuses redirects, because .NET carries a custom header such as <c>X-Api-Key</c> to whatever
/// host a redirect names.
/// </remarks>
public sealed partial class ArrConnection
{
    private ArrConnection(ArrKind kind, Uri apiRoot, string apiKey)
    {
        Kind = kind;
        ApiRoot = apiRoot;
        ApiKey = apiKey;
        Fingerprint = kind.Id() + "|" + apiRoot.AbsoluteUri + "|"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..16];
    }

    /// <summary>
    /// Gets the service.
    /// </summary>
    public ArrKind Kind { get; }

    /// <summary>
    /// Gets the service's API root, always ending in <c>/api/v3/</c>.
    /// </summary>
    public Uri ApiRoot { get; }

    /// <summary>
    /// Gets a value that changes whenever the address or the key does, for cache keys. Holds a
    /// short hash of the key, never the key.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Gets the API key.
    /// </summary>
    internal string ApiKey { get; }

    /// <summary>
    /// Validates an address and key.
    /// </summary>
    /// <param name="kind">The service.</param>
    /// <param name="url">The address as an administrator typed it.</param>
    /// <param name="apiKey">The API key.</param>
    /// <returns>The connection.</returns>
    /// <exception cref="ArrConfigurationException">The address or key cannot be used.</exception>
    public static ArrConnection Create(ArrKind kind, string? url, string? apiKey)
    {
        var apiRoot = ParseApiRoot(kind, url);
        var key = apiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            throw new ArrConfigurationException(kind.DisplayName() + "'s API key is not set.");
        }

        // A key reaches an HTTP header verbatim, so anything outside visible ASCII could split
        // or corrupt it. Real keys are 32 hexadecimal characters.
        if (!ApiKeyPattern().IsMatch(key))
        {
            throw new ArrConfigurationException(kind.DisplayName() + "'s API key must be one line of letters and numbers, as shown under Settings, General.");
        }

        return new ArrConnection(kind, apiRoot, key);
    }

    /// <summary>
    /// Resolves an address to the service's <c>/api/v3/</c> root.
    /// </summary>
    /// <param name="kind">The service, for messages.</param>
    /// <param name="url">The address, with or without a URL base or a trailing <c>/api/v3</c>.</param>
    /// <returns>The API root.</returns>
    /// <exception cref="ArrConfigurationException">The address cannot be used.</exception>
    public static Uri ParseApiRoot(ArrKind kind, string? url)
    {
        var name = kind.DisplayName();
        var trimmed = url?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArrConfigurationException(name + "'s address is not set.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArrConfigurationException("Enter " + name + "'s address starting with http:// or https://, for example http://" + kind.Id() + ":" + (kind == ArrKind.Sonarr ? "8989" : "7878") + ".");
        }

        // Credentials in the address would go out with every request and into any log line
        // that names it. A query or fragment means something was pasted that is not a base URL.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArrConfigurationException(name + "'s address must not contain a user name or password.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArrConfigurationException(name + "'s address must not contain ? or #.");
        }

        var path = uri.AbsolutePath.Trim('/');
        foreach (var suffix in new[] { "api/v3", "api" })
        {
            if (path.Equals(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = string.Empty;
                break;
            }

            if (path.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^(suffix.Length + 1)];
                break;
            }
        }

        var builder = new UriBuilder(uri)
        {
            Path = path.Length == 0 ? "api/v3/" : path + "/api/v3/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    /// <summary>
    /// Builds an absolute API URL.
    /// </summary>
    /// <param name="relativePath">A path under <c>/api/v3/</c>, such as <c>calendar</c>.</param>
    /// <param name="query">Query values, escaped here.</param>
    /// <returns>The URL.</returns>
    /// <exception cref="ArrConfigurationException">The path would leave the API root.</exception>
    public Uri Build(string relativePath, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var resolved = new Uri(ApiRoot, relativePath.TrimStart('/'));

        // Uri resolution honours dot segments, so this is what makes leaving the API root
        // impossible rather than merely unlikely, whatever a caller passed.
        if (!ApiRoot.IsBaseOf(resolved) || resolved.AbsolutePath.Contains("/../", StringComparison.Ordinal))
        {
            throw new ArrConfigurationException("Refusing to build a URL outside " + Kind.DisplayName() + "'s API.");
        }

        if (query is null)
        {
            return resolved;
        }

        var parts = query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)).ToArray();
        return parts.Length == 0 ? resolved : new UriBuilder(resolved) { Query = string.Join('&', parts) }.Uri;
    }

    /// <inheritdoc />
    public override string ToString() => Kind.DisplayName() + " at " + ApiRoot.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^[\x21-\x7E]{1,256}$")]
    private static partial Regex ApiKeyPattern();
}
