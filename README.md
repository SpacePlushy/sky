# Sky Over Phoenix

A mission-control-style dashboard that tracks satellites in real time and
predicts visible passes over a ground observer. The default observer is
Phoenix, Arizona.

It pulls orbital elements from [CelesTrak](https://celestrak.org), propagates
orbits with SGP4, and shows where satellites are now, what's coming overhead
next, and when it's worth going outside to look.

> **Status:** Milestones 1 (orbital core), 2 (pass prediction and visibility), and 3 (dashboard)
> are built and in review.

## Try it

**The dashboard, with one command** (needs Docker):

```bash
docker compose up --build        # then open http://localhost:8080
```

This shows recorded data from 2026-09-24 on a clock started then, with no network access. For live
data, fetch it with the CLI first, then point the container at the CLI's cache (it only reads it):

```bash
dotnet run --project src/Sky.Cli -- passes
SKY_CACHE_DIR="$HOME/Library/Application Support/sky/celestrak" SKY_CLOCK_START= docker compose up --build
```

**The command line** (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```bash
dotnet test --solution Sky.slnx                        # every test runs offline
dotnet run --project src/Sky.Cli -- now                # where the ISS is right now
dotnet run --project src/Sky.Cli -- passes             # its next 5 passes over Phoenix
dotnet run --project src/Sky.Cli -- passes --visible   # only the passes you can see
dotnet run --project src/Sky.Cli -- passes --sat 48274 --count 3 --min-elevation 30
```

**The dashboard without Docker**, for development: run the API (`dotnet run --project src/Sky.Api
--urls http://localhost:5080`) and the web app (`npm run dev` in `web/`); see `web/README.md`.

The CLI's first run downloads the `stations` group from CelesTrak and caches it. Later runs reuse
the cache for 6 hours. Sky never requests the same data within 2 hours of its last request, even
across restarts, an interrupted run, or the CLI and the API running side by side on one machine,
and it stops and asks for a person if CelesTrak answers with an error. The Docker image never
contacts CelesTrak at all.

To use your own location, copy `src/Sky.Cli/appsettings.Local.example.json` to
`appsettings.Local.json` in the same folder and edit it; the API reads the same file. It is
gitignored and never goes into the Docker image; for the container, put `SKY_Observer__*` variables
in a gitignored `.env.observer` file. The time zone must be an IANA name such as `America/Phoenix`.
A misspelled setting name is an error, so a typo cannot silently fall back to the Phoenix default.

## How the math is verified

Correctness is checked at every step, against sources that share no code with Sky:

- **SGP4** reproduces all 666 states in Vallado's published verification set within
  0.12 mm, and all 7 of its expected error codes.
- **Frame conversions** match the worked examples in Vallado's 2006 paper and textbook, and
  satisfy their defining properties across thousands of seeded random inputs.
- **The full pipeline** matches Skyfield, an independent Python library, to under a
  millimeter and 10⁻¹⁰ degrees for the ISS over Phoenix.
- **Pass predictions** match Skyfield's 25 passes over 7 days: rise and set within 0.23 ms and
  peaks within 0.07 ms, found by Brent's method. Heavens-Above, run on the same element set,
  agrees to within a second.
- **Visibility** (satellite sunlit, Sun below −6°) uses a Sun that matches JPL's DE421 ephemeris
  to 0.003° and an Earth's shadow on the WGS-84 ellipsoid. Shadow entry and exit match an
  independent reference within 16 ms over a week, and civil twilight matches the U.S. Naval
  Observatory's published times to the minute.

These figures measure Sky's implementation of SGP4, not SGP4's physics. SGP4 itself is
accurate to about a kilometer at the element set's epoch, and degrades by kilometers per day
as the elements age.

Every tolerance was set from analysis before its test ran. Tests run on Linux, macOS, and
Windows on every push, and weekly. Details, measured results, and the issues this process
found in reference sources are in [docs/verification.md](docs/verification.md).

## Stack

- **Backend:** .NET 10, ASP.NET Core minimal API
- **Frontend:** TypeScript + Vite, a 2D world map for ground tracks, and a
  polar sky plot for passes
- **Tests:** xUnit for orbital math and services, Playwright for end-to-end
- **CI:** GitHub Actions
- **Run:** Docker Compose, one command

## Roadmap

1. **Orbital core.** Fetch GP data, SGP4 propagation, coordinate transforms,
   validation against Vallado's published test cases.
2. **Pass prediction.** Rise, culmination, and set times refined by
   root-finding, plus visibility flags.
3. **Dashboard.** Live map, telemetry panel, 7-day pass table, sky plot.
4. **Alerts and polish.** Optional pass notifications, full README, E2E tests.

## Credits

- **SGP4:** D. A. Vallado, P. Crawford, R. Hujsak, and T. S. Kelso, "Revisiting Spacetrack
  Report #3," AIAA 2006-6753, 2006. Code and verification data from
  https://celestrak.org/publications/AIAA/2006-6753/. See `src/Sky.Sgp4/NOTICE.md`.
- **Orbital data:** [CelesTrak](https://celestrak.org), used within its
  [usage policy](https://celestrak.org/usage-policy.php).
- **Reference output** `tcppver.out` from [python-sgp4](https://github.com/brandon-rhodes/python-sgp4) (MIT).
- **Independent cross-check:** [Skyfield](https://rhodesmill.org/skyfield/) (MIT).

## Why I built this

_TODO: Frankie to write._

## License

[MIT](LICENSE)
