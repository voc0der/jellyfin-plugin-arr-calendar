import { join } from 'node:path';
import { HARNESS_ROOT, JELLYFIN_IMAGE, startStack, stopStack } from './stack.mjs';

// Builds the plugin, boots Jellyfin and the stand-in Sonarr and Radarr with everything linked,
// and hands the addresses and users to the tests through the environment.
export default async function globalSetup() {
    const started = Date.now();
    const stack = await startStack();
    process.env.ARR_CALENDAR_E2E = JSON.stringify(stack);

    const seconds = ((Date.now() - started) / 1000).toFixed(1);
    console.log(`Jellyfin ${stack.jellyfinVersion} (${JELLYFIN_IMAGE}) at ${stack.base}`);
    console.log(`Stand-in Sonarr at ${stack.sonarrBase}, Radarr at ${stack.radarrBase}`);
    console.log(`Ready in ${seconds}s`);

    return async () => {
        if (process.env.HARNESS_KEEP) {
            console.log(`HARNESS_KEEP set: left running. Jellyfin ${stack.base} (admin / harness-admin-pw).`);
            console.log('Remove with: docker rm -f -v arr-calendar-e2e-jellyfin arr-calendar-e2e-arr && docker network rm arr-calendar-e2e');
            return;
        }

        await stopStack({ logDir: join(HARNESS_ROOT, 'test-results') });
    };
}
