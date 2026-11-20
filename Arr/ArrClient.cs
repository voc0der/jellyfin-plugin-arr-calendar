using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using Jellyfin.Plugin.ArrCalendar.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// An image read from a service's media cover endpoint.
/// </summary>
/// <param name="Bytes">The image.</param>
/// <param name="ContentType">One of the allowlisted image types.</param>
public sealed record ArrImage(byte[] Bytes, string ContentType);

/// <summary>
/// Reads from Sonarr and Radarr.
/// </summary>
public interface IArrClient
{
    /// <summary>
    /// Gets a JSON document from the service's API.
    /// </summary>
    /// <param name="connection">The service.</param>
    /// <param name="path">A path under <c>/api/v3/</c>.</param>
    /// <param name="query">Query values.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed document's root, detached from the document.</returns>
    Task<JsonElement> GetJsonAsync(
        ArrConnection connection,
        string path,
        IEnumerable<KeyValuePair<string, string>>? query,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets an image from the service's API, or <c>null</c> when it has none.
    /// </summary>
    /// <param name="connection">The service.</param>
    /// <param name="path">A path under <c>/api/v3/</c>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image, or <c>null</c>.</returns>
    Task<ArrImage?> GetImageAsync(ArrConnection connection, string path, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ArrClient : IArrClient
{
    /// <summary>
    /// Most bytes read from one JSON answer.
    /// </summary>
    /// <remarks>
    /// The body is buffered before it is parsed, so without a bound a misbehaving upstream could
    /// spend the Jellyfin server's memory. A two-month Sonarr calendar with each series embedded
    /// is around a megabyte even for busy libraries.
    /// </remarks>
    internal const int MaxJsonBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Most bytes read from one image. Resized covers are tens of kilobytes.
    /// </summary>
    internal const int MaxImageBytes = 5 * 1024 * 1024;

    private const int ReadBufferSize = 8192;

    private static readonly string[] AllowedImageTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"];

    private readonly HttpClient _httpClient;
    private readonly ILogger<ArrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClient"/> class.
    /// </summary>
    /// <param name="httpClient">The client, built with <see cref="CreateHandler"/>.</param>
    /// <param name="logger">Logger.</param>
    public ArrClient(HttpClient httpClient, ILogger<ArrClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds the outbound handler. Its settings are security policy rather than tuning, so they
    /// are asserted directly by the tests.
    /// </summary>
    /// <returns>The handler.</returns>
    internal static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            // The API key travels in X-Api-Key, and .NET only strips Authorization across a
            // cross-origin redirect: a custom header goes to whatever host the redirect names.
            // Nothing this plugin calls redirects, so refusing them costs nothing.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,

            // A singleton client otherwise pins the first DNS answer for the life of the process,
            // so a container that moves stays unreachable until Jellyfin restarts.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),

            // Self-signed certificates are accepted on private and loopback IP addresses only;
            // host names are always verified. See LocalAddress.
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = LocalAddress.ValidateCertificate
            }
        };
    }

    /// <summary>
    /// Builds the client used in production.
    /// </summary>
    /// <returns>The client.</returns>
    internal static HttpClient CreateHttpClient()
    {
        return new HttpClient(CreateHandler(), disposeHandler: true)
        {
            // Per-request deadlines come from the configured timeout. The client's own default
            // would silently cap a longer configured value.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc />
    public async Task<JsonElement> GetJsonAsync(
        ArrConnection connection,
        string path,
        IEnumerable<KeyValuePair<string, string>>? query,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var request = CreateRequest(connection, connection.Build(path, query), "application/json");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var name = connection.Kind.DisplayName();

        using var response = await SendAsync(connection, request, timeout, timeoutSource.Token, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(connection, request, response);

        var body = await ReadBoundedAsync(response, MaxJsonBytes, name, timeout, timeoutSource.Token, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Every /api/v3 endpoint answers JSON. Anything else on a success status means the
            // request did not reach the service: a login page or error page from something in
            // front of it, which is never passed on.
            throw new ArrApiException(HttpStatusCode.BadGateway, name + " answered with something other than JSON. Check the address, and that nothing in front of " + name + " asks for a login.");
        }
    }

    /// <inheritdoc />
    public async Task<ArrImage?> GetImageAsync(ArrConnection connection, string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var request = CreateRequest(connection, connection.Build(path), "image/*");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var name = connection.Kind.DisplayName();

        using var response = await SendAsync(connection, request, timeout, timeoutSource.Token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(connection, request, response);
        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (contentType is null || !AllowedImageTypes.Contains(contentType))
        {
            return null;
        }

        var bytes = await ReadBoundedAsync(response, MaxImageBytes, name, timeout, timeoutSource.Token, cancellationToken).ConfigureAwait(false);
        return bytes.Length == 0 ? null : new ArrImage(bytes, contentType);
    }

    private static HttpRequestMessage CreateRequest(ArrConnection connection, Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.TryAddWithoutValidation("X-Api-Key", connection.ApiKey);
        return request;
    }

    private static string Seconds(TimeSpan timeout)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        return seconds.ToString(CultureInfo.InvariantCulture) + (seconds == 1 ? " second" : " seconds");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int limit,
        string name,
        TimeSpan timeout,
        CancellationToken token,
        CancellationToken callerToken)
    {
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new ArrApiException(HttpStatusCode.BadGateway, name + " answered with more data than the calendar accepts.");
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var accumulator = new MemoryStream();
                var buffer = new byte[ReadBufferSize];
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (accumulator.Length + read > limit)
                    {
                        throw new ArrApiException(HttpStatusCode.BadGateway, name + " answered with more data than the calendar accepts.");
                    }

                    accumulator.Write(buffer, 0, read);
                }

                return accumulator.ToArray();
            }
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        {
            throw new ArrConnectionException(name + " did not answer within " + Seconds(timeout) + ".", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ArrConnectionException(name + " stopped answering part way through.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        ArrConnection connection,
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken token,
        CancellationToken callerToken)
    {
        var name = connection.Kind.DisplayName();
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        {
            throw new ArrConnectionException(name + " did not answer within " + Seconds(timeout) + ".", ex);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new ArrConnectionException(name + "'s HTTPS certificate is not trusted by the Jellyfin server. Self-signed certificates are accepted only when the address is a private IP, such as https://192.168.1.20.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ArrConnectionException(name + " could not be reached at " + connection.ApiRoot.GetLeftPart(UriPartial.Authority) + ".", ex);
        }
    }

    private void EnsureSuccess(ArrConnection connection, HttpRequestMessage request, HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var name = connection.Kind.DisplayName();
        var status = (int)response.StatusCode;
        _logger.LogWarning(
            "{Service} answered HTTP {StatusCode} for {Path}",
            name,
            status,
            LogSanitizer.ForLog(request.RequestUri?.AbsolutePath));

        // Upstream bodies are never read into a message: an error page can hold anything, and
        // the API key is never echoed back.
        if (status is >= 300 and < 400)
        {
            throw new ArrConfigurationException(name + " answered with a redirect. Check the address, including http or https and any URL base.");
        }

        throw status switch
        {
            401 or 403 => new ArrApiException(HttpStatusCode.Unauthorized, name + " rejected the API key."),
            404 => new ArrApiException(HttpStatusCode.NotFound, name + " has no API at this address. Check the port and any URL base."),
            _ => new ArrApiException(response.StatusCode, name + " answered HTTP " + status.ToString(CultureInfo.InvariantCulture) + ".")
        };
    }
}
