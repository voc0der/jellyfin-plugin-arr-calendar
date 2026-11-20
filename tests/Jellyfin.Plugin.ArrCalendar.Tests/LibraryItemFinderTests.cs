using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ArrCalendar.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using NSubstitute;

namespace Jellyfin.Plugin.ArrCalendar.Tests;

public class LibraryItemFinderTests
{
    private static readonly Guid UserId = Guid.Parse("4e6ea056-9821-4e7c-940d-2af1806f1f8e");

    private static (LibraryItemFinder Finder, ILibraryManager Library, List<InternalItemsQuery> Queries) Create(
        Func<InternalItemsQuery, IReadOnlyList<BaseItem>> answer,
        bool userExists = true)
    {
        var library = Substitute.For<ILibraryManager>();
        var queries = new List<InternalItemsQuery>();
        library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(call =>
        {
            var query = call.Arg<InternalItemsQuery>();
            queries.Add(query);
            return answer(query);
        });
        var users = Substitute.For<IUserManager>();
        users.GetUserById(UserId).Returns(userExists ? new User("alice", "Default", "Default") : null);
        return (new LibraryItemFinder(library, users), library, queries);
    }

    [Fact]
    public void FindEpisode_FindsTheSeriesByAnyIdAndThenTheEpisodeAsTheUser()
    {
        var series = new Series { Id = Guid.NewGuid() };
        var episode = new Episode { Id = Guid.NewGuid() };
        var (finder, _, queries) = Create(query => query.IncludeItemTypes[0] == BaseItemKind.Series ? [series] : [episode]);

        var match = finder.FindEpisode(UserId, new ProviderIds(76177, 1667, "tt0072562"), 52, 4);

        Assert.Equal(episode.Id.ToString("N"), match!.ItemId);
        Assert.Equal("episode", match.Kind);
        Assert.Equal(2, queries.Count);
        Assert.Equal("alice", queries[0].User!.Username);
        Assert.Equal(["76177"], queries[0].HasAnyProviderIds!["Tvdb"]);
        Assert.Equal(["1667"], queries[0].HasAnyProviderIds!["Tmdb"]);
        Assert.Equal(["tt0072562"], queries[0].HasAnyProviderIds!["Imdb"]);
        Assert.Equal([series.Id], queries[1].AncestorIds);
        Assert.Equal(52, queries[1].ParentIndexNumber);
        Assert.Equal(4, queries[1].IndexNumber);
        Assert.False(queries[1].IsVirtualItem);
        Assert.Same(queries[0].User, queries[1].User);
    }

    [Fact]
    public void FindEpisode_FallsBackToTheSeriesWhenTheEpisodeIsNotThereYet()
    {
        var series = new Series { Id = Guid.NewGuid() };
        var (finder, _, _) = Create(query => query.IncludeItemTypes[0] == BaseItemKind.Series ? [series] : []);

        var match = finder.FindEpisode(UserId, new ProviderIds(76177, null, null), 52, 9);

        Assert.Equal(series.Id.ToString("N"), match!.ItemId);
        Assert.Equal("series", match.Kind);
    }

    [Fact]
    public void FindEpisode_WithoutNumbersLinksTheSeries()
    {
        var series = new Series { Id = Guid.NewGuid() };
        var (finder, _, queries) = Create(_ => [series]);

        Assert.Equal("series", finder.FindEpisode(UserId, new ProviderIds(1, null, null), null, null)!.Kind);
        Assert.Single(queries);
    }

    [Fact]
    public void FindMovie_LooksForARealMovieAsTheUser()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        var (finder, _, queries) = Create(_ => [movie]);

        var match = finder.FindMovie(UserId, new ProviderIds(null, 969681, "tt22084616"));

        Assert.Equal("movie", match!.Kind);
        Assert.Equal(movie.Id.ToString("N"), match.ItemId);
        Assert.Equal([BaseItemKind.Movie], queries[0].IncludeItemTypes);
        Assert.False(queries[0].HasAnyProviderIds!.ContainsKey("Tvdb"));
        Assert.False(queries[0].IsVirtualItem);
    }

    [Fact]
    public void Finders_AnswerNothingForAMissingUserNoIdsOrNoMatch()
    {
        var (missingUser, library, _) = Create(_ => [new Movie()], userExists: false);
        Assert.Null(missingUser.FindMovie(UserId, new ProviderIds(null, 1, null)));
        Assert.Null(missingUser.FindEpisode(UserId, new ProviderIds(1, null, null), 1, 1));
        library.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());

        var (noMatch, _, _) = Create(_ => []);
        Assert.Null(noMatch.FindMovie(UserId, new ProviderIds(null, 1, null)));
        Assert.Null(noMatch.FindEpisode(UserId, new ProviderIds(1, null, null), 1, 1));
        Assert.Null(noMatch.FindMovie(UserId, new ProviderIds(null, null, null)));
    }
}
