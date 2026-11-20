# Contributing to *jellyfin-plugin-arr-calendar*

Issues and pull requests are welcome!

## Getting Started

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Submit a pull request

## Building

```bash
dotnet build --configuration Release
```

The Calendar page is `Web/arr-calendar.js` and the JavaScript Injector loader is
`Browser/bootstrap.js`. Both are embedded in the DLL, so a change to either needs a rebuild to
reach a server. There is no JavaScript build step.

## Testing

Three layers, all run by CI. The C# tests live outside the plugin project's directory so that
`dotnet build` at the repository root still resolves to the plugin alone, so give them an explicit
path:

```bash
dotnet test tests/Jellyfin.Plugin.ArrCalendar.Tests --configuration Release
node --test tests/web/
```

The C# tests cover the HTTP client (including real TLS handshakes for the certificate rule), the
mapping from Sonarr's and Radarr's answers, the service's caching and failure handling, artwork
signing, the library lookup and every endpoint. The mapper's fixtures in
`tests/Jellyfin.Plugin.ArrCalendar.Tests/Fixtures` are answers recorded from real Sonarr 4.0.20
and Radarr 7.0.3 instances, trimmed; the queue fixtures are built from the services' documented
shape. To refresh them, add a few series and movies to throwaway instances and save the answers to
the calls listed in [docs/DESIGN.md](docs/DESIGN.md#sonarr-and-radarr-calls).

`tests/web` loads the page script into a sandbox and checks its model: dates and weeks, routes,
statuses, grouping, artwork paths and the API client. It runs in New York time, so time zone
handling is exercised. It needs nothing but Node.

`tests/e2e` drives the page in a real Jellyfin with JavaScript Injector and Seerr Integration,
next to a stand-in Sonarr and Radarr. It is the only test of where navigation lands, with and
without Seerr Integration, of the page inside Jellyfin Web, and of what reaches the browser. Run it
after any change under `Web/`, `Browser/`, `Api/`, `Arr/` or `Configuration/`; see
[tests/e2e/README.md](tests/e2e/README.md). CI runs it for those paths too.

The rules in [docs/DESIGN.md](docs/DESIGN.md#what-never-reaches-a-browser) are requirements. A
change that breaks one of them is a bug even if every test passes.

## Coverage

The coverage badge in the README is a **manually updated** number, measured the same way as the
other plugins in this family: coverlet's overall line rate of a Release test run, with no
exclusions. It covers the C# only. Refresh it in the same commit as any change that moves it
meaningfully.

```bash
dotnet test tests/Jellyfin.Plugin.ArrCalendar.Tests \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --results-directory /tmp/arr-calendar-coverage
```

The badge number is the top-level `line-rate` attribute of the `coverage.cobertura.xml` written
under that directory, as a percentage, rounded to the nearest whole number:

```bash
python3 - <<'EOF'
import glob, xml.etree.ElementTree as ET
report = glob.glob("/tmp/arr-calendar-coverage/**/coverage.cobertura.xml", recursive=True)[0]
root = ET.parse(report).getroot()
rate = float(root.get("line-rate")) * 100
print("line-rate %.2f%% (%s/%s lines) -> badge %d%%" % (
    rate, root.get("lines-covered"), root.get("lines-valid"), round(rate)))
EOF
```

Badge colours: `brightgreen` at or above 95, `green` under 95, `yellowgreen` under 85, `yellow`
under 75, `orange` under 60.

## Plugin GUID Safety

Before the first release, and any time plugin metadata changes, verify that the plugin GUID is
consistent and not already used by a known catalog:

```bash
bash scripts/verify-plugin-guid.sh
CHECK_REMOTE_GUIDS=1 bash scripts/verify-plugin-guid.sh
```

Do not copy GUIDs from Jellyfin's plugin template or from another plugin repository.

## Linting

Run lint checks locally before opening a PR:

```bash
dotnet format whitespace --verify-no-changes
dotnet format style --verify-no-changes --severity warn
dotnet format tests/Jellyfin.Plugin.ArrCalendar.Tests/Jellyfin.Plugin.ArrCalendar.Tests.csproj whitespace --verify-no-changes
dotnet format tests/Jellyfin.Plugin.ArrCalendar.Tests/Jellyfin.Plugin.ArrCalendar.Tests.csproj style --verify-no-changes --severity warn
node --check Web/arr-calendar.js
```

## Reporting Issues

- Search existing issues before opening a new one
- Include the Jellyfin version, the display mode (Settings -> Display), and the browser or app
- Include the Sonarr and Radarr versions, and the JavaScript Injector version
- The settings page's status lines, the Test connection results and any browser console errors
  mentioning Arr Calendar help

## Rules

- Keep branches, commits, and PRs focused. Do not mix unrelated local changes into the same PR.
- Use semantic names by default.

## Naming

- Branches: `fix/<scope>-<summary>`, `feat/<scope>-<summary>`, `refactor/<scope>-<summary>`
- Commits: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`
- PR titles: `fix(scope): summary`, `feat(scope): summary`, `refactor(scope): summary`

## Pull Requests

- Keep changes focused and minimal
- Test against a running Jellyfin instance before submitting
- Describe what your PR changes and why
- Merging to `main` does not publish anything by itself. Add the `release` label before merging to cut a new tag and release; unlabelled changes go out with the next labelled merge

## Release Metadata

- `manifest.json` is updated by the release workflow when a new version is published.

## LLM Disclosure

This project uses LLM-assisted development. Contributions generated with AI assistance are
welcome, but please review and test all code before submitting.
