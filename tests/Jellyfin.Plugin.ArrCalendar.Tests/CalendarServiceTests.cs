using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Configuration;
using Jellyfin.Plugin.ArrCalendar.Posters;
using Jellyfin.Plugin.ArrCalendar.Security;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

public class CalendarServiceTests
{
    private const string SonarrKey = "0123456789abcdef0123456789abcdef";
    private const string RadarrKey = "fedcba9876543210fedcba9876543210";
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 12, 5);

    private static PluginConfiguration Both() => new()
    {
        SonarrUrl = "http://sonarr:8989",
        SonarrApiKey = SonarrKey,
        RadarrUrl = "http://radarr:7878",
        RadarrApiKey = RadarrKey
    };

    private static (CalendarService Service, FakeArrClient Client, ListLogger<CalendarService> Logger, ManualTime Time) Create(
        PluginConfiguration configuration,
        ArrCalendarRateLimiter? limiter = null)
    {
        var client = new FakeArrClient();
        client.Json["sonarr:calendar"] = _ => Fixtures.Json("sonarr-calendar.json");
        client.Json["sonarr:queue/details"] = _ => Fixtures.Json("sonarr-queue.json");
        client.Json["radarr:calendar"] = _ => Fixtures.Json("radarr-calendar.json");
        client.Json["radarr:queue/details"] = _ => Fixtures.Json("radarr-queue.json");
        var logger = new ListLogger<CalendarService>();
        var time = new ManualTime();
        var service = new CalendarService(
            client,
            new ArrSecretSource(_ => null, _ => string.Empty),
            new ImageSigner(),
            limiter ?? new ArrCalendarRateLimiter(),
            () => configuration,
            time,
            logger);
        return (service, client, logger, time);
    }

    [Fact]
    public async Task GetCalendarAsync_MergesBothServicesInTimeOrder()
    {
        var (service, _, _, _) = Create(Both());

        var calendar = await service.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.Equal("2026-11-01", calendar.Start);
        Assert.Equal("2026-12-05", calendar.End);
        Assert.Equal(["ok", "ok"], calendar.Sources.Select(source => source.State).ToArray());
        Assert.Equal(["sonarr", "radarr"], calendar.Sources.Select(source => source.Source).ToArray());
        Assert.Contains(calendar.Events, item => item.Kind == "episode");
        Assert.Contains(calendar.Events, item => item.Key == "radarr:movie:11:physical");
        Assert.Equal(calendar.Events.Select(item => item.Key), CalendarMapper.Sort(calendar.Events).Select(item => item.Key));
        Assert.Equal(62, calendar.Events.Single(item => item.Title == "Late Night with Seth Meyers").Download!.Progress);
    }

    [Fact]
    public async Task GetCalendarAsync_AsksForAWidenedUtcRangeWithEachServicesOwnOptions()
    {
        var configuration = Both();
        configuration.IncludeUnmonitored = true;
        var (service, client, _, _) = Create(configuration);

        await service.GetCalendarAsync(Start, End, CancellationToken.None);

        var sonarr = client.Calls.Single(call => call.Kind == ArrKind.Sonarr && call.Path == "calendar").Query;
        Assert.Equal("2026-10-31T00:00:00Z", sonarr["start"]);
        Assert.Equal("2026-12-07T00:00:00Z", sonarr["end"]);
        Assert.Equal("true", sonarr["unmonitored"]);
        Assert.Equal("true", sonarr["includeSeries"]);
        Assert.Equal("false", sonarr["includeEpisodeFile"]);

        var radarr = client.Calls.Single(call => call.Kind == ArrKind.Radarr && call.Path == "calendar").Query;
        Assert.False(radarr.ContainsKey("includeSeries"));
        Assert.Equal("true", radarr["unmonitored"]);
        Assert.Equal("false", client.Calls.Single(call => call.Kind == ArrKind.Radarr && call.Path == "queue/details").Query["includeMovie"]);
    }

    [Fact]
    public async Task GetCalendarAsync_CarriesTheWordingSettingEvenWhenTheAnswersComeFromTheCache()
    {
        var configuration = Both();
        var (service, client, _, _) = Create(configuration);

        Assert.False((await service.GetCalendarAsync(Start, End, CancellationToken.None)).DontSpecifyMethod);
        var calls = client.Calls.Count;

        configuration.DontSpecifyMethod = true;
        var calendar = await service.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.True(calendar.DontSpecifyMethod);
        Assert.Equal(calls, client.Calls.Count);
        Assert.NotEmpty(calendar.Events);
    }

    [Fact]
    public async Task GetCalendarAsync_ReportsAnUnconfiguredServiceWithoutAskingIt()
    {
        var (service, client, _, _) = Create(new PluginConfiguration { RadarrUrl = "http://radarr:7878", RadarrApiKey = RadarrKey });

        var calendar = await service.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.Equal("unconfigured", calendar.Sources[0].State);
        Assert.Null(calendar.Sources[0].Error);
        Assert.Equal("ok", calendar.Sources[1].State);
        Assert.DoesNotContain(client.Calls, call => call.Kind == ArrKind.Sonarr);
        Assert.All(calendar.Events, item => Assert.Equal("movie", item.Kind));
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("unauthorized")]
    [InlineData("misconfigured")]
    [InlineData("failed")]
    public async Task GetCalendarAsync_KeepsOneServicesEntriesWhenTheOtherFails(string error)
    {
        var (service, client, logger, _) = Create(Both());
        client.Json["radarr:calendar"] = _ => throw error switch
        {
            "unreachable" => (Exception)new ArrConnectionException("Radarr could not be reached at http://radarr:7878.", new HttpRequestException()),
            "unauthorized" => new ArrApiException(HttpStatusCode.Unauthorized, "Radarr rejected the API key."),
            "misconfigured" => new ArrConfigurationException("Radarr answered with a redirect."),
            _ => new ArrApiException(HttpStatusCode.InternalServerError, "Radarr answered HTTP 500.")
        };

        var calendar = await service.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.Equal("ok", calendar.Sources[0].State);
        var radarr = calendar.Sources[1];
        Assert.Equal("error", radarr.State);
        Assert.Equal(error, radarr.Error);
        Assert.StartsWith("Radarr", radarr.Message, StringComparison.Ordinal);

        // Every user sees this message: it never says where the service lives.
        Assert.DoesNotContain("radarr:7878", radarr.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("http", radarr.Message, StringComparison.Ordinal);
        Assert.All(calendar.Events, item => Assert.Equal("episode", item.Kind));
        Assert.NotEmpty(calendar.Events);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Radarr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCalendarAsync_ShowsTheCalendarWhenOnlyTheQueueFails()
    {
        var (service, client, _, _) = Create(Both());
        client.Json["sonarr:queue/details"] = _ => throw new ArrApiException(HttpStatusCode.InternalServerError, "boom");

        var calendar = await service.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.Equal("ok", calendar.Sources[0].State);
        Assert.All(calendar.Events.Where(item => item.Kind == "episode"), item => Assert.Null(item.Download));
    }

    [Fact]
    public async Task GetCalendarAsync_ReportsAnUnusableSavedAddressAsMisconfigured()
    {
        var configuration = Both();
        configuration.SonarrUrl = "sonarr:8989";
        var (service, client, _, _) = Create(configuration);

        var calendar = await service.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.Equal("misconfigured", calendar.Sources[0].Error);
        Assert.DoesNotContain(client.Calls, call => call.Kind == ArrKind.Sonarr);
    }

    [Fact]
    public async Task GetCalendarAsync_CachesBrieflyAndSharesConcurrentRequests()
    {
        var (service, client, _, time) = Create(Both());

        await Task.WhenAll(
            service.GetCalendarAsync(Start, End, CancellationToken.None),
            service.GetCalendarAsync(Start, End, CancellationToken.None),
            service.GetCalendarAsync(Start, End, CancellationToken.None));
        Assert.Equal(1, client.CountOf(ArrKind.Sonarr, "calendar"));
        Assert.Equal(1, client.CountOf(ArrKind.Sonarr, "queue/details"));

        time.Advance(TimeSpan.FromSeconds(20));
        await service.GetCalendarAsync(Start, End, CancellationToken.None);
        Assert.Equal(1, client.CountOf(ArrKind.Sonarr, "calendar"));
        Assert.Equal(2, client.CountOf(ArrKind.Sonarr, "queue/details"));

        time.Advance(TimeSpan.FromSeconds(45));
        await service.GetCalendarAsync(Start, End, CancellationToken.None);
        Assert.Equal(2, client.CountOf(ArrKind.Sonarr, "calendar"));
    }

    [Fact]
    public async Task GetCalendarAsync_DoesNotServeAnotherKeysAnswer()
    {
        var configuration = Both();
        var (service, client, _, _) = Create(configuration);

        await service.GetCalendarAsync(Start, End, CancellationToken.None);
        configuration.SonarrApiKey = "11111111111111111111111111111111";
        await service.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.Equal(2, client.CountOf(ArrKind.Sonarr, "calendar"));
    }

    [Theory]
    [InlineData(2026, 11, 2, 2026, 11, 1)]
    [InlineData(2026, 11, 1, 2027, 1, 2)]
    public async Task GetCalendarAsync_RefusesBackwardsOrOverlongRanges(int y1, int m1, int d1, int y2, int m2, int d2)
    {
        var (service, _, _, _) = Create(Both());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetCalendarAsync(new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2), CancellationToken.None));
    }

    [Fact]
    public async Task GetImageAsync_FetchesThePreRenderedSizeOnceAndRemembersAMissingImageBriefly()
    {
        var (service, client, _, time) = Create(Both());
        var served = 0;
        client.Image = (_, path) =>
        {
            served++;
            return path.EndsWith("poster-250.jpg", StringComparison.Ordinal) ? new ArrImage([9], "image/jpeg") : null;
        };

        var image = await service.GetImageAsync(ArrKind.Sonarr, 2, "poster", 250, CancellationToken.None);
        await service.GetImageAsync(ArrKind.Sonarr, 2, "poster", 250, CancellationToken.None);
        Assert.Equal(new byte[] { 9 }, image!.Bytes);
        Assert.Equal(1, served);
        Assert.Contains(client.Calls, call => call.Path == "mediacover/2/poster-250.jpg");

        Assert.Null(await service.GetImageAsync(ArrKind.Sonarr, 2, "fanart", 360, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(await service.GetImageAsync(ArrKind.Sonarr, 2, "fanart", 360, CancellationToken.None));
        Assert.Equal(3, served);
    }

    [Theory]
    [InlineData("poster", 300)]
    [InlineData("banner", 250)]
    [InlineData("fanart", 250)]
    public async Task GetImageAsync_OnlyServesTheSizesTheServicesRender(string type, int size)
    {
        var (service, client, _, _) = Create(Both());

        Assert.Null(await service.GetImageAsync(ArrKind.Sonarr, 2, type, size, CancellationToken.None));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task GetImageAsync_AnswersNothingForAnUnconfiguredService()
    {
        var (service, client, _, _) = Create(new PluginConfiguration());

        Assert.Null(await service.GetImageAsync(ArrKind.Radarr, 1, "poster", 250, CancellationToken.None));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task GetImageAsync_StopsFetchingWhenTheBudgetIsSpentWithoutRememberingIt()
    {
        using var limiter = new ArrCalendarRateLimiter(10, 10, 1, 10, TimeSpan.FromMinutes(1));
        var (service, client, _, _) = Create(Both(), limiter);

        await service.GetImageAsync(ArrKind.Sonarr, 1, "poster", 250, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ArrApiException>(() => service.GetImageAsync(ArrKind.Sonarr, 2, "poster", 250, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, client.Calls.Count);
    }
}
