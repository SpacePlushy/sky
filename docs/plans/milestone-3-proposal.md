# Milestone 3 proposal: Dashboard

**Status:** Self-reviewed 2026-09-25, not approved by the owner, for the same reason as
Milestone 2: the owner asked for the project to be finished while they were away. A multi-agent
review stands in for approval, and its outcome is recorded at the end.
**Date:** 2026-09-25.

## Goal

A web dashboard over Milestones 1 and 2: a live world map with the ground track, a telemetry
panel, a 7-day pass table, and a polar sky plot. Dark mission-control look, usable on a phone.
One command runs it.

### Done when

- `dotnet test` and the web checks (type check, lint, unit tests, build) pass locally and in CI.
- The API's numbers are the orbital core's numbers: every endpoint is tested against the same
  references as the CLI.
- `docker compose up` serves the dashboard at `http://localhost:8080`.
- The dashboard works at phone width (375 px) and desktop width, checked with screenshots.
- Running the API and the CLI side by side on one machine cannot break CelesTrak's rules, and the
  Docker image never contacts CelesTrak.

## Decisions

| # | Decision | Choice | Main alternative |
|---|---|---|---|
| D1 | Settings shared by CLI and API | Move `SkySettings` into a small `Sky.Settings` library both reference | Duplicate it in the API |
| D2 | Map | Natural Earth 110 m land outlines (public domain, via the `world-atlas` package), drawn as SVG with `d3-geo`'s equirectangular projection. No tiles, no keys, works offline | Leaflet with OpenStreetMap tiles: free but rate-limited, needs attribution, and makes tests depend on a tile server |
| D3 | Frontend framework | None: TypeScript modules and SVG. The UI is four panels | React or Svelte: more dependencies for no gain here |
| D4 | Frontend unit tests | Vitest, for projection, track splitting, and time formatting | Only Playwright, which Milestone 4 adds for end to end |
| D5 | Live updates | The browser polls `/now` once a second; the track and passes are fetched on satellite change and every few minutes | Server-sent events: more moving parts for a local tool |
| D6 | Offline and test mode | `CelesTrak:Offline = true` serves the cache only and never requests. `Clock:StartUtc` starts the API's clock at a fixed instant, running at real speed. Tests, screenshots, and the Docker demo use both with the recorded fixture | A mock CelesTrak server |
| D7 | Two processes, one cache | A lock file in the cache folder serializes requests across processes, so the CLI and the API together still make at most one request per group per 2 hours | Document it and hope |
| D8 | Serving | The API serves the built frontend. Development runs Vite's server with a proxy to the API | Two containers |

## API

All times are UTC ISO 8601 with a `Z`. The observer's IANA zone comes from `/api/config`, and the
browser formats times in it with `Intl.DateTimeFormat`, the edge conversion CLAUDE.md asks for.

| Endpoint | Returns |
|---|---|
| `GET /api/config` | Observer name, position, IANA zone, minimum elevation, the satellites to offer |
| `GET /api/satellites` | Satellites in the cached groups: catalog number, name, group, epoch, age |
| `GET /api/satellites/{id}/now?at=` | Position, velocity, subpoint, look angles, sunlit, Sun elevation, subsolar point, elements epoch and age, data warnings |
| `GET /api/satellites/{id}/track?minutes=` | Ground track from one orbit back to one ahead, 30 s spacing, each point with sunlit |
| `GET /api/satellites/{id}/passes?days=7` | Passes with rise, peak, set, visible parts, and the sky-plot path every 10 s |
| `GET /api/health` | Status and the data's age |

Errors use RFC 9457 problem details: unknown satellite 404, no data 503 with the cache's warning,
bad query 400. Each request builds its own `Sgp4Propagator`, which is not thread-safe and costs
microseconds to build. Parsed element sets are cached in memory per group until the file changes.

## Frontend

- **Map.** Equirectangular world with graticule, land, the day side shaded from the subsolar
  point, the observer, the satellite, and its track: the past dimmer than the future, sunlit
  and shadowed stretches styled differently, split where it crosses the antimeridian. The
  footprint circle marks where the satellite is above the horizon.
- **Telemetry.** Local and UTC time, latitude, longitude, altitude, speed, azimuth, elevation,
  range, range rate, sunlit, Sun elevation, element age, and a countdown to the next pass.
- **Pass table.** 7 days: rise, peak, and set in the observer's zone, peak elevation, and the
  visible part. Visible passes stand out; a pass in progress says so. Selecting a row draws it
  on the sky plot.
- **Sky plot.** Polar: zenith at the center, horizon at the edge, north up, east left as seen
  looking up. The pass path, its visible part, rise and set markers, and the satellite's current
  position when a pass is in progress.
- **Look.** Near-black background, one accent color for live data and one for visible passes,
  tabular figures, and a layout that stacks to one column on a phone. Colors meet WCAG AA
  contrast.

## Verification

| What | Check |
|---|---|
| `/now` against Skyfield | Clock at 2026-09-24 04:00 UTC: position and look angles match the Skyfield reference within Milestone 1's pipeline bounds (plus the UT1 = UTC allowance already used by the CLI test) |
| `/passes` | Identical to `PassFinder` and `Visibility` for the same inputs, and within Milestone 2's bounds of Skyfield |
| `/track` | Each point equals the orbital core's subpoint at that instant; spacing and span as specified |
| Errors | 404, 400, and 503 cases return problem details; no stack traces |
| Offline mode | No request is ever made; an empty cache gives a 503 that says why |
| Cross-process lock | Two caches on one folder, released concurrently: one request, not two |
| Frontend math | Vitest: antimeridian splitting, the subsolar terminator, sky-plot coordinates (azimuth 90° plots east, which is left), time formatting in `America/Phoenix` and a DST zone |
| Look and layout | Screenshots at 375 px and 1440 px with the fixture and a fixed clock, reviewed by eye |

## Build order

1. `Sky.Settings`, moved with its tests; the CLI uses it.
2. Offline mode and the cross-process lock in `Sky.CelesTrak`, with tests.
3. `Sky.Api` with each endpoint and its tests.
4. The frontend skeleton: Vite, TypeScript strict, ESLint, Vitest, the API proxy.
5. Map, telemetry, pass table, sky plot, each with its unit tests.
6. Docker: a multi-stage build and `compose.yaml` with a cache volume.
7. CI: web checks and the Docker build.
8. Screenshots and docs.

## Out of scope

- Notifications, the hiring-manager README, and Playwright end-to-end tests: Milestone 4.
- Several satellites on the map at once. The selector switches between them.
- Accounts, persistence beyond the CelesTrak cache, and anything served beyond the local machine.

## Plan review

A multi-agent review (backend, frontend, and operations lenses, each finding checked by a skeptic)
confirmed 37 findings. These changed the build:

- **A simulated clock stays offline.** `Clock:StartUtc` now requires `CelesTrak:Offline`. Without
  that, a clock set to 2026 would write false instants into the shared request history that the
  2-hour rule depends on. Offline mode returns before any write and takes no lock.
- **The container never contacts CelesTrak.** A container's cache would be a second request
  history on the same public address, and file locks do not reliably cross into Docker's Linux VM.
  So the image is offline: by default it serves the recorded demo data on a clock started at the
  recording, and for live data it reads the host CLI's cache folder read-only. The CLI stays the one
  process that fetches. On a single host, the API run with `dotnet run` may fetch, coordinated by the
  lock.
- **Private settings stay out.** `.dockerignore` excludes `appsettings.Local.json`, the API never
  publishes it, API tests never read it (settings load lazily and the test host supplies its own),
  and CI plants a sentinel file and checks the image does not contain it. The port binds to
  127.0.0.1 only.
- **One clock.** The browser takes "now" from the API, whose clock can be simulated; every response
  states the instant it used.
- **Geometry by d3-geo.** The footprint (at the minimum elevation, with a faint horizon ring), the
  night side, and the track's antimeridian cuts are spherical shapes that d3-geo projects, not
  circles or lines drawn on the flat map.
- **Times** go out in UTC with a Z, truncated to the millisecond, and track and sky-path points are
  computed at exactly the time they report. Sky paths include each visible part's ends.
- **Satellite selection** follows the CLI's rule: the first configured group that holds the
  satellite, its newest epoch there.
- **The cache lock** waits at most 2 minutes, then serves cached data with a warning, and cache
  files are read with shared access so Windows cannot block a rename.
- **The base image** is Ubuntu noble, which includes tzdata, and the app runs as a non-root user.
- **WCAG contrast** is checked by a unit test over the palette, and color never carries meaning
  alone.
