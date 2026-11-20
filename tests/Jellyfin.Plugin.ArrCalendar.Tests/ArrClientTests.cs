using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Jellyfin.Plugin.ArrCalendar.Arr;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

public class ArrClientTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ArrConnection Sonarr(string url = "http://sonarr:8989") => ArrConnection.Create(ArrKind.Sonarr, url, Key);

    private static (ArrClient Client, StubHandler Handler, ListLogger<ArrClient> Logger) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var logger = new ListLogger<ArrClient>();
        return (new ArrClient(new HttpClient(handler), logger), handler, logger);
    }

    [Fact]
    public async Task GetJsonAsync_SendsTheKeyInItsHeaderToTheApiRootOnly()
    {
        var (client, handler, _) = Create(_ => StubHandler.Json(Fixtures.Text("sonarr-status.json")));

        var status = await client.GetJsonAsync(Sonarr("http://sonarr:8989/base"), "system/status", [new("a", "b")], Timeout, CancellationToken.None);

        Assert.Equal("Sonarr", status.GetProperty("appName").GetString());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://sonarr:8989/base/api/v3/system/status?a=b", request.RequestUri!.AbsoluteUri);
        Assert.Equal(Key, Assert.Single(request.Headers.GetValues("X-Api-Key")));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, "rejected the API key")]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, "rejected the API key")]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound, "no API at this address")]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, "answered HTTP 500")]
    [InlineData(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, "answered HTTP 502")]
    public async Task GetJsonAsync_ExplainsErrorStatusesWithoutRelayingTheBody(HttpStatusCode upstream, HttpStatusCode reported, string message)
    {
        var (client, _, logger) = Create(_ => StubHandler.Json("{\"message\":\"secret-detail " + Key + "\"}", upstream));

        var error = await Assert.ThrowsAsync<ArrApiException>(() => client.GetJsonAsync(Sonarr(), "calendar", null, Timeout, CancellationToken.None));

        Assert.Equal(reported, error.StatusCode);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-detail", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(Key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task GetJsonAsync_TreatsARedirectAsAWrongAddress(HttpStatusCode status)
    {
        var (client, _, _) = Create(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        });

        var error = await Assert.ThrowsAsync<ArrConfigurationException>(() => client.GetJsonAsync(Sonarr(), "calendar", null, Timeout, CancellationToken.None));
        Assert.Contains("redirect", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJsonAsync_RefusesASuccessThatIsNotJson()
    {
        var (client, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in to Authelia</html>", Encoding.UTF8, "text/html") });

        var error = await Assert.ThrowsAsync<ArrApiException>(() => client.GetJsonAsync(Sonarr(), "calendar", null, Timeout, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Contains("something other than JSON", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Authelia", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJsonAsync_RefusesAnAnswerLargerThanTheBound()
    {
        var (client, _, _) = Create(_ => StubHandler.Json("[" + new string(' ', ArrClient.MaxJsonBytes) + "]"));

        var error = await Assert.ThrowsAsync<ArrApiException>(() => client.GetJsonAsync(Sonarr(), "calendar", null, Timeout, CancellationToken.None));
        Assert.Contains("more data than the calendar accepts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJsonAsync_ReportsAnUnreachableServiceWithItsAddressButNotItsKey()
    {
        var (client, _, _) = Create(_ => throw new HttpRequestException("Connection refused"));

        var error = await Assert.ThrowsAsync<ArrConnectionException>(() => client.GetJsonAsync(Sonarr(), "calendar", null, Timeout, CancellationToken.None));

        Assert.Equal("Sonarr could not be reached at http://sonarr:8989.", error.Message);
    }

    [Fact]
    public async Task GetJsonAsync_TurnsItsOwnTimeoutIntoAConnectionError()
    {
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return StubHandler.Json("[]");
        });
        var client = new ArrClient(new HttpClient(handler), new ListLogger<ArrClient>());

        var error = await Assert.ThrowsAsync<ArrConnectionException>(() => client.GetJsonAsync(Sonarr(), "calendar", null, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.Contains("did not answer within 1 second.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJsonAsync_LetsTheCallersCancellationThrough()
    {
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return StubHandler.Json("[]");
        });
        var client = new ArrClient(new HttpClient(handler), new ListLogger<ArrClient>());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetJsonAsync(Sonarr(), "calendar", null, TimeSpan.FromSeconds(10), cancel.Token));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public async Task GetImageAsync_ReturnsAllowlistedImages(string type)
    {
        var (client, handler, _) = Create(_ => StubHandler.Bytes([1, 2, 3], type));

        var image = await client.GetImageAsync(Sonarr(), "mediacover/2/poster-250.jpg", Timeout, CancellationToken.None);

        Assert.NotNull(image);
        Assert.Equal(type, image.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Bytes);
        Assert.Equal(Key, Assert.Single(Assert.Single(handler.Requests).Headers.GetValues("X-Api-Key")));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    public async Task GetImageAsync_RefusesAnythingThatIsNotAPlainImage(string type)
    {
        var (client, _, _) = Create(_ => StubHandler.Bytes(Encoding.UTF8.GetBytes("<svg onload=alert(1)>"), type));

        Assert.Null(await client.GetImageAsync(Sonarr(), "mediacover/2/poster-250.jpg", Timeout, CancellationToken.None));
    }

    [Fact]
    public async Task GetImageAsync_AnswersNothingForAMissingImageAndThrowsForARefusal()
    {
        var missing = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).Client;
        Assert.Null(await missing.GetImageAsync(Sonarr(), "mediacover/9/poster-250.jpg", Timeout, CancellationToken.None));

        var refused = Create(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)).Client;
        await Assert.ThrowsAsync<ArrApiException>(() => refused.GetImageAsync(Sonarr(), "mediacover/9/poster-250.jpg", Timeout, CancellationToken.None));
    }

    [Fact]
    public void Handler_NeverFollowsRedirectsAndValidatesCertificatesByTheLocalRule()
    {
        using var handler = ArrClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
        Assert.Equal(LocalAddress.ValidateCertificate, handler.SslOptions.RemoteCertificateValidationCallback);

        using var client = ArrClient.CreateHttpClient();
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, client.Timeout);
    }
}

public class LocalAddressTests
{
    // The same table as Torrent Throttle's, so both plugins treat an address the same way.
    [Theory]
    [InlineData("https://172.30.12.103/", true)]
    [InlineData("https://172.16.0.1:8443/", true)]
    [InlineData("https://10.1.2.3/", true)]
    [InlineData("https://192.168.1.20:8989/", true)]
    [InlineData("https://127.0.0.1/", true)]
    [InlineData("https://169.254.10.10/", true)]
    [InlineData("https://[::1]/", true)]
    [InlineData("https://[fd00::20]/", true)]
    [InlineData("https://[fe80::1]/", true)]
    [InlineData("https://[::ffff:192.168.1.20]/", true)]
    [InlineData("https://172.32.0.1/", false)]
    [InlineData("https://172.15.255.255/", false)]
    [InlineData("https://8.8.8.8/", false)]
    [InlineData("https://[2001:db8::1]/", false)]
    [InlineData("https://localhost/", false)]
    [InlineData("https://sonarr/", false)]
    [InlineData("https://sonarr.example.net/", false)]
    public void OnlyPrivateAndLoopbackAddressesSkipValidation(string url, bool expected)
    {
        Assert.Equal(expected, LocalAddress.IsPrivateAddress(new Uri(url)));
    }

    [Theory]
    [InlineData("https://192.168.1.20:8989/api/v3/calendar", SslPolicyErrors.RemoteCertificateChainErrors, true)]
    [InlineData("https://192.168.1.20:8989/api/v3/calendar", SslPolicyErrors.RemoteCertificateNameMismatch, true)]
    [InlineData("https://sonarr.example.net/api/v3/calendar", SslPolicyErrors.RemoteCertificateChainErrors, false)]
    [InlineData("https://8.8.8.8/api/v3/calendar", SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData("https://sonarr.example.net/api/v3/calendar", SslPolicyErrors.None, true)]
    public void ValidateCertificate_DecidesPerRequestAddress(string url, SslPolicyErrors errors, bool accepted)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Assert.Equal(accepted, LocalAddress.ValidateCertificate(request, null, null, errors));
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("[fd00::20]", true)]
    [InlineData("fd00::20", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("sonarr", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPrivateHost_ReadsHostsWithOrWithoutBrackets(string? host, bool expected)
    {
        Assert.Equal(expected, LocalAddress.IsPrivateHost(host));
    }

    [Fact]
    public void ValidateCertificate_RejectsAFailureWithoutARequestToJudge()
    {
        Assert.False(LocalAddress.ValidateCertificate(new object(), null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(LocalAddress.ValidateCertificate(new object(), null, null, SslPolicyErrors.None));
    }

    [Fact]
    public async Task SelfSignedCertificateIsAcceptedOnAPrivateAddress()
    {
        using var certificate = CreateSelfSignedCertificate();
        var (listener, port) = Listen();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeOnce(listener, certificate, timeout.Token);
        try
        {
            using var http = ArrClient.CreateHttpClient();
            var client = new ArrClient(http, new ListLogger<ArrClient>());
            var status = await client.GetJsonAsync(
                ArrConnection.Create(ArrKind.Sonarr, $"https://127.0.0.1:{port}", "key"),
                "system/status",
                null,
                TimeSpan.FromSeconds(5),
                timeout.Token);
            Assert.Equal("Sonarr", status.GetProperty("appName").GetString());
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public async Task SelfSignedCertificateIsRejectedForAHostName()
    {
        using var certificate = CreateSelfSignedCertificate();
        var (listener, port) = Listen();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = ServeOnce(listener, certificate, timeout.Token);
        try
        {
            using var http = ArrClient.CreateHttpClient();
            var client = new ArrClient(http, new ListLogger<ArrClient>());
            var error = await Assert.ThrowsAsync<ArrConnectionException>(() => client.GetJsonAsync(
                ArrConnection.Create(ArrKind.Sonarr, $"https://localhost:{port}", "key"),
                "system/status",
                null,
                TimeSpan.FromSeconds(5),
                timeout.Token));
            Assert.Contains("certificate is not trusted", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=sonarr.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Reload so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private static Task ServeOnce(TcpListener listener, X509Certificate2 certificate, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellationToken);
            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await tls.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            Assert.StartsWith("GET /api/v3/system/status ", request.ToString(), StringComparison.Ordinal);
            var body = "{\"appName\":\"Sonarr\",\"version\":\"4.0.20.3014\"}";
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The rejecting client aborts the handshake; the test asserts on the client side.
        }
    });
}
