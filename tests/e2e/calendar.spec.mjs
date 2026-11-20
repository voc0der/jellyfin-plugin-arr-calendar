import { expect, test } from '@playwright/test';
import {
    arrLog,
    calendarSettings,
    clearArrLog,
    failArr,
    openCalendar,
    seerrIntegration,
    signIn,
    stack,
    utcDay
} from './harness/session.mjs';
import { ADMIN, PLUGIN_ID, RADARR_KEY, SONARR_KEY, jellyfin } from './harness/stack.mjs';

// The stand-in Sonarr and Radarr build their schedule around today (see harness/fake-arr.mjs),
// and the browser runs in UTC unless a test says otherwise, so `utcDay(n)` names the day an
// entry lands on.

test.describe.configure({ mode: 'serial' });

// Any uncaught error from the page's own script fails the test. Jellyfin Web has errors of its
// own, so only errors whose stack runs through this plugin's file count.
let clientErrors = [];
test.beforeEach(async ({ page }) => {
    clientErrors = [];
    page.on('pageerror', error => {
        if (/arr-calendar\.js/.test(error.stack || '')) {
            clientErrors.push(error.stack);
        }
    });
});
test.afterEach(() => {
    expect(clientErrors, clientErrors.join('\n')).toEqual([]);
});

const column = (page, offset) => page.locator(`.acal-col[data-day="${utcDay(offset)}"]`);
const cell = (page, offset) => page.locator(`.acal-day[data-day="${utcDay(offset)}"]`);

// Calls this plugin's API from the page, as the signed-in user.
function fromPage(page, path, query) {
    return page.evaluate(async ({ path, query }) => {
        const client = window.ApiClient;
        const headers = {};
        if (typeof client.setRequestHeaders === 'function') {
            client.setRequestHeaders(headers);
        }

        headers.Authorization = headers.Authorization || 'MediaBrowser Token="' + client.accessToken() + '"';
        const response = await fetch(client.getUrl(path, query), { headers });
        return { status: response.status, body: await response.text() };
    }, { path, query });
}

test('Calendar sits right after Home in the legacy drawer', async ({ page }) => {
    await signIn(page, stack().users.alice, 'desktop-legacy');
    await page.locator('.mainDrawerButton').click();
    const entry = page.locator('.mainDrawer-scrollContainer [data-arr-calendar-nav="drawer"]');
    await expect(entry).toBeVisible();
    await expect(entry).toHaveText(/Calendar/);
    const before = await entry.evaluate(node => {
        let previous = node.previousElementSibling;
        while (previous && previous.classList.contains('customMenuOptions')) {
            previous = previous.previousElementSibling;
        }

        return previous && previous.getAttribute('href');
    });
    expect(before).toBe('#/home');

    await entry.click();
    await expect(page).toHaveURL(/#\/calendar$/);
    await expect(page.locator('#arrCalendarPage')).toBeVisible();
    await expect(page.locator('#fallbackPage')).toBeHidden();
    await expect(page.locator('.acal-month .acal-day').first()).toBeVisible();
    await expect(entry).toHaveClass(/navMenuOption-selected/);
});

test('Calendar sits after Favorites in the Modern app bar', async ({ page }) => {
    await signIn(page, stack().users.alice);
    const entry = page.locator('.MuiAppBar-root [data-arr-calendar-nav="appbar"]');
    await expect(entry).toBeVisible();
    await expect(entry).toHaveText('Calendar');
    expect(await entry.evaluate(node => node.previousElementSibling.getAttribute('href'))).toBe('#/home?tab=1');

    await entry.click();
    await expect(page).toHaveURL(/#\/calendar$/);
    await expect(page.locator('#arrCalendarPage')).toBeVisible();
    await expect(entry).toHaveAttribute('aria-current', 'page');
});

test('Calendar sits after Favorites in the Modern phone menu', async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 412, height: 860 }, isMobile: true, hasTouch: true, timezoneId: 'UTC' });
    const page = await context.newPage();
    try {
        await signIn(page, stack().users.alice);
        await page.locator('button[aria-label="Open Menu"]').click();
        const entry = page.locator('.MuiDrawer-root [data-arr-calendar-nav="menu"]');
        await expect(entry).toBeVisible();
        await expect(entry).toHaveText(/Calendar/);
        expect(await entry.evaluate(node => node.previousElementSibling.querySelector('a').getAttribute('href'))).toBe('#/home?tab=1');

        await entry.click();
        await expect(page).toHaveURL(/#\/calendar$/);
        await expect(page.locator('#arrCalendarPage')).toBeVisible();
        await expect(page.locator('.MuiDrawer-root .MuiBackdrop-root')).toHaveCount(0);
    } finally {
        await context.close();
    }
});

test('the month view is the default, and Week switches to the week', async ({ page }) => {
    await signIn(page, stack().users.alice);
    await openCalendar(page);
    await expect(page.locator('.acal-switch [data-view="month"]')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.acal-month')).toBeVisible();
    await expect(page.locator(`.acal-day.acal-today[data-day="${utcDay(0)}"]`)).toBeVisible();
    const monthName = new Date().toLocaleString('en-US', { month: 'long', timeZone: 'UTC' });
    await expect(page.locator('.acal-title')).toContainText(monthName);

    await page.locator('.acal-switch [data-view="week"]').click();
    await expect(page).toHaveURL(/#\/calendar\?view=week$/);
    await expect(page.locator('.acal-switch [data-view="week"]')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.acal-week .acal-col')).toHaveCount(7);
    await expect(page.locator(`.acal-col.acal-today[data-day="${utcDay(0)}"]`)).toBeVisible();

    await page.locator('.acal-switch [data-view="month"]').click();
    await expect(page).toHaveURL(/#\/calendar$/);
    await expect(page.locator('.acal-month')).toBeVisible();
});

test('the arrows, the arrow keys and Today page through the calendar without filling history', async ({ page }) => {
    await signIn(page, stack().users.alice);
    await openCalendar(page, '?view=week');

    await page.locator('[data-action="next"]').click();
    await expect(page).toHaveURL(new RegExp(`view=week&date=${utcDay(7)}$`));
    await expect(column(page, 7)).toBeVisible();

    await page.keyboard.press('ArrowLeft');
    await expect(page).toHaveURL(/#\/calendar\?view=week$/);
    await page.keyboard.press('ArrowLeft');
    await expect(page).toHaveURL(new RegExp(`date=${utcDay(-7)}$`));
    await expect(column(page, -7)).toBeVisible();

    await page.locator('[data-action="today"]').click();
    await expect(page).toHaveURL(/#\/calendar\?view=week$/);

    // Paging replaces the history entry, so Back leaves the Calendar.
    await page.goBack();
    await expect(page).toHaveURL(/#\/home/);
    await expect(page.locator('#arrCalendarPage')).toBeHidden();
});

test('TV and movies are told apart by their glyph, and the legend says which is which', async ({ page }) => {
    await signIn(page, stack().users.alice);
    await openCalendar(page, `?view=week&date=${utcDay(-2)}`);

    const day = column(page, -2);
    const show = day.locator('.acal-card[data-kind="episode"]').first();
    const film = day.locator('.acal-card[data-kind="movie"]').first();
    await expect(show.locator('.acal-card-kicker .acal-glyph')).toHaveText('tv');
    await expect(film.locator('.acal-card-kicker .acal-glyph')).toHaveText('movie');
    const colour = card => card.locator('.acal-card-kicker .acal-glyph').evaluate(node => getComputedStyle(node).color);
    expect(await colour(show)).not.toBe(await colour(film));
    await expect(film).toContainText('Digital');
    await expect(page.locator('.acal-legend [data-kind="episode"]')).toHaveText(/TV/);
    await expect(page.locator('.acal-legend [data-kind="movie"]')).toHaveText(/Movies/);

    await page.locator('.acal-switch [data-view="month"]').click();
    await expect(cell(page, -2).locator('.acal-pill[data-kind="movie"] .acal-glyph')).toHaveText('movie');
    await expect(cell(page, -2).locator('.acal-pill[data-kind="movie"] .acal-pill-title')).toHaveText('Glass Harbor');
    await expect(cell(page, -2).locator('.acal-pill[data-kind="episode"] .acal-glyph')).toHaveText(['tv']);
});

test('downloads in progress show how far they have got', async ({ page }) => {
    await signIn(page, stack().users.alice);

    await openCalendar(page, `?view=week&date=${utcDay(-1)}`);
    const episodeCard = column(page, -1).locator('.acal-card[data-status="downloading"]');
    await expect(episodeCard).toContainText('Late Night Live');
    await expect(episodeCard).toContainText('Downloading 62%');
    await expect(episodeCard).toHaveAttribute('style', /--acal-progress:\s*62%/);
    await expect(episodeCard.locator('.acal-tile-art')).toHaveClass(/acal-loaded/);

    await episodeCard.click();
    const dialog = page.locator('.acal-dialog');
    await expect(dialog.locator('[role="progressbar"]')).toHaveAttribute('aria-valuenow', '62');
    await expect(dialog.locator('.acal-progress-label')).toHaveText(/^Downloading 62% · about \d+ minutes left$/);
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);

    await openCalendar(page, `?view=week&date=${utcDay(-2)}`);
    await expect(column(page, -2).locator('.acal-card[data-kind="movie"][data-status="downloading"]')).toContainText('Downloading 40%');

    await openCalendar(page, `?date=${utcDay(-1)}`);
    await expect(cell(page, -1).locator('.acal-pill[data-status="downloading"] .acal-pill-pct')).toHaveText('62%');
});

test('a season released at once is one entry that lists every episode', async ({ page }) => {
    await signIn(page, stack().users.alice);
    await openCalendar(page, `?view=week&date=${utcDay(2)}`);

    const drop = column(page, 2).locator('.acal-card').filter({ hasText: 'The Lighthouse Keepers' });
    await expect(drop).toHaveCount(1);
    await expect(drop).toContainText('S01E01–E03');
    await expect(drop).toContainText('3 episodes');

    await drop.click();
    const dialog = page.locator('.acal-dialog');
    await expect(dialog.locator('.acal-episodes li')).toHaveCount(3);
    await expect(dialog.locator('.acal-episodes li').first()).toContainText('S01E01');
    await expect(dialog.locator('.acal-episodes li').first()).toContainText('First Light');
    await expect(dialog.locator('.acal-chips')).toContainText('Series premiere');
});

test('details show the episode, its finale, and the series when the episode has no overview', async ({ page }) => {
    await signIn(page, stack().users.alice);

    await openCalendar(page, `?view=week&date=${utcDay(4)}`);
    await column(page, 4).locator('.acal-card').filter({ hasText: 'Cold Harbor' }).click();
    const dialog = page.locator('.acal-dialog');
    await expect(dialog.locator('h2')).toHaveText('Cold Harbor');
    await expect(dialog.locator('.acal-kicker')).toHaveText(/TV · Northwind · TV-14/);
    await expect(dialog.locator('.acal-dialog-sub')).toHaveText('S03E10 · The End of the Pier');
    await expect(dialog.locator('.acal-dialog-when')).toContainText('1:00 AM – 1:55 AM');
    await expect(dialog.locator('.acal-chips')).toContainText('Upcoming');
    await expect(dialog.locator('.acal-chips')).toContainText('Season finale');
    await expect(dialog.locator('.acal-overview')).toHaveText('What happens in Cold Harbor S3E10.');
    await expect(dialog.locator('.acal-dialog-poster')).toHaveClass(/acal-loaded/);
    await page.keyboard.press('Escape');

    await openCalendar(page, `?view=week&date=${utcDay(6)}`);
    await column(page, 6).locator('.acal-card').filter({ hasText: 'Orbit Street' }).click();
    await expect(dialog.locator('.acal-overview')).toHaveText('About the seriesThe Orbit Street series overview.');
    await page.keyboard.press('Escape');

    await openCalendar(page, '?view=week');
    await column(page, 0).locator('.acal-card').filter({ hasText: 'Signal Fire' }).click();
    await expect(dialog.locator('.acal-chips [data-status="airing"]')).toHaveText('On air now');
});

test('nothing about Sonarr or Radarr reaches the browser', async ({ page }) => {
    const { base, sonarrBase, radarrBase } = stack();
    const requested = [];
    page.on('request', request => requested.push(request.url()));
    await clearArrLog();

    await signIn(page, stack().users.alice);
    await openCalendar(page, `?view=week&date=${utcDay(-1)}`);
    await expect(column(page, -1).locator('.acal-tile-art.acal-loaded').first()).toBeVisible();

    // The browser never reached Sonarr, Radarr or an artwork host: the calendar and its
    // pictures all came through Jellyfin. (Jellyfin Web loads its own Chromecast script.)
    for (const url of requested) {
        expect(url.startsWith(sonarrBase) || url.startsWith(radarrBase), url).toBeFalsy();
        expect(/thetvdb\.com|image\.tmdb\.org|artworks\.example|image\.example/.test(url), url).toBeFalsy();
    }

    const own = requested.filter(url => url.includes('/Plugins/ArrCalendar/'));
    expect(own.some(url => url.includes('/Plugins/ArrCalendar/Image/'))).toBeTruthy();
    expect(own.every(url => url.startsWith(base)), own.join('\n')).toBeTruthy();

    // The calendar answer carries no server path and no key.
    const answer = await fromPage(page, 'Plugins/ArrCalendar/Calendar', { start: utcDay(-7), end: utcDay(7) });
    expect(answer.status).toBe(200);
    expect(answer.body).not.toMatch(/\/tv\/|\/movies\/|\/downloads\/|outputPath|folderName|qualityProfile/);
    expect(answer.body).not.toContain(SONARR_KEY);
    expect(answer.body).not.toContain(RADARR_KEY);

    // Artwork is served anonymously, but only with the signature the calendar handed out.
    const src = await column(page, -1).locator('.acal-tile-art.acal-loaded').first()
        .evaluate(node => node.style.backgroundImage.replace(/^url\("?/, '').replace(/"?\)$/, ''));
    const image = await fetch(src);
    expect(image.status).toBe(200);
    expect(image.headers.get('content-type')).toMatch(/^image\//);
    expect((await fetch(src.replace(/sig=[^&]+/, 'sig=' + 'A'.repeat(22)))).status).toBe(404);
    expect((await fetch(src.replace(/([?&])sig=[^&]+&?/, '$1'))).status).toBe(404);
    expect((await fetch(src.replace(/Image\/(sonarr|radarr)\/\d+/, 'Image/$1/99999'))).status).toBe(404);

    // Sonarr and Radarr were asked with their own keys, for their API only.
    const log = await arrLog();
    expect(log.length).toBeGreaterThan(0);
    for (const entry of log) {
        expect(entry.keyOk, `${entry.service} ${entry.path}`).toBeTruthy();
        expect(entry.path.startsWith('/api/v3/'), entry.path).toBeTruthy();
    }
});

test('a failing Radarr leaves Sonarr\'s entries in place and says why', async ({ page }) => {
    // A setting that is part of the cache key, so this run asks the services afresh.
    await calendarSettings({ IncludeUnmonitored: true });
    await failArr('radarr', 'unreachable');
    try {
        await signIn(page, stack().users.alice);
        await openCalendar(page, `?view=week&date=${utcDay(-1)}`);
        await expect(page.locator('.acal-notice')).toContainText('Radarr can\'t be reached right now.');
        await expect(page.locator('.acal-card[data-kind="episode"]').first()).toBeVisible();
        await expect(page.locator('.acal-card[data-kind="movie"]')).toHaveCount(0);
        // The message says what is wrong, not where Radarr lives.
        await expect(page.locator('.acal-notice')).not.toContainText('arr:7878');
    } finally {
        await failArr('radarr', 'none');
        await calendarSettings({ IncludeUnmonitored: false });
    }
});

test('a calendar with nothing connected says so', async ({ page }) => {
    await calendarSettings({ SonarrUrl: '', RadarrUrl: '' });
    try {
        await signIn(page, stack().users.alice);
        await openCalendar(page);
        await expect(page.locator('.acal-message h2')).toHaveText('The calendar is not set up yet');
    } finally {
        await calendarSettings({ SonarrUrl: 'http://arr:8989', RadarrUrl: 'http://arr:7878' });
    }
});

test('west of Greenwich, episodes move to the viewer\'s own evening and release days stay put', async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, timezoneId: 'America/Los_Angeles', locale: 'en-US' });
    const page = await context.newPage();
    try {
        await signIn(page, stack().users.alice);

        // Orbit Street S02E02 airs at 02:00 UTC on day -1: the previous evening in Los Angeles.
        await openCalendar(page, `?view=week&date=${utcDay(-2)}`);
        const evening = column(page, -2).locator('.acal-card').filter({ hasText: 'Orbit Street' });
        await expect(evening).toContainText('S02E02');
        await expect(evening).toContainText('6:00 PM');
        // Glass Harbor's digital release is day -2 wherever the viewer is.
        await expect(column(page, -2)).toContainText('Glass Harbor');

        await openCalendar(page, `?view=week&date=${utcDay(-1)}`);
        await expect(column(page, -1).locator('.acal-card').filter({ hasText: 'Orbit Street' })).toHaveCount(0);
    } finally {
        await context.close();
    }
});

test('phones get a compact month with the chosen day listed underneath', async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 412, height: 860 }, isMobile: true, hasTouch: true, timezoneId: 'UTC' });
    const page = await context.newPage();
    try {
        await signIn(page, stack().users.alice);
        await openCalendar(page, `?date=${utcDay(-1)}`);
        await expect(page.locator('.acal-compact')).toBeVisible();
        await expect(page.locator('.acal-pill')).toHaveCount(0);

        const day = page.locator(`.acal-mday[data-day="${utcDay(-1)}"]`);
        await expect(day.locator('.acal-dots i[data-kind="episode"]').first()).toBeVisible();
        await day.tap();
        await expect(day).toHaveAttribute('aria-pressed', 'true');
        const agenda = page.locator(`.acal-agenda-day[data-day="${utcDay(-1)}"]`);
        await expect(agenda.locator('.acal-card').filter({ hasText: 'Late Night Live' })).toContainText('Downloading 62%');

        await openCalendar(page, `?view=week&date=${utcDay(-1)}`);
        await expect(page.locator('.acal-agenda-day')).toHaveCount(7);
        await expect(page.locator('.acal-week')).toHaveCount(0);
    } finally {
        await context.close();
    }
});

test('the settings page tests connections and keeps what it saves', async ({ page }) => {
    const { base, adminToken } = stack();
    await signIn(page, ADMIN);
    await page.evaluate(() => { window.location.hash = '#/configurationpage?name=Arr%20Calendar'; });
    await expect(page.locator('#ArrCalendarConfigPage #txtSonarrUrl')).toHaveValue('http://arr:8989');
    await expect(page.locator('#statusList')).toContainText('JavaScript Injector: the Calendar is registered.');
    await expect(page.locator('#statusList')).toContainText('Sonarr is set up');
    // Seerr Integration is installed here, but its Requests page is switched off for now.
    const seerrLine = page.locator('#statusList > div', { hasText: 'Seerr Integration' });
    await expect(seerrLine).toContainText('Seerr Integration\'s Requests page is switched off');
    await expect(seerrLine.locator('.material-icons')).toHaveText('info_outline');

    await page.locator('#btnTestSonarr').click();
    await expect(page.locator('#sonarrTestResult')).toHaveText('✓ Connected to Sonarr 4.0.20.3014.');

    // Radarr's address and key typed into the Sonarr fields.
    await page.locator('#txtSonarrUrl').fill('http://arr:7878');
    await page.locator('#txtSonarrApiKey').fill(RADARR_KEY);
    await page.locator('#btnTestSonarr').click();
    await expect(page.locator('#sonarrTestResult')).toHaveText('✗ That address is Radarr, not Sonarr.');

    // Nothing was saved by testing.
    let config = await jellyfin(base, 'GET', `/Plugins/${PLUGIN_ID}/Configuration`, { token: adminToken });
    expect(config.SonarrUrl).toBe('http://arr:8989');

    await page.locator('#txtSonarrUrl').fill('http://arr:8989');
    await page.locator('#txtSonarrApiKey').fill(SONARR_KEY);
    await page.locator('#chkIncludeUnmonitored').check({ force: true });
    await page.locator('#txtTimeout').fill('20');
    await page.locator('#btnSave').click();
    await expect.poll(async () => (await jellyfin(base, 'GET', `/Plugins/${PLUGIN_ID}/Configuration`, { token: adminToken })).RequestTimeoutSeconds).toBe(20);
    config = await jellyfin(base, 'GET', `/Plugins/${PLUGIN_ID}/Configuration`, { token: adminToken });
    expect(config.IncludeUnmonitored).toBe(true);
    expect(config.SonarrApiKey).toBe(SONARR_KEY);
    expect(config.RadarrApiKey).toBe(RADARR_KEY);
    await calendarSettings({ IncludeUnmonitored: false, RequestTimeoutSeconds: 15 });
});

test('leaving the Calendar gives Jellyfin its page back', async ({ page }) => {
    await signIn(page, stack().users.alice, 'desktop-legacy');
    await expect(page.locator('.skinHeader .headerTabs .emby-tab-button').first()).toBeVisible();
    await openCalendar(page);
    await expect(page.locator('.skinHeader .pageTitle')).toHaveText('Calendar');
    await expect(page.locator('.skinHeader .headerTabs .emby-tab-button')).toHaveCount(0);
    await page.locator('.mainDrawerButton').click();
    await page.locator('.mainDrawer-scrollContainer a.navMenuOption[href="#/home"]').click();
    await expect(page).toHaveURL(/#\/home/);
    await expect(page.locator('#arrCalendarPage')).toBeHidden();
    await expect(page.locator('html')).not.toHaveClass(/arr-calendar-active/);
    await expect(page.locator('#indexPage:not(.hide)')).toBeVisible();
});

test('switching the Calendar off takes it out of Jellyfin Web', async ({ page }) => {
    await calendarSettings({ Enabled: false });
    try {
        await signIn(page, stack().users.alice);
        await expect(page.locator('.MuiAppBar-root a[href="#/home?tab=1"]')).toBeVisible();
        await page.waitForTimeout(2000);
        await expect(page.locator('[data-arr-calendar-nav]')).toHaveCount(0);
        expect(await page.evaluate(() => typeof window.ArrCalendar)).toBe('undefined');
        expect((await fromPage(page, 'Plugins/ArrCalendar/Calendar', { start: utcDay(0), end: utcDay(6) })).status).toBe(403);
    } finally {
        await calendarSettings({ Enabled: true });
    }
});

test.describe('with Seerr Integration installed', () => {
    test.beforeAll(async () => {
        await seerrIntegration(true);
    });

    test.afterAll(async () => {
        await seerrIntegration(false);
    });

    test('Calendar is a small menu under Requests in the app bar', async ({ page }) => {
        await signIn(page, stack().users.alice);
        const requests = page.locator('.MuiAppBar-root [data-seerr-integration-nav="appbar"]');
        await expect(requests).toBeVisible();
        await page.waitForTimeout(1000);
        await expect(page.locator('.MuiAppBar-root [data-arr-calendar-nav="appbar"]')).toHaveCount(0);

        const flyout = page.locator('.acal-flyout');
        await requests.hover();
        await expect(flyout).toHaveClass(/acal-open/);
        await expect(requests).toHaveAttribute('aria-expanded', 'true');

        // Moving away closes it; moving from Requests into it keeps it open.
        await page.mouse.move(700, 600);
        await expect(flyout).not.toHaveClass(/acal-open/);
        await requests.hover();
        await flyout.locator('a').hover();
        await expect(flyout).toHaveClass(/acal-open/);
        await flyout.locator('a').click();
        await expect(page).toHaveURL(/#\/calendar$/);
        await expect(page.locator('#arrCalendarPage')).toBeVisible();
        await expect(flyout).not.toHaveClass(/acal-open/);

        // From the keyboard: Requests takes focus, Arrow Down moves into the menu.
        await requests.focus();
        await expect(flyout).toHaveClass(/acal-open/);
        await page.keyboard.press('ArrowDown');
        await expect(flyout.locator('a')).toBeFocused();
        await page.keyboard.press('Escape');
        await expect(flyout).not.toHaveClass(/acal-open/);
        await expect(requests).toBeFocused();
        await page.waitForTimeout(400);
        await expect(flyout).not.toHaveClass(/acal-open/);
    });

    test('Calendar opens under Requests in the legacy drawer', async ({ page }) => {
        await signIn(page, stack().users.alice, 'desktop-legacy');
        await page.locator('.mainDrawerButton').click();
        const requests = page.locator('.mainDrawer-scrollContainer [data-seerr-integration-nav="drawer"]');
        const nested = page.locator('.mainDrawer-scrollContainer [data-arr-calendar-nav="drawer-nested"]');
        await expect(requests).toBeVisible();
        await expect(page.locator('.mainDrawer-scrollContainer [data-arr-calendar-nav="drawer"]')).toHaveCount(0);
        expect(await nested.evaluate(node => node.previousElementSibling.getAttribute('data-seerr-integration-nav'))).toBe('drawer');

        await page.mouse.move(900, 700);
        await expect(nested).toBeHidden();
        await requests.hover();
        await expect(nested).toBeVisible();
        await nested.hover();
        await nested.click();
        await expect(page).toHaveURL(/#\/calendar$/);
        await expect(page.locator('#arrCalendarPage')).toBeVisible();
        await expect(nested).toHaveClass(/navMenuOption-selected/);
    });

    test('in a narrow window with a mouse, Calendar opens under Requests in the menu', async ({ browser }) => {
        const context = await browser.newContext({ viewport: { width: 700, height: 860 }, timezoneId: 'UTC' });
        const page = await context.newPage();
        try {
            await signIn(page, stack().users.alice);
            await page.locator('button[aria-label="Open Menu"]').click();
            const requests = page.locator('.MuiDrawer-root [data-seerr-integration-nav="menu"]');
            const nested = page.locator('.MuiDrawer-root [data-arr-calendar-nav="menu-nested"]');
            await expect(requests).toBeVisible();
            await expect(page.locator('.MuiDrawer-root [data-arr-calendar-nav="menu"]')).toHaveCount(0);
            expect(await nested.evaluate(node => node.previousElementSibling.getAttribute('data-seerr-integration-nav'))).toBe('menu');

            await page.mouse.move(650, 800);
            await expect(nested).toBeHidden();
            await requests.hover();
            await expect(nested).toBeVisible();
            await nested.hover();
            await nested.locator('a').click();
            await expect(page).toHaveURL(/#\/calendar$/);
            await expect(page.locator('#arrCalendarPage')).toBeVisible();
        } finally {
            await context.close();
        }
    });

    test('on a touch screen, Calendar stays its own entry right after Requests', async ({ browser }) => {
        const context = await browser.newContext({ viewport: { width: 412, height: 860 }, isMobile: true, hasTouch: true, timezoneId: 'UTC' });
        const page = await context.newPage();
        try {
            await signIn(page, stack().users.alice);
            await page.locator('button[aria-label="Open Menu"]').click();
            const entry = page.locator('.MuiDrawer-root [data-arr-calendar-nav="menu"]');
            await expect(entry).toBeVisible();
            await expect(page.locator('.MuiDrawer-root [data-arr-calendar-nav="menu-nested"]')).toHaveCount(0);
            expect(await entry.evaluate(node => node.previousElementSibling.getAttribute('data-seerr-integration-nav'))).toBe('menu');
            await entry.click();
            await expect(page).toHaveURL(/#\/calendar$/);
        } finally {
            await context.close();
        }

        const legacy = await browser.newContext({ viewport: { width: 412, height: 860 }, isMobile: true, hasTouch: true, timezoneId: 'UTC' });
        const legacyPage = await legacy.newPage();
        try {
            await signIn(legacyPage, stack().users.alice, 'mobile-legacy');
            await legacyPage.locator('.mainDrawerButton').click();
            const entry = legacyPage.locator('.mainDrawer-scrollContainer [data-arr-calendar-nav="drawer"]');
            await expect(entry).toBeVisible();
            expect(await entry.evaluate(node => node.previousElementSibling.getAttribute('data-seerr-integration-nav'))).toBe('drawer');
        } finally {
            await legacy.close();
        }
    });

    test('the settings page checks off Seerr Integration', async ({ page }) => {
        await signIn(page, ADMIN);
        await page.evaluate(() => { window.location.hash = '#/configurationpage?name=Arr%20Calendar'; });
        const seerrLine = page.locator('#ArrCalendarConfigPage #statusList > div', { hasText: 'Seerr Integration is active' });
        await expect(seerrLine).toContainText('Calendar sits under Requests on devices with a mouse');
        await expect(seerrLine.locator('.material-icons')).toHaveText('check_circle');
    });

    test('details link to the title in Requests', async ({ page }) => {
        await signIn(page, stack().users.alice);
        await openCalendar(page, `?view=week&date=${utcDay(3)}`);
        await column(page, 3).locator('.acal-card').filter({ hasText: 'Starfall' }).click();
        const link = page.locator('.acal-dialog a', { hasText: 'View in Requests' });
        await expect(link).toHaveAttribute('href', '#/requests?view=movie&id=700011');
        await link.click();
        await expect(page).toHaveURL(/#\/requests\?view=movie&id=700011$/);
        await expect(page.locator('#seerrIntegrationPage')).toBeVisible();
        await expect(page.locator('.acal-dialog')).toHaveCount(0);
        await expect(page.locator('#arrCalendarPage')).toBeHidden();
    });
});
