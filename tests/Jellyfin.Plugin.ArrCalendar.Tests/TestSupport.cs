using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

/// <summary>
/// Stands a <see cref="Plugin"/> instance up around a given configuration.
/// </summary>
/// <remarks>
/// Jellyfin loads a plugin's configuration lazily through <see cref="IXmlSerializer"/>, so
/// answering <c>DeserializeFromFile</c> with the desired object installs it without touching
/// disk. The constructor assigns the process-wide <c>Plugin.Instance</c>, so every test class that
/// constructs a plugin joins the <c>PluginInstance</c> collection.
/// </remarks>
internal static class PluginTestHost
{
    public static Plugin WithConfiguration(PluginConfiguration configuration)
    {
        var applicationPaths = Substitute.For<IApplicationPaths>();
        applicationPaths.PluginConfigurationsPath.Returns(Path.Combine(Path.GetTempPath(), "arr-calendar-tests"));

        var xmlSerializer = Substitute.For<IXmlSerializer>();
        xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(configuration);

        var plugin = new Plugin(applicationPaths, xmlSerializer);
        _ = plugin.Configuration;
        return plugin;
    }
}

[CollectionDefinition("PluginInstance")]
public sealed class PluginInstanceCollection
{
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>
/// Answers outbound requests from a script, and remembers them.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public List<HttpRequestMessage> Requests { get; } = new();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes, string contentType, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _respond(request, cancellationToken);
    }
}

internal static class Fixtures
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static JsonElement Json(string name)
    {
        using var document = JsonDocument.Parse(Text(name));
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Stands in for Sonarr and Radarr above the HTTP layer, counting what was asked.
/// </summary>
internal sealed class FakeArrClient : IArrClient
{
    public Dictionary<string, Func<ArrConnection, JsonElement>> Json { get; } = new(StringComparer.Ordinal);

    public Func<ArrConnection, string, ArrImage?> Image { get; set; } = (_, _) => new ArrImage([1, 2, 3], "image/jpeg");

    public List<(ArrKind Kind, string Path, Dictionary<string, string> Query)> Calls { get; } = new();

    public Task<JsonElement> GetJsonAsync(
        ArrConnection connection,
        string path,
        IEnumerable<KeyValuePair<string, string>>? query,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((connection.Kind, path, (query ?? []).ToDictionary(pair => pair.Key, pair => pair.Value)));
        }

        return Json.TryGetValue(connection.Kind.Id() + ":" + path, out var answer)
            ? Task.Run(() => answer(connection), cancellationToken)
            : Task.FromException<JsonElement>(new ArrApiException(HttpStatusCode.NotFound, "no stub for " + path));
    }

    public Task<ArrImage?> GetImageAsync(ArrConnection connection, string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add((connection.Kind, path, new Dictionary<string, string>()));
        }

        return Task.FromResult(Image(connection, path));
    }

    public int CountOf(ArrKind kind, string path)
    {
        lock (Calls)
        {
            return Calls.Count(call => call.Kind == kind && call.Path == path);
        }
    }
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
