// A stand-in Sonarr and Radarr for the end-to-end harness, with no dependencies.
//
// It answers the endpoints Arr Calendar uses, in the shapes Sonarr 4 and Radarr 7 answer them
// (recorded from real instances; see tests/Jellyfin.Plugin.ArrCalendar.Tests/Fixtures), with a
// schedule built around today so every state the calendar draws is always on screen: downloaded,
// missing, on air now, upcoming, a season released at once, a finale, and downloads in progress.
//
// Real services would need internet metadata and would never hold still: their schedules move
// with the real world. The contract itself is pinned by the C# tests against recorded answers.
//
// Like the real ones, the answers carry server paths. The harness checks they never reach a
// browser.

import { createServer } from 'node:http';
import { deflateSync } from 'node:zlib';

const SONARR_KEY = process.env.SONARR_KEY ?? 'sonarr-key';
const RADARR_KEY = process.env.RADARR_KEY ?? 'radarr-key';
const DAY = 24 * 60 * 60 * 1000;

const log = [];
const failures = { sonarr: 'none', radarr: 'none' };

function today() {
    const now = new Date();
    return Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
}

const iso = time => new Date(time).toISOString().replace(/\.\d{3}Z$/, 'Z');
const date = time => iso(time).slice(0, 10);
const midnight = time => date(time) + 'T00:00:00Z';

function series(id, title, extra) {
    return Object.assign({
        id,
        title,
        sortTitle: title.toLowerCase(),
        status: 'continuing',
        overview: 'The ' + title + ' series overview.',
        network: 'Northwind',
        airTime: '21:00',
        images: [
            { coverType: 'poster', remoteUrl: 'https://artworks.example/posters/' + id + '.jpg' },
            { coverType: 'fanart', remoteUrl: 'https://artworks.example/fanart/' + id + '.jpg' }
        ],
        seasons: [{ seasonNumber: 1, monitored: true }],
        year: 2024,
        path: '/tv/' + title,
        qualityProfileId: 1,
        seasonFolder: true,
        monitored: true,
        runtime: 45,
        tvdbId: 900000 + id,
        tmdbId: 800000 + id,
        imdbId: 'tt90000' + String(id).padStart(2, '0'),
        seriesType: 'standard',
        certification: 'TV-14',
        genres: ['Drama', 'Mystery'],
        tags: [3]
    }, extra);
}

// The schedule, rebuilt on every request so "today" and "on air now" stay true.
function sonarrEpisodes() {
    const t = today();
    const now = Date.now();
    const orbit = series(1, 'Orbit Street');
    const keepers = series(2, 'The Lighthouse Keepers', { network: 'Streamline', runtime: 50, seriesType: 'standard' });
    const late = series(3, 'Late Night Live', { network: 'NBS', runtime: 60, seriesType: 'daily', tmdbId: 0 });
    const live = series(4, 'Signal Fire', { network: 'Channel 9', runtime: 60 });
    const finale = series(5, 'Cold Harbor', { network: 'Northwind', runtime: 55 });
    let id = 100;
    const episode = (show, season, number, airTime, extra) => Object.assign({
        seriesId: show.id,
        tvdbId: 5000 + (++id),
        episodeFileId: 0,
        seasonNumber: season,
        episodeNumber: number,
        title: show.title + ' episode ' + number,
        airDate: date(airTime),
        airDateUtc: iso(airTime),
        runtime: show.runtime,
        overview: 'What happens in ' + show.title + ' S' + season + 'E' + number + '.',
        hasFile: false,
        monitored: true,
        unverifiedSceneNumbering: false,
        series: show,
        id
    }, extra);

    const episodes = [
        episode(orbit, 2, 1, t - 6 * DAY + 2 * 3600e3, { hasFile: true, episodeFileId: 77, title: 'Arrival' }),
        episode(orbit, 2, 2, t - 1 * DAY + 2 * 3600e3, { title: 'Crossing' }),
        episode(orbit, 2, 3, t + 6 * DAY + 2 * 3600e3, { title: 'Departure', overview: '' }),
        episode(keepers, 1, 1, t + 2 * DAY + 8 * 3600e3, { title: 'First Light' }),
        episode(keepers, 1, 2, t + 2 * DAY + 8 * 3600e3, { title: 'Fog Bank' }),
        episode(keepers, 1, 3, t + 2 * DAY + 8 * 3600e3, { title: 'Last Watch' }),
        episode(finale, 3, 10, t + 4 * DAY + 1 * 3600e3, { title: 'The End of the Pier', finaleType: 'season' }),
        // On air right now, whatever time the run starts.
        episode(live, 1, 7, Math.floor((now - 10 * 60e3) / 60e3) * 60e3, { title: 'Broadcast' })
    ];

    for (let offset = -3; offset <= 3; offset++) {
        episodes.push(episode(late, 14, 50 + offset, t + offset * DAY + 4 * 3600e3, {
            title: 'Guest ' + (50 + offset),
            hasFile: offset === -2 || offset === -3,
            runtime: 0
        }));
    }

    return episodes;
}

function radarrMovies() {
    const t = today();
    const movie = (id, title, extra) => Object.assign({
        title,
        originalTitle: title,
        sortTitle: title.toLowerCase(),
        status: 'released',
        overview: 'The ' + title + ' overview.',
        inCinemas: null,
        physicalRelease: null,
        digitalRelease: null,
        images: [{ coverType: 'poster', remoteUrl: 'https://image.example/' + id + '.jpg' }],
        year: 2026,
        hasFile: false,
        path: '/movies/' + title + ' (2026)',
        folderName: '/movies/' + title + ' (2026)',
        qualityProfileId: 1,
        monitored: true,
        minimumAvailability: 'released',
        isAvailable: false,
        runtime: 118,
        tmdbId: 700000 + id,
        imdbId: 'tt70000' + String(id).padStart(2, '0'),
        certification: 'PG-13',
        genres: ['Science Fiction', 'Adventure'],
        studio: 'Meridian Pictures',
        tags: [],
        id
    }, extra);

    return [
        movie(11, 'Starfall', { inCinemas: midnight(t + 3 * DAY), digitalRelease: midnight(t + 40 * DAY), status: 'announced' }),
        movie(12, 'Paper Moons', { inCinemas: midnight(t - 60 * DAY), digitalRelease: midnight(t - 5 * DAY), physicalRelease: midnight(t + 9 * DAY), hasFile: true, isAvailable: true }),
        movie(13, 'Glass Harbor', { digitalRelease: midnight(t - 2 * DAY), isAvailable: true }),
        movie(14, 'Quiet Engines', { physicalRelease: midnight(t - 8 * DAY), isAvailable: true, monitored: true })
    ];
}

function sonarrQueue() {
    const late = sonarrEpisodes().find(item => item.series.title === 'Late Night Live' && item.airDate === date(today() - DAY));
    return [{
        seriesId: late.seriesId,
        episodeId: late.id,
        seasonNumber: late.seasonNumber,
        size: 1000000000,
        title: 'Late.Night.Live.2026.WEB.h264',
        sizeleft: 380000000,
        timeleft: '00:12:00',
        estimatedCompletionTime: iso(Date.now() + 12 * 60e3),
        status: 'downloading',
        trackedDownloadStatus: 'ok',
        trackedDownloadState: 'downloading',
        downloadClient: 'qBittorrent',
        outputPath: '/downloads/incomplete/Late.Night.Live',
        id: 9001
    }];
}

function radarrQueue() {
    return [{
        movieId: 13,
        size: 4000000000,
        title: 'Glass.Harbor.2026.1080p.WEB',
        sizeleft: 2400000000,
        timeleft: '00:40:00',
        estimatedCompletionTime: iso(Date.now() + 40 * 60e3),
        status: 'downloading',
        trackedDownloadStatus: 'ok',
        trackedDownloadState: 'downloading',
        outputPath: '/downloads/incomplete/Glass.Harbor',
        id: 9002
    }];
}

function inRange(value, start, end) {
    if (!value) {
        return false;
    }

    const time = Date.parse(value);
    return time >= start && time <= end;
}

// A small two-tone PNG per id, so each entry's artwork is distinguishable in a screenshot.
const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) {
        c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }

    return c >>> 0;
});

function crc32(buffer) {
    let c = 0xffffffff;
    for (const byte of buffer) {
        c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
    }

    return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
}

function png(id, wide) {
    const hue = (id * 67) % 360;
    const rgb = (h, l) => {
        const a = 0.55 * Math.min(l, 1 - l);
        const f = n => {
            const k = (n + h / 30) % 12;
            return Math.round(255 * (l - a * Math.max(-1, Math.min(k - 3, 9 - k, 1))));
        };
        return [f(0), f(8), f(4)];
    };
    const width = wide ? 16 : 2;
    const height = wide ? 9 : 3;
    const rows = [];
    for (let y = 0; y < height; y++) {
        const [r, g, b] = rgb(hue, 0.25 + 0.3 * (y / (height - 1)));
        const row = [0];
        for (let x = 0; x < width; x++) {
            row.push(r, g, b);
        }

        rows.push(...row);
    }

    const header = Buffer.alloc(13);
    header.writeUInt32BE(width, 0);
    header.writeUInt32BE(height, 4);
    header[8] = 8;
    header[9] = 2;
    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        chunk('IHDR', header),
        chunk('IDAT', deflateSync(Buffer.from(rows))),
        chunk('IEND', Buffer.alloc(0))
    ]);
}

function send(response, status, body, type = 'application/json; charset=utf-8') {
    const payload = Buffer.isBuffer(body) ? body : Buffer.from(typeof body === 'string' ? body : JSON.stringify(body));
    response.writeHead(status, { 'Content-Type': type, 'Content-Length': payload.length });
    response.end(payload);
}

function serve(service, port) {
    const key = service === 'sonarr' ? SONARR_KEY : RADARR_KEY;
    createServer((request, response) => {
        const url = new URL(request.url, 'http://fake');
        if (url.pathname.startsWith('/__')) {
            control(url, request, response);
            return;
        }

        log.push({
            service,
            method: request.method,
            path: url.pathname,
            query: Object.fromEntries(url.searchParams),
            keyOk: request.headers['x-api-key'] === key,
            headers: Object.keys(request.headers)
        });

        if (failures[service] === 'unreachable') {
            request.socket.destroy();
            return;
        }

        if (failures[service] === 'html') {
            send(response, 200, '<html><body>Sign in</body></html>', 'text/html');
            return;
        }

        if (request.headers['x-api-key'] !== key || failures[service] === 'unauthorized') {
            send(response, 401, '');
            return;
        }

        const start = Date.parse(url.searchParams.get('start') ?? '') || today();
        const end = Date.parse(url.searchParams.get('end') ?? '') || today() + 2 * DAY;
        const cover = /^\/api\/v3\/mediacover\/(\d+)\/(poster|fanart)-\d+\.jpg$/.exec(url.pathname);

        if (url.pathname === '/api/v3/system/status') {
            send(response, 200, { appName: service === 'sonarr' ? 'Sonarr' : 'Radarr', version: service === 'sonarr' ? '4.0.20.3014' : '7.0.3.10918', urlBase: '' });
        } else if (url.pathname === '/api/v3/calendar' && service === 'sonarr') {
            send(response, 200, sonarrEpisodes().filter(item => inRange(item.airDateUtc, start, end)));
        } else if (url.pathname === '/api/v3/calendar') {
            send(response, 200, radarrMovies().filter(item => ['inCinemas', 'digitalRelease', 'physicalRelease'].some(field => inRange(item[field], start, end))));
        } else if (url.pathname === '/api/v3/queue/details') {
            send(response, 200, service === 'sonarr' ? sonarrQueue() : radarrQueue());
        } else if (cover) {
            send(response, 200, png(Number(cover[1]) + (service === 'radarr' ? 50 : 0), cover[2] === 'fanart'), 'image/png');
        } else {
            send(response, 404, { message: 'NotFound' });
        }
    }).listen(port, '0.0.0.0');
}

// Harness controls: read and clear the request log, and make a service fail.
function control(url, request, response) {
    if (url.pathname === '/__log' && request.method === 'GET') {
        send(response, 200, log);
    } else if (url.pathname === '/__log' && request.method === 'DELETE') {
        log.length = 0;
        send(response, 200, {});
    } else if (url.pathname === '/__fail' && request.method === 'POST') {
        failures[url.searchParams.get('service')] = url.searchParams.get('mode') ?? 'none';
        send(response, 200, failures);
    } else {
        send(response, 404, {});
    }
}

serve('sonarr', 8989);
serve('radarr', 7878);
console.log('fake arr listening on 8989 (Sonarr) and 7878 (Radarr)');
