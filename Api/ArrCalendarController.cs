using System.Globalization;
using System.Net.Mime;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ArrCalendar.Arr;
using Jellyfin.Plugin.ArrCalendar.Browser;
using Jellyfin.Plugin.ArrCalendar.Configuration;
using Jellyfin.Plugin.ArrCalendar.Library;
using Jellyfin.Plugin.ArrCalendar.Models;
using Jellyfin.Plugin.ArrCalendar.Posters;
using Jellyfin.Plugin.ArrCalendar.Security;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ArrCalendarPlugin = Jellyfin.Plugin.ArrCalendar.Plugin;

namespace Jellyfin.Plugin.ArrCalendar.Api;

/// <summary>
/// Serves the Calendar page and its data to Jellyfin Web, and its status to the settings page.
/// </summary>
/// <remarks>
/// Sonarr's and Radarr's API keys never leave the server. The page asks this controller, as the
/// signed-in user, and gets back entries built field by field, never an upstream answer.
/// </remarks>
[ApiController]
[Authorize]
[Route("Plugins/ArrCalendar")]
public partial class ArrCalendarController : ControllerBase
{
    /// <summary>
    /// Client files this endpoint serves, keyed by the name in the URL, so a path can never be
    /// used to read another embedded resource.
    /// </summary>
    private static readonly Dictionary<string, (string Resource, string ContentType)> ClientFiles = new(StringComparer.Ordinal)
    {
        ["arr-calendar.js"] = ("Jellyfin.Plugin.ArrCalendar.Web.arr-calendar.js", "application/javascript; charset=utf-8")
    };

    private readonly IPluginManager _pluginManager;
    private readonly BrowserScriptRegistrar _registrar;
    private readonly CalendarService _calendar;
    private readonly LibraryItemFinder _library;
    private readonly IArrClient _client;
    private readonly ArrSecretSource _secrets;
    private readonly ImageSigner _signer;
    private readonly ArrCalendarRateLimiter _rateLimiter;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<ArrCalendarController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrCalendarController"/> class.
    /// </summary>
    /// <param name="pluginManager">Jellyfin's plugin manager, for the kill switch and Seerr Integration.</param>
    /// <param name="registrar">Keeps the page's loader registered with JavaScript Injector.</param>
    /// <param name="calendar">Builds the calendar.</param>
    /// <param name="library">Finds entries in the user's library.</param>
    /// <param name="client">Reads from Sonarr and Radarr, for connection tests.</param>
    /// <param name="secrets">Supplies the API keys.</param>
    /// <param name="signer">Checks artwork URLs.</param>
    /// <param name="rateLimiter">Bounds how often this plugin does work.</param>
    /// <param name="authorizationContext">Jellyfin's authorization context.</param>
    /// <param name="logger">Logger.</param>
    public ArrCalendarController(
        IPluginManager pluginManager,
        BrowserScriptRegistrar registrar,
        CalendarService calendar,
        LibraryItemFinder library,
        IArrClient client,
        ArrSecretSource secrets,
        ImageSigner signer,
        ArrCalendarRateLimiter rateLimiter,
        IAuthorizationContext authorizationContext,
        ILogger<ArrCalendarController> logger)
    {
        _pluginManager = pluginManager;
        _registrar = registrar;
        _calendar = calendar;
        _library = library;
        _client = client;
        _secrets = secrets;
        _signer = signer;
        _rateLimiter = rateLimiter;
        _authorizationContext = authorizationContext;
        _logger = logger;
    }

    private static PluginConfiguration Configuration => ArrCalendarPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a file of the Calendar page.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="v">The plugin version the loader asked for; a match is cached for good.</param>
    /// <response code="200">The file.</response>
    /// <response code="404">The plugin is not active, or no such file exists.</response>
    /// <returns>The file contents.</returns>
    [HttpGet("Client/{fileName}")]
    // Loaded with a script tag, which cannot carry Jellyfin's authorization header. The file is
    // the same public code for everyone and holds nothing about any user or server.
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetClientFile([FromRoute] string fileName, [FromQuery] string? v)
    {
        if (!IsPluginActive() || !ClientFiles.TryGetValue(fileName, out var file))
        {
            return NotFound();
        }

        var stream = typeof(ArrCalendarController).Assembly.GetManifestResourceStream(file.Resource);
        if (stream is null)
        {
            return NotFound();
        }

        // The loader asks for its own version, so a matching URL never changes content. Any
        // other URL is revalidated, so a stale loader still gets the current page.
        Response.Headers.CacheControl = string.Equals(v, BrowserScriptRegistrar.GetVersion(), StringComparison.Ordinal)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(stream, file.ContentType);
    }

    /// <summary>
    /// Gets the episodes and releases between two days, inclusive.
    /// </summary>
    /// <param name="start">The first day, as yyyy-MM-dd.</param>
    /// <param name="end">The last day, as yyyy-MM-dd.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The calendar. A service that failed reports it in <c>sources</c>.</response>
    /// <response code="400">The range is malformed or longer than the calendar answers for.</response>
    /// <response code="403">The Calendar is switched off.</response>
    /// <response code="404">The plugin is not active.</response>
    /// <response code="429">Too many requests; retry after the indicated delay.</response>
    /// <returns>The calendar.</returns>
    [HttpGet("Calendar")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(typeof(CalendarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CalendarResponse>> GetCalendar([FromQuery] string? start, [FromQuery] string? end, CancellationToken cancellationToken)
    {
        if (!IsPluginActive())
        {
            return PluginInactive();
        }

        if (!Configuration.Enabled)
        {
            return Error(StatusCodes.Status403Forbidden, "CalendarDisabled", "The Calendar is switched off on this server.");
        }

        if (!TryParseDay(start, out var from) || !TryParseDay(end, out var to)
            || to < from || to.DayNumber - from.DayNumber + 1 > CalendarService.MaxRangeDays)
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidRange", "Ask for a range of up to " + CalendarService.MaxRangeDays.ToString(CultureInfo.InvariantCulture) + " days, as yyyy-MM-dd.");
        }

        // An API key has no user, so all API keys share one budget, apart from every user's.
        var authorization = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (!_rateLimiter.TryAcquireCalendar(authorization.UserId.ToString("N", CultureInfo.InvariantCulture), out var retryAfter))
        {
            return TooManyRequests(retryAfter);
        }

        var calendar = await _calendar.GetCalendarAsync(from, to, cancellationToken).ConfigureAwait(false);
        Response.Headers.CacheControl = "no-store";
        return calendar;
    }

    /// <summary>
    /// Gets one piece of artwork for a calendar entry.
    /// </summary>
    /// <param name="source"><c>sonarr</c> or <c>radarr</c>.</param>
    /// <param name="id">The series or movie id.</param>
    /// <param name="type"><c>poster</c> or <c>fanart</c>.</param>
    /// <param name="size">A pre-rendered size: 250 or 500 for posters, 180 or 360 for fanart.</param>
    /// <param name="sig">The signature the calendar handed out with the entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The image.</response>
    /// <response code="404">No such image, or the signature does not match.</response>
    /// <response code="503">Too many artwork fetches; retry later.</response>
    /// <returns>The image.</returns>
    [HttpGet("Image/{source}/{id}/{type}")]
    // Loaded by img elements and CSS, which cannot carry Jellyfin's authorization header. The
    // signature stands in for it: see ImageSigner.
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetImage(
        [FromRoute] string source,
        [FromRoute] int id,
        [FromRoute] string type,
        [FromQuery] int? size,
        [FromQuery] string? sig,
        CancellationToken cancellationToken)
    {
        if (!IsPluginActive()
            || !ArrKindNames.TryParse(source, out var kind)
            || id <= 0
            || !CalendarService.ImageSizes.TryGetValue(type, out var sizes))
        {
            return NotFound();
        }

        var chosen = size ?? sizes[0];

        // Checked before anything reads the configuration or the network, so a guessed URL costs
        // one HMAC and learns nothing, not even whether the id exists.
        if (!sizes.Contains(chosen) || !_signer.Verify(kind, id, sig) || !Configuration.Enabled)
        {
            return NotFound();
        }

        try
        {
            var image = await _calendar.GetImageAsync(kind, id, type, chosen, cancellationToken).ConfigureAwait(false);
            if (image is null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "public, max-age=86400";
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(image.Bytes, image.ContentType);
        }
        catch (ArrApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is ArrApiException or ArrConnectionException or ArrConfigurationException)
        {
            _logger.LogDebug(ex, "Artwork {Source}/{Id} could not be loaded", kind.Id(), id);
            return NotFound();
        }
    }

    /// <summary>
    /// Finds a calendar entry in the signed-in user's library.
    /// </summary>
    /// <param name="kind"><c>episode</c> or <c>movie</c>.</param>
    /// <param name="tvdbId">The series' TVDB id.</param>
    /// <param name="tmdbId">The series' or movie's TMDB id.</param>
    /// <param name="imdbId">The series' or movie's IMDb id.</param>
    /// <param name="season">The season number, for an episode.</param>
    /// <param name="episode">The episode number, for an episode.</param>
    /// <response code="200">The item: the episode, its series when the episode is not there yet, or the movie.</response>
    /// <response code="204">Nothing in the user's library matches.</response>
    /// <response code="400">The ids are malformed.</response>
    /// <response code="401">The caller is not a Jellyfin user.</response>
    /// <response code="404">The plugin is not active.</response>
    /// <response code="429">Too many requests; retry after the indicated delay.</response>
    /// <returns>The match.</returns>
    [HttpGet("Library")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(typeof(LibraryItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<LibraryItemResponse>> GetLibraryItem(
        [FromQuery] string? kind,
        [FromQuery] int? tvdbId,
        [FromQuery] int? tmdbId,
        [FromQuery] string? imdbId,
        [FromQuery] int? season,
        [FromQuery] int? episode)
    {
        if (!IsPluginActive())
        {
            return PluginInactive();
        }

        if (!Configuration.Enabled)
        {
            return Error(StatusCodes.Status403Forbidden, "CalendarDisabled", "The Calendar is switched off on this server.");
        }

        // The lookup runs with the user's own library access, so it needs a user: an API key
        // alone would otherwise see every library.
        var authorization = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (authorization.UserId.Equals(Guid.Empty))
        {
            return Error(StatusCodes.Status401Unauthorized, "MissingJellyfinUser", "Looking in the library needs a signed-in user.");
        }

        var userKey = authorization.UserId.ToString("N", CultureInfo.InvariantCulture);
        if (!_rateLimiter.TryAcquireLibrary(userKey, out var retryAfter))
        {
            return TooManyRequests(retryAfter);
        }

        if ((kind != "episode" && kind != "movie")
            || tvdbId is <= 0 || tmdbId is <= 0
            || (imdbId is not null && !ImdbPattern().IsMatch(imdbId))
            || season is < 0 or > 100000 || episode is < 0 or > 100000)
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidLookup", "The lookup ids are malformed.");
        }

        var ids = new ProviderIds(kind == "episode" ? tvdbId : null, tmdbId, imdbId);
        if (ids.IsEmpty)
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidLookup", "Give at least one id to look up.");
        }

        var match = kind == "episode"
            ? _library.FindEpisode(authorization.UserId, ids, season, episode)
            : _library.FindMovie(authorization.UserId, ids);
        return match is null ? NoContent() : match;
    }

    /// <summary>
    /// Gets whether the Calendar is reaching Jellyfin Web, for the settings page.
    /// </summary>
    /// <response code="200">The registration state.</response>
    /// <response code="404">The plugin is not active.</response>
    /// <returns>The registration state.</returns>
    [HttpGet("Registration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(typeof(RegistrationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<RegistrationStatusResponse> GetRegistration()
    {
        if (!IsPluginActive())
        {
            return NotFound();
        }

        var configuration = Configuration;
        var seerr = _pluginManager.Plugins.FirstOrDefault(candidate => candidate.Id.Equals(SeerrIntegrationPlugin.Id));
        var seerrActive = seerr is not null && seerr.IsEnabledAndSupported && seerr.Manifest.Status == PluginStatus.Active;
        return new RegistrationStatusResponse
        {
            Enabled = configuration.Enabled,
            LoaderState = _registrar.State.ToString(),
            LoaderDetail = _registrar.Detail,
            Version = BrowserScriptRegistrar.GetVersion(),
            SeerrIntegrationActive = seerrActive,
            SeerrIntegrationRequestsEnabled = seerrActive ? IsSeerrRequestsPageOn(seerr) : null,
            Sonarr = DescribeSource(ArrKind.Sonarr, configuration),
            Radarr = DescribeSource(ArrKind.Radarr, configuration)
        };
    }

    /// <summary>
    /// Reads whether Seerr Integration shows its Requests page, from its own settings.
    /// </summary>
    /// <remarks>
    /// Calendar only nests under Requests when there is a Requests entry, which Seerr Integration
    /// adds only while its page is switched on. Its configuration type lives in its own assembly,
    /// so the switch is read by name. Anything unexpected reads as unknown rather than failing the
    /// settings page.
    /// </remarks>
    /// <param name="seerr">Seerr Integration's plugin record.</param>
    /// <returns>The switch, or <c>null</c> when it cannot be read.</returns>
    internal static bool? IsSeerrRequestsPageOn(LocalPlugin? seerr)
    {
        try
        {
            var settings = (seerr?.Instance as IHasPluginConfiguration)?.Configuration;
            return settings?.GetType().GetProperty("Enabled")?.GetValue(settings) as bool?;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tests an address and key from the settings page, saved or not.
    /// </summary>
    /// <param name="request">What to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The outcome, including a failed connection.</response>
    /// <response code="400">The request names no service.</response>
    /// <response code="404">The plugin is not active.</response>
    /// <response code="429">Too many requests; retry after the indicated delay.</response>
    /// <returns>The outcome.</returns>
    [HttpPost("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(typeof(TestConnectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<TestConnectionResponse>> TestConnection([FromBody] TestConnectionRequest? request, CancellationToken cancellationToken)
    {
        if (!IsPluginActive())
        {
            return NotFound();
        }

        if (!_rateLimiter.TryAcquireAdmin(out var retryAfter))
        {
            return TooManyRequests(retryAfter);
        }

        if (request is null || !ArrKindNames.TryParse(request.Source, out var kind))
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidSource", "Name sonarr or radarr.");
        }

        var configuration = Configuration;
        var name = kind.DisplayName();
        var url = request.Url?.Trim() ?? string.Empty;
        var apiKey = request.ApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            // The saved key, or the environment's, is only ever sent to the saved address.
            // Otherwise this button would post a key the administrator cannot read back, such as
            // one from the environment, to any host typed into the field.
            apiKey = IsSavedAddress(kind, url, configuration) ? _secrets.ResolveApiKey(kind, configuration) : null;
            if (apiKey is null)
            {
                return new TestConnectionResponse { Ok = false, Message = "Enter " + name + "'s API key to test this address." };
            }
        }

        try
        {
            var connection = ArrConnection.Create(kind, url, apiKey);
            var status = await _client.GetJsonAsync(connection, "system/status", null, configuration.GetRequestTimeout(), cancellationToken).ConfigureAwait(false);
            var appName = JsonRead.String(status, "appName");
            var version = JsonRead.String(status, "version");
            if (!string.Equals(appName, name, StringComparison.OrdinalIgnoreCase))
            {
                return new TestConnectionResponse
                {
                    Ok = false,
                    Message = appName is not null && AppNamePattern().IsMatch(appName)
                        ? "That address is " + appName + ", not " + name + "."
                        : "That address answered, but not as " + name + "."
                };
            }

            var cleanVersion = version is not null && VersionPattern().IsMatch(version) ? version : null;
            return new TestConnectionResponse
            {
                Ok = true,
                Version = cleanVersion,
                Message = "Connected to " + name + (cleanVersion is null ? string.Empty : " " + cleanVersion) + "."
            };
        }
        catch (Exception ex) when (ex is ArrApiException or ArrConnectionException or ArrConfigurationException)
        {
            // These messages are this plugin's own wording, never an upstream body.
            return new TestConnectionResponse { Ok = false, Message = ex.Message };
        }
    }

    private static bool TryParseDay(string? value, out DateOnly day)
    {
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    private static bool IsSavedAddress(ArrKind kind, string url, PluginConfiguration configuration)
    {
        try
        {
            return ArrConnection.ParseApiRoot(kind, url) == ArrConnection.ParseApiRoot(kind, configuration.GetUrl(kind));
        }
        catch (ArrConfigurationException)
        {
            return false;
        }
    }

    private SourceConfigurationStatus DescribeSource(ArrKind kind, PluginConfiguration configuration)
    {
        var fromEnvironment = _secrets.GetEnvironmentApiKey(kind) is not null;
        var status = new SourceConfigurationStatus
        {
            Configured = _secrets.IsConfigured(kind, configuration),
            ApiKeySource = fromEnvironment ? "environment" : configuration.GetStoredApiKey(kind).Trim().Length > 0 ? "configuration" : "none"
        };

        if (status.Configured)
        {
            try
            {
                ArrConnection.Create(kind, configuration.GetUrl(kind), _secrets.ResolveApiKey(kind, configuration));
            }
            catch (ArrConfigurationException ex)
            {
                status.Problem = ex.Message;
            }
        }

        return status;
    }

    /// <summary>
    /// Determines whether Jellyfin currently considers this plugin fully active.
    /// </summary>
    /// <remarks>
    /// Controllers stay routed after the plugin is disabled at runtime, and a disabled plugin's
    /// in-memory status is <see cref="PluginStatus.Restart"/>, which <c>IsEnabledAndSupported</c>
    /// still counts as enabled. Fails closed when no record matches this assembly.
    /// </remarks>
    /// <returns><c>true</c> only if the plugin record is present, supported, and active.</returns>
    private bool IsPluginActive()
    {
        var plugin = _pluginManager.Plugins.FirstOrDefault(candidate => candidate.Id.Equals(ArrCalendarPlugin.PluginId));
        return plugin is not null
            && plugin.IsEnabledAndSupported
            && plugin.Manifest.Status == PluginStatus.Active;
    }

    private ObjectResult PluginInactive()
    {
        return Error(StatusCodes.Status404NotFound, "PluginInactive", "Arr Calendar is not active.");
    }

    private ObjectResult TooManyRequests(TimeSpan retryAfter)
    {
        Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Error(StatusCodes.Status429TooManyRequests, "RateLimited", "Too many requests. Try again shortly.");
    }

    private ObjectResult Error(int statusCode, string error, string message)
    {
        return StatusCode(statusCode, new ErrorResponse(statusCode, error, message));
    }

    [GeneratedRegex(@"^tt\d{1,10}$")]
    private static partial Regex ImdbPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9 ]{0,39}$")]
    private static partial Regex AppNamePattern();

    [GeneratedRegex(@"^[0-9][0-9A-Za-z.\-]{0,39}$")]
    private static partial Regex VersionPattern();
}
