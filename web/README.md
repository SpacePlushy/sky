# Sky Over Phoenix: web dashboard

The dashboard over `src/Sky.Api`: a world map with the ground track, live telemetry, a 7-day pass
table, a polar sky plot, and pass alerts (browser notifications and a calendar export). It uses
TypeScript and Vite with no UI framework: plain modules drawing SVG.

Everything the page needs is bundled: the Natural Earth land outlines (from `world-atlas`), code,
and styles. At runtime it makes no requests except to the API on its own origin. The built page
has a Content Security Policy that enforces this.

## Develop

Node 24 LTS or later. From this folder:

```bash
npm ci          # install the exact versions in package-lock.json
npm run dev     # http://localhost:5173, with /api proxied to http://localhost:5080
```

The dev server needs the API. For development, run the API **offline** on the committed demo
cache (`deploy/demo-cache`, the recorded CelesTrak response) with a simulated clock. It never
contacts CelesTrak, and in offline mode the cache only reads, so the committed file never changes.
From the repo root:

```bash
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec   # Homebrew .NET; already in ~/.zprofile
SKY_CelesTrak__Offline=true SKY_CelesTrak__Groups=stations \
SKY_CelesTrak__CacheDirectory=$PWD/deploy/demo-cache \
SKY_Clock__StartUtc=2026-09-24T04:00:00Z \
SKY_Observer__Name="Arizona State Capitol, Phoenix" SKY_Observer__LatitudeDegrees=33.4478 \
SKY_Observer__LongitudeDegrees=-112.0972 SKY_Observer__HeightMeters=331 \
SKY_Observer__TimeZone=America/Phoenix \
dotnet run --project src/Sky.Api --urls http://127.0.0.1:5080
```

To have the API serve the built page on its own origin, as the Docker image does, run
`npm run build` here first and add `ASPNETCORE_WEBROOT=$PWD/web/dist` to that command; the
dashboard is then at http://127.0.0.1:5080/. The end-to-end tests run the API exactly this way.

`?sat=<NORAD id>` in the URL picks the satellite; the selector keeps it up to date.

## Check

```bash
npm run typecheck    # tsc, strict, with noUncheckedIndexedAccess (the page, then e2e/ under Node)
npm run lint         # ESLint with typescript-eslint's strict type-checked rules; no warnings allowed
npm run test         # Vitest unit tests
npm run build        # type check, then build to dist/
npm run preview      # serve dist/ (with the same /api proxy) to check the built page
npm run e2e          # Playwright end-to-end tests in Chromium, against the API offline
npm run screenshots  # regenerate docs/images/dashboard-desktop.png and dashboard-mobile.png
```

`npm run e2e` and `npm run screenshots` need the .NET SDK (and `DOTNET_ROOT` exported, as above)
and Chromium for Playwright, installed once with `npx playwright install chromium` (on Linux CI,
`npx playwright install --with-deps chromium`). See [End-to-end tests](#end-to-end-tests).

## Pass alerts

Both kinds are opt-in and live in the pass panel.

- **Calendar file.** "Add visible passes to your calendar" downloads
  `/api/satellites/{id}/passes.ics?days=7&visibleOnly=true&alarm=10` for the selected satellite:
  one event per visible part, times in UTC, each with a display alarm 10 minutes before. Whether
  that alarm goes off depends on the calendar that imports the file: Apple Calendar and Outlook
  keep it, while Google Calendar ignores alarms in imported files and uses its own default
  notifications. The note beside the link says so.
  - The link follows the satellite selector; `calendarPath` in `api.ts` builds it and refuses
    values the API would reject.
  - It is offered only when the loaded pass list has a visible part (`hasVisiblePass` in
    `passes.ts`), because the API answers 404 with problem details when there is nothing to
    export. With the list loaded and nothing visible, a note says "No visible passes in the next 7
    days, so there is nothing to add to a calendar." While the list loads, or after its last
    request failed, neither shows.
- **Browser notifications.** "Turn on alerts" asks for notification permission (only when clicked)
  and then notifies 5, 10, or 15 minutes (default 10) before each upcoming pass of the selected
  satellite: at the start of its first visible part, or at its rise for a pass with none when
  "Visible passes only" is off (it is on by default). The notification names the satellite, says
  "visible in N min" or "rises in N min", and gives the start in the observer's zone, where to
  look, and how high it gets.
  - The alert time is the start minus the lead **on the server's clock** (`ServerClock`), never
    the browser's, so it is right with a simulated clock too. The plan is worked out again every
    second from the current pass list, so it follows every refresh and every change of satellite
    or options.
  - A page opened after an alert time but before its pass starts notifies once, at once. No pass
    notifies twice: shown alerts are remembered by satellite and start time, in memory and in
    `localStorage`, and a pass whose start or rise moved by under 2 minutes (new elements) counts
    as the same pass.
  - **Several open tabs show an alert once between them** (`notifier.ts`). Showing is serialized
    with the Web Locks API (`navigator.locks`, one exclusive lock): holding it, a tab re-reads the
    stored log, merges it, works out again what is due, logs that, **writes the log, then shows**.
    The next tab to get the lock finds it logged. A cheap check without the lock comes first, so
    the lock is requested only when something looks due; a browser without Web Locks takes the
    same steps unlocked. The notification tag is the satellite and the minute the start falls in
    (`25544@2026-09-25T03:07Z`), the same in every tab even when their lists put the start 1 ms
    apart, so a copy that still gets through replaces the first instead of stacking.
  - A restarted simulated clock replays the same passes. A logged alert whose start is more than
    17 minutes ahead (the longest lead plus the 2-minute tolerance) can only come from a clock
    that went backwards, so it is forgotten, in `localStorage` too, and the replayed pass alerts
    again.
  - The choices (on or off, lead, visible only) are kept in `localStorage` and shared by every open
    tab: a change in one reaches the others through the `storage` event, and each check reads them
    again. Every storage access is guarded; without storage the page still works and remembers
    for the visit only.
  - Notifications come from the page, so they **only work while a dashboard tab is open**, and the
    control says so. A background tab may run the page's timers only once a minute, so an alert
    can be up to a minute late there. The state reads On, Off, Blocked (permission denied: allow it
    in the site settings), or Unavailable: no Notification API, not a secure context (the page must
    be on HTTPS or localhost), or a Notification constructor that throws `TypeError`, as Chrome for
    Android's does (pages there may notify only through a service worker). That last case is
    found at the first alert, which is then not logged as shown.
  - While the browser asks for permission the button is `aria-disabled`, not disabled, so keyboard
    focus stays on it.

## How it works

| Module | Role |
|---|---|
| `api.ts`, `model.ts` | The API client. Responses are checked field by field and their UTC instants become milliseconds. Problem details become `ApiError` with a title and detail |
| `time.ts` | UTC parsing, formatting in the observer's IANA zone with `Intl.DateTimeFormat`, and `ServerClock` |
| `geo.ts`, `track.ts`, `map.ts` | The map: night side, footprints, and ground track as spherical GeoJSON that `d3-geo` projects and cuts at the antimeridian |
| `passes.ts`, `passlist.ts`, `skyplot.ts` | Pass status, selection by rise time, visible-part text, the pass list, and the sky plot |
| `telemetry.ts` | The telemetry panel and countdowns |
| `alerts.ts` | Pass-alert rules, pure: which event each pass alerts for, alert times, the shown-alert log, notification text and tag |
| `notifier.ts` | Showing due alerts once across tabs: the Web Locks protocol, write before show, the unusable-constructor case. Storage, `Notification`, and the locks are passed in |
| `alertcontrol.ts`, `storage.ts` | The alert control, permission, and choices shared across tabs; `localStorage` access that never throws |
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
| `api.test.ts` | Problem-details parsing, network errors, cancellation, contract violations; the calendar link's path and its limits |
| `alerts.test.ts` | Which events alert (visible start, rise, visible only); alert times from the given server time and lead, whatever the browser's clock says; never twice, across a reload and when new elements move a pass; entries more than 17 minutes ahead forgotten, so a replayed pass alerts again; the late-open case; rescheduling when the passes, options, or satellite change; the notification text in Phoenix time and its minute tag; stored preferences |
| `notifier.test.ts` | With stub storage, locks, and `Notification`: two tabs due at once show one notification, made inside the lock after the log was stored; the lock only when something looks due; the fallbacks without Web Locks or when the lock is refused; one tag for starts 1 ms apart; a constructor that throws `TypeError` makes alerts unavailable and logs nothing; a restarted clock's stale entry is forgotten in storage and the pass alerts again |
| `passes.test.ts`, `format.test.ts` | Default and kept selection, visible-part text, whether there is anything for the calendar, compass points, number formatting |

## End-to-end tests

`npm run e2e` runs Playwright (`@playwright/test`, Chromium only, its new headless mode) against
the real API, offline. `playwright.config.ts` starts two API servers from the repo root, each with
the committed demo cache, `CelesTrak:Offline`, the public Capitol observer, and
`ASPNETCORE_WEBROOT=web/dist`, so the built page and the API share an origin as in Docker:

| Server | Clock starts (UTC) | Used for |
|---|---|---|
| `127.0.0.1:5095` | 2026-09-24 04:00:00 | Everything else, and the screenshots |
| `127.0.0.1:5096` | 2026-09-25 02:51:00 | Notifications: 16 min 26 s before the first evening visible ISS pass (03:07:26 UTC), inside the 17 minutes within which a shown alert's start never looks like a replay |

The first server's command builds the dashboard and the API; the second starts after it and reuses
the build. Locally, a server already listening on either port is reused (it must serve a current
build), and on CI both always start fresh. Tests are not retried: a flaky test is a bug.

| File | Checks |
|---|---|
| `fixtures.ts` | Around every test: no request to any host but 127.0.0.1, and no uncaught page error |
| `dashboard.spec.ts` | Offline and simulated-clock notices; telemetry latitude, longitude, and elevation match `/now` within what 2 s of motion allows (measured with `/now?at=`) plus half the last digit; the pass list matches `/passes` row by row (Phoenix date and rise, visible ones marked in text); selecting by Enter, Tab and Space, and click updates the sky plot, and the choice and keyboard focus survive a refresh forced with `page.clock` in which every rise moved 1 ms; the map and sky plot have accessible names and Tab reaches the controls and pass rows. Rows are addressed by rise (`data-rise`), never by position, using passes that rise more than 10 minutes after the server's now: the clock starts inside a pass that ends at 04:01:03, and the page drops that row then |
| `zones.spec.ts` | In a browser set to `Asia/Tokyo` and `de-DE`, the clock, pass list, and caption are in `America/Phoenix` |
| `phone.spec.ts` | At 375 x 812 nothing scrolls sideways and passes are two-column cards |
| `alerts.spec.ts` | With `window.Notification` replaced by a recorder: permission is asked once on the first click; with a 15-minute lead, nothing 3 s before the alert time and exactly one notification 5 s after (satellite name, Phoenix start time, minute tag), none again after more ticks, a pass refresh, and a reload; Blocked when permission is denied; the button keeps keyboard focus (and is `aria-disabled`) while permission is pending; turning alerts on and off, the lead, and visible only in one tab reach another open tab |
| `calendar.spec.ts` | The link downloads `sky-25544-passes.ics`: CRLF lines of at most 75 octets, `BEGIN:VCALENDAR`, one `VEVENT` per visible part in `/passes`, each with a UTC `DTSTART` matching its part and a 10-minute `VALARM`; the alarm note names the calendars that keep it; the link follows the selected satellite; it is hidden while the pass list loads and after it fails; for a satellite with no visible pass (the API answers 404) the "nothing to add" note shows instead |

Pass times are refined to 1 ms, so the page's request and the test's can put an event 1 ms apart
(2 of 26 rises did, measured). Comparisons of rounded times therefore accept ±2 ms around the
API's instant, which changes the shown second only when that straddles a half second.

### Screenshots

`npm run screenshots` runs `e2e/screenshots.ts` against the 5095 server and writes
`docs/images/dashboard-desktop.png` (1440 x 900 viewport, full page) and `dashboard-mobile.png`
(375 x 812 at 2x, first screen only) once the data has loaded. It first reads `/api/config` and
stops, writing nothing, unless the observer is "Arizona State Capitol, Phoenix" at 33.4478,
-112.0972, so a real location from a local settings file can never end up in a committed image.
