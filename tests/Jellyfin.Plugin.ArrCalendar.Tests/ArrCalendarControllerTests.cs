using System.Net;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ArrCalendar.Api;
using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Browser;
using Jellyfin.Plugin.ArrCalendar.Configuration;
using Jellyfin.Plugin.ArrCalendar.Library;
using Jellyfin.Plugin.ArrCalendar.Models;
using Jellyfin.Plugin.ArrCalendar.Posters;
using Jellyfin.Plugin.ArrCalendar.Security;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using NSubstitute;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

[Collection("PluginInstance")]
public class ArrCalendarControllerTests
{
    private const string SonarrKey = "0123456789abcdef0123456789abcdef";
    private const string EnvironmentKey = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly Guid AliceId = Guid.Parse("4e6ea056-9821-4e7c-940d-2af1806f1f8e");

    private sealed class Rig
    {
        public required ArrCalendarController Controller { get; init; }

        public required FakeArrClient Client { get; init; }

        public required ImageSigner Signer { get; init; }

        public required ILibraryManager Library { get; init; }
    }

    private static PluginConfiguration Configured() => new()
    {
        SonarrUrl = "http://sonarr:8989",
        SonarrApiKey = SonarrKey,
        RadarrUrl = "http://radarr:7878",
        RadarrApiKey = "fedcba9876543210fedcba9876543210"
    };

    private static Rig Create(
        PluginConfiguration? configuration = null,
        PluginStatus status = PluginStatus.Active,
        bool apiKey = false,
        ArrCalendarRateLimiter? limiter = null,
        PluginStatus? seerrStatus = null,
        Dictionary<string, string?>? environment = null,
        IPlugin? seerrInstance = null)
    {
        var config = configuration ?? Configured();
        PluginTestHost.WithConfiguration(config);

        var plugins = new List<LocalPlugin>
        {
            new("/plugins/arr-calendar", true, new PluginManifest { Id = Plugin.PluginId, Name = "Arr Calendar", Version = "1.0.0.0", Status = status })
        };
        if (seerrStatus is not null)
        {
            plugins.Add(new LocalPlugin("/plugins/seerr-integration", true, new PluginManifest { Id = SeerrIntegrationPlugin.Id, Name = "Seerr Integration", Version = "1.0.0.2", Status = seerrStatus.Value })
            {
                Instance = seerrInstance
            });
        }

        var manager = Substitute.For<IPluginManager>();
        manager.Plugins.Returns(plugins);

        var client = new FakeArrClient();
        client.Json["sonarr:calendar"] = _ => Fixtures.Json("sonarr-calendar.json");
        client.Json["sonarr:queue/details"] = _ => Fixtures.Json("sonarr-queue.json");
        client.Json["radarr:calendar"] = _ => Fixtures.Json("radarr-calendar.json");
        client.Json["radarr:queue/details"] = _ => Fixtures.Json("radarr-queue.json");
        client.Json["sonarr:system/status"] = _ => Fixtures.Json("sonarr-status.json");
        client.Json["radarr:system/status"] = _ => Fixtures.Json("radarr-status.json");

        var secrets = new ArrSecretSource(name => environment?.GetValueOrDefault(name), _ => throw new IOException());
        var signer = new ImageSigner();
        var rateLimiter = limiter ?? new ArrCalendarRateLimiter();
        var calendar = new CalendarService(client, secrets, signer, rateLimiter, () => config, TimeProvider.System, new ListLogger<CalendarService>());

        var library = Substitute.For<ILibraryManager>();
        library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());
        var alice = new User("alice", "Default", "Default") { Id = AliceId };
        var users = Substitute.For<IUserManager>();
        users.GetUserById(AliceId).Returns(alice);

        // An API key authenticates without a user, which is how Jellyfin reports it.
        var authorization = Substitute.For<IAuthorizationContext>();
        authorization.GetAuthorizationInfo(Arg.Any<HttpContext>()).Returns(new AuthorizationInfo { User = apiKey ? null : alice, IsApiKey = apiKey, IsAuthenticated = true });

        var registrar = new BrowserScriptRegistrar(new JavaScriptInjectorBridge(() => null), () => string.Empty, new ListLogger<BrowserScriptRegistrar>());
        var controller = new ArrCalendarController(
            manager,
            registrar,
            calendar,
            new LibraryItemFinder(library, users),
            client,
            secrets,
            signer,
            rateLimiter,
            authorization,
            new ListLogger<ArrCalendarController>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return new Rig { Controller = controller, Client = client, Signer = signer, Library = library };
    }

    private static ErrorResponse Error(IConvertToActionResult result, int status)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Convert());
        Assert.Equal(status, objectResult.StatusCode);
        return Assert.IsType<ErrorResponse>(objectResult.Value);
    }

    [Fact]
    public void GetClientFile_ServesThePageScriptAndCachesTheCurrentVersionForGood()
    {
        var rig = Create();

        var result = Assert.IsType<FileStreamResult>(rig.Controller.GetClientFile("arr-calendar.js", BrowserScriptRegistrar.GetVersion()));

        Assert.Equal("application/javascript; charset=utf-8", result.ContentType);
        using var reader = new StreamReader(result.FileStream);
        Assert.Contains("window.ArrCalendar", reader.ReadToEnd(), StringComparison.Ordinal);
        Assert.Equal("public, max-age=31536000, immutable", rig.Controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", rig.Controller.Response.Headers.XContentTypeOptions.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0.1")]
    public void GetClientFile_RevalidatesAnyOtherVersion(string? version)
    {
        var rig = Create();
        rig.Controller.GetClientFile("arr-calendar.js", version);
        Assert.Equal("no-cache", rig.Controller.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("bootstrap.js")]
    [InlineData("arr-calendar.css")]
    [InlineData("..%2Fconfigpage.html")]
    [InlineData("Jellyfin.Plugin.ArrCalendar.Configuration.configPage.html")]
    public void GetClientFile_RefusesAnythingOutsideTheAllowlist(string fileName)
    {
        Assert.IsType<NotFoundResult>(Create().Controller.GetClientFile(fileName, null));
    }

    [Theory]
    [InlineData(PluginStatus.Disabled)]
    [InlineData(PluginStatus.Restart)]
    [InlineData(PluginStatus.Malfunctioned)]
    public async Task EveryEndpoint_StopsWhenThePluginIsNotActive(PluginStatus status)
    {
        var rig = Create(status: status);

        Assert.IsType<NotFoundResult>(rig.Controller.GetClientFile("arr-calendar.js", null));
        Assert.Equal("PluginInactive", Error(await rig.Controller.GetCalendar("2026-11-01", "2026-11-30", CancellationToken.None), 404).Error);
        Assert.IsType<NotFoundResult>(await rig.Controller.GetImage("sonarr", 2, "poster", 250, rig.Signer.Sign(ArrKind.Sonarr, 2), CancellationToken.None));
        Assert.Equal("PluginInactive", Error(await rig.Controller.GetLibraryItem("movie", null, 1, null, null, null), 404).Error);
        Assert.IsType<NotFoundResult>(rig.Controller.GetRegistration().Result);
        Assert.IsType<NotFoundResult>((await rig.Controller.TestConnection(new TestConnectionRequest { Source = "sonarr" }, CancellationToken.None)).Result);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task GetCalendar_AnswersWithBothServicesAndIsNeverCached()
    {
        var rig = Create();

        var result = await rig.Controller.GetCalendar("2026-11-01", "2026-12-05", CancellationToken.None);

        var calendar = result.Value!;
        Assert.Equal(2, calendar.Sources.Count);
        Assert.NotEmpty(calendar.Events);
        Assert.Equal("no-store", rig.Controller.Response.Headers.CacheControl.ToString());
        var json = JsonSerializer.Serialize(calendar);
        Assert.DoesNotContain(SonarrKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("/tv/", json, StringComparison.Ordinal);
        Assert.Contains("\"airDateUtc\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCalendar_RefusesWhileTheCalendarIsSwitchedOff()
    {
        var configuration = Configured();
        configuration.Enabled = false;
        var rig = Create(configuration);

        Assert.Equal("CalendarDisabled", Error(await rig.Controller.GetCalendar("2026-11-01", "2026-11-30", CancellationToken.None), 403).Error);
        Assert.Empty(rig.Client.Calls);
    }

    [Theory]
    [InlineData(null, "2026-11-30")]
    [InlineData("2026-11-01", null)]
    [InlineData("11/01/2026", "2026-11-30")]
    [InlineData("2026-11-31", "2026-12-30")]
    [InlineData("2026-11-30", "2026-11-01")]
    [InlineData("2026-11-01", "2027-01-02")]
    public async Task GetCalendar_RefusesMalformedOrOverlongRanges(string? start, string? end)
    {
        var rig = Create();
        Assert.Equal("InvalidRange", Error(await rig.Controller.GetCalendar(start, end, CancellationToken.None), 400).Error);
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task GetCalendar_IsRateLimitedPerUser()
    {
        using var limiter = new ArrCalendarRateLimiter(1, 1, 1, 1, TimeSpan.FromMinutes(1));
        var rig = Create(limiter: limiter);

        await rig.Controller.GetCalendar("2026-11-01", "2026-11-30", CancellationToken.None);
        var refused = Error(await rig.Controller.GetCalendar("2026-11-01", "2026-11-30", CancellationToken.None), 429);

        Assert.Equal("RateLimited", refused.Error);
        Assert.True(int.Parse(rig.Controller.Response.Headers.RetryAfter.ToString(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
    }

    [Fact]
    public async Task GetImage_ServesSignedArtworkWithCachingHeaders()
    {
        var rig = Create();

        var result = Assert.IsType<FileContentResult>(await rig.Controller.GetImage("sonarr", 2, "poster", null, rig.Signer.Sign(ArrKind.Sonarr, 2), CancellationToken.None));

        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal("public, max-age=86400", rig.Controller.Response.Headers.CacheControl.ToString());
        Assert.Contains(rig.Client.Calls, call => call.Path == "mediacover/2/poster-250.jpg");
    }

    [Theory]
    [InlineData("sonarr", 3, "poster", 250, false)]
    [InlineData("radarr", 2, "poster", 250, false)]
    [InlineData("sonarr", 2, "poster", 300, true)]
    [InlineData("sonarr", 2, "banner", 250, true)]
    [InlineData("lidarr", 2, "poster", 250, true)]
    [InlineData("sonarr", 0, "poster", 250, true)]
    public async Task GetImage_RefusesUnsignedOrUnknownArtworkBeforeAnyWork(string source, int id, string type, int size, bool validSignatureForTheRequest)
    {
        var rig = Create();
        var signature = validSignatureForTheRequest && ArrKindNames.TryParse(source, out var kind) && id > 0
            ? rig.Signer.Sign(kind, id)
            : rig.Signer.Sign(ArrKind.Sonarr, 2);

        Assert.IsType<NotFoundResult>(await rig.Controller.GetImage(source, id, type, size, signature, CancellationToken.None));
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task GetImage_AnswersNotFoundForAMissingImageAndServiceUnavailableWhenTheBudgetIsSpent()
    {
        using var limiter = new ArrCalendarRateLimiter(10, 10, 1, 10, TimeSpan.FromMinutes(1));
        var rig = Create(limiter: limiter);
        rig.Client.Image = (_, _) => null;

        Assert.IsType<NotFoundResult>(await rig.Controller.GetImage("radarr", 11, "poster", 500, rig.Signer.Sign(ArrKind.Radarr, 11), CancellationToken.None));
        var refused = Assert.IsType<StatusCodeResult>(await rig.Controller.GetImage("radarr", 12, "poster", 500, rig.Signer.Sign(ArrKind.Radarr, 12), CancellationToken.None));
        Assert.Equal(503, refused.StatusCode);
        Assert.Equal("30", rig.Controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task GetLibraryItem_NeedsAUserAndAnswersNoContentWithoutAMatch()
    {
        var withApiKey = Create(apiKey: true);
        Assert.Equal("MissingJellyfinUser", Error(await withApiKey.Controller.GetLibraryItem("movie", null, 1, null, null, null), 401).Error);

        var rig = Create();
        Assert.IsType<NoContentResult>((await rig.Controller.GetLibraryItem("movie", null, 969681, "tt22084616", null, null)).Result);
    }

    [Fact]
    public async Task GetLibraryItem_ReturnsTheMatch()
    {
        var rig = Create();
        var movie = new Movie { Id = Guid.NewGuid() };
        rig.Library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { movie });

        var match = (await rig.Controller.GetLibraryItem("movie", null, 969681, null, null, null)).Value!;

        Assert.Equal(movie.Id.ToString("N"), match.ItemId);
        Assert.Equal("movie", match.Kind);
    }

    [Theory]
    [InlineData("series", null, 1, null, null, null)]
    [InlineData(null, null, 1, null, null, null)]
    [InlineData("movie", null, -1, null, null, null)]
    [InlineData("movie", null, null, "nm0000123", null, null)]
    [InlineData("movie", null, null, "tt1' OR 1=1", null, null)]
    [InlineData("episode", 0, null, null, 1, 1)]
    [InlineData("episode", 5, null, null, -1, 1)]
    [InlineData("movie", null, null, null, null, null)]
    [InlineData("movie", 76177, null, null, null, null)]
    public async Task GetLibraryItem_RefusesMalformedLookups(string? kind, int? tvdb, int? tmdb, string? imdb, int? season, int? episode)
    {
        Assert.Equal("InvalidLookup", Error(await Create().Controller.GetLibraryItem(kind, tvdb, tmdb, imdb, season, episode), 400).Error);
    }

    [Fact]
    public void GetRegistration_DescribesEachServiceAndSeerrIntegration()
    {
        var configuration = Configured();
        configuration.RadarrUrl = "radarr:7878";
        var rig = Create(configuration, seerrStatus: PluginStatus.Active, environment: new() { [ArrSecretSource.SonarrApiKeyVariable] = EnvironmentKey });

        var status = rig.Controller.GetRegistration().Value!;

        Assert.True(status.Enabled);
        Assert.Equal("Pending", status.LoaderState);
        Assert.True(status.SeerrIntegrationActive);
        Assert.Equal(BrowserScriptRegistrar.GetVersion(), status.Version);
        Assert.True(status.Sonarr.Configured);
        Assert.Equal("environment", status.Sonarr.ApiKeySource);
        Assert.Null(status.Sonarr.Problem);
        Assert.True(status.Radarr.Configured);
        Assert.Equal("configuration", status.Radarr.ApiKeySource);
        Assert.Contains("starting with http", status.Radarr.Problem, StringComparison.Ordinal);
        Assert.Empty(rig.Client.Calls);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(PluginStatus.Disabled, false)]
    [InlineData(PluginStatus.Active, true)]
    public void GetRegistration_OnlyCountsAnActiveSeerrIntegration(PluginStatus? seerr, bool expected)
    {
        Assert.Equal(expected, Create(seerrStatus: seerr).Controller.GetRegistration().Value!.SeerrIntegrationActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetRegistration_ReadsWhetherSeerrIntegrationShowsItsRequestsPage(bool requestsPage)
    {
        var status = Create(seerrStatus: PluginStatus.Active, seerrInstance: SeerrLike(new SeerrLikeConfiguration { Enabled = requestsPage }))
            .Controller.GetRegistration().Value!;

        Assert.True(status.SeerrIntegrationActive);
        Assert.Equal(requestsPage, status.SeerrIntegrationRequestsEnabled);
    }

    [Fact]
    public void GetRegistration_LeavesTheRequestsPageUnknownWhenItCannotBeRead()
    {
        // No instance, settings of another shape, and an inactive plugin all read as unknown.
        Assert.Null(Create(seerrStatus: PluginStatus.Active).Controller.GetRegistration().Value!.SeerrIntegrationRequestsEnabled);
        Assert.Null(Create(seerrStatus: PluginStatus.Active, seerrInstance: SeerrLike(new BasePluginConfiguration())).Controller.GetRegistration().Value!.SeerrIntegrationRequestsEnabled);
        Assert.Null(Create(seerrStatus: PluginStatus.Disabled, seerrInstance: SeerrLike(new SeerrLikeConfiguration { Enabled = true })).Controller.GetRegistration().Value!.SeerrIntegrationRequestsEnabled);
        Assert.Null(ArrCalendarController.IsSeerrRequestsPageOn(null));
        Assert.Null(ArrCalendarController.IsSeerrRequestsPageOn(new LocalPlugin("/plugins/x", true, new PluginManifest()) { Instance = Substitute.For<IPlugin>() }));
    }

    private static IPlugin SeerrLike(BasePluginConfiguration settings)
    {
        var instance = Substitute.For<IPlugin, IHasPluginConfiguration>();
        ((IHasPluginConfiguration)instance).Configuration.Returns(settings);
        return instance;
    }

    /// <summary>
    /// Stands in for Seerr Integration's own configuration type, which the controller reads by name.
    /// </summary>
    public sealed class SeerrLikeConfiguration : BasePluginConfiguration
    {
        public bool Enabled { get; set; }
    }

    [Fact]
    public async Task TestConnection_ChecksTheTypedValuesAndTheServicesIdentity()
    {
        var rig = Create(new PluginConfiguration());

        var ok = (await rig.Controller.TestConnection(new TestConnectionRequest { Source = "sonarr", Url = "http://sonarr:8989", ApiKey = SonarrKey }, CancellationToken.None)).Value!;
        Assert.True(ok.Ok);
        Assert.Equal("4.0.20.3014", ok.Version);
        Assert.Equal("Connected to Sonarr 4.0.20.3014.", ok.Message);

        // A Radarr address typed into the Sonarr fields.
        rig.Client.Json["sonarr:system/status"] = _ => Fixtures.Json("radarr-status.json");
        var wrong = (await rig.Controller.TestConnection(new TestConnectionRequest { Source = "sonarr", Url = "http://radarr:7878", ApiKey = SonarrKey }, CancellationToken.None)).Value!;
        Assert.False(wrong.Ok);
        Assert.Equal("That address is Radarr, not Sonarr.", wrong.Message);
    }

    [Fact]
    public async Task TestConnection_DoesNotEchoAnUnrecognisedAppName()
    {
        var rig = Create(new PluginConfiguration());
        rig.Client.Json["sonarr:system/status"] = _ => JsonSerializer.Deserialize<JsonElement>("{\"appName\":\"<script>alert(1)</script>\",\"version\":\"x y\"}");

        var result = (await rig.Controller.TestConnection(new TestConnectionRequest { Source = "sonarr", Url = "http://sonarr:8989", ApiKey = SonarrKey }, CancellationToken.None)).Value!;

        Assert.False(result.Ok);
        Assert.Equal("That address answered, but not as Sonarr.", result.Message);
    }

    [Fact]
    public async Task TestConnection_SendsTheSavedKeyOnlyToTheSavedAddress()
    {
        var rig = Create(environment: new() { [ArrSecretSource.SonarrApiKeyVariable] = EnvironmentKey });

        var saved = (await rig.Controller.TestConnection(new TestConnectionRequest { Source = "sonarr", Url = "http://sonarr:8989/" }, CancellationToken.None)).Value!;
        Assert.True(saved.Ok);

        var elsewhere = (await rig.Controller.TestConnection(new TestConnectionRequest { Source = "sonarr", Url = "http://attacker.example:8989" }, CancellationToken.None)).Value!;
        Assert.False(elsewhere.Ok);
        Assert.Equal("Enter Sonarr's API key to test this address.", elsewhere.Message);
        Assert.Single(rig.Client.Calls);
        Assert.DoesNotContain(rig.Client.Calls, call => call.Path == "system/status" && call.Kind == ArrKind.Sonarr && call.Query.Count > 0);
    }

    [Fact]
    public async Task TestConnection_ReportsFailuresInItsOwnWords()
    {
        var rig = Create(new PluginConfiguration());
        rig.Client.Json["radarr:system/status"] = _ => throw new ArrApiException(HttpStatusCode.Unauthorized, "Radarr rejected the API key.");

        var refused = (await rig.Controller.TestConnection(new TestConnectionRequest { Source = "radarr", Url = "http://radarr:7878", ApiKey = "bad" }, CancellationToken.None)).Value!;
        Assert.False(refused.Ok);
        Assert.Equal("Radarr rejected the API key.", refused.Message);

        var malformed = (await rig.Controller.TestConnection(new TestConnectionRequest { Source = "radarr", Url = "radarr", ApiKey = "bad" }, CancellationToken.None)).Value!;
        Assert.Contains("starting with http", malformed.Message, StringComparison.Ordinal);

        Assert.Equal("InvalidSource", Error(await rig.Controller.TestConnection(new TestConnectionRequest { Source = "lidarr" }, CancellationToken.None), 400).Error);
        Assert.Equal("InvalidSource", Error(await rig.Controller.TestConnection(null, CancellationToken.None), 400).Error);
    }

    [Fact]
    public async Task TestConnection_IsRateLimitedProcessWide()
    {
        using var limiter = new ArrCalendarRateLimiter(10, 10, 10, 1, TimeSpan.FromMinutes(1));
        var rig = Create(limiter: limiter);
        var request = new TestConnectionRequest { Source = "sonarr", Url = "http://sonarr:8989", ApiKey = SonarrKey };

        await rig.Controller.TestConnection(request, CancellationToken.None);
        Assert.Equal("RateLimited", Error(await rig.Controller.TestConnection(request, CancellationToken.None), 429).Error);
    }
}
