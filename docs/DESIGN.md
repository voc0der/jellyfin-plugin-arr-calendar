# Design

## Responsibilities

```text
Sonarr, Radarr   hold the schedule, the files and the download queues
Arr Calendar     reads them with keys only it holds, and draws one calendar
Jellyfin         provides the signed-in user, the page host, and the library entries link into
```

The plugin's server side keeps a small loader registered with JavaScript Injector, serves the
page's script, answers the page's calendar, artwork and library lookups, and reports its state to
the settings page. The page itself only ever talks to this plugin.

## What never reaches a browser

These are requirements, not preferences. A change that breaks one of them is a bug even if every
test passes.

```text
- Sonarr's and Radarr's API keys never leave the server: not in a response, an error or a log line
- a key travels only to its own service's /api/v3/ root, and redirects are refused
- a key from the environment, or a saved one, is only ever sent to the saved address
- the calendar answer is built field by field; no upstream body is ever passed through
- the browser never contacts Sonarr, Radarr, TVDB or TMDB
- artwork is served through Jellyfin, and only by a URL the calendar signed
- every endpoint stops answering when the plugin is disabled
- a library link is looked up as the signed-in user, with their own library access
```

### Keys

A Sonarr or Radarr API key is a full administrator credential for that service. The settings page
stores keys in the plugin configuration like any other plugin, which writes them to Jellyfin's
configuration XML and lets any administrator credential read them back. An operator who wants
neither can supply them in Jellyfin's environment instead:

```text
ARR_CALENDAR_SONARR_API_KEY_FILE   path to a file holding Sonarr's key   (preferred)
ARR_CALENDAR_SONARR_API_KEY        Sonarr's key
ARR_CALENDAR_RADARR_API_KEY_FILE   path to a file holding Radarr's key   (preferred)
ARR_CALENDAR_RADARR_API_KEY        Radarr's key
```

The environment wins over the configuration. The names are deliberately not prefixed
`JELLYFIN_`: Jellyfin logs every such variable, with its value, at startup. When the environment
supplies a key, the settings page says so and makes the field inert. This is the same scheme
Seerr Proxy uses for Seerr's key.

**Test connection** checks the address and key as typed, saved or not. When the key field is left
empty it falls back to the saved or environment key, but only if the address is the saved one.
Otherwise the button would send a key the administrator cannot read back to any host typed into
the field.

### The HTTP client

`ArrClient` builds every URL from `ArrConnection`, which checks that the composed URL is still
under the service's `/api/v3/` root. Its handler refuses redirects: the key travels in
`X-Api-Key`, and .NET carries a custom header to whatever host a redirect names. A 3xx is reported
to the administrator as a wrong address instead.

Bodies are read into memory up to a bound (16 MiB for JSON, 5 MiB for an image). A success that is
not JSON means something in front of the service answered, such as a login page, and is refused
rather than relayed. Error messages are this plugin's own wording; an upstream error body is never
read into one.

HTTPS certificates are verified, except on private, loopback and link-local IP addresses, where
self-signed certificates are normal. Host names are always verified. This is Torrent Throttle's
rule, and `tests` carries the same address table. The handler is shared by Sonarr and Radarr, so
`LocalAddress.ValidateCertificate` decides per connection, from the host the connection was opened
for (`SslStream.TargetHostName`).

### The calendar answer

`CalendarMapper` copies named fields from Sonarr's and Radarr's answers onto `CalendarEvent`. Both
services embed server paths (`path`, `folderName`), quality profiles and tags in the same objects,
and `includeSeries` repeats the whole series, seasons and all, in every episode, so a pass-through
would hand every user the server's folder layout. Recorded answers from real instances, paths
included, are the fixtures that prove none of it survives.

Each service reports its own state in `sources`. One failing never hides the other, and the
message every user sees says what is wrong, not where the service lives; the address and the
underlying error go to the server log, and Test connection shows them to the administrator.

### Artwork

Posters and fanart come from each service's own pre-rendered covers
(`/api/v3/mediacover/{id}/poster-250.jpg` and friends): about 20 KB, against hundreds of kilobytes
for the TVDB and TMDB originals, and never requested from a third party by the browser.

Browsers load artwork with `img` and CSS, which cannot carry Jellyfin's authorization header, so
the image endpoint cannot ask who is calling. The services' ids are small sequential numbers,
though, and an open endpoint keyed by them would let anyone walk the library. Each artwork path
the calendar hands out therefore carries an HMAC of the service and id, under a key that is new at
every start and never leaves the process. A URL without a valid signature is a 404, decided before
the configuration or the network is touched. Only cache misses reach the services, and those are
capped at 600 a minute process-wide.

## Getting the page into Jellyfin Web

Exactly as Seerr Integration does it, for the same reasons (see its `docs/DESIGN.md`). A plugin
cannot add a user-facing page to Jellyfin Web by itself, so a small ES5 loader is registered with
[JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) as a script that
requires authentication. It adds one script tag for
`Plugins/ArrCalendar/Client/arr-calendar.js?v=<plugin version>`, built with `ApiClient.getUrl` so
a base URL is honoured. The URL carries the version, so it is cached for good and changes with each
release. The loader is unregistered when the Calendar is switched off and when the plugin is
uninstalled.

## Navigation

Calendar sits near the top of the navigation. When Seerr Integration is installed, a device that
can hover gets it as a small menu under Requests instead; a touch device, which cannot hover, keeps
it as its own entry, right after Requests.

| Display mode | Without Seerr Integration | With it, mouse | With it, touch |
| --- | --- | --- | --- |
| Auto, wide screen | app bar link after Favorites | menu under Requests, on hover or focus | app bar link after Requests |
| Auto, narrow screen | phone menu item after Favorites | revealed under Requests on hover | phone menu item after Requests |
| Desktop, Mobile (Legacy) | drawer entry right after Home | revealed under Requests on hover | drawer entry right after Requests |
| TV | the same drawer entry, not reachable | | |

"Can hover" is `matchMedia('(hover: hover) and (pointer: fine)')`, re-checked when it changes, so a
tablet that gains a mouse moves Calendar under Requests.

The app bar menu lives on `<body>`, positioned under the Requests link when it opens, so nothing in
the app bar can clip it. It opens on hover and focus, Arrow Down moves into it, Escape closes it
and returns focus, and moving from Requests into it keeps it open. In the drawers the Calendar
entry stays in the list right after Requests, collapsed until Requests or the entry itself is
hovered or focused, and stays open while the Calendar is the current page. A chevron on Requests
says there is more under it. All of it is CSS on attributes, so it survives Jellyfin Web redrawing
its navigation.

### The contract with Seerr Integration

Calendar reads three things Seerr Integration provides, and touches none of its elements beyond
the chevron drawn with CSS and the listeners that open the menu:

```text
data-seerr-integration-nav="appbar" | "menu" | "drawer"   marks its Requests entries
window.__seerrIntegrationLoader                           set by its loader when Requests is coming
window.SeerrIntegration                                   set once its page script has run
```

When the loader flag is set and Requests has not appeared yet, Calendar waits up to four seconds
before placing itself, so it does not appear on its own and then jump under Requests. If Seerr
Integration ever changes any of these, Calendar falls back to its own entry; it never breaks.

Detail dialogs link to the title in Requests (`#/requests?view=movie&id=<tmdb>` or `view=tv`)
whenever Seerr Integration is present and the entry has a TMDB id, where the request and its
download progress are shown too.

## The page

The page owns `#/calendar`. `view=week` switches to the week, `date=yyyy-MM-dd` picks the month or
week to show; without either it is this month. Paging and switching views replace the history
entry rather than adding one, so Back leaves the Calendar instead of stepping back through it.
The page is hosted exactly as Seerr Integration's is: a `mainAnimatedPage` among Jellyfin's own,
announced with `pagebeforeshow` and `pageshow` so the legacy header shows its title.

| Width | Month | Week |
| --- | --- | --- |
| wide | grid of days with compact entries; `+N more` opens the day | seven columns of tiles with the series' or movie's fanart |
| phone | grid of days with a dot per entry, coloured by type; the chosen day listed underneath | the seven days as a list |

The week's first day follows the user's locale (`Intl.Locale` week info), as do month and day
names and times. Swiping across the calendar pages through it on a touch screen; the arrow keys do
on a keyboard, while the focus is on the page.

### Time

Episodes follow their air time into the viewer's own time zone, as Sonarr's calendar does: an
episode at 23:30 in New York is the next morning in Berlin, and sits on that day. Release dates
are calendar days with no time of day. Radarr stores them as UTC midnight, so the day is the date
part, never converted, which would move every release a day early west of Greenwich.

The page asks for whole local days. The server widens the range by a day before and two after when
it asks the services, which compare in UTC, so a browser anywhere from UTC-12 to UTC+14 gets every
entry for its days.

### Telling TV from movies

Every entry starts with a small glyph: a screen for an episode, a clapperboard for a movie,
tinted in the colours of Sonarr's and Radarr's own branding. Episodes carry their code
(`S02E03`), movies which release it is (in cinemas, digital, physical). The legend at the top
says which is which.

### Statuses

Status is the coloured edge on every entry, as in Sonarr and Radarr, and is worked out in the
browser so it follows the clock while the page stays open.

| Status | Episode | Movie |
| --- | --- | --- |
| Downloaded | has a file | has a file |
| Downloading | in Sonarr's queue, with progress | in Radarr's queue, with progress |
| On air | between its air time and the end of its runtime | |
| Missing | monitored, aired, no file | monitored, available in Radarr's terms, no file |
| Upcoming | not aired yet | not available yet |
| Unmonitored | only shown when the setting includes them | the same |

With **Don't specify method** on, the page never says how a title arrives: Downloaded reads
Ready, and Downloading, with every step of the download client's (queued, paused, importing,
needs attention), reads Pending, with the percentage while it moves. A digital release keeps a
plain cloud instead of one with a download arrow. The setting comes with every calendar answer
and the newest one decides, so ranges the page kept from before a change follow it too. The
legend is drawn invisibly until the first answer arrives, so no word changes on screen.

Downloads show their progress as a fill on month entries, a bar along the bottom of week tiles,
and a progress bar with the time left in the details. While something is downloading the page
reloads every minute; otherwise it only redraws, so on air and missing move with the clock.

Episodes of one series that air at the same moment, as a streaming season does, become one entry
(`S01E01–E08`) whose details list every episode.

## Sonarr and Radarr calls

| Call | Used for |
| --- | --- |
| `GET calendar?start&end&unmonitored&includeSeries=true` | Sonarr's episodes, each with its series |
| `GET calendar?start&end&unmonitored` | Radarr's movies with a release in range |
| `GET queue/details` | download progress, matched by episode or movie id |
| `GET mediacover/{id}/{poster,fanart}-{size}.jpg` | artwork, through the signed image endpoint |
| `GET system/status` | Test connection, which also checks the address is the right service |

All under `/api/v3/`, which Sonarr 4, Sonarr 5 and Radarr 5 to 7 answer. Calendar answers are
cached for a minute and the queue for fifteen seconds, keyed by the address and a hash of the key,
and identical requests in flight are shared: a household paging through the same month costs each
service one request a minute. A failed queue read only drops download progress.

Each user is limited to 60 calendar requests and 60 library lookups a minute; the administrator's
Test connection to 30 a minute in all.

## Library links

When an entry is opened, the page asks `Plugins/ArrCalendar/Library` for the matching item in the
user's library, by TVDB, TMDB and IMDb id, with the user's own access and parental controls
applied. A match adds Go to episode, Go to series (when the episode is not in the library yet) or Go
to movie. An API key has no user, so it gets no library lookups.

## Styling

Everything is scoped to `#arrCalendarPage` and the `.acal-` dialogs and menu, and coloured with the
Material UI variables Jellyfin's themes define, with the dark theme's values as fallbacks.
Surfaces are mixed from the theme's own text colour (`color-mix`), so the same rules read on light
themes. Status colours are fixed, as in the arr calendars. No rule touches Jellyfin's own elements
except hiding its pages while `#/calendar` is active and the navigation entries described above.

## Compatibility

The page is ES2015 without async functions, optional chaining, nullish coalescing or object spread,
so it runs in older Android WebViews; a test enforces that. It is served on its own, so a browser
that cannot parse it loses only the Calendar. The loader is ES5, because the injector serves every
plugin's script as one file.

## Testing

```text
tests/Jellyfin.Plugin.ArrCalendar.Tests   xUnit: client, mapper, service, controller, signing, TLS rule
tests/web                                 node:test: dates, routes, statuses, grouping, the API client
tests/e2e                                 Playwright: Jellyfin 12.4, JavaScript Injector, Seerr Integration
```

The mapper tests run against answers recorded from real Sonarr 4.0.20 and Radarr 7.0.3 instances.
The end-to-end harness uses a stand-in Sonarr and Radarr whose schedule is built around today, so
every status is always on screen; see [tests/e2e/README.md](../tests/e2e/README.md).
