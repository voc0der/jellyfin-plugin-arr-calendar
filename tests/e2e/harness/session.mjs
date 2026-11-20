import { expect } from '@playwright/test';
import { configureCalendar, setSeerrIntegration } from './stack.mjs';

export function stack() {
    return JSON.parse(process.env.ARR_CALENDAR_E2E);
}

// Signs a user in through Jellyfin Web's own login form. `layout` is a Display mode value:
// undefined for Auto (Modern), or 'desktop-legacy', 'mobile-legacy', 'tv'.
export async function signIn(page, user, layout) {
    const { base } = stack();
    await page.addInitScript(value => {
        if (value) {
            localStorage.setItem('layout', value);
        } else {
            localStorage.removeItem('layout');
        }
    }, layout ?? '');
    await page.goto(`${base}/web/`);
    await page.locator('#txtManualName').fill(user.name);
    await page.locator('#txtManualPassword').fill(user.password);
    await page.locator('#txtManualPassword').press('Enter');
    await page.waitForURL(/#\/home/, { timeout: 30_000 });
}

export async function openCalendar(page, query = '') {
    await page.evaluate(hash => { window.location.hash = hash; }, '#/calendar' + query);
    await expect(page.locator('#arrCalendarPage')).toBeVisible();
}

export async function seerrIntegration(enabled) {
    const { base, adminToken } = stack();
    await setSeerrIntegration(base, adminToken, enabled);
}

export async function calendarSettings(values) {
    const { base, adminToken } = stack();
    await configureCalendar(base, adminToken, values);
}

// What the stand-in Sonarr and Radarr were asked, and a way to make one of them fail.
export async function arrLog() {
    const response = await fetch(`${stack().sonarrBase}/__log`);
    return response.json();
}

export async function clearArrLog() {
    await fetch(`${stack().sonarrBase}/__log`, { method: 'DELETE' });
}

export async function failArr(service, mode) {
    await fetch(`${stack().sonarrBase}/__fail?service=${service}&mode=${mode}`, { method: 'POST' });
}

// Today's date in the browser's time zone, which the config pins to UTC.
export function utcDay(offsetDays = 0) {
    const now = new Date();
    return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + offsetDays)).toISOString().slice(0, 10);
}
