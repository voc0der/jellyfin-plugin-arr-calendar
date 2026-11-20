import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
    testDir: '.',
    testMatch: '*.spec.mjs',
    globalSetup: './harness/global-setup.mjs',

    // One Jellyfin for the whole run, and some tests switch Seerr Integration on and off, so they
    // run in order.
    workers: 1,
    fullyParallel: false,
    retries: 0,
    forbidOnly: !!process.env.CI,

    timeout: 90_000,
    expect: { timeout: 15_000 },

    reporter: process.env.CI
        ? [['list'], ['html', { open: 'never' }]]
        : [['list']],

    use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 900 },
        locale: 'en-US',
        timezoneId: 'UTC',
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure'
    }
});
