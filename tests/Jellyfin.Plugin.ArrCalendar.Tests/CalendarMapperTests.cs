using System.Text.Json;
using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Models;
using Jellyfin.Plugin.ArrCalendar.Posters;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

/// <remarks>
/// <c>sonarr-calendar.json</c> and <c>radarr-calendar.json</c> are answers recorded from real
/// Sonarr 4.0.20 and Radarr 7.0.3 instances, trimmed to a few entries. The queue fixtures are
/// built from the services' documented QueueResource shape, since recording one needs a live
/// download.
/// </remarks>
public class CalendarMapperTests
{
    private static readonly ImageSigner Signer = new(Enumerable.Repeat((byte)7, 32).ToArray());

    private static List<CalendarEvent> Sonarr(JsonElement? queue = null)
        => CalendarMapper.MapSonarr(Fixtures.Json("sonarr-calendar.json"), queue, Signer);

    private static List<CalendarEvent> Radarr(DateOnly from, DateOnly to, JsonElement? queue = null)
        => CalendarMapper.MapRadarr(Fixtures.Json("radarr-calendar.json"), queue, Signer, from, to);

    [Fact]
    public void MapSonarr_CopiesTheFieldsTheCalendarShows()
    {
        var snl = Sonarr().Single(item => item.Title == "Saturday Night Live");

        Assert.Equal("sonarr:episode:2280", snl.Key);
        Assert.Equal("episode", snl.Kind);
        Assert.Equal(2, snl.SeriesId);
        Assert.Equal("Inde Navarrette / Gracie Abrams", snl.EpisodeTitle);
        Assert.Equal(52, snl.Season);
        Assert.Equal(4, snl.Episode);
        Assert.Equal("2026-11-01T03:30:00Z", snl.AirDateUtc);
        Assert.Equal("2026-10-31", snl.AirDate);
        Assert.Equal(67, snl.Runtime);
        Assert.Equal("NBC", snl.Network);
        Assert.Equal("TV-14", snl.Certification);
        Assert.True(snl.HasFile);
        Assert.True(snl.Monitored);
        Assert.Equal("Inde Navarrette hosts, and Gracie Abrams performs.", snl.Overview);
        Assert.Null(snl.OverviewIsSeries);
        Assert.Equal(["Comedy"], snl.Genres);
        Assert.Equal(76177, snl.TvdbId);
        Assert.Equal(1667, snl.TmdbId);
        Assert.Equal("tt0072562", snl.ImdbId);
        Assert.Equal("sonarr/2", snl.Image);
        Assert.Equal(Signer.Sign(ArrKind.Sonarr, 2), snl.ImageSig);
        Assert.Null(snl.Download);
        Assert.Null(snl.Absolute);
    }

    [Fact]
    public void MapSonarr_FallsBackToTheSeriesRuntimeWhenTheEpisodeHasNone()
    {
        // Sonarr reports runtime 0 for an episode whose length is not known yet.
        var simpsons = Sonarr().Single(item => item.Title == "The Simpsons");
        Assert.True(simpsons.Runtime > 0);
    }

    [Fact]
    public void MapSonarr_LeavesOutATmdbIdOfZero()
    {
        var seth = Sonarr().Single(item => item.Title == "Late Night with Seth Meyers");
        Assert.Null(seth.TmdbId);
        Assert.Equal(270262, seth.TvdbId);
        Assert.False(seth.HasFile);
    }

    [Fact]
    public void MapSonarr_NeverCarriesServerPathsOrUpstreamOnlyFields()
    {
        var json = JsonSerializer.Serialize(Sonarr());

        Assert.Contains("\"path\": \"/tv/", Fixtures.Text("sonarr-calendar.json"), StringComparison.Ordinal);
        Assert.DoesNotContain("/tv/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("qualityProfileId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("remoteUrl", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tags", json, StringComparison.Ordinal);
        Assert.DoesNotContain("artworks.thetvdb.com", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MapSonarr_FillsInTheSeriesOverviewWhenTheEpisodeHasNone()
    {
        var calendar = JsonSerializer.Deserialize<JsonElement>("""
            [{ "id": 1, "seriesId": 5, "seasonNumber": 1, "episodeNumber": 1, "airDateUtc": "2026-11-20T02:00:00Z", "overview": "",
               "series": { "title": "Show", "overview": "About the show.", "seriesType": "anime" }, "absoluteEpisodeNumber": 25 }]
            """);

        var item = Assert.Single(CalendarMapper.MapSonarr(calendar, null, Signer));

        Assert.Equal("About the show.", item.Overview);
        Assert.True(item.OverviewIsSeries);
        Assert.Equal(25, item.Absolute);
        Assert.Null(item.AirDate);
    }

    [Fact]
    public void MapSonarr_SkipsEntriesWithoutAnIdOrADate()
    {
        var calendar = JsonSerializer.Deserialize<JsonElement>("""
            [{ "seriesId": 1, "airDateUtc": "2026-11-20T02:00:00Z" },
             { "id": 2, "seriesId": 1 },
             { "id": 3, "airDate": "not-a-date" },
             "not an object",
             { "id": 4, "airDate": "2026-11-21", "series": { "title": "Kept" }, "finaleType": "Season" }]
            """);

        var item = Assert.Single(CalendarMapper.MapSonarr(calendar, null, Signer));

        Assert.Equal("sonarr:episode:4", item.Key);
        Assert.Equal("Kept", item.Title);
        Assert.Equal("2026-11-21", item.AirDate);
        Assert.Null(item.AirDateUtc);
        Assert.Equal("season", item.Finale);
        Assert.Null(item.Image);
    }

    [Fact]
    public void MapSonarr_IgnoresAnAnswerThatIsNotAList()
    {
        Assert.Empty(CalendarMapper.MapSonarr(JsonSerializer.Deserialize<JsonElement>("{\"message\":\"x\"}"), null, Signer));
    }

    [Fact]
    public void MapSonarr_AttachesDownloadProgressFromTheQueue()
    {
        var seth = Sonarr(Fixtures.Json("sonarr-queue.json")).Single(item => item.Title == "Late Night with Seth Meyers");

        Assert.NotNull(seth.Download);
        Assert.Equal(62, seth.Download.Progress);
        Assert.Equal("downloading", seth.Download.State);
        Assert.Equal("2026-11-20T03:12:00Z", seth.Download.Eta);
    }

    [Fact]
    public void Downloads_CombineAPackAndReportEachState()
    {
        var downloads = CalendarMapper.Downloads(Fixtures.Json("sonarr-queue.json"), "episodeId");

        // Two halves of a pack: 1 GB in total, 0.7 GB left, one half still downloading.
        Assert.Equal(30, downloads[9800].Progress);
        Assert.Equal("downloading", downloads[9800].State);
        Assert.Equal("2026-11-20T04:00:00Z", downloads[9800].Eta);
        Assert.Equal("importing", downloads[9901].State);
        Assert.Equal(100, downloads[9901].Progress);
        Assert.Equal("warning", downloads[9902].State);
        Assert.DoesNotContain(0, downloads.Keys);
        Assert.Equal(4, downloads.Count);

        var movies = CalendarMapper.Downloads(Fixtures.Json("radarr-queue.json"), "movieId");
        Assert.Equal(40, movies[1].Progress);
        Assert.Null(movies[77].Progress);
        Assert.Equal("paused", movies[77].State);
        Assert.Empty(CalendarMapper.Downloads(null, "movieId"));
    }

    [Theory]
    [InlineData("""{"status":"queued"}""", "queued")]
    [InlineData("""{"status":"delay"}""", "queued")]
    [InlineData("""{"status":"downloadClientUnavailable"}""", "queued")]
    [InlineData("""{"status":"paused"}""", "paused")]
    [InlineData("""{"status":"completed","trackedDownloadState":"importing"}""", "importing")]
    [InlineData("""{"status":"completed","trackedDownloadState":"importBlocked"}""", "warning")]
    [InlineData("""{"status":"failed"}""", "warning")]
    [InlineData("""{"status":"downloading","trackedDownloadStatus":"error"}""", "warning")]
    [InlineData("""{"status":"downloading","trackedDownloadStatus":"ok"}""", "downloading")]
    [InlineData("""{}""", "downloading")]
    public void StateOf_MapsQueueStatesToThePagesTerms(string item, string expected)
    {
        Assert.Equal(expected, CalendarMapper.StateOf(JsonSerializer.Deserialize<JsonElement>(item)));
    }

    [Fact]
    public void Downloads_KeepProgressInsideZeroToAHundredWhateverTheNumbersSay()
    {
        var queue = JsonSerializer.Deserialize<JsonElement>("""
            [{ "movieId": 1, "size": 100, "sizeleft": 500 }, { "movieId": 2, "size": 100, "sizeleft": -5 }, { "movieId": 3, "size": 100, "sizeLeft": 25 }]
            """);

        var downloads = CalendarMapper.Downloads(queue, "movieId");

        Assert.Equal(0, downloads[1].Progress);
        Assert.Equal(100, downloads[2].Progress);
        Assert.Equal(75, downloads[3].Progress);
    }

    [Fact]
    public void MapRadarr_AddsOneEntryPerReleaseInsideTheRange()
    {
        var events = Radarr(new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31));

        Assert.Equal(
            ["radarr:movie:11:physical", "radarr:movie:1:cinema"],
            events.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray());

        var spider = events.Single(item => item.MovieId == 11);
        Assert.Equal("movie", spider.Kind);
        Assert.Equal("Spider-Man: Brand New Day", spider.Title);
        Assert.Equal("physical", spider.Release);
        Assert.Equal("2026-12-02", spider.Date);
        Assert.Equal("2026-07-29", spider.Releases!["cinema"]);
        Assert.Equal("2026-10-06", spider.Releases["digital"]);
        Assert.Equal("2026-12-02", spider.Releases["physical"]);
        Assert.True(spider.HasFile);
        Assert.True(spider.Available);
        Assert.Equal(2026, spider.Year);
        Assert.Equal(145, spider.Runtime);
        Assert.Equal("Marvel Studios", spider.Studio);
        Assert.Equal(969681, spider.TmdbId);
        Assert.Equal("tt22084616", spider.ImdbId);
        Assert.Equal("radarr/11", spider.Image);
        Assert.Equal(Signer.Sign(ArrKind.Radarr, 11), spider.ImageSig);
        Assert.Null(spider.TvdbId);

        var hunger = events.Single(item => item.MovieId == 1);
        Assert.False(hunger.Available);
        Assert.False(hunger.HasFile);
        Assert.Equal("2026-11-18", hunger.Date);
    }

    [Fact]
    public void MapRadarr_KeepsReleaseDaysAsCalendarDays()
    {
        // Radarr stores release dates as UTC midnight; the day must not move with time zones.
        var movie = Radarr(new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 18)).Single();
        Assert.Equal("2026-11-18", movie.Date);
        Assert.Empty(Radarr(new DateOnly(2026, 11, 19), new DateOnly(2026, 11, 30)));
    }

    [Fact]
    public void MapRadarr_NeverCarriesServerPaths()
    {
        var json = JsonSerializer.Serialize(Radarr(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)));

        Assert.Contains("\"path\": \"/movies/", Fixtures.Text("radarr-calendar.json"), StringComparison.Ordinal);
        Assert.DoesNotContain("/movies/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("image.tmdb.org", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MapRadarr_AttachesDownloadProgressFromTheQueue()
    {
        var hunger = Radarr(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), Fixtures.Json("radarr-queue.json")).Single();

        Assert.Equal(40, hunger.Download!.Progress);
        Assert.Equal("downloading", hunger.Download.State);
    }

    [Fact]
    public void Sort_OrdersByTimeThenTitle()
    {
        var events = new List<CalendarEvent>
        {
            new() { Key = "c", Title = "Zed", AirDateUtc = "2026-11-20T02:00:00Z" },
            new() { Key = "b", Title = "Movie", Date = "2026-11-20" },
            new() { Key = "a", Title = "Alpha", AirDateUtc = "2026-11-20T02:00:00Z" },
            new() { Key = "d", Title = "Early", AirDate = "2026-11-19" }
        };

        Assert.Equal(["d", "b", "a", "c"], CalendarMapper.Sort(events).Select(item => item.Key).ToArray());
    }

    [Theory]
    [InlineData("2026-11-01T03:30:00Z", "2026-11-01T03:30:00Z")]
    [InlineData("2026-11-01T03:30:00.123Z", "2026-11-01T03:30:00Z")]
    [InlineData("2026-11-01T03:30:00", "2026-11-01T03:30:00Z")]
    [InlineData("2026-10-31T23:30:00-04:00", "2026-11-01T03:30:00Z")]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Instant_NormalizesToUtc(string? value, string? expected)
    {
        Assert.Equal(expected, CalendarMapper.Instant(value));
    }

    [Theory]
    [InlineData("2026-11-18T00:00:00Z", "2026-11-18")]
    [InlineData("2026-11-18", "2026-11-18")]
    [InlineData("2026-02-30", null)]
    [InlineData("2026-11", null)]
    [InlineData(null, null)]
    public void DateOnlyText_TakesTheDayAsWritten(string? value, string? expected)
    {
        Assert.Equal(expected, CalendarMapper.DateOnlyText(value));
    }

    [Fact]
    public void Overview_IsTrimmedToAWholeWord()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 400));

        var trimmed = CalendarMapper.Overview(words)!;

        Assert.True(trimmed.Length <= CalendarMapper.MaxOverviewLength);
        Assert.EndsWith("word…", trimmed, StringComparison.Ordinal);
        Assert.Null(CalendarMapper.Overview("   "));
        Assert.Equal("Short.", CalendarMapper.Overview(" Short. "));
    }
}
