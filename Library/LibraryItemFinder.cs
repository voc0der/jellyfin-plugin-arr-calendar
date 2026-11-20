using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArrCalendar.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArrCalendar.Library;

/// <summary>
/// Ids a calendar entry carries, to find it in Jellyfin's library.
/// </summary>
/// <param name="TvdbId">The TVDB id.</param>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="ImdbId">The IMDb id.</param>
public sealed record ProviderIds(int? TvdbId, int? TmdbId, string? ImdbId)
{
    /// <summary>
    /// Gets a value indicating whether there is anything to look up by.
    /// </summary>
    public bool IsEmpty => TvdbId is null && TmdbId is null && ImdbId is null;

    /// <summary>
    /// Gets the ids in the shape <see cref="InternalItemsQuery.HasAnyProviderIds"/> takes.
    /// </summary>
    /// <returns>The ids by provider.</returns>
    public Dictionary<string, string[]> ToQuery()
    {
        var ids = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (TvdbId is not null)
        {
            ids[nameof(MetadataProvider.Tvdb)] = [TvdbId.Value.ToString(CultureInfo.InvariantCulture)];
        }

        if (TmdbId is not null)
        {
            ids[nameof(MetadataProvider.Tmdb)] = [TmdbId.Value.ToString(CultureInfo.InvariantCulture)];
        }

        if (ImdbId is not null)
        {
            ids[nameof(MetadataProvider.Imdb)] = [ImdbId];
        }

        return ids;
    }
}

/// <summary>
/// Finds the item a calendar entry refers to in a user's Jellyfin library.
/// </summary>
/// <remarks>
/// Every query is made as the user, so Jellyfin applies their library access and parental
/// controls: an entry links to something only if that user could open it anyway.
/// </remarks>
public sealed class LibraryItemFinder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryItemFinder"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="userManager">Jellyfin's users.</param>
    public LibraryItemFinder(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
    }

    /// <summary>
    /// Finds an episode, or its series when the episode is not in the library.
    /// </summary>
    /// <param name="userId">The user looking.</param>
    /// <param name="ids">The series' ids.</param>
    /// <param name="season">The season number.</param>
    /// <param name="episode">The episode number.</param>
    /// <returns>The match, or <c>null</c>.</returns>
    public LibraryItemResponse? FindEpisode(Guid userId, ProviderIds ids, int? season, int? episode)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var user = _userManager.GetUserById(userId);
        if (user is null || ids.IsEmpty)
        {
            return null;
        }

        var series = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Series],
            HasAnyProviderIds = ids.ToQuery(),
            Recursive = true,
            Limit = 1,
            DtoOptions = new DtoOptions(false)
        }).FirstOrDefault();
        if (series is null)
        {
            return null;
        }

        if (season is not null && episode is not null)
        {
            var match = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [series.Id],
                ParentIndexNumber = season,
                IndexNumber = episode,
                IsVirtualItem = false,
                Recursive = true,
                Limit = 1,
                DtoOptions = new DtoOptions(false)
            }).FirstOrDefault();
            if (match is not null)
            {
                return new LibraryItemResponse { ItemId = match.Id.ToString("N", CultureInfo.InvariantCulture), Kind = "episode" };
            }
        }

        return new LibraryItemResponse { ItemId = series.Id.ToString("N", CultureInfo.InvariantCulture), Kind = "series" };
    }

    /// <summary>
    /// Finds a movie.
    /// </summary>
    /// <param name="userId">The user looking.</param>
    /// <param name="ids">The movie's ids.</param>
    /// <returns>The match, or <c>null</c>.</returns>
    public LibraryItemResponse? FindMovie(Guid userId, ProviderIds ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var user = _userManager.GetUserById(userId);
        if (user is null || ids.IsEmpty)
        {
            return null;
        }

        var movie = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            HasAnyProviderIds = ids.ToQuery(),
            IsVirtualItem = false,
            Recursive = true,
            Limit = 1,
            DtoOptions = new DtoOptions(false)
        }).FirstOrDefault();

        return movie is null ? null : new LibraryItemResponse { ItemId = movie.Id.ToString("N", CultureInfo.InvariantCulture), Kind = "movie" };
    }
}
