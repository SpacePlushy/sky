# Sky Over Phoenix

A mission-control-style dashboard that tracks satellites in real time and
predicts visible passes over a ground observer. The default observer is
Phoenix, Arizona.

It pulls orbital elements from [CelesTrak](https://celestrak.org), propagates
orbits with SGP4, and shows where satellites are now, what's coming overhead
next, and when it's worth going outside to look.

> **Status:** Milestone 1 (orbital core) is complete and in review. It is a command-line
> tool; the dashboard arrives in Milestone 3.

## Try it

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test --solution Sky.slnx                        # every test runs offline
dotnet run --project src/Sky.Cli -- now                # where the ISS is right now
dotnet run --project src/Sky.Cli -- passes             # its next 5 passes over Phoenix
dotnet run --project src/Sky.Cli -- passes --sat 48274 --count 3 --min-elevation 30
```

The first run downloads the `stations` group from CelesTrak and caches it. Later runs reuse
the cache for 6 hours. Sky never requests the same data within 2 hours of its last request,
even across restarts or an interrupted run, and it stops and asks for a person if CelesTrak
answers with an error. That holds for one process at a time; two copies started at the same
moment against one cache could each make a request.

To use your own location, copy `src/Sky.Cli/appsettings.Local.example.json` to
`appsettings.Local.json` in the same folder and edit it. That file is gitignored. The time
zone must be an IANA name such as `America/Phoenix`. A misspelled setting name is an error,
so a typo cannot silently fall back to the Phoenix default.

## How the math is verified

Correctness is checked at every step, against sources that share no code with Sky:

- **SGP4** reproduces all 666 states in Vallado's published verification set within
  0.12 mm, and all 7 of its expected error codes.
- **Frame conversions** match the worked examples in Vallado's 2006 paper and textbook, and
  satisfy their defining properties across thousands of seeded random inputs.
- **The full pipeline** matches Skyfield, an independent Python library, to under a
  millimeter and 10⁻¹⁰ degrees for the ISS over Phoenix.
- **Pass predictions** match Skyfield's 25 passes over 7 days within the finder's stated
  bounds: rise and set within 10 s, and peaks within 0.1 s and an elevation uncertainty the
  finder computes for each pass. Milestone 2 replaces the 10 s rise and set grid with
  root-finding.

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
