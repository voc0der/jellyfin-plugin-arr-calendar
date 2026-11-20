// Boots a throwaway Jellyfin with this repository's build of the plugin, JavaScript Injector and
// Seerr Integration installed, next to a stand-in Sonarr and Radarr, and links them the way an
// admin would.
//
// Jellyfin is real because what this harness exists to catch lives in its seams: the injector
// decides whether the page loads at all, Jellyfin Web decides where navigation lives, and Seerr
// Integration decides whether Calendar has a Requests entry to sit under.

import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const sleep = ms => new Promise(done => setTimeout(done, ms));

const HERE = dirname(fileURLToPath(import.meta.url));
export const HARNESS_ROOT = resolve(HERE, '..');
export const REPO_ROOT = resolve(HARNESS_ROOT, '../..');

// The newest Jellyfin release: the page has to work on what people run.
export const JELLYFIN_IMAGE = process.env.JELLYFIN_IMAGE ?? 'jellyfin/jellyfin:12.4';
export const NODE_IMAGE = process.env.NODE_IMAGE ?? 'node:26-alpine';

// The releases people install, pinned by checksum: their DLLs are loaded into Jellyfin, so a
// replaced release asset must fail the run rather than be installed.
const INJECTOR_ZIP = {
    url: process.env.INJECTOR_ZIP_URL
        ?? 'https://github.com/n00bcodr/Jellyfin-JavaScript-Injector/releases/download/4.1.0.0/Jellyfin.Plugin.JavaScriptInjector_12.0.0.zip',
    sha256: process.env.INJECTOR_ZIP_SHA256 ?? '099aa99dd9aa7df330ea6aeb9e9e74adb5373f01927bc9f0988493b3cefd18d6'
};
const SEERR_INTEGRATION_ZIP = {
    url: process.env.SEERR_INTEGRATION_ZIP_URL
        ?? 'https://github.com/voc0der/jellyfin-plugin-seerr-integration/releases/download/1.0.0.2/Seerr_Integration_1.0.0.2.zip',
    sha256: process.env.SEERR_INTEGRATION_ZIP_SHA256 ?? '22ea948a713c1e1203ce53846d88ca5ea16ab81e6283d45b239df2e606fda32b'
};

export const PLUGIN_ID = 'c3d840e2-b80d-4f3f-93c7-8b246d01ce0f';
export const SEERR_INTEGRATION_ID = 'c937e280-0ac7-4250-a620-8c5892e628fe';
export const ADMIN = { name: 'admin', password: 'harness-admin-pw' };
export const USERS = ['alice', 'bob'];
export const SONARR_KEY = '5e2e0000000000000000000000005e2e';
export const RADARR_KEY = '7e2e0000000000000000000000007e2e';

const NETWORK = 'arr-calendar-e2e';
const JELLYFIN = 'arr-calendar-e2e-jellyfin';
const ARR = 'arr-calendar-e2e-arr';
const CLIENT_AUTH = 'MediaBrowser Client="Arr Calendar harness", Device="harness", '
    + 'DeviceId="arr-calendar-e2e", Version="1.0.0"';

async function docker(...args) {
    const { stdout } = await exec('docker', args, { maxBuffer: 64 * 1024 * 1024 });
    return stdout.trim();
}

// Rootless Docker can briefly report a new container's filesystem busy, notably right after a
// container of the same name was removed, so copies into one are retried.
async function dockerCopy(source, target) {
    for (let attempt = 1; ; attempt++) {
        try {
            return await docker('cp', source, target);
        } catch (error) {
            if (attempt >= 5 || !/busy|failed to mount/i.test(String(error.stderr ?? error.message))) {
                throw error;
            }

            await sleep(1000 * attempt);
        }
    }
}

const sha256Of = bytes => createHash('sha256').update(bytes).digest('hex');

// A cached copy is used only if it still matches the checksum. A download lands beside the cache
// and replaces it only once its checksum has been checked.
async function download({ url, sha256 }) {
    const target = join(HARNESS_ROOT, '.cache', basename(new URL(url).pathname));
    const cached = await readFile(target).catch(() => null);
    if (cached && sha256Of(cached) === sha256) {
        return target;
    }

    await mkdir(dirname(target), { recursive: true });
    const partial = `${target}.partial`;
    await exec('curl', ['--fail', '--silent', '--show-error', '--location', '--output', partial, url]);
    const actual = sha256Of(await readFile(partial));
    if (actual !== sha256) {
        await rm(partial, { force: true });
        throw new Error(`${url} has SHA-256 ${actual}, not the pinned ${sha256}. `
            + 'For another release, set the matching *_ZIP_SHA256 variable.');
    }

    await rename(partial, target);
    return target;
}

// The plugin folders Jellyfin loads, staged inside the harness folder: rootless Docker cannot see
// this process's /tmp, so a staging folder there would arrive empty.
export async function stagePlugins() {
    const build = join(HARNESS_ROOT, '.build');
    const plugins = join(build, 'plugins');
    await rm(plugins, { recursive: true, force: true });

    const own = join(plugins, 'Arr Calendar_1.0.0.0');
    await exec('dotnet', [
        'build', join(REPO_ROOT, 'Jellyfin.Plugin.ArrCalendar.csproj'),
        '--configuration', 'Release', '--nologo', '--verbosity', 'quiet', '--output', join(build, 'plugin')
    ], { maxBuffer: 64 * 1024 * 1024 });
    await mkdir(own, { recursive: true });
    await exec('cp', [join(build, 'plugin', 'Jellyfin.Plugin.ArrCalendar.dll'), own]);

    const injector = join(plugins, 'JavaScript Injector_4.1.0.0');
    await mkdir(injector, { recursive: true });
    await exec('unzip', ['-q', '-o', '-j', await download(INJECTOR_ZIP), '*.dll', '-d', injector]);

    const seerr = join(plugins, 'Seerr Integration_1.0.0.2');
    await mkdir(seerr, { recursive: true });
    await exec('unzip', ['-q', '-o', '-j', await download(SEERR_INTEGRATION_ZIP), '*.dll', '-d', seerr]);

    return plugins;
}

async function waitFor(url, accept, timeoutMs, what) {
    const deadline = Date.now() + timeoutMs;
    let last = 'never responded';
    while (Date.now() < deadline) {
        try {
            const response = await fetch(url);
            const type = response.headers.get('content-type') ?? '';
            if (response.ok && type.includes('json')) {
                const body = await response.json();
                if (accept(body)) {
                    return body;
                }

                last = 'not ready yet';
            } else {
                last = `${response.status} ${type}`;
            }
        } catch (error) {
            last = error.message;
        }

        await sleep(1000);
    }

    throw new Error(`${what} did not come up within ${timeoutMs}ms (last: ${last})`);
}

// Jellyfin rewrites its configuration between wizard steps and briefly answers 404 or 503.
async function retry(fn, attempts = 30) {
    let last;
    for (let i = 0; i < attempts; i++) {
        try {
            return await fn();
        } catch (error) {
            last = error;
            await sleep(1000);
        }
    }

    throw last;
}

export async function jellyfin(base, method, path, { token, body } = {}) {
    const response = await fetch(base + path, {
        method,
        headers: {
            'Content-Type': 'application/json',
            Authorization: token ? `${CLIENT_AUTH}, Token="${token}"` : CLIENT_AUTH
        },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    if (!response.ok) {
        throw new Error(`${method} ${path} -> ${response.status} ${text.slice(0, 300)}`);
    }

    return text ? JSON.parse(text) : null;
}

export async function stopStack({ logDir } = {}) {
    if (logDir) {
        await mkdir(logDir, { recursive: true });
        for (const name of [JELLYFIN, ARR]) {
            const logs = await exec('docker', ['logs', name], { maxBuffer: 64 * 1024 * 1024 })
                .then(({ stdout, stderr }) => stdout + stderr)
                .catch(() => '');
            if (logs) {
                await writeFile(join(logDir, `${name}.log`), logs);
            }
        }
    }

    await docker('rm', '-f', '-v', JELLYFIN, ARR).catch(() => {});
    await docker('network', 'rm', NETWORK).catch(() => {});
}

export async function startStack() {
    const plugins = await stagePlugins();
    await stopStack();

    await docker('network', 'create', NETWORK);
    // The stand-in's code goes in as an argument rather than a copied file: some rootless Docker
    // setups cannot mount a container that has never run, which `docker cp` needs.
    await docker('run', '--detach', '--name', ARR, '--network', NETWORK, '--network-alias', 'arr',
        '--publish', '127.0.0.1::8989', '--publish', '127.0.0.1::7878',
        '-e', `SONARR_KEY=${SONARR_KEY}`, '-e', `RADARR_KEY=${RADARR_KEY}`, '-e', 'TZ=UTC',
        NODE_IMAGE, 'node', '--input-type=module', '-e', await readFile(join(HERE, 'fake-arr.mjs'), 'utf8'));

    await docker('create', '--name', JELLYFIN, '--network', NETWORK, '--network-alias', 'jellyfin',
        '--publish', '127.0.0.1::8096', '-e', 'TZ=UTC', JELLYFIN_IMAGE);
    await dockerCopy(plugins, `${JELLYFIN}:/config/`);
    await docker('start', JELLYFIN);

    const port = async (name, inner) => (await docker('port', name, inner)).split('\n')[0].split(':').pop();
    const base = `http://127.0.0.1:${await port(JELLYFIN, '8096/tcp')}`;
    const sonarrBase = `http://127.0.0.1:${await port(ARR, '8989/tcp')}`;
    const radarrBase = `http://127.0.0.1:${await port(ARR, '7878/tcp')}`;

    // A booting Jellyfin first answers with a placeholder, then 503, then the real thing.
    let info = await waitFor(`${base}/System/Info/Public`, body => 'Version' in body, 180_000, 'Jellyfin');
    if (!info.StartupWizardCompleted) {
        await waitFor(`${base}/Startup/Configuration`, () => true, 120_000, 'the Jellyfin wizard');
        await retry(() => jellyfin(base, 'POST', '/Startup/Configuration', {
            body: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' }
        }));
        await retry(() => jellyfin(base, 'GET', '/Startup/User'));
        await retry(() => jellyfin(base, 'POST', '/Startup/User', { body: { Name: ADMIN.name, Password: ADMIN.password } }));
        await retry(() => jellyfin(base, 'POST', '/Startup/RemoteAccess', {
            body: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false }
        }));
        await retry(() => jellyfin(base, 'POST', '/Startup/Complete'));
    }

    info = await waitFor(`${base}/System/Info/Public`, body => 'Version' in body, 60_000, 'Jellyfin');
    const adminSession = await retry(() => jellyfin(base, 'POST', '/Users/AuthenticateByName', {
        body: { Username: ADMIN.name, Pw: ADMIN.password }
    }));
    const adminToken = adminSession.AccessToken;

    const loaded = await jellyfin(base, 'GET', '/Plugins', { token: adminToken });
    for (const name of ['Arr Calendar', 'JavaScript Injector', 'Seerr Integration']) {
        const plugin = loaded.find(entry => entry.Name === name);
        if (!plugin || plugin.Status !== 'Active') {
            const tail = (await exec('docker', ['logs', '--tail', '80', JELLYFIN]).catch(() => ({ stdout: '' }))).stdout;
            throw new Error(`${name} did not load on Jellyfin ${info.Version} (status: ${plugin?.Status ?? 'not listed'}).\n${tail}`);
        }
    }

    const users = {};
    for (const name of USERS) {
        const created = await jellyfin(base, 'POST', '/Users/New', {
            token: adminToken,
            body: { Name: name, Password: `${name}-harness-pw` }
        });
        users[name] = { name, password: `${name}-harness-pw`, jellyfinId: created.Id };
    }

    await configureCalendar(base, adminToken, {
        SonarrUrl: 'http://arr:8989',
        SonarrApiKey: SONARR_KEY,
        RadarrUrl: 'http://arr:7878',
        RadarrApiKey: RADARR_KEY
    });

    // Seerr Integration starts switched off, so the first tests see Calendar on its own.
    await setSeerrIntegration(base, adminToken, false);

    return {
        base,
        sonarrBase,
        radarrBase,
        jellyfinVersion: info.Version,
        adminToken,
        users
    };
}

export async function configureCalendar(base, token, values) {
    const config = await jellyfin(base, 'GET', `/Plugins/${PLUGIN_ID}/Configuration`, { token });
    await jellyfin(base, 'POST', `/Plugins/${PLUGIN_ID}/Configuration`, { token, body: Object.assign(config, values) });
}

// Seerr Integration registers or removes its loader as soon as its switch is saved; browsers
// pick the change up on their next page load.
export async function setSeerrIntegration(base, token, enabled) {
    const config = await jellyfin(base, 'GET', `/Plugins/${SEERR_INTEGRATION_ID}/Configuration`, { token });
    config.Enabled = enabled;
    await jellyfin(base, 'POST', `/Plugins/${SEERR_INTEGRATION_ID}/Configuration`, { token, body: config });
}
