# Sky Over Phoenix: web dashboard

The dashboard over `src/Sky.Api`: a world map with the ground track, live telemetry, a 7-day pass
table, and a polar sky plot. It uses TypeScript and Vite with no UI framework: plain modules
drawing SVG.

Everything the page needs is bundled: the Natural Earth land outlines (from `world-atlas`), code,
and styles. At runtime it makes no requests except to the API on its own origin. The built page
has a Content Security Policy that enforces this.

## Develop

Node 24 LTS or later. From this folder:

```bash
npm ci          # install the exact versions in package-lock.json
npm run dev     # http://localhost:5173, with /api proxied to http://localhost:5080
```

The dev server needs the API. For development and screenshots, run the API **offline** on the
recorded fixture with a simulated clock. It never contacts CelesTrak. From the repo root:

```bash
C=$(mktemp -d); cp tests/Sky.CelesTrak.Tests/Fixtures/stations-2026-09-24.json $C/stations.json
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
SKY_CelesTrak__Offline=true SKY_CelesTrak__Groups=stations SKY_CelesTrak__CacheDirectory=$C \
SKY_Clock__StartUtc=2026-09-24T04:00:00Z \
SKY_Observer__Name="Arizona State Capitol, Phoenix" SKY_Observer__LatitudeDegrees=33.4478 \
SKY_Observer__LongitudeDegrees=-112.0972 SKY_Observer__HeightMeters=331 \
SKY_Observer__TimeZone=America/Phoenix \
dotnet run --project src/Sky.Api --urls http://localhost:5080
```

`?sat=<NORAD id>` in the URL picks the satellite; the selector keeps it up to date.

## Check

```bash
npm run typecheck   # tsc, strict, with noUncheckedIndexedAccess
npm run lint        # ESLint with typescript-eslint's strict type-checked rules; no warnings allowed
npm run test        # Vitest unit tests
npm run build       # type check, then build to dist/
npm run preview     # serve dist/ (with the same /api proxy) to check the built page
```

## How it works

| Module | Role |
|---|---|
| `api.ts`, `model.ts` | The API client. Responses are checked field by field and their UTC instants become milliseconds. Problem details become `ApiError` with a title and detail |
| `time.ts` | UTC parsing, formatting in the observer's IANA zone with `Intl.DateTimeFormat`, and `ServerClock` |
| `geo.ts`, `track.ts`, `map.ts` | The map: night side, footprints, and ground track as spherical GeoJSON that `d3-geo` projects and cuts at the antimeridian |
| `passes.ts`, `passlist.ts`, `skyplot.ts` | Pass status, selection by rise time, visible-part text, the pass list, and the sky plot |
| `telemetry.ts` | The telemetry panel and countdowns |
| `palette.ts`, `contrast.ts` | The colors (the same as the custom properties in `styles.css`) and the WCAG contrast formula |
| `app.ts` | Polling, refresh schedule, satellite switching, banners |

- **One clock.** "Now" is the server's clock, which may be simulated. `ServerClock` estimates the
  offset from each response's timestamp and the request's round trip. The browser's own time is
  used only to measure elapsed time.
- **Time zones.** Every instant is UTC inside the page. Times are shown in the observer's zone
  (`America/Phoenix` by default) through `Intl.DateTimeFormat`, never with a fixed offset. When
  the offset changes during the listed passes (daylight saving time in other zones), each time
  shows its offset.
- **Polling.** `/now` is polled every second. A poll is skipped while the previous one is in
  flight or the tab is hidden. The track is refreshed every 2 minutes and passes every 5.
  Switching satellites aborts every request for the old one, and late responses are ignored.
- **Geometry.** The night side is `d3.geoCircle` with radius 90° around the antisolar point; a
  darker circle of radius 84° marks where the Sun is more than 6° down. Footprints are
  `d3.geoCircle`s with the API's radii. The track is GeoJSON LineStrings that `d3.geoPath` cuts
  at ±180°. Nothing is drawn as a flat circle or split by hand.
- **Sky plot.** Zenith at the center, horizon at the rim, r = R(90 − el)/90, north up and east
  on the left, as seen lying on your back.
- **Accessibility.** Colors meet WCAG AA, which a test checks. Every colored state also has a
  text, dash, shape, or width cue. The SVGs have titles. Pass rows are buttons. Motion stops
  under `prefers-reduced-motion`.

## Tests

The unit tests derive their expected values independently of the code under test: hand-computed
epoch arithmetic and US daylight saving rules, the spherical direct formula for points at a
given arc distance, and published WCAG contrast values.

| File | Covers |
|---|---|
| `time.test.ts` | Parsing; `America/Phoenix` all year; `America/Denver` across the 2026-03-08 and 2026-11-01 changes; observer-local dates near midnight; countdowns; server-clock offset math |
| `skyplot.test.ts` | Azimuth 0/90/180/270 plot up/left/down/right; elevation 90 at the center, 0 on the rim, 30 at two thirds |
| `track.test.ts` | Splitting at now and at sunlit changes keeps every point in order and shares boundaries (with seeded random tracks); d3 cuts a line at the antimeridian |
| `geo.test.ts` | Night: subsolar point is day, antisolar is night, 89° is day, 91° is night; footprint vertices lie at the footprint radius within 1e-6 rad |
| `palette.test.ts` | The WCAG formula against published values; every declared color pair meets its ratio; `palette.ts` matches `styles.css` |
| `api.test.ts` | Problem-details parsing, network errors, cancellation, contract violations |
| `passes.test.ts`, `format.test.ts` | Default and kept selection, visible-part text, compass points, number formatting |
