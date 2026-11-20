/*
 * Arr Calendar: the Calendar page for Jellyfin Web.
 *
 * Loaded by the plugin's JavaScript Injector loader once a user is signed in. It adds a
 * Calendar entry to the navigation and draws the page on the #/calendar route: upcoming
 * episodes from Sonarr and movie releases from Radarr, in one month or week view.
 *
 * Navigation: Calendar sits near the top of the side menu and app bar. When Seerr Integration
 * is installed too, devices that can hover get it as a small menu under Requests instead; touch
 * devices, which cannot hover, keep it as its own entry, right after Requests.
 *
 * The page only ever talks to this plugin's own endpoints, as the signed-in user. Sonarr's and
 * Radarr's keys stay on the server.
 *
 * Syntax stays at ES2015 (no async/await, optional chaining, nullish coalescing or object
 * spread) so the page also runs in older Android WebViews. It is served as its own file, so
 * nothing here can break the scripts other plugins register.
 */
(function () {
    'use strict';

    if (window.ArrCalendar) {
        return;
    }

    const VERSION = readVersion();
    const ROUTE = '/calendar';
    const PAGE_ID = 'arrCalendarPage';
    const NAV_ATTR = 'data-arr-calendar-nav';
    const SEERR_NAV_ATTR = 'data-seerr-integration-nav';
    const ACTIVE_CLASS = 'arr-calendar-active';
    const API_PATH = 'Plugins/ArrCalendar/';
    const CALENDAR_ICON_PATH = 'M19 4h-1V2h-2v2H8V2H6v2H5c-1.11 0-1.99.9-1.99 2L3 20c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 16H5V10h14v10zM9 14H7v-2h2v2zm4 0h-2v-2h2v2zm4 0h-2v-2h2v2zm-8 4H7v-2h2v2zm4 0h-2v-2h2v2zm4 0h-2v-2h2v2z';
    const MAX_PILLS = 4;
    const CACHE_TTL_MS = 60 * 1000;
    const REFRESH_MS = 60 * 1000;
    const SEERR_GRACE_MS = 4000;
    const COMPACT_QUERY = '(max-width: 50em)';
    // A phone on its side is wider than the compact layout, but short, with a touch screen.
    const SIDEWAYS_PHONE_QUERY = '(pointer: coarse) and (max-height: 50em)';
    const HOVER_QUERY = '(hover: hover) and (pointer: fine)';

    function readVersion() {
        const script = document.currentScript;
        const match = script && /[?&]v=([^&#]+)/.exec(script.src || '');
        return match ? decodeURIComponent(match[1]) : 'dev';
    }

    // ---------------------------------------------------------------------------------
    // Calendar model. Pure functions, unit tested in tests/web.
    // ---------------------------------------------------------------------------------

    function pad2(value) {
        return (value < 10 ? '0' : '') + value;
    }

    // A local calendar day as yyyy-MM-dd.
    function dayKey(date) {
        return date.getFullYear() + '-' + pad2(date.getMonth() + 1) + '-' + pad2(date.getDate());
    }

    // yyyy-MM-dd to local midnight, or null for anything that is not a real day.
    function parseDayKey(value) {
        const match = typeof value === 'string' && /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
        if (!match) {
            return null;
        }

        const year = Number(match[1]);
        const month = Number(match[2]) - 1;
        const day = Number(match[3]);
        const date = new Date(year, month, day);
        return date.getFullYear() === year && date.getMonth() === month && date.getDate() === day ? date : null;
    }

    // Day arithmetic on local dates. Constructing from parts keeps midnight across DST changes.
    function addDays(date, days) {
        return new Date(date.getFullYear(), date.getMonth(), date.getDate() + days);
    }

    function addMonths(date, months) {
        const target = new Date(date.getFullYear(), date.getMonth() + months, 1);
        const last = new Date(target.getFullYear(), target.getMonth() + 1, 0).getDate();
        return new Date(target.getFullYear(), target.getMonth(), Math.min(date.getDate(), last));
    }

    function startOfDay(date) {
        return new Date(date.getFullYear(), date.getMonth(), date.getDate());
    }

    function startOfWeek(date, firstDay) {
        return addDays(startOfDay(date), -((date.getDay() - firstDay + 7) % 7));
    }

    // The weeks a month view shows: the week holding the 1st through the week holding the last.
    function monthWeeks(anchor, firstDay) {
        const last = new Date(anchor.getFullYear(), anchor.getMonth() + 1, 0);
        const weeks = [];
        let day = startOfWeek(new Date(anchor.getFullYear(), anchor.getMonth(), 1), firstDay);
        while (day <= last) {
            const week = [];
            for (let i = 0; i < 7; i++) {
                week.push(day);
                day = addDays(day, 1);
            }

            weeks.push(week);
        }

        return weeks;
    }

    function weekDays(anchor, firstDay) {
        const start = startOfWeek(anchor, firstDay);
        const days = [];
        for (let i = 0; i < 7; i++) {
            days.push(addDays(start, i));
        }

        return days;
    }

    function visibleRange(view, anchor, firstDay) {
        if (view === 'week') {
            const days = weekDays(anchor, firstDay);
            return { start: days[0], end: days[6] };
        }

        const weeks = monthWeeks(anchor, firstDay);
        return { start: weeks[0][0], end: weeks[weeks.length - 1][6] };
    }

    function shiftAnchor(view, anchor, direction) {
        return view === 'week' ? addDays(anchor, 7 * direction) : addMonths(anchor, direction);
    }

    // Regions whose weeks start on Sunday or Saturday, for browsers without Intl week info.
    const SUNDAY_FIRST = ['AG', 'AR', 'AS', 'AU', 'BD', 'BR', 'BS', 'BT', 'BW', 'BZ', 'CA', 'CN', 'CO', 'DM', 'DO', 'ET', 'GT', 'GU', 'HK', 'HN', 'ID', 'IL', 'IN', 'JM', 'JP', 'KE', 'KH', 'KR', 'LA', 'MH', 'MM', 'MO', 'MT', 'MX', 'MZ', 'NI', 'NP', 'PA', 'PE', 'PH', 'PK', 'PR', 'PT', 'PY', 'SA', 'SG', 'SV', 'TH', 'TT', 'TW', 'UM', 'US', 'VE', 'VI', 'WS', 'YE', 'ZA', 'ZW'];
    const SATURDAY_FIRST = ['AE', 'AF', 'BH', 'DJ', 'DZ', 'EG', 'IQ', 'IR', 'JO', 'KW', 'LY', 'OM', 'QA', 'SD', 'SY'];

    // 0 for Sunday through 6 for Saturday, from the locale's own convention.
    function firstDayOfWeek(locale) {
        try {
            const info = new Intl.Locale(locale);
            const week = typeof info.getWeekInfo === 'function' ? info.getWeekInfo() : info.weekInfo;
            if (week && typeof week.firstDay === 'number') {
                return week.firstDay % 7;
            }
        } catch (e) {
            // Fall through to the region lists.
        }

        const parts = String(locale || '').split(/[-_]/);
        const region = parts.length > 1 ? parts[parts.length - 1].toUpperCase() : (/^en$/i.test(parts[0]) ? 'US' : '');
        if (SATURDAY_FIRST.indexOf(region) !== -1) {
            return 6;
        }

        return SUNDAY_FIRST.indexOf(region) !== -1 ? 0 : 1;
    }

    // The page's own route. Anything else belongs to Jellyfin.
    function parseRoute(hash) {
        if (typeof hash !== 'string' || hash.charAt(0) !== '#') {
            return null;
        }

        const raw = hash.slice(1);
        const queryStart = raw.indexOf('?');
        const path = (queryStart === -1 ? raw : raw.slice(0, queryStart)).replace(/\/+$/, '');
        if (path.toLowerCase() !== ROUTE) {
            return null;
        }

        const params = new URLSearchParams(queryStart === -1 ? '' : raw.slice(queryStart + 1));
        const date = parseDayKey(params.get('date'));
        const chosen = params.get('view');
        return { view: chosen === 'week' || chosen === 'month' ? chosen : null, date: date ? dayKey(date) : null };
    }

    function routeHash(route) {
        const params = new URLSearchParams();
        if (route.view === 'week' || route.view === 'month') {
            params.set('view', route.view);
        }

        if (route.date) {
            params.set('date', route.date);
        }

        const query = params.toString();
        return '#' + ROUTE + (query ? '?' + query : '');
    }

    function isDayKey(value) {
        return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value);
    }

    // When an episode starts, or null for a movie or an episode without an air time.
    function eventStart(event) {
        if (!event || event.kind !== 'episode' || typeof event.airDateUtc !== 'string') {
            return null;
        }

        const time = Date.parse(event.airDateUtc);
        return isNaN(time) ? null : new Date(time);
    }

    function eventEnd(event) {
        const start = eventStart(event);
        return start ? new Date(start.getTime() + (event.runtime > 0 ? event.runtime : 30) * 60000) : null;
    }

    // The local day an entry belongs on. Episodes follow their air time into the viewer's own
    // time zone, as Sonarr's calendar does. Release dates are calendar days and never move.
    function eventDay(event) {
        if (!event) {
            return null;
        }

        if (event.kind === 'movie') {
            return isDayKey(event.date) ? event.date : null;
        }

        const start = eventStart(event);
        if (start) {
            return dayKey(start);
        }

        return isDayKey(event.airDate) ? event.airDate : null;
    }

    function byEpisodeNumber(a, b) {
        return (a.season || 0) - (b.season || 0) || (a.episode || 0) - (b.episode || 0);
    }

    // Episodes of one series that drop at the same moment become one entry, as a streaming
    // service releasing a whole season does. The entry keeps every episode for its details.
    function groupEpisodes(events) {
        const result = [];
        const groups = new Map();
        (Array.isArray(events) ? events : []).forEach(function (event) {
            if (!event || event.kind !== 'episode' || !event.seriesId || !event.airDateUtc) {
                result.push(event);
                return;
            }

            const key = event.seriesId + '|' + event.airDateUtc;
            const group = groups.get(key);
            if (group) {
                group.episodes.push(event);
                return;
            }

            const created = { groupKey: key, episodes: [event] };
            groups.set(key, created);
            result.push(created);
        });

        return result.filter(Boolean).map(function (item) {
            if (!item.groupKey) {
                return item;
            }

            if (item.episodes.length === 1) {
                return item.episodes[0];
            }

            const episodes = item.episodes.slice().sort(byEpisodeNumber);
            return Object.assign({}, episodes[0], {
                key: 'group:' + item.groupKey,
                group: true,
                episodes: episodes,
                episodeTitle: null,
                hasFile: episodes.every(function (episode) { return episode.hasFile; }),
                download: combineDownloads(episodes)
            });
        });
    }

    function combineDownloads(episodes) {
        const active = episodes.filter(function (episode) { return episode.download; });
        if (!active.length) {
            return null;
        }

        const known = active.filter(function (episode) { return typeof episode.download.progress === 'number'; });
        const states = active.map(function (episode) { return episode.download.state; });
        return {
            progress: known.length
                ? Math.round(known.reduce(function (sum, episode) { return sum + episode.download.progress; }, 0) / known.length)
                : null,
            state: ['downloading', 'importing', 'queued', 'paused', 'warning'].filter(function (state) { return states.indexOf(state) !== -1; })[0] || 'downloading',
            eta: active.map(function (episode) { return episode.download.eta; }).filter(Boolean).sort().pop() || null
        };
    }

    const STATUS_ORDER = ['downloading', 'airing', 'missing', 'upcoming', 'unmonitored', 'downloaded'];

    // What an entry shows, at a given moment. Mirrors Sonarr's and Radarr's own calendars: a
    // file wins, then a download, then the time of day decides between on air, missing and
    // upcoming.
    function statusOf(event, now) {
        if (event.group) {
            const statuses = event.episodes.map(function (episode) { return statusOf(episode, now); });
            if (statuses.every(function (status) { return status === 'downloaded'; })) {
                return 'downloaded';
            }

            return STATUS_ORDER.filter(function (status) { return statuses.indexOf(status) !== -1; })[0] || 'upcoming';
        }

        if (event.hasFile) {
            return 'downloaded';
        }

        if (event.download) {
            return 'downloading';
        }

        if (!event.monitored) {
            return 'unmonitored';
        }

        if (event.kind === 'movie') {
            return event.available ? 'missing' : 'upcoming';
        }

        const start = eventStart(event);
        if (start) {
            const end = eventEnd(event);
            if (now >= start.getTime() && now < end.getTime()) {
                return 'airing';
            }

            return now >= end.getTime() ? 'missing' : 'upcoming';
        }

        const day = parseDayKey(event.airDate);
        return day && now >= addDays(day, 1).getTime() ? 'missing' : 'upcoming';
    }

    // Movies are all-day, so they lead the day; then episodes in air-time order.
    function compareForDay(a, b) {
        const startA = eventStart(a);
        const startB = eventStart(b);
        if (!startA !== !startB) {
            return startA ? 1 : -1;
        }

        const byTime = startA && startB ? startA.getTime() - startB.getTime() : 0;
        return byTime
            || String(a.title || '').localeCompare(String(b.title || ''))
            || byEpisodeNumber(a, b);
    }

    function bucketByDay(events) {
        const days = new Map();
        groupEpisodes(events).forEach(function (event) {
            const day = eventDay(event);
            if (!day) {
                return;
            }

            if (!days.has(day)) {
                days.set(day, []);
            }

            days.get(day).push(event);
        });
        days.forEach(function (list) { list.sort(compareForDay); });
        return days;
    }

    function episodeCode(season, episode) {
        if (typeof season !== 'number' || typeof episode !== 'number') {
            return '';
        }

        return 'S' + pad2(season) + 'E' + pad2(episode);
    }

    // S02E03, or S02E01–E08 for a season that drops at once.
    function codeOf(event) {
        if (!event || event.kind !== 'episode') {
            return '';
        }

        if (!event.group) {
            return episodeCode(event.season, event.episode);
        }

        const first = event.episodes[0];
        const last = event.episodes[event.episodes.length - 1];
        const sameSeason = event.episodes.every(function (episode) { return episode.season === first.season; });
        return sameSeason
            ? episodeCode(first.season, first.episode) + '–E' + pad2(last.episode)
            : event.episodes.length + ' episodes';
    }

    const RELEASE_LABELS = { cinema: 'In cinemas', digital: 'Digital', physical: 'Physical' };
    const RELEASE_CHIPS = { cinema: 'In cinemas', digital: 'Digital release', physical: 'Physical release' };
    const RELEASE_ICONS = { cinema: 'theaters', digital: 'cloud_download', physical: 'album' };

    function releaseLabel(release) {
        return RELEASE_LABELS[release] || 'Release';
    }

    // The settings' "Don't specify method" (`neutral` below) keeps the Calendar from saying how
    // titles arrive: Ready instead of Downloaded, Pending instead of Downloading or any step of
    // it, and a plain cloud for a digital release instead of one with a download arrow.
    function releaseIcon(release, neutral) {
        if (release === 'digital' && neutral) {
            return 'cloud';
        }

        return RELEASE_ICONS[release] || 'event';
    }

    function downloadLabel(download, neutral) {
        if (!download) {
            return '';
        }

        if (neutral) {
            const step = ['queued', 'paused', 'importing', 'warning'].indexOf(download.state) !== -1;
            return !step && typeof download.progress === 'number' ? 'Pending ' + download.progress + '%' : 'Pending';
        }

        switch (download.state) {
            case 'queued':
                return 'Queued';
            case 'paused':
                return 'Paused';
            case 'importing':
                return 'Importing';
            case 'warning':
                return 'Download needs attention';
            default:
                return typeof download.progress === 'number' ? 'Downloading ' + download.progress + '%' : 'Downloading';
        }
    }

    function statusLabel(event, status, now, neutral) {
        switch (status) {
            case 'downloaded':
                return neutral ? 'Ready' : 'Downloaded';
            case 'downloading':
                return downloadLabel(event.download, neutral);
            case 'airing':
                return 'On air now';
            case 'missing':
                return 'Missing';
            case 'unmonitored':
                return 'Unmonitored';
            default: {
                const day = event.kind === 'movie' ? parseDayKey(event.date) : null;
                return day && now >= day.getTime() ? 'Not available yet' : 'Upcoming';
            }
        }
    }

    function legendStatuses(neutral) {
        return [
            ['downloaded', neutral ? 'Ready' : 'Downloaded'],
            ['downloading', neutral ? 'Pending' : 'Downloading'],
            ['airing', 'On air'],
            ['missing', 'Missing'],
            ['upcoming', 'Upcoming']
        ];
    }

    // The view a calendar opens on: the week on a phone, the month elsewhere.
    function defaultView(phone) {
        return phone ? 'week' : 'month';
    }

    function formatTimeLeft(eta, now) {
        if (!eta) {
            return '';
        }

        const minutes = Math.round((eta.getTime() - now) / 60000);
        if (minutes <= 1) {
            return 'almost done';
        }

        if (minutes < 90) {
            return 'about ' + minutes + ' minutes left';
        }

        const hours = Math.round(minutes / 60);
        return hours < 48 ? 'about ' + hours + ' hours left' : 'about ' + Math.round(hours / 24) + ' days left';
    }

    // Artwork URLs come from the server as a service/id path and a signature. Both are checked
    // here too, so a value from upstream can never break out of a URL or a style attribute.
    function imagePath(event, type, size) {
        if (!event || typeof event.image !== 'string' || !/^(sonarr|radarr)\/[1-9]\d{0,9}$/.test(event.image)
            || typeof event.imageSig !== 'string' || !/^[A-Za-z0-9_-]{22}$/.test(event.imageSig)
            || (type !== 'poster' && type !== 'fanart')) {
            return null;
        }

        return { path: API_PATH + 'Image/' + event.image + '/' + type, query: { size: size, sig: event.imageSig } };
    }

    function seerrRoute(event) {
        if (!event || !(event.tmdbId > 0)) {
            return '';
        }

        return '#/requests?view=' + (event.kind === 'movie' ? 'movie' : 'tv') + '&id=' + Math.floor(event.tmdbId);
    }

    function escapeHtml(value) {
        return String(value === null || value === undefined ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // ---------------------------------------------------------------------------------
    // API client. The one place this page talks to the server.
    // ---------------------------------------------------------------------------------

    function ApiError(status, code, message, retryAfter) {
        this.name = 'ApiError';
        this.status = status;
        this.code = code || '';
        this.message = message || '';
        this.retryAfter = retryAfter > 0 ? retryAfter : 0;
    }

    ApiError.prototype = Object.create(Error.prototype);
    ApiError.prototype.constructor = ApiError;

    function createApi(fetchImpl, getClient) {
        // The user's own Jellyfin authorization, exactly as Jellyfin Web sends it. Plain fetch
        // rather than ApiClient.ajax, so a refusal never reaches Jellyfin Web's own failure
        // handling.
        function headers() {
            const result = { Accept: 'application/json' };
            const client = getClient();
            if (client && typeof client.setRequestHeaders === 'function') {
                client.setRequestHeaders(result);
            }

            if (!result.Authorization && client && typeof client.accessToken === 'function' && client.accessToken()) {
                result.Authorization = 'MediaBrowser Token="' + client.accessToken() + '"';
            }

            return result;
        }

        function get(path, query) {
            const client = getClient();
            if (!client || typeof client.getUrl !== 'function') {
                return Promise.reject(new ApiError(401, 'MissingJellyfinUser', ''));
            }

            return fetchImpl(client.getUrl(API_PATH + path, query), {
                method: 'GET',
                headers: headers(),
                cache: 'no-store'
            }).then(function (response) {
                if (response.status === 204) {
                    return null;
                }

                return response.text().then(function (text) {
                    let data = null;
                    try {
                        data = text ? JSON.parse(text) : null;
                    } catch (e) {
                        data = null;
                    }

                    if (response.ok) {
                        return data;
                    }

                    const retryAfter = parseInt(response.headers && response.headers.get('Retry-After'), 10);
                    throw new ApiError(response.status, data && (data.error || data.Error), data && (data.message || data.Message), retryAfter);
                });
            }, function () {
                throw new ApiError(0, 'NetworkError', '');
            });
        }

        return { get: get };
    }

    // What to tell the user about a failed call. `blocking` errors replace the whole page.
    function describeError(error) {
        if (!(error instanceof ApiError)) {
            return { blocking: false, title: 'Something went wrong', text: 'The calendar hit an unexpected problem. Reload and try again.' };
        }

        switch (error.status) {
            case 0:
                return { blocking: false, title: 'Connection problem', text: 'Jellyfin could not be reached. Check your connection and try again.' };
            case 401:
                return { blocking: true, kind: 'session', title: 'Signed out', text: 'Your Jellyfin session has ended. Sign in again to see the calendar.' };
            case 403:
                if (error.code === 'CalendarDisabled') {
                    return { blocking: true, kind: 'disabled', title: 'The calendar is switched off', text: 'The server admin has switched the calendar off on this server.' };
                }

                return { blocking: false, title: 'Not allowed', text: error.message || 'You are not allowed to do that.' };
            case 404:
                if (error.code === 'PluginInactive' || !error.code) {
                    return { blocking: true, kind: 'inactive', title: 'The calendar is unavailable', text: 'Arr Calendar is not active on this server. Ask the server admin to check it.' };
                }

                return { blocking: false, title: 'Not found', text: error.message || 'That could not be found.' };
            case 429:
                return { blocking: false, title: 'Slow down', text: 'Too many requests at once. Try again in ' + (error.retryAfter || 30) + ' seconds.' };
            default:
                return { blocking: false, title: 'Something went wrong', text: error.message || ('The server answered with HTTP ' + error.status + '.') };
        }
    }

    const model = {
        dayKey: dayKey,
        parseDayKey: parseDayKey,
        addDays: addDays,
        addMonths: addMonths,
        startOfWeek: startOfWeek,
        monthWeeks: monthWeeks,
        weekDays: weekDays,
        visibleRange: visibleRange,
        shiftAnchor: shiftAnchor,
        firstDayOfWeek: firstDayOfWeek,
        parseRoute: parseRoute,
        routeHash: routeHash,
        eventStart: eventStart,
        eventEnd: eventEnd,
        eventDay: eventDay,
        groupEpisodes: groupEpisodes,
        statusOf: statusOf,
        statusLabel: statusLabel,
        downloadLabel: downloadLabel,
        releaseIcon: releaseIcon,
        legendStatuses: legendStatuses,
        defaultView: defaultView,
        bucketByDay: bucketByDay,
        episodeCode: episodeCode,
        codeOf: codeOf,
        releaseLabel: releaseLabel,
        formatTimeLeft: formatTimeLeft,
        imagePath: imagePath,
        seerrRoute: seerrRoute,
        escapeHtml: escapeHtml,
        createApi: createApi,
        describeError: describeError,
        ApiError: ApiError
    };

    window.ArrCalendar = { version: VERSION, model: model };

    if (window.__arrCalendarNoInit) {
        return;
    }

    // ---------------------------------------------------------------------------------
    // State. Everything is keyed to the signed-in user, so switching users in one tab never
    // shows the previous user's calendar.
    // ---------------------------------------------------------------------------------

    const api = createApi(function (input, init) { return window.fetch(input, init); }, function () { return window.ApiClient; });
    const responseCache = new Map();
    const startedAt = Date.now();
    let sessionKeyCache = '';
    let currentRoute = null;
    let lastHash = null;
    let renderToken = 0;
    let view = null;
    let refreshTimer = 0;
    let neutralWording = false;
    let wordingKnown = false;

    function currentSessionKey() {
        const client = window.ApiClient;
        if (!client || typeof client.getCurrentUserId !== 'function') {
            return '';
        }

        const userId = client.getCurrentUserId();
        const token = typeof client.accessToken === 'function' ? client.accessToken() : '';
        return userId && token ? userId + ':' + token : '';
    }

    function resetIfUserChanged() {
        const key = currentSessionKey();
        if (key !== sessionKeyCache) {
            sessionKeyCache = key;
            responseCache.clear();
        }

        return key;
    }

    function isSignedIn() {
        return !!currentSessionKey();
    }

    function locale() {
        return document.documentElement.getAttribute('lang') || navigator.language || 'en-US';
    }

    const formatters = new Map();
    function format(options, date) {
        const key = locale() + JSON.stringify(options);
        let formatter = formatters.get(key);
        if (!formatter) {
            try {
                formatter = new Intl.DateTimeFormat(locale(), options);
            } catch (e) {
                formatter = new Intl.DateTimeFormat('en-US', options);
            }

            formatters.set(key, formatter);
        }

        return formatter.format(date);
    }

    function formatTime(date) {
        return format({ hour: 'numeric', minute: '2-digit' }, date);
    }

    function loadRange(range, fresh) {
        const key = resetIfUserChanged();
        if (!key) {
            return Promise.reject(new ApiError(401, 'MissingJellyfinUser', ''));
        }

        const cacheKey = dayKey(range.start) + '|' + dayKey(range.end);
        const hit = responseCache.get(cacheKey);
        if (!fresh && hit && Date.now() - hit.at < CACHE_TTL_MS) {
            return hit.promise;
        }

        const promise = api.get('Calendar', { start: dayKey(range.start), end: dayKey(range.end) }).then(function (data) {
            if (currentSessionKey() !== key) {
                throw new ApiError(401, 'MissingJellyfinUser', '');
            }

            // The newest answer decides the wording, so a range kept from before a change of the
            // setting is drawn in the new words too.
            neutralWording = !!(data && data.dontSpecifyMethod);
            wordingKnown = true;
            return data || { sources: [], events: [] };
        });
        promise.catch(function () {
            if (responseCache.get(cacheKey) && responseCache.get(cacheKey).promise === promise) {
                responseCache.delete(cacheKey);
            }
        });
        responseCache.set(cacheKey, { at: Date.now(), promise: promise });
        if (responseCache.size > 24) {
            responseCache.delete(responseCache.keys().next().value);
        }

        return promise;
    }

    // ---------------------------------------------------------------------------------
    // Styles. Scoped to this page's own elements and themed with the variables Jellyfin's
    // themes define, with the dark theme's values as fallbacks.
    // ---------------------------------------------------------------------------------

    const STYLE = `
#${PAGE_ID}, .acal-layer, .acal-flyout {
  --acal-accent: var(--jf-palette-primary-main, #00a4dc);
  --acal-text: var(--jf-palette-text-primary, #fff);
  --acal-muted: var(--jf-palette-text-secondary, rgba(255, 255, 255, 0.7));
  --acal-paper: var(--jf-palette-background-paper, #202020);
  --acal-ground: var(--jf-palette-background-default, #101010);
  --acal-divider: var(--jf-palette-divider, rgba(255, 255, 255, 0.12));
  --acal-hover: var(--jf-palette-action-hover, rgba(255, 255, 255, 0.08));
  --acal-cell: rgba(255, 255, 255, 0.025);
  --acal-pill: rgba(255, 255, 255, 0.06);
  --acal-pill-hover: rgba(255, 255, 255, 0.11);
  --acal-placeholder: rgba(255, 255, 255, 0.06);
  --acal-tv: #4fb8f0;
  --acal-movie: #f5bf42;
  --acal-downloaded: #3fb950;
  --acal-downloading: #a371f7;
  --acal-airing: #ff9f43;
  --acal-missing: #f85149;
  --acal-upcoming: #768390;
  --acal-unmonitored: #4b525c;
}
@supports (color: color-mix(in srgb, red 50%, blue)) {
  #${PAGE_ID}, .acal-layer, .acal-flyout {
    --acal-cell: color-mix(in srgb, var(--acal-text) 2.5%, transparent);
    --acal-pill: color-mix(in srgb, var(--acal-text) 6%, transparent);
    --acal-pill-hover: color-mix(in srgb, var(--acal-text) 11%, transparent);
    --acal-placeholder: color-mix(in srgb, var(--acal-text) 7%, transparent);
    --acal-tv: color-mix(in srgb, #4fb8f0 82%, var(--acal-text));
    --acal-movie: color-mix(in srgb, #f5bf42 78%, var(--acal-text));
  }
}
html.${ACTIVE_CLASS} .mainAnimatedPage:not(#${PAGE_ID}) { display: none !important; }
#${PAGE_ID} { color: var(--acal-text); }
#${PAGE_ID} .acal-root { padding-top: 0.75em; padding-bottom: 3em; }
#${PAGE_ID} [data-kind="episode"], .acal-layer [data-kind="episode"] { --acal-type: var(--acal-tv); }
#${PAGE_ID} [data-kind="movie"], .acal-layer [data-kind="movie"] { --acal-type: var(--acal-movie); }
#${PAGE_ID} [data-status], .acal-layer [data-status] { --acal-status: var(--acal-upcoming); }
#${PAGE_ID} [data-status="downloaded"], .acal-layer [data-status="downloaded"] { --acal-status: var(--acal-downloaded); }
#${PAGE_ID} [data-status="downloading"], .acal-layer [data-status="downloading"] { --acal-status: var(--acal-downloading); }
#${PAGE_ID} [data-status="airing"], .acal-layer [data-status="airing"] { --acal-status: var(--acal-airing); }
#${PAGE_ID} [data-status="missing"], .acal-layer [data-status="missing"] { --acal-status: var(--acal-missing); }
#${PAGE_ID} [data-status="unmonitored"], .acal-layer [data-status="unmonitored"] { --acal-status: var(--acal-unmonitored); }

#${PAGE_ID} .acal-toolbar { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 0.75em 1.5em; margin-bottom: 0.4em; }
#${PAGE_ID} .acal-heading { display: flex; align-items: center; gap: 0.6em 1em; flex-wrap: wrap; min-width: 0; }
#${PAGE_ID} .acal-title { margin: 0; font-size: 1.75em; font-weight: 400; letter-spacing: -0.01em; line-height: 1.2; white-space: nowrap; }
#${PAGE_ID} .acal-title strong { font-weight: 600; }
#${PAGE_ID} .acal-title .acal-title-soft { color: var(--acal-muted); }
#${PAGE_ID} .acal-stepper { display: inline-flex; align-items: center; gap: 0.15em; }
#${PAGE_ID} .acal-icon-btn, .acal-layer .acal-icon-btn { display: inline-flex; align-items: center; justify-content: center; width: 2.25em; height: 2.25em; border: 0; border-radius: 50%; background: transparent; color: var(--acal-text); cursor: pointer; padding: 0; }
#${PAGE_ID} .acal-icon-btn:hover, .acal-layer .acal-icon-btn:hover { background: var(--acal-hover); }
#${PAGE_ID} .acal-btn, .acal-layer .acal-btn { display: inline-flex; align-items: center; justify-content: center; gap: 0.45em; border: 0; border-radius: 2em; padding: 0.5em 1.05em; font: inherit; font-weight: 600; font-size: 0.92em; cursor: pointer; color: var(--acal-text); background: var(--acal-hover); text-decoration: none; }
#${PAGE_ID} .acal-btn:hover, .acal-layer .acal-btn:hover { filter: brightness(1.2); }
#${PAGE_ID} .acal-btn .material-icons, .acal-layer .acal-btn .material-icons { font-size: 1.25em; }
.acal-layer .acal-btn-primary { background: var(--acal-accent); color: #fff; }
#${PAGE_ID} button:focus-visible, #${PAGE_ID} a:focus-visible, .acal-layer button:focus-visible, .acal-layer a:focus-visible { outline: 2px solid var(--acal-accent); outline-offset: 2px; }
#${PAGE_ID} .acal-busy { width: 1.2em; height: 1.2em; }
#${PAGE_ID} .acal-busy[hidden] { display: none; }
.acal-spinner { width: 100%; height: 100%; border-radius: 50%; border: 0.18em solid var(--acal-hover); border-top-color: var(--acal-accent); animation: acal-spin 0.9s linear infinite; box-sizing: border-box; }
@keyframes acal-spin { to { transform: rotate(360deg); } }

#${PAGE_ID} .acal-switch { display: inline-flex; padding: 0.2em; border-radius: 2em; background: var(--acal-hover); }
#${PAGE_ID} .acal-switch button { border: 0; background: transparent; color: var(--acal-muted); font: inherit; font-weight: 600; font-size: 0.9em; padding: 0.42em 1.1em; border-radius: 2em; cursor: pointer; transition: background 0.15s ease, color 0.15s ease; }
#${PAGE_ID} .acal-switch button:hover { color: var(--acal-text); }
#${PAGE_ID} .acal-switch button[aria-pressed="true"] { background: var(--acal-paper); color: var(--acal-text); box-shadow: 0 1px 3px rgba(0, 0, 0, 0.35); }

#${PAGE_ID} .acal-legend { display: flex; flex-wrap: wrap; align-items: center; gap: 0.35em 1.1em; margin: 0 0 1em; color: var(--acal-muted); font-size: 0.8em; }
#${PAGE_ID} .acal-legend-group { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 0.35em 1.1em; }
#${PAGE_ID} .acal-legend-item { display: inline-flex; align-items: center; gap: 0.4em; white-space: nowrap; }
#${PAGE_ID} .acal-legend-sep { width: 1px; height: 1em; background: var(--acal-divider); }
#${PAGE_ID} .acal-swatch { display: inline-block; width: 0.75em; height: 0.75em; border-radius: 0.2em; background: var(--acal-status); }
#${PAGE_ID} .acal-glyph, .acal-layer .acal-glyph { color: var(--acal-type); font-size: 1.15em; line-height: 1; flex: 0 0 auto; }
#${PAGE_ID} .acal-legend .acal-glyph { font-size: 1.25em; }

#${PAGE_ID} .acal-notice { display: flex; align-items: center; gap: 0.6em; padding: 0.6em 0.9em; margin-bottom: 0.8em; border-radius: 0.6em; background: rgba(248, 81, 73, 0.1); box-shadow: inset 3px 0 0 var(--acal-missing); font-size: 0.92em; }
#${PAGE_ID} .acal-notice .material-icons { color: var(--acal-missing); font-size: 1.3em; }
#${PAGE_ID} .acal-notice-info { background: var(--acal-cell); box-shadow: inset 3px 0 0 var(--acal-upcoming); }
#${PAGE_ID} .acal-notice-info .material-icons { color: var(--acal-muted); }

#${PAGE_ID} .acal-month { border-radius: 0.9em; overflow: hidden; border: 1px solid var(--acal-divider); }
#${PAGE_ID} .acal-dows { display: grid; grid-template-columns: repeat(7, minmax(0, 1fr)); background: var(--acal-cell); border-bottom: 1px solid var(--acal-divider); }
#${PAGE_ID} .acal-dow { padding: 0.55em 0.75em; font-size: 0.72em; font-weight: 600; letter-spacing: 0.08em; text-transform: uppercase; color: var(--acal-muted); }
#${PAGE_ID} .acal-grid { display: grid; grid-template-columns: repeat(7, minmax(0, 1fr)); gap: 1px; background: var(--acal-divider); }
#${PAGE_ID} .acal-day { background: var(--acal-ground); min-height: 8.6em; padding: 0.4em 0.45em 0.5em; display: flex; flex-direction: column; gap: 0.25em; min-width: 0; }
#${PAGE_ID} .acal-day-head { display: flex; align-items: center; justify-content: space-between; padding: 0.05em 0.15em 0.15em; }
#${PAGE_ID} .acal-day-num { display: inline-flex; align-items: center; justify-content: center; min-width: 1.85em; height: 1.85em; padding: 0 0.3em; border-radius: 1em; font-size: 0.85em; font-weight: 600; box-sizing: border-box; }
#${PAGE_ID} .acal-outside { background: var(--acal-ground); }
#${PAGE_ID} .acal-outside .acal-day-num { color: var(--acal-muted); opacity: 0.55; font-weight: 400; }
#${PAGE_ID} .acal-past .acal-day-num { color: var(--acal-muted); }
#${PAGE_ID} .acal-day.acal-today { background: linear-gradient(rgba(0, 164, 220, 0.1), rgba(0, 164, 220, 0.03)), var(--acal-ground); }
#${PAGE_ID} .acal-today .acal-day-num { background: var(--acal-accent); color: #fff; opacity: 1; }
#${PAGE_ID} .acal-day-list { display: flex; flex-direction: column; gap: 0.2em; min-width: 0; }
#${PAGE_ID} .acal-outside .acal-day-list, #${PAGE_ID} .acal-outside .acal-more { opacity: 0.55; }

#${PAGE_ID} .acal-pill, .acal-layer .acal-pill { position: relative; display: flex; align-items: center; gap: 0.38em; width: 100%; min-width: 0; padding: 0.3em 0.5em 0.3em 0.6em; border: 0; border-radius: 0.4em; background: var(--acal-pill); color: inherit; font: inherit; font-size: 0.8em; line-height: 1.25; text-align: left; cursor: pointer; box-shadow: inset 3px 0 0 var(--acal-status); transition: background 0.12s ease; box-sizing: border-box; }
#${PAGE_ID} .acal-pill:hover { background-color: var(--acal-pill-hover); }
#${PAGE_ID} .acal-pill[data-status="downloading"] { background-image: linear-gradient(90deg, rgba(163, 113, 247, 0.28) var(--acal-progress, 0%), transparent var(--acal-progress, 0%)); }
#${PAGE_ID} .acal-pill[data-status="unmonitored"] { opacity: 0.6; }
#${PAGE_ID} .acal-pill-text { display: flex; flex-wrap: wrap; align-items: baseline; column-gap: 0.4em; min-width: 0; flex: 1 1 auto; height: 1.25em; overflow: hidden; }
#${PAGE_ID} .acal-pill-title { flex: 0 1 auto; max-width: 100%; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
#${PAGE_ID} .acal-pill-meta { color: var(--acal-muted); white-space: nowrap; font-size: 0.92em; flex: 0 0 auto; font-variant-numeric: tabular-nums; }
#${PAGE_ID} .acal-pill-pct { flex: 0 0 auto; font-size: 0.88em; font-weight: 700; color: var(--acal-downloading); font-variant-numeric: tabular-nums; }
#${PAGE_ID} .acal-more { align-self: flex-start; border: 0; background: transparent; color: var(--acal-muted); font: inherit; font-size: 0.78em; font-weight: 600; padding: 0.15em 0.5em; border-radius: 0.4em; cursor: pointer; }
#${PAGE_ID} .acal-more:hover { color: var(--acal-text); background: var(--acal-hover); }

#${PAGE_ID} .acal-week { display: grid; grid-template-columns: repeat(7, minmax(0, 1fr)); gap: 0.6em; }
#${PAGE_ID} .acal-col { display: flex; flex-direction: column; min-width: 0; min-height: 22em; border-radius: 0.9em; background: var(--acal-cell); border: 1px solid var(--acal-divider); }
#${PAGE_ID} .acal-col-head { display: flex; align-items: baseline; gap: 0.45em; padding: 0.7em 0.8em 0.5em; }
#${PAGE_ID} .acal-col-dow { font-size: 0.72em; font-weight: 600; letter-spacing: 0.08em; text-transform: uppercase; color: var(--acal-muted); }
#${PAGE_ID} .acal-col-num { font-size: 1.35em; font-weight: 600; line-height: 1; }
#${PAGE_ID} .acal-col.acal-past .acal-col-num { color: var(--acal-muted); }
#${PAGE_ID} .acal-col.acal-today { border-color: rgba(0, 164, 220, 0.55); background: linear-gradient(rgba(0, 164, 220, 0.08), rgba(0, 164, 220, 0.015) 9em); }
#${PAGE_ID} .acal-col.acal-today .acal-col-num { color: var(--acal-accent); }
#${PAGE_ID} .acal-col-list { display: flex; flex-direction: column; gap: 0.45em; padding: 0 0.45em 0.6em; }
#${PAGE_ID} .acal-col-empty { padding: 0.2em 0.4em; font-size: 0.8em; color: var(--acal-muted); opacity: 0.6; }

#${PAGE_ID} .acal-card, .acal-layer .acal-card { position: relative; display: flex; gap: 0.6em; width: 100%; min-width: 0; padding: 0.45em; border: 0; border-radius: 0.6em; background: var(--acal-pill); color: inherit; font: inherit; text-align: left; cursor: pointer; box-sizing: border-box; overflow: hidden; transition: background 0.12s ease; }
#${PAGE_ID} .acal-card:hover, .acal-layer .acal-card:hover { background: var(--acal-pill-hover); }
#${PAGE_ID} .acal-card-art, .acal-layer .acal-card-art { flex: 0 0 auto; width: 2.7em; aspect-ratio: 2 / 3; border-radius: 0.35em; background: var(--acal-placeholder) center / cover no-repeat; display: flex; align-items: center; justify-content: center; align-self: flex-start; box-shadow: inset 0 -3px 0 var(--acal-status); }
#${PAGE_ID} .acal-card-art .material-icons, .acal-layer .acal-card-art .material-icons { color: var(--acal-type); opacity: 0.55; font-size: 1.3em; }
#${PAGE_ID} .acal-card-art.acal-loaded .material-icons, .acal-layer .acal-card-art.acal-loaded .material-icons { display: none; }
#${PAGE_ID} .acal-card-body, .acal-layer .acal-card-body { display: flex; flex-direction: column; gap: 0.15em; min-width: 0; flex: 1 1 auto; }
#${PAGE_ID} .acal-tile { flex-direction: column; gap: 0; padding: 0; }
#${PAGE_ID} .acal-tile-art { position: relative; display: flex; align-items: center; justify-content: center; width: 100%; aspect-ratio: 16 / 9; background: var(--acal-placeholder) center 30% / cover no-repeat; box-shadow: inset 0 -3px 0 var(--acal-status); }
#${PAGE_ID} .acal-tile-art .material-icons { font-size: 1.9em; color: var(--acal-type); opacity: 0.5; }
#${PAGE_ID} .acal-tile-art.acal-loaded .material-icons { display: none; }
#${PAGE_ID} .acal-tile[data-status="downloading"] .acal-tile-art { box-shadow: inset 0 -3px 0 rgba(163, 113, 247, 0.28); }
#${PAGE_ID} .acal-tile[data-status="downloading"] .acal-tile-art::after { content: ''; position: absolute; left: 0; bottom: 0; height: 3px; width: var(--acal-progress, 0%); background: var(--acal-downloading); }
#${PAGE_ID} .acal-tile .acal-card-body { padding: 0.5em 0.6em 0.6em; }
#${PAGE_ID} .acal-card-kicker, .acal-layer .acal-card-kicker { display: flex; align-items: center; gap: 0.3em; font-size: 0.74em; color: var(--acal-muted); font-variant-numeric: tabular-nums; white-space: nowrap; overflow: hidden; }
#${PAGE_ID} .acal-card-kicker .acal-glyph, .acal-layer .acal-card-kicker .acal-glyph { font-size: 1.3em; }
#${PAGE_ID} .acal-card-kicker-text, .acal-layer .acal-card-kicker-text { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
#${PAGE_ID} .acal-card-title, .acal-layer .acal-card-title { font-weight: 600; font-size: 0.88em; line-height: 1.25; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; overflow-wrap: anywhere; }
#${PAGE_ID} .acal-card-sub, .acal-layer .acal-card-sub { font-size: 0.76em; color: var(--acal-muted); display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; overflow-wrap: anywhere; }
#${PAGE_ID} .acal-card-status, .acal-layer .acal-card-status { display: flex; align-items: center; gap: 0.35em; font-size: 0.72em; font-weight: 600; color: var(--acal-status); margin-top: 0.1em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#${PAGE_ID} .acal-dot, .acal-layer .acal-dot { flex: 0 0 auto; width: 0.55em; height: 0.55em; border-radius: 50%; background: var(--acal-status); }
#${PAGE_ID} [data-status="airing"] .acal-dot, .acal-layer [data-status="airing"] .acal-dot { animation: acal-pulse 1.6s ease-in-out infinite; }
@keyframes acal-pulse { 50% { opacity: 0.35; } }
#${PAGE_ID} .acal-meter, .acal-layer .acal-meter { height: 0.25em; border-radius: 0.2em; background: var(--acal-hover); overflow: hidden; margin-top: 0.2em; }
#${PAGE_ID} .acal-meter > span, .acal-layer .acal-meter > span { display: block; height: 100%; background: var(--acal-downloading); border-radius: inherit; }

#${PAGE_ID} .acal-compact .acal-grid { gap: 1px; }
#${PAGE_ID} .acal-mday { background: var(--acal-ground); border: 0; color: inherit; font: inherit; min-height: 3.4em; padding: 0.35em 0 0.4em; display: flex; flex-direction: column; align-items: center; gap: 0.3em; cursor: pointer; }
#${PAGE_ID} .acal-mday.acal-outside .acal-day-num { opacity: 0.4; }
#${PAGE_ID} .acal-mday.acal-selected { background: var(--acal-hover); }
#${PAGE_ID} .acal-mday.acal-selected:not(.acal-today) .acal-day-num { box-shadow: inset 0 0 0 1.5px var(--acal-text); }
#${PAGE_ID} .acal-dots { display: flex; gap: 0.2em; height: 0.4em; }
#${PAGE_ID} .acal-dots i { width: 0.4em; height: 0.4em; border-radius: 50%; background: var(--acal-type); }
#${PAGE_ID} .acal-agenda { margin-top: 1em; display: flex; flex-direction: column; gap: 1.1em; }
#${PAGE_ID} .acal-agenda-day h2 { display: flex; align-items: baseline; gap: 0.5em; margin: 0 0 0.5em; font-size: 1em; font-weight: 600; }
#${PAGE_ID} .acal-agenda-day h2 small { font-size: 0.8em; font-weight: 400; color: var(--acal-muted); }
#${PAGE_ID} .acal-agenda-day.acal-today h2 { color: var(--acal-accent); }
#${PAGE_ID} .acal-agenda-list { display: flex; flex-direction: column; gap: 0.5em; }
#${PAGE_ID} .acal-agenda .acal-card { padding: 0.55em; }
#${PAGE_ID} .acal-agenda .acal-card-art { width: 3.4em; }
#${PAGE_ID} .acal-agenda .acal-card-title { font-size: 0.98em; }
#${PAGE_ID} .acal-agenda .acal-card-sub { font-size: 0.84em; }
#${PAGE_ID} .acal-agenda .acal-card-kicker, #${PAGE_ID} .acal-agenda .acal-card-status { font-size: 0.8em; }

#${PAGE_ID} .acal-message { max-width: 34em; margin: 4em auto; text-align: center; }
#${PAGE_ID} .acal-message > .material-icons { font-size: 3em; color: var(--acal-muted); }
#${PAGE_ID} .acal-message h2 { margin: 0.4em 0; }
#${PAGE_ID} .acal-message p { color: var(--acal-muted); line-height: 1.5; }
#${PAGE_ID} .acal-empty { margin: 0.9em 0 0; text-align: center; color: var(--acal-muted); font-size: 0.9em; }

.acal-layer { position: fixed; inset: 0; z-index: 1100; display: flex; align-items: center; justify-content: center; padding: 1em; background: rgba(0, 0, 0, 0.62); color: var(--acal-text); animation: acal-fade 0.14s ease; }
@keyframes acal-fade { from { opacity: 0; } }
.acal-dialog { position: relative; width: 100%; max-width: 40em; max-height: 100%; overflow: auto; border-radius: 1em; background: var(--acal-paper); box-shadow: 0 1.5em 4em rgba(0, 0, 0, 0.55); outline: none; }
.acal-dialog-hero { position: relative; height: 9em; background: var(--acal-placeholder) center 25% / cover no-repeat; }
.acal-dialog-hero::after { content: ''; position: absolute; inset: 0; background: linear-gradient(to bottom, rgba(0, 0, 0, 0.1), var(--acal-paper)); }
.acal-dialog-close { position: absolute; top: 0.6em; right: 0.6em; z-index: 2; background: rgba(0, 0, 0, 0.45) !important; color: #fff !important; }
.acal-dialog-main { position: relative; z-index: 1; display: flex; gap: 1.2em; padding: 0 1.4em 1.4em; margin-top: -5em; }
.acal-dialog-poster { flex: 0 0 auto; width: 8.5em; aspect-ratio: 2 / 3; border-radius: 0.6em; background: var(--acal-placeholder) center / cover no-repeat; box-shadow: 0 0.5em 1.6em rgba(0, 0, 0, 0.5); display: flex; align-items: center; justify-content: center; }
.acal-dialog-poster .material-icons { font-size: 2.6em; color: var(--acal-type); opacity: 0.5; }
.acal-dialog-poster.acal-loaded .material-icons { display: none; }
.acal-dialog-info { flex: 1 1 auto; min-width: 0; padding-top: 4.6em; }
.acal-kicker { display: flex; align-items: center; gap: 0.4em; font-size: 0.8em; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; color: var(--acal-muted); }
.acal-kicker .acal-glyph { font-size: 1.35em; }
.acal-dialog h2 { margin: 0.25em 0 0.15em; font-size: 1.45em; line-height: 1.2; }
.acal-dialog-sub { font-size: 1em; color: var(--acal-text); margin-bottom: 0.35em; }
.acal-dialog-when { color: var(--acal-muted); font-size: 0.9em; margin-bottom: 0.8em; }
.acal-chips { display: flex; flex-wrap: wrap; gap: 0.4em; margin-bottom: 0.8em; }
.acal-chip { display: inline-flex; align-items: center; gap: 0.4em; padding: 0.25em 0.75em; border-radius: 1em; font-size: 0.8em; font-weight: 600; background: var(--acal-hover); color: var(--acal-text); }
.acal-chip[data-status] { color: var(--acal-status); box-shadow: inset 0 0 0 1px var(--acal-status); background: transparent; }
.acal-progress { margin: 0 0 0.9em; max-width: 26em; }
.acal-progress-label { font-size: 0.85em; color: var(--acal-muted); margin-bottom: 0.35em; }
.acal-progress .acal-meter { height: 0.35em; }
.acal-overview { line-height: 1.55; font-size: 0.94em; margin: 0 0 0.9em; }
.acal-overview-label { display: block; font-size: 0.78em; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; color: var(--acal-muted); margin-bottom: 0.25em; }
.acal-facts { display: grid; grid-template-columns: auto 1fr; gap: 0.3em 1em; font-size: 0.88em; margin: 0 0 1em; }
.acal-facts dt { color: var(--acal-muted); }
.acal-facts dd { margin: 0; }
.acal-facts .acal-fact-now { color: var(--acal-text); font-weight: 600; }
.acal-episodes { list-style: none; padding: 0; margin: 0 0 1em; border-top: 1px solid var(--acal-divider); }
.acal-episodes li { display: flex; align-items: center; gap: 0.6em; padding: 0.45em 0; border-bottom: 1px solid var(--acal-divider); font-size: 0.88em; }
.acal-episodes li .acal-ep-code { font-variant-numeric: tabular-nums; color: var(--acal-muted); flex: 0 0 auto; }
.acal-episodes li .acal-ep-title { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.acal-dialog-actions { display: flex; flex-wrap: wrap; gap: 0.5em; min-height: 2.3em; }
.acal-day-dialog { padding: 1.2em 1.2em 1.3em; }
.acal-day-dialog h2 { margin: 0 2.4em 0.8em 0; font-size: 1.2em; }
.acal-day-dialog .acal-dialog-close { background: transparent !important; color: var(--acal-text) !important; }
.acal-day-dialog .acal-agenda-list { display: flex; flex-direction: column; gap: 0.5em; }
@media (max-width: 34em) {
  .acal-dialog-main { flex-direction: column; align-items: center; text-align: center; margin-top: -6em; }
  .acal-dialog-info { padding-top: 0; }
  .acal-kicker, .acal-chips, .acal-dialog-actions { justify-content: center; }
  .acal-facts, .acal-episodes, .acal-overview { text-align: left; }
  .acal-progress { margin-left: auto; margin-right: auto; text-align: left; }
}

.acal-toast { position: fixed; left: 50%; bottom: 2em; transform: translateX(-50%); z-index: 1200; max-width: calc(100% - 2em); background: var(--jf-palette-SnackbarContent-bg, #323232); color: var(--jf-palette-SnackbarContent-color, #fff); padding: 0.8em 1.2em; border-radius: 0.4em; box-shadow: 0 0.3em 1em rgba(0, 0, 0, 0.4); }

.acal-flyout { position: fixed; z-index: 1300; min-width: 11em; padding: 0.35em; border-radius: 0.65em; background: var(--acal-paper); color: var(--acal-text); box-shadow: 0 0.7em 2em rgba(0, 0, 0, 0.5), 0 0 0 1px var(--acal-divider); opacity: 0; transform: translateY(-0.3em); pointer-events: none; transition: opacity 0.12s ease, transform 0.12s ease; }
.acal-flyout.acal-open { opacity: 1; transform: none; pointer-events: auto; }
.acal-flyout::before { content: ''; position: absolute; left: 0; right: 0; top: -0.7em; height: 0.7em; }
.acal-flyout-item { display: flex; align-items: center; gap: 0.75em; padding: 0.6em 0.85em; border-radius: 0.45em; color: inherit; text-decoration: none; font-size: 0.95em; white-space: nowrap; }
.acal-flyout-item:hover, .acal-flyout-item:focus-visible, .acal-flyout-item.acal-current { background: var(--acal-hover); outline: none; }
.acal-flyout-item .material-icons { font-size: 1.3em; opacity: 0.9; }
html.arr-calendar-nested-appbar .MuiAppBar-root [${SEERR_NAV_ATTR}="appbar"]::after { content: 'expand_more'; font-family: 'Material Icons'; font-weight: normal; font-style: normal; font-size: 1.15em; line-height: 1; letter-spacing: normal; text-transform: none; font-feature-settings: 'liga'; -webkit-font-feature-settings: 'liga'; margin-left: 0.15em; opacity: 0.7; transition: transform 0.15s ease; }
html.arr-calendar-nested-appbar .MuiAppBar-root [${SEERR_NAV_ATTR}="appbar"][aria-expanded="true"]::after { transform: rotate(180deg); }

.mainDrawer-scrollContainer a[${NAV_ATTR}="drawer-nested"] { position: relative; max-height: 0; padding-top: 0 !important; padding-bottom: 0 !important; padding-left: 3.6em !important; opacity: 0; overflow: hidden; transition: max-height 0.18s ease 0.22s, padding 0.18s ease 0.22s, opacity 0.12s ease 0.22s; }
.mainDrawer-scrollContainer a[${SEERR_NAV_ATTR}="drawer"]:hover + a[${NAV_ATTR}="drawer-nested"],
.mainDrawer-scrollContainer a[${SEERR_NAV_ATTR}="drawer"]:focus + a[${NAV_ATTR}="drawer-nested"],
.mainDrawer-scrollContainer a[${NAV_ATTR}="drawer-nested"]:hover,
.mainDrawer-scrollContainer a[${NAV_ATTR}="drawer-nested"]:focus,
.mainDrawer-scrollContainer a[${NAV_ATTR}="drawer-nested"].navMenuOption-selected { max-height: 4em; padding-top: 0.75em !important; padding-bottom: 0.75em !important; opacity: 1; transition-delay: 0s; }
.mainDrawer-scrollContainer a[${NAV_ATTR}="drawer-nested"]::before { content: ''; position: absolute; left: 3.05em; top: 0; height: 50%; width: 0.35em; border-left: 1px solid currentColor; border-bottom: 1px solid currentColor; border-bottom-left-radius: 0.3em; opacity: 0.35; }
html.arr-calendar-nested-drawer .mainDrawer-scrollContainer a[${SEERR_NAV_ATTR}="drawer"]::after { content: 'expand_more'; font-family: 'Material Icons'; font-size: 1.25em; line-height: 1; font-feature-settings: 'liga'; -webkit-font-feature-settings: 'liga'; margin-left: auto; margin-right: 1.2em; opacity: 0.55; transition: transform 0.15s ease; }
html.arr-calendar-nested-drawer .mainDrawer-scrollContainer a[${SEERR_NAV_ATTR}="drawer"]:hover::after { transform: rotate(180deg); }

.MuiDrawer-root li[${NAV_ATTR}="menu-nested"] { max-height: 0; padding-top: 0; padding-bottom: 0; opacity: 0; overflow: hidden; transition: max-height 0.18s ease 0.22s, opacity 0.12s ease 0.22s; }
.MuiDrawer-root li[${NAV_ATTR}="menu-nested"] > a { padding-left: 2.6em; }
.MuiDrawer-root li[${SEERR_NAV_ATTR}="menu"]:hover + li[${NAV_ATTR}="menu-nested"],
.MuiDrawer-root li[${SEERR_NAV_ATTR}="menu"]:focus-within + li[${NAV_ATTR}="menu-nested"],
.MuiDrawer-root li[${NAV_ATTR}="menu-nested"]:hover,
.MuiDrawer-root li[${NAV_ATTR}="menu-nested"]:focus-within,
.MuiDrawer-root li[${NAV_ATTR}="menu-nested"].acal-current { max-height: 5em; opacity: 1; transition-delay: 0s; }
html.arr-calendar-nested-menu .MuiDrawer-root li[${SEERR_NAV_ATTR}="menu"] > a::after { content: 'expand_more'; font-family: 'Material Icons'; font-size: 1.3em; line-height: 1; font-feature-settings: 'liga'; -webkit-font-feature-settings: 'liga'; margin-left: auto; opacity: 0.55; transition: transform 0.15s ease; }
html.arr-calendar-nested-menu .MuiDrawer-root li[${SEERR_NAV_ATTR}="menu"]:hover > a::after { transform: rotate(180deg); }

@media (max-width: 50em) {
  #${PAGE_ID} .acal-title { font-size: 1.4em; }
  #${PAGE_ID} .acal-toolbar { gap: 0.6em; }
  #${PAGE_ID} .acal-dow { padding: 0.5em 0; text-align: center; letter-spacing: 0.04em; }
  #${PAGE_ID} .acal-legend { font-size: 0.76em; gap: 0.3em 0.8em; }
  #${PAGE_ID} .acal-legend-group { gap: 0.3em 0.8em; }
}
@media (prefers-reduced-motion: reduce) {
  .acal-spinner { animation-duration: 3s; }
  .acal-layer, .acal-flyout, #${PAGE_ID} [data-status="airing"] .acal-dot { animation: none; transition: none; }
  .mainDrawer-scrollContainer a[${NAV_ATTR}="drawer-nested"], .MuiDrawer-root li[${NAV_ATTR}="menu-nested"] { transition: none; }
}
`;

    function injectStyles() {
        if (document.getElementById('arr-calendar-style')) {
            return;
        }

        const style = document.createElement('style');
        style.id = 'arr-calendar-style';
        style.textContent = STYLE;
        document.head.appendChild(style);
    }

    // ---------------------------------------------------------------------------------
    // Navigation entries.
    // ---------------------------------------------------------------------------------

    function matches(query) {
        return !!(window.matchMedia && window.matchMedia(query).matches);
    }

    // A device that can hover gets Calendar under Requests; one that cannot keeps its own entry.
    function canHover() {
        return matches(HOVER_QUERY);
    }

    // Seerr Integration's loader runs from the same JavaScript Injector file as this page's, so
    // by the time this script runs its flag is set if Requests is coming. For a few seconds the
    // Calendar entry waits for it rather than appearing on its own and then jumping under it.
    function waitingForRequests() {
        return canHover()
            && !!(window.__seerrIntegrationLoader || window.SeerrIntegration)
            && Date.now() - startedAt < SEERR_GRACE_MS;
    }

    function setNestedClass(name, on) {
        document.documentElement.classList.toggle('arr-calendar-nested-' + name, on);
    }

    function setLastTextNode(element, text) {
        for (let node = element.lastChild; node; node = node.previousSibling) {
            if (node.nodeType === 3 && node.textContent.trim()) {
                node.textContent = text;
                return true;
            }
        }

        return false;
    }

    function placeAfter(node, anchor) {
        if (node.previousElementSibling !== anchor || node.parentNode !== anchor.parentNode) {
            anchor.parentNode.insertBefore(node, anchor.nextSibling);
        }
    }

    function removeNav() {
        document.querySelectorAll('[' + NAV_ATTR + ']').forEach(function (node) { node.remove(); });
        ['drawer', 'menu', 'appbar'].forEach(function (name) { setNestedClass(name, false); });
    }

    // The legacy drawer, used by the Desktop, Mobile and TV display modes: right after Home, where
    // Jellyfin puts its own custom menu links, or right after Requests when Seerr Integration has
    // put that there.
    function ensureLegacyDrawerEntry(active, hover) {
        const drawer = document.querySelector('.mainDrawer-scrollContainer');
        if (!drawer) {
            return;
        }

        const home = drawer.querySelector('a.navMenuOption[href="#/home"]');
        if (!home) {
            return;
        }

        const requests = drawer.querySelector('a[' + SEERR_NAV_ATTR + '="drawer"]');
        let link = drawer.querySelector('[' + NAV_ATTR + '^="drawer"]');
        if (!requests && waitingForRequests()) {
            if (link) {
                link.remove();
            }

            return;
        }

        const nested = !!requests && hover;
        setNestedClass('drawer', nested);
        if (!link) {
            link = document.createElement('a');
            link.setAttribute('is', 'emby-linkbutton');
            link.className = 'navMenuOption lnkMediaFolder emby-button';
            link.href = '#' + ROUTE;
            link.innerHTML = '<span class="material-icons navMenuOptionIcon" aria-hidden="true">calendar_month</span>'
                + '<span class="sectionName navMenuOptionText">Calendar</span>';
        }

        link.setAttribute(NAV_ATTR, nested ? 'drawer-nested' : 'drawer');
        let anchor = requests;
        if (!anchor) {
            const custom = drawer.querySelector('.customMenuOptions');
            anchor = custom && custom.parentNode === home.parentNode ? custom : home;
        }

        placeAfter(link, anchor);
        link.classList.toggle('navMenuOption-selected', active);
    }

    // The Modern app bar on wide screens: a clone of Favorites, so it carries the same Material
    // UI classes and follows the theme. With Requests there and a pointer that hovers, Calendar
    // becomes a small menu under Requests instead.
    function ensureAppBarEntry(active, hover) {
        const favorites = document.querySelector('.MuiAppBar-root a[href="#/home?tab=1"]');
        if (!favorites) {
            setNestedClass('appbar', false);
            closeFlyout(true);
            return;
        }

        const container = favorites.parentNode;
        const requests = container.querySelector('[' + SEERR_NAV_ATTR + '="appbar"]');
        let link = container.querySelector('[' + NAV_ATTR + '="appbar"]');
        const nested = !!requests && hover;
        setNestedClass('appbar', nested);
        if (nested) {
            if (link) {
                link.remove();
            }

            bindFlyout(requests);
            return;
        }

        closeFlyout(true);
        if (!requests && waitingForRequests()) {
            if (link) {
                link.remove();
            }

            return;
        }

        if (!link) {
            link = favorites.cloneNode(true);
            link.setAttribute(NAV_ATTR, 'appbar');
            link.setAttribute('href', '#' + ROUTE);
            link.removeAttribute('aria-current');
            link.classList.remove('Mui-selected');
            const path = link.querySelector('svg path');
            if (path) {
                path.setAttribute('d', CALENDAR_ICON_PATH);
                Array.prototype.slice.call(link.querySelectorAll('svg path'), 1).forEach(function (extra) { extra.remove(); });
            }

            if (!setLastTextNode(link, 'Calendar')) {
                link.appendChild(document.createTextNode('Calendar'));
            }
        }

        placeAfter(link, requests || favorites);
        if (active) {
            link.setAttribute('aria-current', 'page');
        } else {
            link.removeAttribute('aria-current');
        }
    }

    // The Modern menu that replaces the app bar links on narrow screens. Material UI mounts it only
    // while open, so the entry is added each time it appears.
    function ensureModernMenuEntry(active, hover) {
        const favorites = document.querySelector('.MuiDrawer-root a[href="#/home?tab=1"]');
        if (!favorites) {
            setNestedClass('menu', false);
            return;
        }

        const item = favorites.closest('li') || favorites;
        const list = item.parentNode;
        const requests = list.querySelector('[' + SEERR_NAV_ATTR + '="menu"]');
        let entry = list.querySelector('[' + NAV_ATTR + '^="menu"]');
        if (!requests && waitingForRequests()) {
            if (entry) {
                entry.remove();
            }

            return;
        }

        const nested = !!requests && hover;
        setNestedClass('menu', nested);
        if (!entry) {
            entry = item.cloneNode(true);
            const link = entry.matches('a') ? entry : entry.querySelector('a');
            link.setAttribute('href', '#' + ROUTE);
            link.classList.remove('Mui-selected');
            link.removeAttribute('aria-current');
            const path = link.querySelector('svg path');
            if (path) {
                path.setAttribute('d', CALENDAR_ICON_PATH);
            }

            const label = link.querySelector('.MuiListItemText-primary') || link.querySelector('.MuiTypography-root');
            if (label) {
                label.textContent = 'Calendar';
            } else {
                setLastTextNode(link, 'Calendar');
            }

            // The clone has none of React's handlers, so close the menu the way a click outside
            // it would.
            link.addEventListener('click', function () {
                const root = link.closest('.MuiDrawer-root');
                const backdrop = root && root.querySelector('.MuiBackdrop-root');
                if (backdrop) {
                    setTimeout(function () { backdrop.click(); }, 0);
                }
            });
        }

        entry.setAttribute(NAV_ATTR, nested ? 'menu-nested' : 'menu');
        placeAfter(entry, requests || item);
        const entryLink = entry.matches('a') ? entry : entry.querySelector('a');
        if (entryLink) {
            entryLink.classList.toggle('Mui-selected', active);
        }

        entry.classList.toggle('acal-current', active);
    }

    // The small menu under Requests in the app bar. It lives on <body>, positioned under the
    // link when it opens, so nothing in the app bar can clip it.
    let flyout = null;
    let flyoutAnchor = null;
    let flyoutTimer = 0;
    let returningFocus = false;

    function flyoutElement() {
        if (!flyout) {
            flyout = document.createElement('div');
            flyout.className = 'acal-flyout';
            flyout.setAttribute(NAV_ATTR, 'flyout');
            flyout.setAttribute('role', 'menu');
            flyout.innerHTML = '<a class="acal-flyout-item" role="menuitem" href="#' + ROUTE + '">'
                + '<span class="material-icons" aria-hidden="true">calendar_month</span><span>Calendar</span></a>';
            flyout.addEventListener('mouseenter', function () { clearTimeout(flyoutTimer); });
            flyout.addEventListener('mouseleave', scheduleCloseFlyout);
            flyout.addEventListener('focusout', function (event) {
                if (!flyout.contains(event.relatedTarget) && event.relatedTarget !== flyoutAnchor) {
                    scheduleCloseFlyout();
                }
            });
            flyout.addEventListener('keydown', function (event) {
                if (event.key === 'Escape') {
                    // Focus goes back to Requests, whose focus would otherwise open the menu
                    // again straight away.
                    const anchor = flyoutAnchor;
                    closeFlyout(true);
                    if (anchor) {
                        returningFocus = true;
                        anchor.focus();
                        returningFocus = false;
                    }
                }
            });
            flyout.addEventListener('click', function () { closeFlyout(true); });
            document.body.appendChild(flyout);
        }

        return flyout;
    }

    function openFlyout(anchor) {
        clearTimeout(flyoutTimer);
        const element = flyoutElement();
        const rect = anchor.getBoundingClientRect();
        element.style.left = Math.max(8, Math.round(rect.left)) + 'px';
        element.style.top = Math.round(rect.bottom + 6) + 'px';
        element.querySelector('.acal-flyout-item').classList.toggle('acal-current', !!parseRoute(window.location.hash));
        element.classList.add('acal-open');
        if (flyoutAnchor && flyoutAnchor !== anchor) {
            flyoutAnchor.setAttribute('aria-expanded', 'false');
        }

        flyoutAnchor = anchor;
        anchor.setAttribute('aria-expanded', 'true');
    }

    function scheduleCloseFlyout() {
        clearTimeout(flyoutTimer);
        flyoutTimer = setTimeout(function () { closeFlyout(false); }, 220);
    }

    function closeFlyout(immediately) {
        clearTimeout(flyoutTimer);
        if (!flyout || !flyout.classList.contains('acal-open')) {
            return;
        }

        if (!immediately && (flyout.matches(':hover') || flyout.contains(document.activeElement))) {
            return;
        }

        flyout.classList.remove('acal-open');
        if (flyoutAnchor) {
            flyoutAnchor.setAttribute('aria-expanded', 'false');
        }

        flyoutAnchor = null;
    }

    // Seerr Integration re-adds its entry whenever React redraws the app bar, so each new element
    // is bound once.
    function bindFlyout(anchor) {
        if (anchor.__arrCalendarFlyout) {
            return;
        }

        anchor.__arrCalendarFlyout = true;
        anchor.setAttribute('aria-haspopup', 'menu');
        anchor.setAttribute('aria-expanded', 'false');
        anchor.addEventListener('mouseenter', function () {
            if (canHover()) {
                openFlyout(anchor);
            }
        });
        anchor.addEventListener('mouseleave', scheduleCloseFlyout);
        anchor.addEventListener('focus', function () {
            if (!returningFocus) {
                openFlyout(anchor);
            }
        });
        anchor.addEventListener('blur', function (event) {
            if (!flyout || !flyout.contains(event.relatedTarget)) {
                scheduleCloseFlyout();
            }
        });
        anchor.addEventListener('keydown', function (event) {
            if (event.key === 'ArrowDown') {
                event.preventDefault();
                openFlyout(anchor);
                flyoutElement().querySelector('a').focus();
            } else if (event.key === 'Escape') {
                closeFlyout(true);
            }
        });
    }

    function ensureNav() {
        if (!isSignedIn()) {
            removeNav();
            closeFlyout(true);
            return;
        }

        const active = !!parseRoute(window.location.hash);
        const hover = canHover();
        ensureLegacyDrawerEntry(active, hover);
        ensureAppBarEntry(active, hover);
        ensureModernMenuEntry(active, hover);
    }

    // Runs at most once a frame for any DOM change. Jellyfin Web's router navigates with
    // pushState, which fires no event, but every navigation renders a page, so this is also where
    // such a navigation away from the Calendar is noticed.
    let navQueued = false;
    function queueNav() {
        if (navQueued) {
            return;
        }

        navQueued = true;
        requestAnimationFrame(function () {
            navQueued = false;
            try {
                if (window.location.hash !== lastHash) {
                    onLocationChange();
                }

                ensureNav();
            } catch (e) {
                console.error('Arr Calendar: could not add the Calendar entry', e);
            }
        });
    }

    // ---------------------------------------------------------------------------------
    // Page host.
    // ---------------------------------------------------------------------------------

    function pageElement() {
        let page = document.getElementById(PAGE_ID);
        if (!page) {
            page = document.createElement('div');
            page.id = PAGE_ID;
            page.className = 'page libraryPage mainAnimatedPage hide';
            page.setAttribute('data-title', 'Calendar');
            page.setAttribute('data-backbutton', 'true');
            page.setAttribute('role', 'main');
            page.innerHTML = '<div class="padded-left padded-right acal-root">'
                + '<header class="acal-toolbar">'
                + '<div class="acal-heading">'
                + '<h1 class="acal-title" aria-live="polite"></h1>'
                + '<div class="acal-stepper">'
                + '<button type="button" class="acal-icon-btn" data-action="prev"><span class="material-icons" aria-hidden="true">chevron_left</span></button>'
                + '<button type="button" class="acal-btn" data-action="today">Today</button>'
                + '<button type="button" class="acal-icon-btn" data-action="next"><span class="material-icons" aria-hidden="true">chevron_right</span></button>'
                + '</div>'
                + '<div class="acal-busy" role="status" aria-label="Loading" hidden><div class="acal-spinner"></div></div>'
                + '</div>'
                + '<div class="acal-switch" role="group" aria-label="Calendar view">'
                + '<button type="button" data-view="month" aria-pressed="true">Month</button>'
                + '<button type="button" data-view="week" aria-pressed="false">Week</button>'
                + '</div>'
                + '</header>'
                + legendHtml()
                + '<div class="acal-notices"></div>'
                + '<div class="acal-view"></div>'
                + '</div>';
            const host = document.querySelector('.mainAnimatedPages') || document.querySelector('#reactRoot') || document.body;
            host.appendChild(page);
            bindPage(page);
        }

        return page;
    }

    // The legend's words depend on the server's "Don't specify method", which arrives with the
    // calendar, so it is drawn invisibly until then: the space is kept and no word changes on
    // screen.
    function legendHtml() {
        return '<div class="acal-legend" aria-label="Legend" style="visibility:hidden">' + legendInnerHtml(false) + '</div>';
    }

    function legendInnerHtml(neutral) {
        return '<span class="acal-legend-group">'
            + '<span class="acal-legend-item" data-kind="episode"><span class="material-icons acal-glyph" aria-hidden="true">tv</span>TV</span>'
            + '<span class="acal-legend-item" data-kind="movie"><span class="material-icons acal-glyph" aria-hidden="true">movie</span>Movies</span>'
            + '</span>'
            + '<span class="acal-legend-sep" aria-hidden="true"></span>'
            + '<span class="acal-legend-group">'
            + legendStatuses(neutral).map(function (status) {
                return '<span class="acal-legend-item" data-status="' + status[0] + '"><i class="acal-swatch" aria-hidden="true"></i>' + escapeHtml(status[1]) + '</span>';
            }).join('')
            + '</span>';
    }

    function renderLegend(page) {
        const legend = page.querySelector('.acal-legend');
        if (legend.getAttribute('data-neutral') !== String(neutralWording)) {
            legend.innerHTML = legendInnerHtml(neutralWording);
            legend.setAttribute('data-neutral', String(neutralWording));
        }

        legend.style.visibility = wordingKnown ? '' : 'hidden';
    }

    function showPage() {
        injectStyles();
        const page = pageElement();
        document.documentElement.classList.add(ACTIVE_CLASS);
        if (page.classList.contains('hide')) {
            page.classList.remove('hide');
            announce(page);
            startRefresh();
        }

        return page;
    }

    // The events Jellyfin Web's own pages raise. Its legacy header answers them by clearing the
    // previous page's section tabs, showing this page's title and back button. In the Modern
    // layout nothing listens, and they do nothing.
    function announce(page) {
        ['pagebeforeshow', 'pageshow'].forEach(function (type) {
            page.dispatchEvent(new CustomEvent(type, { bubbles: true, detail: { isRestored: false } }));
        });
        document.title = 'Calendar';
    }

    function hidePage() {
        document.documentElement.classList.remove(ACTIVE_CLASS);
        const page = document.getElementById(PAGE_ID);
        if (page) {
            page.classList.add('hide');
            const host = page.querySelector('.acal-view');
            if (host) {
                host.innerHTML = '';
            }
        }

        currentRoute = null;
        view = null;
        renderToken++;
        stopRefresh();
        closeLayers();
    }

    function onLocationChange() {
        const hash = window.location.hash;
        if (hash === lastHash) {
            return;
        }

        lastHash = hash;
        const route = parseRoute(hash);
        queueNav();
        closeFlyout(true);
        if (!route || !isSignedIn()) {
            if (currentRoute) {
                hidePage();
            }

            return;
        }

        resetIfUserChanged();
        const page = showPage();
        currentRoute = route;
        render(page, route);
    }

    function today() {
        return startOfDay(new Date());
    }

    // Moves the calendar without leaving a history entry for every month paged through: Back
    // leaves the Calendar rather than stepping back through it.
    function navigate(route) {
        const todayKey = dayKey(today());
        const next = { view: route.view, date: route.date === todayKey ? null : route.date };
        history.replaceState(history.state, '', routeHash(next));
        onLocationChange();
    }

    // ---------------------------------------------------------------------------------
    // Rendering.
    // ---------------------------------------------------------------------------------

    function messageHtml(icon, title, text, retry) {
        return '<div class="acal-message">'
            + '<span class="material-icons" aria-hidden="true">' + escapeHtml(icon) + '</span>'
            + '<h2>' + escapeHtml(title) + '</h2>'
            + '<p>' + escapeHtml(text) + '</p>'
            + (retry ? '<button type="button" class="acal-btn" data-action="retry"><span class="material-icons" aria-hidden="true">refresh</span>Try again</button>' : '')
            + '</div>';
    }

    const BLOCKING_ICONS = { session: 'lock', disabled: 'event_busy', inactive: 'extension_off' };

    function isCompact() {
        return matches(COMPACT_QUERY);
    }

    // Phones open on the week either way up, so turning one keeps the view.
    function isPhone() {
        return isCompact() || matches(SIDEWAYS_PHONE_QUERY);
    }

    function titleHtml(shown, anchor, firstDay) {
        if (shown === 'week') {
            const days = weekDays(anchor, firstDay);
            let text;
            try {
                text = new Intl.DateTimeFormat(locale(), { month: 'short', day: 'numeric', year: 'numeric' }).formatRange(days[0], days[6]);
            } catch (e) {
                text = format({ month: 'short', day: 'numeric' }, days[0]) + ' – ' + format({ month: 'short', day: 'numeric', year: 'numeric' }, days[6]);
            }

            return escapeHtml(text);
        }

        try {
            return new Intl.DateTimeFormat(locale(), { month: 'long', year: 'numeric' }).formatToParts(anchor).map(function (part) {
                return part.type === 'month'
                    ? '<strong>' + escapeHtml(part.value) + '</strong>'
                    : (part.type === 'year' ? '<span class="acal-title-soft">' + escapeHtml(part.value) + '</span>' : escapeHtml(part.value));
            }).join('');
        } catch (e) {
            return escapeHtml(format({ month: 'long', year: 'numeric' }, anchor));
        }
    }

    function render(page, route) {
        const token = ++renderToken;
        const firstDay = firstDayOfWeek(locale());
        const anchor = parseDayKey(route.date) || today();
        // Without a chosen view, phones open on the week and wider screens on the month.
        const shown = route.view || defaultView(isPhone());
        const range = visibleRange(shown, anchor, firstDay);
        const sameRange = view && view.view === shown && dayKey(view.range.start) === dayKey(range.start);
        view = {
            view: shown,
            anchor: anchor,
            firstDay: firstDay,
            range: range,
            data: sameRange ? view.data : null,
            selected: sameRange ? view.selected : null,
            compact: isCompact()
        };

        page.querySelector('.acal-title').innerHTML = titleHtml(shown, anchor, firstDay);
        page.querySelectorAll('.acal-switch button').forEach(function (button) {
            button.setAttribute('aria-pressed', String(button.getAttribute('data-view') === shown));
        });
        page.querySelector('[data-action="prev"]').setAttribute('aria-label', shown === 'week' ? 'Previous week' : 'Previous month');
        page.querySelector('[data-action="next"]').setAttribute('aria-label', shown === 'week' ? 'Next week' : 'Next month');
        page.classList.remove('acal-blocked');
        page.querySelector('.acal-legend').hidden = false;
        drawView(page);
        load(page, token, false);
    }

    function load(page, token, fresh) {
        const busy = page.querySelector('.acal-busy');
        busy.hidden = false;
        loadRange(view.range, fresh).then(function (data) {
            if (token !== renderToken) {
                return;
            }

            busy.hidden = true;
            view.data = data;
            drawView(page);
        }).catch(function (error) {
            if (token !== renderToken) {
                return;
            }

            busy.hidden = true;
            const info = describeError(error);
            if (info.blocking) {
                page.classList.add('acal-blocked');
                page.querySelector('.acal-legend').hidden = true;
                page.querySelector('.acal-notices').innerHTML = '';
                page.querySelector('.acal-view').innerHTML = messageHtml(BLOCKING_ICONS[info.kind] || 'error_outline', info.title, info.text, false);
            } else if (view.data) {
                toast(info.text);
            } else {
                page.querySelector('.acal-view').innerHTML = messageHtml('error_outline', info.title, info.text, true);
            }
        });
    }

    // Redraws the current range from what is already loaded. Cheap, so it also runs on a timer,
    // keeping on air and missing in step with the clock.
    function drawView(page) {
        if (!view) {
            return;
        }

        const data = view.data;
        const events = data && Array.isArray(data.events) ? data.events : [];
        const days = bucketByDay(events);
        const now = Date.now();
        view.compact = isCompact();
        renderLegend(page);
        page.querySelector('.acal-notices').innerHTML = noticesHtml(data);
        const host = page.querySelector('.acal-view');
        if (data && allUnconfigured(data)) {
            host.innerHTML = messageHtml('event_note', 'The calendar is not set up yet', 'Ask the server admin to connect Sonarr or Radarr in the Arr Calendar settings.', false);
            return;
        }

        if (view.view === 'week') {
            host.innerHTML = view.compact ? weekAgendaHtml(days, now) : weekGridHtml(days, now);
        } else {
            host.innerHTML = view.compact ? compactMonthHtml(days, now) : monthGridHtml(days, now);
        }

        if (data && !inRange(days)) {
            host.insertAdjacentHTML('beforeend', '<p class="acal-empty">Nothing scheduled ' + (view.view === 'week' ? 'this week' : 'this month') + '.</p>');
        }

        loadImages(host);
    }

    function allUnconfigured(data) {
        const sources = Array.isArray(data.sources) ? data.sources : [];
        return sources.length > 0 && sources.every(function (source) { return source.state === 'unconfigured'; });
    }

    function inRange(days) {
        const start = dayKey(view.range.start);
        const end = dayKey(view.range.end);
        let any = false;
        days.forEach(function (list, day) {
            if (day >= start && day <= end && list.length) {
                any = true;
            }
        });
        return any;
    }

    function noticesHtml(data) {
        const sources = data && Array.isArray(data.sources) ? data.sources : [];
        return sources.filter(function (source) { return source.state === 'error'; }).map(function (source) {
            return '<div class="acal-notice" role="status"><span class="material-icons" aria-hidden="true">cloud_off</span>'
                + '<span>' + escapeHtml(source.message || 'A source could not be loaded.') + ' The rest of the calendar is still shown.</span></div>';
        }).join('');
    }

    function dayClasses(day, now) {
        const key = dayKey(day);
        const todayKey = dayKey(new Date(now));
        const classes = [];
        if (key === todayKey) {
            classes.push('acal-today');
        } else if (key < todayKey) {
            classes.push('acal-past');
        }

        if (view.view === 'month' && day.getMonth() !== view.anchor.getMonth()) {
            classes.push('acal-outside');
        }

        return classes.join(' ');
    }

    function weekdayNames(firstDay) {
        const names = [];
        // 2023-01-01 was a Sunday.
        for (let i = 0; i < 7; i++) {
            names.push(format({ weekday: 'short' }, new Date(2023, 0, 1 + ((firstDay + i) % 7))));
        }

        return names;
    }

    function longDay(day) {
        return format({ weekday: 'long', month: 'long', day: 'numeric' }, day);
    }

    function summaryOf(event, status, now) {
        const parts = [event.title];
        const code = codeOf(event);
        if (code) {
            parts.push(code);
        }

        if (event.episodeTitle) {
            parts.push(event.episodeTitle);
        }

        const start = eventStart(event);
        parts.push(start ? formatTime(start) : (event.kind === 'movie' ? releaseLabel(event.release) : ''));
        parts.push(event.kind === 'movie' ? 'Movie' : 'TV');
        parts.push(statusLabel(event, status, now, neutralWording));
        return parts.filter(Boolean).join(', ');
    }

    function progressStyle(event) {
        const progress = event.download && typeof event.download.progress === 'number' ? event.download.progress : null;
        return progress === null ? '' : ' style="--acal-progress:' + Math.max(0, Math.min(100, progress)) + '%"';
    }

    function pillHtml(event, now) {
        const status = statusOf(event, now);
        const meta = event.kind === 'movie' ? releaseLabel(event.release) : codeOf(event);
        const progress = status === 'downloading' && event.download && typeof event.download.progress === 'number'
            ? '<span class="acal-pill-pct">' + event.download.progress + '%</span>'
            : '';
        const summary = summaryOf(event, status, now);
        return '<button type="button" class="acal-pill" data-key="' + escapeHtml(event.key) + '" data-kind="' + escapeHtml(event.kind) + '" data-status="' + status + '"' + progressStyle(event)
            + ' title="' + escapeHtml(summary) + '" aria-label="' + escapeHtml(summary) + '">'
            + '<span class="material-icons acal-glyph" aria-hidden="true">' + (event.kind === 'movie' ? 'movie' : 'tv') + '</span>'
            + '<span class="acal-pill-text"><span class="acal-pill-title">' + escapeHtml(event.title) + '</span>'
            + (meta ? '<span class="acal-pill-meta">' + escapeHtml(meta) + '</span>' : '') + '</span>'
            + progress
            + '</button>';
    }

    function artAttributes(event, tile) {
        const poster = imagePath(event, 'poster', tile ? 500 : 250);
        const fanart = tile ? imagePath(event, 'fanart', (window.devicePixelRatio || 1) > 1.4 ? 360 : 180) : null;
        const first = fanart || poster;
        if (!first) {
            return '';
        }

        const url = function (art) { return escapeHtml(window.ApiClient.getUrl(art.path, art.query)); };
        return ' data-src="' + url(first) + '"' + (fanart && poster ? ' data-fallback="' + url(poster) + '"' : '');
    }

    // A row (poster beside the text) for lists, or a tile (wide artwork above the text) for the
    // week's narrow columns, where the text needs the full width.
    function cardHtml(event, now, variant) {
        const tile = variant === 'tile';
        const status = statusOf(event, now);
        const start = eventStart(event);
        const movie = event.kind === 'movie';
        const kicker = '<span class="acal-card-kicker-text">' + escapeHtml(movie
            ? releaseLabel(event.release)
            : (start ? formatTime(start) : 'All day') + (event.network ? ' · ' + event.network : '')) + '</span>';
        const sub = movie
            ? [event.year, event.runtime ? event.runtime + ' min' : ''].filter(Boolean).join(' · ')
            : [codeOf(event), event.group ? event.episodes.length + ' episodes' : event.episodeTitle].filter(Boolean).join(' · ');
        const summary = summaryOf(event, status, now);
        const meter = !tile && status === 'downloading' && event.download && typeof event.download.progress === 'number'
            ? '<span class="acal-meter" aria-hidden="true"><span style="width:' + Math.max(0, Math.min(100, event.download.progress)) + '%"></span></span>'
            : '';
        return '<button type="button" class="acal-card' + (tile ? ' acal-tile' : '') + '" data-key="' + escapeHtml(event.key) + '" data-kind="' + escapeHtml(event.kind) + '" data-status="' + status + '"'
            + (tile ? progressStyle(event) : '') + ' aria-label="' + escapeHtml(summary) + '">'
            + '<span class="' + (tile ? 'acal-tile-art' : 'acal-card-art') + '"' + artAttributes(event, tile) + '>'
            + '<span class="material-icons" aria-hidden="true">' + (movie ? 'movie' : 'tv') + '</span></span>'
            + '<span class="acal-card-body">'
            + '<span class="acal-card-kicker"><span class="material-icons acal-glyph" aria-hidden="true">' + (movie ? 'movie' : 'tv') + '</span>' + kicker + '</span>'
            + '<span class="acal-card-title">' + escapeHtml(event.title) + '</span>'
            + (sub ? '<span class="acal-card-sub">' + escapeHtml(sub) + '</span>' : '')
            + '<span class="acal-card-status"><i class="acal-dot" aria-hidden="true"></i>' + escapeHtml(statusLabel(event, status, now, neutralWording)) + '</span>'
            + meter
            + '</span>'
            + '</button>';
    }

    function monthGridHtml(days, now) {
        const weeks = monthWeeks(view.anchor, view.firstDay);
        return '<div class="acal-month">'
            + '<div class="acal-dows" aria-hidden="true">' + weekdayNames(view.firstDay).map(function (name) { return '<div class="acal-dow">' + escapeHtml(name) + '</div>'; }).join('') + '</div>'
            + '<div class="acal-grid">'
            + weeks.map(function (week) {
                return week.map(function (day) {
                    const key = dayKey(day);
                    const list = days.get(key) || [];
                    const shown = list.length > MAX_PILLS ? list.slice(0, MAX_PILLS - 1) : list;
                    return '<div class="acal-day ' + dayClasses(day, now) + '" data-day="' + key + '">'
                        + '<div class="acal-day-head"><span class="acal-day-num" aria-label="' + escapeHtml(longDay(day)) + '">' + day.getDate() + '</span></div>'
                        + '<div class="acal-day-list">' + shown.map(function (event) { return pillHtml(event, now); }).join('')
                        + (list.length > shown.length ? '<button type="button" class="acal-more" data-action="day" data-day="' + key + '">+' + (list.length - shown.length) + ' more</button>' : '')
                        + '</div>'
                        + '</div>';
                }).join('');
            }).join('')
            + '</div>'
            + '</div>';
    }

    function weekGridHtml(days, now) {
        return '<div class="acal-week">'
            + weekDays(view.anchor, view.firstDay).map(function (day) {
                const key = dayKey(day);
                const list = days.get(key) || [];
                return '<section class="acal-col ' + dayClasses(day, now) + '" data-day="' + key + '" aria-label="' + escapeHtml(longDay(day)) + '">'
                    + '<header class="acal-col-head"><span class="acal-col-dow">' + escapeHtml(format({ weekday: 'short' }, day)) + '</span><span class="acal-col-num">' + day.getDate() + '</span></header>'
                    + '<div class="acal-col-list">'
                    + (list.length ? list.map(function (event) { return cardHtml(event, now, 'tile'); }).join('') : '<div class="acal-col-empty">Nothing</div>')
                    + '</div>'
                    + '</section>';
            }).join('')
            + '</div>';
    }

    function agendaDayHtml(day, list, now, showEmpty) {
        const todayKey = dayKey(new Date(now));
        const key = dayKey(day);
        return '<section class="acal-agenda-day' + (key === todayKey ? ' acal-today' : '') + '" data-day="' + key + '">'
            + '<h2>' + escapeHtml(longDay(day)) + (key === todayKey ? ' <small>Today</small>' : '') + '</h2>'
            + (list.length
                ? '<div class="acal-agenda-list">' + list.map(function (event) { return cardHtml(event, now); }).join('') + '</div>'
                : (showEmpty ? '<div class="acal-col-empty">Nothing scheduled</div>' : ''))
            + '</section>';
    }

    function weekAgendaHtml(days, now) {
        return '<div class="acal-agenda">'
            + weekDays(view.anchor, view.firstDay).map(function (day) {
                return agendaDayHtml(day, days.get(dayKey(day)) || [], now, true);
            }).join('')
            + '</div>';
    }

    // Phones get the month as a compact grid of days with a dot per entry, coloured by type, and
    // the selected day's entries listed underneath.
    function compactMonthHtml(days, now) {
        const weeks = monthWeeks(view.anchor, view.firstDay);
        const todayKey = dayKey(new Date(now));
        if (!view.selected || view.selected.slice(0, 7) !== dayKey(view.anchor).slice(0, 7)) {
            view.selected = todayKey.slice(0, 7) === dayKey(view.anchor).slice(0, 7) ? todayKey : dayKey(new Date(view.anchor.getFullYear(), view.anchor.getMonth(), 1));
        }

        const selectedDay = parseDayKey(view.selected);
        return '<div class="acal-month acal-compact">'
            + '<div class="acal-dows" aria-hidden="true">' + weekdayNames(view.firstDay).map(function (name) { return '<div class="acal-dow">' + escapeHtml(name.slice(0, 2)) + '</div>'; }).join('') + '</div>'
            + '<div class="acal-grid">'
            + weeks.map(function (week) {
                return week.map(function (day) {
                    const key = dayKey(day);
                    const list = days.get(key) || [];
                    return '<button type="button" class="acal-mday ' + dayClasses(day, now) + (key === view.selected ? ' acal-selected' : '') + '" data-action="select" data-day="' + key + '"'
                        + ' aria-pressed="' + (key === view.selected) + '" aria-label="' + escapeHtml(longDay(day) + (list.length ? ', ' + list.length + (list.length === 1 ? ' entry' : ' entries') : '')) + '">'
                        + '<span class="acal-day-num">' + day.getDate() + '</span>'
                        + '<span class="acal-dots" aria-hidden="true">' + list.slice(0, 4).map(function (event) { return '<i data-kind="' + escapeHtml(event.kind) + '"></i>'; }).join('') + '</span>'
                        + '</button>';
                }).join('');
            }).join('')
            + '</div>'
            + '</div>'
            + '<div class="acal-agenda">' + agendaDayHtml(selectedDay, days.get(view.selected) || [], now, true) + '</div>';
    }

    let lazyObserver = null;
    function loadImages(root) {
        const pending = root.querySelectorAll('[data-src]');
        if (!('IntersectionObserver' in window)) {
            pending.forEach(showImage);
            return;
        }

        if (!lazyObserver) {
            lazyObserver = new IntersectionObserver(function (entries) {
                entries.forEach(function (entry) {
                    if (entry.isIntersecting) {
                        lazyObserver.unobserve(entry.target);
                        showImage(entry.target);
                    }
                });
            }, { rootMargin: '300px' });
        }

        pending.forEach(function (element) { lazyObserver.observe(element); });
    }

    // Shown only once the image has arrived, so a missing picture keeps its placeholder icon. A
    // tile without fanart falls back to the poster.
    function showImage(element) {
        const src = element.getAttribute('data-src');
        element.removeAttribute('data-src');
        if (!src) {
            return;
        }

        const image = new Image();
        image.onload = function () {
            element.style.backgroundImage = 'url("' + src.replace(/["\\\n\r]/g, '') + '")';
            element.classList.add('acal-loaded');
        };
        image.onerror = function () {
            const fallback = element.getAttribute('data-fallback');
            element.removeAttribute('data-fallback');
            if (fallback) {
                element.setAttribute('data-src', fallback);
                showImage(element);
            }
        };
        image.src = src;
    }

    let toastTimer = 0;
    function toast(text) {
        let node = document.querySelector('.acal-toast');
        if (!node) {
            node = document.createElement('div');
            node.className = 'acal-toast';
            node.setAttribute('role', 'status');
            node.setAttribute('aria-live', 'polite');
            document.body.appendChild(node);
        }

        node.textContent = text;
        node.hidden = false;
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { node.hidden = true; }, 4500);
    }

    // ---------------------------------------------------------------------------------
    // Dialogs.
    // ---------------------------------------------------------------------------------

    function closeLayers() {
        document.querySelectorAll('.acal-layer').forEach(function (layer) {
            if (typeof layer.__close === 'function') {
                layer.__close();
            } else {
                layer.remove();
            }
        });
    }

    function openLayer(html, className, label) {
        closeLayers();
        const previousFocus = document.activeElement;
        const layer = document.createElement('div');
        layer.className = 'acal-layer';
        layer.innerHTML = '<div class="acal-dialog ' + (className || '') + '" role="dialog" aria-modal="true" tabindex="-1" aria-label="' + escapeHtml(label) + '">' + html + '</div>';
        const dialog = layer.firstChild;

        function close() {
            document.removeEventListener('keydown', onKey, true);
            layer.remove();
            if (previousFocus && typeof previousFocus.focus === 'function' && document.contains(previousFocus)) {
                previousFocus.focus();
            }
        }

        function onKey(event) {
            if (event.key === 'Escape') {
                event.stopPropagation();
                close();
            }
        }

        layer.__close = close;
        layer.addEventListener('click', function (event) {
            const closer = event.target === layer ? layer : event.target.closest('[data-action="close"]');
            if (!closer) {
                return;
            }

            // A link removed from the page during its own click does not navigate, so a link
            // that also closes the dialog closes it once the click is done.
            if (closer.tagName === 'A') {
                setTimeout(close, 0);
            } else {
                close();
            }
        });
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(layer);
        dialog.focus();
        return dialog;
    }

    function findEvent(key) {
        const events = view && view.data && Array.isArray(view.data.events) ? view.data.events : [];
        let found = null;
        bucketByDay(events).forEach(function (list) {
            list.forEach(function (event) {
                if (event.key === key) {
                    found = event;
                }
            });
        });
        return found;
    }

    function whenText(event) {
        const start = eventStart(event);
        if (start) {
            const end = eventEnd(event);
            return longDay(start) + ' · ' + formatTime(start) + (end ? ' – ' + formatTime(end) : '');
        }

        const day = parseDayKey(event.kind === 'movie' ? event.date : event.airDate);
        return day ? longDay(day) : '';
    }

    function seerrPresent() {
        return !!(window.SeerrIntegration || document.querySelector('[' + SEERR_NAV_ATTR + ']'));
    }

    function detailHtml(event, now) {
        const status = statusOf(event, now);
        const movie = event.kind === 'movie';
        const fanart = imagePath(event, 'fanart', 360);
        const poster = imagePath(event, 'poster', 500);
        const kicker = movie
            ? ['Movie', event.year, event.certification].filter(Boolean)
            : ['TV', event.network, event.certification].filter(Boolean);
        const chips = ['<span class="acal-chip" data-status="' + status + '"><i class="acal-dot" aria-hidden="true"></i>' + escapeHtml(statusLabel(event, status, now, neutralWording)) + '</span>'];
        if (!movie && event.episode === 1 && typeof event.season === 'number' && event.season > 0) {
            chips.push('<span class="acal-chip">' + (event.season === 1 ? 'Series premiere' : 'Season premiere') + '</span>');
        }

        if (event.finale) {
            chips.push('<span class="acal-chip">' + escapeHtml(event.finale === 'series' ? 'Series finale' : event.finale === 'midseason' ? 'Midseason finale' : 'Season finale') + '</span>');
        }

        if (movie && event.release) {
            chips.push('<span class="acal-chip"><span class="material-icons" aria-hidden="true" style="font-size:1.2em">' + releaseIcon(event.release, neutralWording) + '</span>' + escapeHtml(RELEASE_CHIPS[event.release] || 'Release') + '</span>');
        }

        let progress = '';
        if (status === 'downloading' && event.download) {
            const eta = event.download.eta ? new Date(event.download.eta) : null;
            const left = eta && !isNaN(eta.getTime()) ? formatTimeLeft(eta, now) : '';
            const percent = typeof event.download.progress === 'number' ? event.download.progress : 0;
            progress = '<div class="acal-progress"><div class="acal-progress-label">' + escapeHtml([downloadLabel(event.download, neutralWording), left].filter(Boolean).join(' · ')) + '</div>'
                + '<div class="acal-meter" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="' + percent + '"><span style="width:' + percent + '%"></span></div></div>';
        }

        let sub = '';
        if (!movie) {
            sub = [codeOf(event), event.group ? null : event.episodeTitle].filter(Boolean).join(' · ');
            if (event.absolute) {
                sub += ' (' + event.absolute + ')';
            }
        } else if (event.genres && event.genres.length) {
            sub = event.genres.join(', ');
        }

        let extra = '';
        if (movie && event.releases) {
            extra = '<dl class="acal-facts">' + ['cinema', 'digital', 'physical'].filter(function (release) { return isDayKey(event.releases[release]); }).map(function (release) {
                const day = parseDayKey(event.releases[release]);
                return '<dt>' + escapeHtml(releaseLabel(release)) + '</dt><dd class="' + (release === event.release ? 'acal-fact-now' : '') + '">' + escapeHtml(format({ year: 'numeric', month: 'long', day: 'numeric' }, day)) + '</dd>';
            }).join('') + (event.studio ? '<dt>Studio</dt><dd>' + escapeHtml(event.studio) + '</dd>' : '') + '</dl>';
        } else if (event.group) {
            extra = '<ul class="acal-episodes">' + event.episodes.map(function (episode) {
                const episodeStatus = statusOf(episode, now);
                return '<li data-status="' + episodeStatus + '"><i class="acal-dot" title="' + escapeHtml(statusLabel(episode, episodeStatus, now, neutralWording)) + '"></i>'
                    + '<span class="acal-ep-code">' + escapeHtml(episodeCode(episode.season, episode.episode)) + '</span>'
                    + '<span class="acal-ep-title">' + escapeHtml(episode.episodeTitle || 'TBA') + '</span></li>';
            }).join('') + '</ul>';
        }

        const overview = event.overview && !event.group
            ? '<p class="acal-overview">' + (event.overviewIsSeries ? '<span class="acal-overview-label">About the series</span>' : '') + escapeHtml(event.overview) + '</p>'
            : '';

        return '<div class="acal-dialog-hero"' + (fanart ? ' data-src="' + escapeHtml(window.ApiClient.getUrl(fanart.path, fanart.query)) + '"' : '') + '>'
            + '<button type="button" class="acal-icon-btn acal-dialog-close" data-action="close" aria-label="Close"><span class="material-icons" aria-hidden="true">close</span></button>'
            + '</div>'
            + '<div class="acal-dialog-main" data-kind="' + escapeHtml(event.kind) + '">'
            + '<div class="acal-dialog-poster"' + (poster ? ' data-src="' + escapeHtml(window.ApiClient.getUrl(poster.path, poster.query)) + '"' : '') + '><span class="material-icons" aria-hidden="true">' + (movie ? 'movie' : 'tv') + '</span></div>'
            + '<div class="acal-dialog-info">'
            + '<div class="acal-kicker"><span class="material-icons acal-glyph" aria-hidden="true">' + (movie ? 'movie' : 'tv') + '</span>' + escapeHtml(kicker.join(' · ')) + '</div>'
            + '<h2>' + escapeHtml(event.title) + '</h2>'
            + (sub ? '<div class="acal-dialog-sub">' + escapeHtml(sub) + '</div>' : '')
            + '<div class="acal-dialog-when">' + escapeHtml(whenText(event)) + '</div>'
            + '<div class="acal-chips">' + chips.join('') + '</div>'
            + progress
            + overview
            + extra
            + '<div class="acal-dialog-actions" data-slot="actions"></div>'
            + '</div>'
            + '</div>';
    }

    function openDetail(event) {
        const dialog = openLayer(detailHtml(event, Date.now()), '', event.title);
        loadImages(dialog);
        const actions = dialog.querySelector('[data-slot="actions"]');
        const route = seerrPresent() ? seerrRoute(event) : '';
        if (route) {
            actions.insertAdjacentHTML('beforeend', '<a class="acal-btn" href="' + escapeHtml(route) + '" data-action="close"><span class="material-icons" aria-hidden="true">add_to_queue</span>View in Requests</a>');
        }

        const query = event.kind === 'movie'
            ? { kind: 'movie', tmdbId: event.tmdbId, imdbId: event.imdbId }
            : { kind: 'episode', tvdbId: event.tvdbId, tmdbId: event.tmdbId, imdbId: event.imdbId, season: event.group ? undefined : event.season, episode: event.group ? undefined : event.episode };
        if (!query.tvdbId && !query.tmdbId && !query.imdbId) {
            return;
        }

        api.get('Library', query).then(function (match) {
            if (!match || !document.contains(actions) || typeof match.itemId !== 'string' || !/^[0-9a-fA-F]{32}$/.test(match.itemId)) {
                return;
            }

            const label = match.kind === 'episode' ? 'Go to episode' : match.kind === 'series' ? 'Go to series' : 'Go to movie';
            actions.insertAdjacentHTML('afterbegin', '<button type="button" class="acal-btn acal-btn-primary" data-action="open-item" data-item-id="' + match.itemId + '">'
                + '<span class="material-icons" aria-hidden="true">' + (match.kind === 'series' ? 'tv' : 'play_arrow') + '</span>' + label + '</button>');
        }).catch(function () {
            // A link into the library is a nicety; the details stand without it.
        });
    }

    function openDay(key) {
        const day = parseDayKey(key);
        const list = day && view && view.data ? (bucketByDay(view.data.events || []).get(key) || []) : [];
        if (!day || !list.length) {
            return;
        }

        const now = Date.now();
        const dialog = openLayer('<button type="button" class="acal-icon-btn acal-dialog-close" data-action="close" aria-label="Close"><span class="material-icons" aria-hidden="true">close</span></button>'
            + '<h2>' + escapeHtml(longDay(day)) + '</h2>'
            + '<div class="acal-agenda-list">' + list.map(function (event) { return cardHtml(event, now); }).join('') + '</div>', 'acal-day-dialog', longDay(day));
        loadImages(dialog);
        dialog.addEventListener('click', function (event) {
            const card = event.target.closest('.acal-card');
            const found = card && findEvent(card.getAttribute('data-key'));
            if (found) {
                openDetail(found);
            }
        });
    }

    // ---------------------------------------------------------------------------------
    // Interaction.
    // ---------------------------------------------------------------------------------

    function move(direction) {
        if (!currentRoute || !view) {
            return;
        }

        navigate({ view: currentRoute.view, date: dayKey(direction === 0 ? today() : shiftAnchor(view.view, view.anchor, direction)) });
    }

    function bindPage(page) {
        page.addEventListener('click', function (event) {
            const target = event.target.closest('[data-action], [data-view], .acal-pill, .acal-card');
            if (!target || !page.contains(target)) {
                return;
            }

            if (target.hasAttribute('data-view')) {
                const next = target.getAttribute('data-view');
                if (currentRoute && view && next !== view.view) {
                    navigate({ view: next, date: dayKey(view.anchor) });
                }

                return;
            }

            if (target.classList.contains('acal-pill') || target.classList.contains('acal-card')) {
                const found = findEvent(target.getAttribute('data-key'));
                if (found) {
                    openDetail(found);
                }

                return;
            }

            switch (target.getAttribute('data-action')) {
                case 'prev':
                    move(-1);
                    break;
                case 'next':
                    move(1);
                    break;
                case 'today':
                    move(0);
                    break;
                case 'retry':
                    if (currentRoute) {
                        render(page, currentRoute);
                    }

                    break;
                case 'day':
                    openDay(target.getAttribute('data-day'));
                    break;
                case 'select':
                    if (view) {
                        view.selected = target.getAttribute('data-day');
                        drawView(page);
                    }

                    break;
                default:
                    break;
            }
        });

        // Swiping across the month or week on a touch screen pages through it.
        let touchStart = null;
        page.addEventListener('touchstart', function (event) {
            touchStart = event.touches.length === 1 && event.target.closest('.acal-view')
                ? { x: event.touches[0].clientX, y: event.touches[0].clientY }
                : null;
        }, { passive: true });
        page.addEventListener('touchend', function (event) {
            if (!touchStart || !event.changedTouches.length) {
                return;
            }

            const dx = event.changedTouches[0].clientX - touchStart.x;
            const dy = event.changedTouches[0].clientY - touchStart.y;
            touchStart = null;
            if (Math.abs(dx) > 70 && Math.abs(dy) < 45) {
                move(dx < 0 ? 1 : -1);
            }
        }, { passive: true });
    }

    function onKeyDown(event) {
        if (!currentRoute || event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || document.querySelector('.acal-layer')) {
            return;
        }

        // Only while nothing else in Jellyfin has the focus: the TV layout moves focus with the
        // arrow keys, and an input needs them for its own text.
        const target = event.target;
        const page = document.getElementById(PAGE_ID);
        if (!target || (target !== document.body && !(page && page.contains(target)))
            || target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) {
            return;
        }

        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
            event.preventDefault();
            move(event.key === 'ArrowLeft' ? -1 : 1);
        }
    }

    function onItemClick(event) {
        const button = event.target.closest('[data-action="open-item"]');
        if (!button) {
            return;
        }

        const itemId = button.getAttribute('data-item-id');
        closeLayers();
        if (window.Emby && window.Emby.Page && typeof window.Emby.Page.showItem === 'function') {
            window.Emby.Page.showItem(itemId);
        } else {
            window.location.hash = '#/details?id=' + encodeURIComponent(itemId);
        }
    }

    // While the page is open: redraw every minute so on air and missing follow the clock, and
    // reload when something is downloading, so its progress moves.
    function startRefresh() {
        stopRefresh();
        refreshTimer = setInterval(function () {
            const page = document.getElementById(PAGE_ID);
            if (!page || !view || document.hidden || page.classList.contains('acal-blocked')) {
                return;
            }

            const events = view.data && Array.isArray(view.data.events) ? view.data.events : [];
            if (events.some(function (event) { return event.download; })) {
                load(page, renderToken, true);
            } else {
                drawView(page);
            }
        }, REFRESH_MS);
    }

    function stopRefresh() {
        clearInterval(refreshTimer);
        refreshTimer = 0;
    }

    // ---------------------------------------------------------------------------------
    // Start.
    // ---------------------------------------------------------------------------------

    function start() {
        injectStyles();
        window.addEventListener('hashchange', onLocationChange);
        window.addEventListener('popstate', onLocationChange);
        document.addEventListener('keydown', onKeyDown);
        document.addEventListener('click', onItemClick);
        window.addEventListener('scroll', function () { closeFlyout(true); }, { passive: true });
        window.addEventListener('resize', function () {
            closeFlyout(true);
            const page = document.getElementById(PAGE_ID);
            if (!page || !view || !currentRoute) {
                return;
            }

            if (!currentRoute.view && defaultView(isPhone()) !== view.view) {
                render(page, currentRoute);
            } else if (view.compact !== isCompact()) {
                drawView(page);
            }
        });
        if (window.matchMedia) {
            const hoverQuery = window.matchMedia(HOVER_QUERY);
            const onChange = function () { queueNav(); };
            if (typeof hoverQuery.addEventListener === 'function') {
                hoverQuery.addEventListener('change', onChange);
            } else if (typeof hoverQuery.addListener === 'function') {
                hoverQuery.addListener(onChange);
            }
        }

        new MutationObserver(queueNav).observe(document.body, { childList: true, subtree: true });
        // Another page shown by Jellyfin always wins over this one.
        document.addEventListener('viewshow', function (event) {
            if (currentRoute && event.target && event.target.id !== PAGE_ID && !parseRoute(window.location.hash)) {
                hidePage();
            }
        });
        // Jellyfin Web's not-found page announces itself when it mounts, which can land after this
        // page did. While the route is still this page's, announce again so the header keeps this
        // page's title.
        document.addEventListener('pageshow', function (event) {
            if (currentRoute && event.target && event.target.id !== PAGE_ID && parseRoute(window.location.hash)) {
                setTimeout(function () {
                    const page = document.getElementById(PAGE_ID);
                    if (currentRoute && page && !page.classList.contains('hide')) {
                        announce(page);
                    }
                }, 0);
            }
        });
        // Seerr Integration's entry may only arrive at the end of the grace period.
        setTimeout(queueNav, SEERR_GRACE_MS + 50);
        queueNav();
        onLocationChange();
    }

    if (document.body) {
        start();
    } else {
        document.addEventListener('DOMContentLoaded', start);
    }
})();
