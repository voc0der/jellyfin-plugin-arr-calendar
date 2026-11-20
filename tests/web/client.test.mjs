// Unit tests for the Calendar page's model and API client. No dependencies: `node --test`.
//
// The page is loaded the way the browser loads it, as one script, into a sandbox that only has
// what the model needs. Values crossing back out of the sandbox are copied into this realm with
// `plain`, since deepStrictEqual compares prototypes.
//
// The process runs in New York time, so that "the viewer's own day" means something other than
// UTC: an episode airing at 03:30 UTC belongs on the previous evening there.

process.env.TZ = 'America/New_York';

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const SOURCE = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../Web/arr-calendar.js'), 'utf8');

function load() {
    const window = { __arrCalendarNoInit: true };
    const document = { currentScript: { src: 'http://jf/Plugins/ArrCalendar/Client/arr-calendar.js?v=1.0.0.9' } };
    vm.runInNewContext(SOURCE, { window, document, URLSearchParams, Intl, console });
    return window.ArrCalendar;
}

const { version, model } = load();
const plain = value => JSON.parse(JSON.stringify(value));
const keys = dates => plain(dates.map(date => model.dayKey(date)));
const at = iso => Date.parse(iso);

const episode = overrides => Object.assign({
    key: 'sonarr:episode:1',
    kind: 'episode',
    title: 'Orbit Street',
    seriesId: 1,
    season: 2,
    episode: 3,
    airDateUtc: '2026-11-20T02:00:00Z',
    airDate: '2026-11-19',
    runtime: 45,
    hasFile: false,
    monitored: true
}, overrides);

const movie = overrides => Object.assign({
    key: 'radarr:movie:11:digital',
    kind: 'movie',
    title: 'Starfall',
    release: 'digital',
    date: '2026-11-20',
    hasFile: false,
    monitored: true,
    available: false
}, overrides);

test('the process really runs in New York time', () => {
    assert.equal(new Date('2026-11-01T03:30:00Z').getHours(), 23);
});

test('reads its own version from the script URL', () => {
    assert.equal(version, '1.0.0.9');
});

test('days are local calendar days, and only real ones parse', () => {
    assert.equal(model.dayKey(new Date(2026, 0, 5)), '2026-01-05');
    assert.equal(model.dayKey(model.parseDayKey('2026-11-19')), '2026-11-19');
    for (const bad of ['2026-02-30', '2026-13-01', '2026-1-5', '20261105', '', null, undefined, 'tomorrow']) {
        assert.equal(model.parseDayKey(bad), null, String(bad));
    }
});

test('day arithmetic keeps local midnight across a DST change', () => {
    // New York leaves DST on 2026-11-01.
    const before = model.parseDayKey('2026-10-31');
    const after = model.addDays(before, 2);
    assert.equal(model.dayKey(after), '2026-11-02');
    assert.equal(after.getHours(), 0);
});

test('adding months clamps to the end of shorter months', () => {
    assert.equal(model.dayKey(model.addMonths(model.parseDayKey('2026-01-31'), 1)), '2026-02-28');
    assert.equal(model.dayKey(model.addMonths(model.parseDayKey('2028-01-31'), 1)), '2028-02-29');
    assert.equal(model.dayKey(model.addMonths(model.parseDayKey('2026-03-31'), -1)), '2026-02-28');
    assert.equal(model.dayKey(model.addMonths(model.parseDayKey('2026-12-15'), 1)), '2027-01-15');
});

test('weeks start on the locale\'s first day', () => {
    const thursday = model.parseDayKey('2026-11-19');
    assert.equal(model.dayKey(model.startOfWeek(thursday, 0)), '2026-11-15');
    assert.equal(model.dayKey(model.startOfWeek(thursday, 1)), '2026-11-16');
    assert.equal(model.dayKey(model.startOfWeek(thursday, 6)), '2026-11-14');
    assert.equal(model.dayKey(model.startOfWeek(model.parseDayKey('2026-11-15'), 0)), '2026-11-15');
});

test('a month view covers whole weeks from the 1st to the last day', () => {
    const november = model.monthWeeks(model.parseDayKey('2026-11-19'), 0);
    assert.equal(november.length, 5);
    assert.equal(model.dayKey(november[0][0]), '2026-11-01');
    assert.equal(model.dayKey(november[4][6]), '2026-12-05');
    assert.ok(november.every(week => week.length === 7));

    // February 2026 starts on a Sunday and has exactly four weeks from Sunday.
    assert.equal(model.monthWeeks(model.parseDayKey('2026-02-10'), 0).length, 4);
    // A Monday-first August 2026 needs six rows.
    assert.equal(model.monthWeeks(model.parseDayKey('2026-08-10'), 1).length, 6);
});

test('the visible range follows the view', () => {
    const anchor = model.parseDayKey('2026-11-19');
    assert.deepEqual(keys(Object.values(model.visibleRange('month', anchor, 1))), ['2026-10-26', '2026-12-06']);
    assert.deepEqual(keys(Object.values(model.visibleRange('week', anchor, 1))), ['2026-11-16', '2026-11-22']);
    assert.deepEqual(keys(model.weekDays(anchor, 0)).slice(0, 2), ['2026-11-15', '2026-11-16']);
    assert.equal(model.dayKey(model.shiftAnchor('week', anchor, -1)), '2026-11-12');
    assert.equal(model.dayKey(model.shiftAnchor('month', anchor, 1)), '2026-12-19');
});

test('first day of the week comes from the locale', () => {
    assert.equal(model.firstDayOfWeek('en-US'), 0);
    assert.equal(model.firstDayOfWeek('en-gb'), 1);
    assert.equal(model.firstDayOfWeek('de-DE'), 1);
    assert.equal(model.firstDayOfWeek('ar-EG'), 6);
    assert.equal(model.firstDayOfWeek('not a locale!'), 1);
});

test('routes: only #/calendar belongs to the page, month is the default', () => {
    assert.equal(model.parseRoute('#/home'), null);
    assert.equal(model.parseRoute('#/calendarx'), null);
    assert.equal(model.parseRoute(''), null);
    assert.equal(model.parseRoute(undefined), null);
    assert.deepEqual(plain(model.parseRoute('#/calendar')), { view: 'month', date: null });
    assert.deepEqual(plain(model.parseRoute('#/Calendar/')), { view: 'month', date: null });
    assert.deepEqual(plain(model.parseRoute('#/calendar?view=week')), { view: 'week', date: null });
    assert.deepEqual(plain(model.parseRoute('#/calendar?view=day&date=2026-11-19')), { view: 'month', date: '2026-11-19' });
    assert.deepEqual(plain(model.parseRoute('#/calendar?date=2026-02-30')), { view: 'month', date: null });
    for (const route of [{ view: 'month', date: null }, { view: 'week', date: '2026-11-16' }, { view: 'month', date: '2027-01-01' }]) {
        assert.deepEqual(plain(model.parseRoute(model.routeHash(route))), route);
    }

    assert.equal(model.routeHash({ view: 'month', date: null }), '#/calendar');
});

test('episodes belong on the viewer\'s own day; release dates never move', () => {
    // SNL: 23:30 on the 31st in New York is 03:30 UTC on the 1st.
    assert.equal(model.eventDay(episode({ airDateUtc: '2026-11-01T03:30:00Z', airDate: '2026-10-31' })), '2026-10-31');
    assert.equal(model.eventDay(episode({ airDateUtc: '2026-11-20T15:00:00Z' })), '2026-11-20');
    assert.equal(model.eventDay(episode({ airDateUtc: null, airDate: '2026-11-21' })), '2026-11-21');
    assert.equal(model.eventDay(movie({ date: '2026-11-18' })), '2026-11-18');
    assert.equal(model.eventDay(movie({ date: 'soon' })), null);
    assert.equal(model.eventDay(episode({ airDateUtc: 'garbage', airDate: null })), null);
});

test('an episode ends after its runtime, or half an hour without one', () => {
    assert.equal(model.eventEnd(episode({ runtime: 45 })).toISOString(), '2026-11-20T02:45:00.000Z');
    assert.equal(model.eventEnd(episode({ runtime: 0 })).toISOString(), '2026-11-20T02:30:00.000Z');
    assert.equal(model.eventStart(movie()), null);
});

test('episode statuses follow the clock, as in Sonarr', () => {
    const show = episode({});
    assert.equal(model.statusOf(show, at('2026-11-20T01:00:00Z')), 'upcoming');
    assert.equal(model.statusOf(show, at('2026-11-20T02:10:00Z')), 'airing');
    assert.equal(model.statusOf(show, at('2026-11-20T03:00:00Z')), 'missing');
    assert.equal(model.statusOf(episode({ hasFile: true }), at('2026-11-20T03:00:00Z')), 'downloaded');
    assert.equal(model.statusOf(episode({ download: { progress: 5, state: 'downloading' } }), at('2026-11-20T03:00:00Z')), 'downloading');
    assert.equal(model.statusOf(episode({ monitored: false }), at('2026-11-20T03:00:00Z')), 'unmonitored');
    // A file wins over a download in progress, as an upgrade still leaves something to watch.
    assert.equal(model.statusOf(episode({ hasFile: true, download: { progress: 5 } }), 0), 'downloaded');

    const untimed = episode({ airDateUtc: null, airDate: '2026-11-19' });
    assert.equal(model.statusOf(untimed, model.parseDayKey('2026-11-19').getTime() + 3600e3), 'upcoming');
    assert.equal(model.statusOf(untimed, model.parseDayKey('2026-11-20').getTime()), 'missing');
});

test('movie statuses follow Radarr\'s availability', () => {
    assert.equal(model.statusOf(movie({}), 0), 'upcoming');
    assert.equal(model.statusOf(movie({ available: true }), 0), 'missing');
    assert.equal(model.statusOf(movie({ available: true, hasFile: true }), 0), 'downloaded');
    assert.equal(model.statusOf(movie({ download: { progress: 40, state: 'downloading' } }), 0), 'downloading');

    const past = model.parseDayKey('2026-11-21').getTime();
    assert.equal(model.statusLabel(movie({}), 'upcoming', past), 'Not available yet');
    assert.equal(model.statusLabel(movie({}), 'upcoming', model.parseDayKey('2026-11-19').getTime()), 'Upcoming');
});

test('download labels say what the download client is doing', () => {
    assert.equal(model.downloadLabel({ progress: 62, state: 'downloading' }), 'Downloading 62%');
    assert.equal(model.downloadLabel({ progress: null, state: 'downloading' }), 'Downloading');
    assert.equal(model.downloadLabel({ state: 'queued' }), 'Queued');
    assert.equal(model.downloadLabel({ state: 'paused' }), 'Paused');
    assert.equal(model.downloadLabel({ state: 'importing' }), 'Importing');
    assert.equal(model.downloadLabel({ state: 'warning' }), 'Download needs attention');
    assert.equal(model.downloadLabel(null), '');
    assert.equal(model.statusLabel(episode({ download: { progress: 62, state: 'downloading' } }), 'downloading', 0), 'Downloading 62%');
    assert.equal(model.statusLabel(episode({}), 'airing', 0), 'On air now');
    assert.equal(model.statusLabel(episode({ hasFile: true }), 'downloaded', 0), 'Downloaded');
});

test('"Don\'t specify method" says Ready and Pending, and never how a title arrives', () => {
    assert.equal(model.statusLabel(episode({ hasFile: true }), 'downloaded', 0, true), 'Ready');
    assert.equal(model.statusLabel(episode({ download: { progress: 62, state: 'downloading' } }), 'downloading', 0, true), 'Pending 62%');
    assert.equal(model.downloadLabel({ progress: 62, state: 'downloading' }, true), 'Pending 62%');
    assert.equal(model.downloadLabel({ progress: null, state: 'downloading' }, true), 'Pending');
    // The download client's own steps would give the method away, so they all read as Pending.
    for (const state of ['queued', 'paused', 'importing', 'warning']) {
        assert.equal(model.downloadLabel({ progress: 100, state: state }, true), 'Pending');
    }

    assert.equal(model.downloadLabel(null, true), '');
    // Everything that never named the method reads the same either way.
    assert.equal(model.statusLabel(episode({}), 'airing', 0, true), 'On air now');
    assert.equal(model.statusLabel(episode({}), 'missing', 0, true), 'Missing');
    assert.equal(model.statusLabel(movie({}), 'upcoming', model.parseDayKey('2026-11-19').getTime(), true), 'Upcoming');

    assert.deepEqual(plain(model.legendStatuses(false).map(status => status[1])), ['Downloaded', 'Downloading', 'On air', 'Missing', 'Upcoming']);
    assert.deepEqual(plain(model.legendStatuses(true).map(status => status[1])), ['Ready', 'Pending', 'On air', 'Missing', 'Upcoming']);
    assert.deepEqual(plain(model.legendStatuses(true).map(status => status[0])), plain(model.legendStatuses(false).map(status => status[0])));

    // A digital release's icon loses its download arrow too.
    assert.equal(model.releaseIcon('digital', false), 'cloud_download');
    assert.equal(model.releaseIcon('digital', true), 'cloud');
    assert.equal(model.releaseIcon('cinema', true), 'theaters');
    assert.equal(model.releaseIcon('physical', true), 'album');
    assert.equal(model.releaseIcon('rumoured', true), 'event');

    for (const status of ['downloaded', 'downloading', 'airing', 'missing', 'unmonitored', 'upcoming']) {
        for (const state of ['downloading', 'queued', 'paused', 'importing', 'warning']) {
            const text = model.statusLabel(episode({ download: { progress: 10, state: state } }), status, 0, true);
            assert.doesNotMatch(text, /download/i);
        }
    }
});

test('a season released at once becomes one entry', () => {
    const drop = [3, 1, 2].map(number => episode({ key: 'e' + number, seriesId: 7, episode: number, season: 1, airDateUtc: '2026-11-22T08:00:00Z', hasFile: number !== 2, download: number === 2 ? { progress: 50, state: 'downloading' } : null }));
    const other = episode({ key: 'x', seriesId: 8 });
    const grouped = model.groupEpisodes(drop.concat([other]));

    assert.equal(grouped.length, 2);
    const group = grouped[0];
    assert.equal(group.group, true);
    assert.deepEqual(plain(group.episodes.map(item => item.episode)), [1, 2, 3]);
    assert.equal(group.key, 'group:7|2026-11-22T08:00:00Z');
    assert.equal(model.codeOf(group), 'S01E01–E03');
    assert.equal(group.hasFile, false);
    assert.equal(group.download.progress, 50);
    assert.equal(model.statusOf(group, at('2026-11-23T00:00:00Z')), 'downloading');
    assert.equal(grouped[1].key, 'x');

    const crossSeason = model.groupEpisodes([episode({ key: 'a', season: 1, episode: 9 }), episode({ key: 'b', season: 2, episode: 1 })]);
    assert.equal(model.codeOf(crossSeason[0]), '2 episodes');

    const allDownloaded = model.groupEpisodes([episode({ key: 'a', hasFile: true }), episode({ key: 'b', episode: 4, hasFile: true })]);
    assert.equal(model.statusOf(allDownloaded[0], 0), 'downloaded');
});

test('days list movies first, then episodes in air-time order', () => {
    const days = model.bucketByDay([
        episode({ key: 'late', airDateUtc: '2026-11-20T04:00:00Z', title: 'Late' }),
        episode({ key: 'early', airDateUtc: '2026-11-19T20:00:00Z', title: 'Early' }),
        movie({ key: 'film', date: '2026-11-19' }),
        movie({ key: 'nowhere', date: null })
    ]);

    assert.deepEqual([...days.keys()].sort(), ['2026-11-19']);
    assert.deepEqual(plain(days.get('2026-11-19').map(item => item.key)), ['film', 'early', 'late']);
});

test('episode codes are zero padded', () => {
    assert.equal(model.episodeCode(2, 3), 'S02E03');
    assert.equal(model.episodeCode(52, 104), 'S52E104');
    assert.equal(model.episodeCode(0, 1), 'S00E01');
    assert.equal(model.episodeCode(null, 1), '');
    assert.equal(model.codeOf(movie()), '');
    assert.equal(model.releaseLabel('cinema'), 'In cinemas');
    assert.equal(model.releaseLabel('streaming'), 'Release');
});

test('artwork paths are only built from values the server could have sent', () => {
    const good = { image: 'sonarr/12', imageSig: 'A'.repeat(22) };
    assert.deepEqual(plain(model.imagePath(good, 'poster', 250)), {
        path: 'Plugins/ArrCalendar/Image/sonarr/12/poster',
        query: { size: 250, sig: 'A'.repeat(22) }
    });
    for (const bad of [
        { image: 'sonarr/12', imageSig: 'short' },
        { image: 'lidarr/1', imageSig: 'A'.repeat(22) },
        { image: 'sonarr/../../x', imageSig: 'A'.repeat(22) },
        { image: 'sonarr/0', imageSig: 'A'.repeat(22) },
        { image: 'sonarr/1', imageSig: 'A'.repeat(21) + '"' },
        { image: null, imageSig: null }
    ]) {
        assert.equal(model.imagePath(bad, 'poster', 250), null, JSON.stringify(bad));
    }

    assert.equal(model.imagePath(good, 'banner', 250), null);
});

test('the Requests link uses the TMDB id, when there is one', () => {
    assert.equal(model.seerrRoute(movie({ tmdbId: 969681 })), '#/requests?view=movie&id=969681');
    assert.equal(model.seerrRoute(episode({ tmdbId: 1667 })), '#/requests?view=tv&id=1667');
    assert.equal(model.seerrRoute(episode({ tmdbId: 0 })), '');
    assert.equal(model.seerrRoute(episode({})), '');
});

test('time left reads naturally', () => {
    const now = Date.parse('2026-11-19T12:00:00Z');
    const after = minutes => new Date(now + minutes * 60000);
    assert.equal(model.formatTimeLeft(null, now), '');
    assert.equal(model.formatTimeLeft(after(-5), now), 'almost done');
    assert.equal(model.formatTimeLeft(after(10), now), 'about 10 minutes left');
    assert.equal(model.formatTimeLeft(after(150), now), 'about 3 hours left');
    assert.equal(model.formatTimeLeft(after(60 * 72), now), 'about 3 days left');
});

test('escapeHtml escapes markup and quotes', () => {
    assert.equal(model.escapeHtml('<img src=x onerror="a">\'&'), '&lt;img src=x onerror=&quot;a&quot;&gt;&#39;&amp;');
    assert.equal(model.escapeHtml(null), '');
    assert.equal(model.escapeHtml(5), '5');
});

test('the page sticks to ES2015 syntax for older WebViews', () => {
    assert.doesNotMatch(SOURCE, /\basync\s+(function|\()/);
    assert.doesNotMatch(SOURCE, /\bawait\s/);
    assert.doesNotMatch(SOURCE, /\?\.[\w([]/);
    assert.doesNotMatch(SOURCE, /\?\?/);
});

// --- The API client -----------------------------------------------------------------

function fakeResponse(status, body, headers = {}) {
    const text = body === undefined ? '' : (typeof body === 'string' ? body : JSON.stringify(body));
    return {
        status,
        ok: status >= 200 && status < 300,
        headers: { get: name => headers[name] ?? null },
        text: async () => text
    };
}

function fakeClient(overrides = {}) {
    return Object.assign({
        getUrl: (name, params) => {
            const query = params ? new URLSearchParams(Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== '')).toString() : '';
            return 'http://jf/' + name + (query ? '?' + query : '');
        },
        setRequestHeaders: headers => { headers.Authorization = 'MediaBrowser Client="Jellyfin Web", Token="user-token"'; },
        accessToken: () => 'user-token'
    }, overrides);
}

function apiWith(responder, client = fakeClient()) {
    const calls = [];
    const api = model.createApi(async (url, init) => {
        calls.push({ url, init });
        return responder(url, init);
    }, () => client);
    return { api, calls };
}

test('the client asks this plugin only, with the user\'s own Jellyfin authorization', async () => {
    const { api, calls } = apiWith(() => fakeResponse(200, { events: [] }));

    const data = await api.get('Calendar', { start: '2026-11-01', end: '2026-12-05' });

    assert.deepEqual(plain(data), { events: [] });
    assert.equal(calls[0].url, 'http://jf/Plugins/ArrCalendar/Calendar?start=2026-11-01&end=2026-12-05');
    assert.equal(calls[0].init.method, 'GET');
    assert.equal(calls[0].init.cache, 'no-store');
    assert.equal(calls[0].init.headers.Authorization, 'MediaBrowser Client="Jellyfin Web", Token="user-token"');
    assert.deepEqual(Object.keys(calls[0].init.headers).filter(name => /x-api/i.test(name)), []);
});

test('the client falls back to a token-only header and refuses without a client', async () => {
    const { api, calls } = apiWith(() => fakeResponse(200, {}), fakeClient({ setRequestHeaders: undefined }));
    await api.get('Calendar');
    assert.equal(calls[0].init.headers.Authorization, 'MediaBrowser Token="user-token"');

    const none = model.createApi(async () => fakeResponse(200, {}), () => undefined);
    await assert.rejects(none.get('Calendar'), error => error.status === 401 && error.code === 'MissingJellyfinUser');
});

test('the client treats 204 as nothing and reports errors in either casing', async () => {
    assert.equal(await apiWith(() => fakeResponse(204)).api.get('Library'), null);

    const camel = apiWith(() => fakeResponse(403, { statusCode: 403, error: 'CalendarDisabled', message: 'off' })).api;
    await assert.rejects(camel.get('Calendar'), error => error.status === 403 && error.code === 'CalendarDisabled' && error.message === 'off');

    const pascal = apiWith(() => fakeResponse(400, { Error: 'InvalidRange', Message: 'bad range' })).api;
    await assert.rejects(pascal.get('Calendar'), error => error.code === 'InvalidRange' && error.message === 'bad range');

    const limited = apiWith(() => fakeResponse(429, { error: 'RateLimited' }, { 'Retry-After': '17' })).api;
    await assert.rejects(limited.get('Calendar'), error => error.status === 429 && error.retryAfter === 17);

    const html = apiWith(() => fakeResponse(404, '<html>not found</html>')).api;
    await assert.rejects(html.get('Calendar'), error => error.status === 404 && error.code === '');

    const offline = model.createApi(async () => { throw new TypeError('Failed to fetch'); }, () => fakeClient());
    await assert.rejects(offline.get('Calendar'), error => error.status === 0);
});

test('describeError explains every way the page can be blocked', () => {
    const E = model.ApiError;
    const kind = error => model.describeError(error).kind;
    assert.equal(kind(new E(401, 'MissingJellyfinUser')), 'session');
    assert.equal(kind(new E(403, 'CalendarDisabled')), 'disabled');
    assert.equal(kind(new E(404, 'PluginInactive')), 'inactive');
    assert.equal(kind(new E(404, '')), 'inactive');
    for (const error of [new E(401, 'MissingJellyfinUser'), new E(403, 'CalendarDisabled'), new E(404, '')]) {
        assert.equal(model.describeError(error).blocking, true);
    }

    assert.equal(model.describeError(new E(400, 'InvalidRange', 'bad')).blocking, false);
    assert.match(model.describeError(new E(429, 'RateLimited', '', 30)).text, /30 seconds/);
    assert.equal(model.describeError(new E(0, 'NetworkError')).title, 'Connection problem');
    assert.equal(model.describeError(new Error('boom')).blocking, false);
});
