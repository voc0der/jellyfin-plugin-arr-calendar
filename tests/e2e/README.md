# End-to-end harness

Drives the Calendar page in a real Jellyfin with this repository's build of the plugin and the
released JavaScript Injector and Seerr Integration, next to a stand-in Sonarr and Radarr, all in
throwaway containers.

It checks where Calendar sits in each display mode, on its own and under Seerr Integration's
Requests entry, with a mouse and on a touch screen; the month and week views, paging, download
progress, grouped seasons and details; time zones and phones; the settings page; and that nothing
about Sonarr or Radarr reaches the browser.

## Running

Needs Docker, the .NET 10 SDK and Node. No internet access is needed beyond pulling the images
and the pinned plugin releases.

```bash
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test
```

A run builds the plugin, starts Jellyfin and the stand-in, creates two users and connects the
Calendar to the stand-in, then runs the tests and removes the containers. About a minute once
the images are cached.

| Variable | Effect |
| --- | --- |
| `HARNESS_KEEP=1` | leave the containers running and print their addresses |
| `JELLYFIN_IMAGE` | another Jellyfin image (default `jellyfin/jellyfin:12.4`) |
| `NODE_IMAGE` | another image to run the stand-in in (default `node:26-alpine`) |
| `INJECTOR_ZIP_URL`, `SEERR_INTEGRATION_ZIP_URL` | other release archives |
| `INJECTOR_ZIP_SHA256`, `SEERR_INTEGRATION_ZIP_SHA256` | their SHA-256, required with another archive |

The default archives are pinned by SHA-256. Their DLLs are loaded into Jellyfin, so a release
asset that changes fails the run instead of being installed.

The users are `alice` and `bob`; passwords are `<name>-harness-pw`, and the admin is `admin` /
`harness-admin-pw`. Seerr Integration starts switched off and the tests that need it switch it on.
It runs without Seerr Proxy: its Requests entry is all the Calendar needs.

## The stand-in Sonarr and Radarr

`harness/fake-arr.mjs` answers the endpoints the plugin uses, in the shapes real Sonarr 4 and
Radarr 7 answer them, server paths included, with a schedule built around today: downloaded,
missing and upcoming episodes, one on air right now, a season released at once, a finale, movies
in cinemas, digital and physical, and downloads at 62% and 40%. Real instances would need internet
metadata and their schedules move with the real world; the shapes themselves are pinned by the C#
tests against answers recorded from real instances.

Its `/__log` endpoint lists every request it received, which the tests use to check each carried
its own service's key, and `/__fail?service=radarr&mode=unreachable` makes a service fail.

Downloads are cached in `.cache/` and the build goes to `.build/`, inside this folder rather than
the system temp folder: rootless Docker cannot see this process's `/tmp`.
