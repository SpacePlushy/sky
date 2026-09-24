# Milestone 1 proposal: Orbital core

**Status:** Approved 2026-09-24.
**Date:** 2026-09-24. All external facts below were checked on this date.

## Goal

Build the part of the system that answers one question: where is a satellite,
as seen from the observer, at a given instant? That covers everything from
CelesTrak data to azimuth and elevation. It also includes a command-line tool
that lets you check the answers by hand.

### Done when

- `dotnet test` passes locally and in GitHub Actions.
- Every row of the verification table below passes at its stated tolerance.
- `sky now` prints the ISS position and look angles from the observer.
- `sky passes` prints the next 5 ISS passes, with times in `America/Phoenix`.
- You compare `sky passes` with Heavens-Above or N2YO, and rise and set agree
  within about 30 seconds. Peak elevation agrees within about 1°.
- `docs/verification.md` lists every assumption, its size, and how it is checked.

## Decisions for you

Each one is explained in its section below. Say which ones to change.

| # | Decision | My recommendation | Main alternative |
|---|---|---|---|
| D1 | SGP4 implementation | Adapt Vallado's reference code into its own project. | SGP.NET from NuGet. It uses WGS-84 constants, so it cannot pass Vallado's test cases. |
| D2 | SGP4 operation mode | Improved mode `'i'`. It matches the reference output, python-sgp4, and Skyfield. | Mode `'a'`, which CelesTrak's FAQ suggests. Both give identical results for low-orbit satellites like the ISS. |
| D3 | Pass finding in this milestone | Coarse 10-second steps. Root-finding comes in Milestone 2. | Build root-finding now. |
| D4 | Docker | Wait until Milestone 3, when there is a server to run. | Put the CLI in a container now. |
| D5 | Independent reference | A Python script using Skyfield generates golden test data. Its output is committed, so `dotnet test` never needs Python. | Check only against published examples. |
| D6 | Default observer | Arizona State Capitol, 33.4478° N, 112.0972° W, 331 m. | Any other public Phoenix landmark. |
| D7 | Downloads | Three, listed in the SGP4 section. Approving this plan approves them. | None. |

## SGP4: adapt Vallado's reference code (D1, D2)

The brief allows "a well-established library or port of Vallado's reference
implementation." No .NET library meets the bar, so this is the port option. The
reasoning also becomes `docs/adr/0001-sgp4-implementation.md`.

### No existing .NET package fits

| Package | Why it doesn't fit |
|---|---|
| SGP.NET 1.6.0 | Active, MIT, and reads OMM directly. But it hard-codes WGS-84 constants and has no opsmode switch, so it cannot match Vallado's verification set. |
| One_Sgp4 1.1.0 | Stale since early 2024. OMM input is XML only and appears to mishandle the epoch year. It computes SGP4 error codes but never reports them. |
| Zeptomoby OrbitTools | The free edition is based on the 1980 report and is licensed for non-commercial use only. |
| IO.Astrodynamics | Wraps NASA's SPICE toolkit. It needs native binaries and data files, and OMM must be converted to TLE first. |
| Vallado's C# on GitHub | The reference code itself, but AGPL-licensed since 2025-02-01. It also targets .NET Framework 4.8 with Windows Forms. |

WGS-84 rules out SGP.NET. SGP4 elements are fitted with WGS-72 constants, so a
propagator using WGS-84 is wrong by design, not just imprecise.

### The approach

CelesTrak hosts the code package for Vallado's 2006 paper, "Revisiting
Spacetrack Report #3." It is dated 2023-05-10 and includes C#, C++, and other
versions. Its FAQ says "there is no license associated with the code" and asks
only for a citation. That is compatible with this MIT repo. python-sgp4, the
most widely used SGP4 library, is based on the same release.

1. Download the package and record its SHA-256 checksum.
2. Commit the upstream C# file into `src/Sky.Sgp4` exactly as downloaded.
3. In a separate commit, remove everything that isn't propagation math. The diff
   between the two commits shows exactly what changed. If the C# can't be cleanly
   separated from Windows dependencies, port the C++ version instead.
4. The math itself is never edited. The project keeps upstream names and is
   excluded from formatting rules, so it stays comparable with the original.
5. `NOTICE.md` in the project gives the citation, source URL, package date,
   checksum, and a list of changes.

`sgp4init` takes mean elements directly, so OMM data goes in without a TLE text
step. Sky.Orbital wraps it in a small typed API. That API returns a TEME state,
or a named error such as `Decayed` in place of Vallado's numeric codes 1 to 6.

**Operation mode.** The reference output was generated in improved mode `'i'`
with WGS-72. python-sgp4 and Skyfield also use `'i'`. The mode only changes
deep-space calculations, which apply to orbits longer than 225 minutes. A test
confirms the ISS result is identical in both modes.

### Downloads this plan needs (D7)

| File | Source | Size | Why |
|---|---|---|---|
| `AIAA-2006-6753.zip` | celestrak.org/publications/AIAA/2006-6753 | about 1.2 MB | SGP4 source and `SGP4-VER.TLE` |
| `tcppver.out` | brandon-rhodes/python-sgp4 on GitHub, MIT | about 140 KB | The C++ reference output. The current zip no longer includes it. |
| Skyfield and its dependencies | PyPI, through `uv` | a few MB | Generating golden data. Not needed to build or test. |

Nothing is copied from the AGPL GitHub repository.

## Data source: CelesTrak

The rules tightened during 2026, so the client is designed around them.

### What CelesTrak requires

- **GP data updates once every 2 hours.** Download each dataset at most once per
  update. Since 2026-03-26, a repeat download inside the window returns HTTP 403.
  Enforcement started with the `active` and `starlink` groups and is spreading.
- **Errors get you firewalled.** Fifty 301, 403, or 404 responses within 2 hours
  block the client's IP address. Automated clients must stop on any non-200
  response and tell a human.
- **Use `https://celestrak.org` exactly.** The `.com` domain returns a 301, which
  counts toward the error limit.
- **Always send `FORMAT=JSON`.** The default format changed to CSV on 2026-05-09.
- **There are no HTTP caching headers.** Responses carry no `Last-Modified`,
  `ETag`, or `Cache-Control`. We must record fetch times ourselves.

### Why OMM and not TLE

The 5-digit catalog numbers ran out on 2026-07-11. New objects get 6-digit
numbers, which the TLE format cannot hold. The `stations` group already contains
two such objects. OMM JSON handles any catalog number, so it is the only input
path for live data. The TLE parser exists only to read Vallado's test files.

### Queries

```text
https://celestrak.org/NORAD/elements/gp.php?GROUP=stations&FORMAT=JSON
https://celestrak.org/NORAD/elements/gp.php?GROUP=visual&FORMAT=JSON
```

The `stations` group covers the ISS, Tiangong, and visiting vehicles. The
`visual` group holds roughly the 100 brightest objects. These two downloads cover
every satellite the project needs.

### Parsing details

- `EPOCH` looks like `2026-09-24T03:24:21.452544`, with no `Z`. It is UTC and is
  parsed as UTC explicitly.
- Numbers switch between integer and decimal forms. All orbital fields are read
  as `double` and IDs as `long`.
- `OBJECT_NAME` can be blank for some analyst objects.
- A 403 body is plain text. A response is accepted only if the status is 200,
  the body starts with `[`, and it parses.

### Cache policy

| Rule | Value |
|---|---|
| Storage | One file per group in a gitignored cache directory. It holds the raw JSON, the UTC fetch time, and the newest element epoch. |
| Minimum refresh interval | 2 hours from the stored timestamp. It holds across restarts and debug runs. |
| Normal refresh | On demand, once data is older than 6 hours. Upstream changes only 2 or 3 times a day. |
| Writes | Write a temporary file, then rename it. A bad response never replaces good data. |
| Redirects | Disabled, so a 301 shows up as an error instead of being followed. |
| Any non-200 response | Stop fetching that group, keep serving the cache, and show the response body. Never retry a 403 or 404 automatically. |
| 5xx or network error | Wait for the next 2-hour window, then back off exponentially up to 24 hours. |
| Staleness warning | Shown when the newest epoch is more than 3 days old. |

Each rule gets a unit test with a fake clock and a fake HTTP handler. No test
touches the network. A real `stations` response captured during this research
becomes the parsing fixture.

## Repo structure

Only what Milestone 1 needs. The API, web app, and Docker files arrive with the
milestones that use them.

```text
sky/
├── Sky.slnx                      # .NET 10 default solution format
├── global.json                   # pins SDK 10.0.x
├── Directory.Build.props         # nullable, warnings as errors, analyzers
├── Directory.Packages.props      # central package versions
├── .editorconfig                 # style rules, enforced by `dotnet format`
├── .github/workflows/ci.yml      # build, format check, test
├── src/
│   ├── Sky.Sgp4/                 # Vallado's reference SGP4, with NOTICE.md
│   ├── Sky.Orbital/              # pure math: no I/O, no network, no clock
│   │   ├── Time/                 #   Julian dates, GMST
│   │   ├── Elements/             #   mean-element model, TLE parser
│   │   ├── Propagation/          #   typed wrapper over Sky.Sgp4
│   │   ├── Frames/               #   TEME to ECEF, geodetic, topocentric
│   │   └── Passes/               #   coarse pass finder
│   ├── Sky.CelesTrak/            # GP download, OMM parsing, disk cache
│   └── Sky.Cli/                  # `sky now`, `sky passes`
├── tests/
│   ├── Sky.Orbital.Tests/
│   │   └── Data/                 #   Vallado files, Skyfield golden data
│   └── Sky.CelesTrak.Tests/
│       └── Fixtures/             #   recorded CelesTrak responses
├── tools/reference/              # Python script that regenerates golden data
└── docs/
    ├── verification.md           # what is checked, against what, how closely
    ├── adr/                      # one short record per significant decision
    └── plans/                    # this file
```

### Why four projects

- **Sky.Sgp4** is third-party reference code with its own provenance. A separate
  project keeps a clear line between Vallado's math and our code.
- **Sky.Orbital** is pure functions over numbers and times. It has no network,
  file, or clock access, so every result is reproducible in a test. The API in
  Milestone 3 reuses it unchanged.
- **Sky.CelesTrak** owns the outside world: HTTP, rate limits, disk cache, and
  JSON. It turns CelesTrak records into Sky.Orbital's element type. Nothing else
  sees CelesTrak's format.
- **Sky.Cli** reads config, wires the other projects together, and prints results.
  It holds no logic worth testing on its own.

## Coordinate pipeline and assumptions

SGP4 outputs position and velocity in the TEME frame. The chain to what you see
in the sky is:

```text
Mean elements ─SGP4─▶ TEME ─rotate by GMST─▶ ECEF ─┬─▶ geodetic lat, lon, alt
                                                   └─▶ topocentric az, el, range, range rate
```

Every simplification is listed with its effect. These rows also go into
`docs/verification.md`.

| # | Assumption | Effect on results | How it is checked |
|---|---|---|---|
| A1 | SGP4 uses WGS-72 constants. | None. The elements were fitted with WGS-72, so this is required. | Vallado verification set |
| A2 | SGP4 runs in improved mode `'i'`. | None for the ISS. The mode only affects deep-space orbits. | A test that compares both modes |
| A3 | TEME to ECEF is a single GMST rotation (IAU-82), as in Vallado 2006. | This is the standard method for SGP4 output. | Vallado's worked example |
| A4 | Polar motion is ignored. | About 12 m today. That is far below SGP4's own error. | Vallado's example includes polar motion, so a test bounds the difference. |
| A5 | UT1 is taken as UTC. | The difference is about −0.015 s today, which is about 7 m. By definition it never exceeds 0.9 s, which would be about 450 m. | Skyfield cross-check, run both with and without UT1 |
| A6 | Geodetic output uses the WGS-84 ellipsoid. | None. This is the GPS and mapping standard. | Vallado's worked example and round-trip tests |
| A7 | Observer height is treated as ellipsoid height. Published elevations are above sea level, and in Phoenix the two differ by about 30 m. | About 0.004° in elevation for a low-orbit pass. | Documented in the config file |
| A8 | Elevation is geometric, with no atmospheric refraction. | Refraction lifts objects about 0.5° at the horizon and about 0.1° at 10°. Rise and set shift by a few seconds. | Documented in CLI output. Revisit in Milestone 2 if reference tools disagree. |

## Verification plan

Tolerances are set here, from analysis, before any code runs. They are not tuned
afterwards to make tests pass.

| What | Reference | Tolerance | Why that tolerance |
|---|---|---|---|
| SGP4 propagation: 33 runs, 667 states | Vallado's `SGP4-VER.TLE` and `tcppver.out` | 2×10⁻⁷ km and km/s, about 0.2 mm | The file prints 8 decimals. python-sgp4 uses the same bound. |
| SGP4 error cases: 7 runs that must fail | Same files | Exact error code, at the exact step | These cases exist to test failure handling. |
| Mode `'a'` vs `'i'` for the ISS | Our own code, both modes | Identical | Checks assumption A2. |
| OMM elements vs TLE elements | One element set downloaded in both formats | Equal to the TLE's printed precision | Proves the OMM path loses nothing. |
| GMST | Vallado, Example 3-5 | 10⁻⁶ degrees | Published to that precision. |
| TEME to ECEF | Vallado 2006, Appendix C | 0.1 mm position, 0.1 mm/s velocity | Same algorithm, so any difference is floating-point noise. |
| Size of ignoring polar motion | Same example, full ITRF result | Under 15 m | Checks assumption A4. |
| ECEF to geodetic | Vallado, Example 3-3 | 10⁻⁶ degrees, 0.1 m | Published to that precision. |
| Geodetic round trips | Equator, poles, 0 to 40,000 km altitude | 1 mm | No reference needed. Converting there and back must return the input. |
| Look angles | Vallado Example 7-1 as a round trip, plus exact cases such as a satellite straight overhead | 1 mm, 10⁻⁶ degrees | Exact geometry. |
| Range rate | Numerical derivative of range | 1 mm/s | Checks the analytic formula independently. |
| Full pipeline, same UT1 | Skyfield: ISS over Phoenix at 100 instants across 3 days | 1 m, 1 arcsecond | Same algebra as ours. Time rounding allows about 0.4 m. |
| Full pipeline, UT1 = UTC | Same data | 50 m, 20 arcseconds | Measures assumption A5 as it runs in production. |
| Pass times, coarse | Skyfield pass events, ISS over Phoenix for 7 days | 10 s, the step size | Milestone 2 tightens this to about 1 s. |

**How Skyfield stays independent.** Skyfield runs Vallado's C++ code through
python-sgp4, and it converts frames with its own code. It shares no code with
this project. Its data comes from a script in `tools/reference/` that pins its
package versions and runs with one `uv` command. The script and its output are
both committed. Anyone can regenerate the data and diff it.

## Pass finding in Milestone 1 (D3)

The brief puts root-finding in Milestone 2 but asks this milestone's CLI to print
the next 5 passes. Milestone 1 steps through time in 10-second increments. It
reports rise, peak, and set at that resolution and labels them as coarse.
Milestone 2 replaces the internals with root-finding and tightens the Skyfield
pass test from 10 s to about 1 s. The milestones stay separate, and the
verification history shows the improvement.

## CLI

```text
sky now    [--sat 25544]              # position, velocity, look angles from observer
sky passes [--sat 25544] [--count 5]  # next passes above 10°, America/Phoenix times
```

It is built with System.CommandLine 2.0, which is stable as of .NET 10. Every
command prints the element-set epoch and its age. Old elements are the most
common reason a prediction disagrees with Heavens-Above.

## Config and privacy

- `src/Sky.Cli/appsettings.json` is committed and holds the default observer:
  the Arizona State Capitol, 33.4478° N, 112.0972° W, 331 m, `America/Phoenix`.
- `appsettings.Local.json` goes next to it and is gitignored. It overrides the
  default with your real location. A committed `appsettings.Local.example.json`
  shows the format.
- Environment variables such as `SKY_Observer__Latitude` also override. CI and
  Docker use these later.
- Times are UTC inside the code. They convert to the observer's IANA zone only
  when printed.

## Tooling

- **xUnit v3** with plain `Assert` calls. FluentAssertions now requires a paid
  license, and tolerance checks don't need a library.
- **Central package management**, so every version lives in one file.
- **Warnings as errors and nullable reference types** in every project except
  Sky.Sgp4, which keeps upstream code as-is.
- **`dotnet format --verify-no-changes`** is the lint step.
- **`TimeProvider`** is injected wherever the current time matters. Cache and
  next-pass logic are tested with a fake clock.
- **CI** runs restore, build, format check, and tests on every push and pull
  request, on ubuntu-latest.

## Build order

Each step is one or two small commits, and tests are written first. You can stop
me after any step.

1. **Skeleton.** Solution, shared build settings, empty projects, and one
   placeholder test. CLAUDE.md commands updated to the real ones.
2. **CI.** GitHub Actions workflow, green on the skeleton.
3. **Vallado import.** Upstream C# committed exactly as downloaded, with
   `NOTICE.md` and the checksum.
4. **Vallado trim.** Non-math code removed in its own reviewable commit.
5. **SGP4 verification.** TLE parser, the Vallado files, and all 33 runs,
   including the error cases and the mode comparison.
6. **Time.** Julian dates and GMST, checked with Example 3-5.
7. **Earth rotation.** TEME to ECEF, checked with Appendix C and the polar-motion
   bound.
8. **Geodetic.** ECEF to latitude, longitude, and altitude, checked with
   Example 3-3 and round trips.
9. **Look angles.** Azimuth, elevation, range, and range rate.
10. **Skyfield cross-check.** The reference script, its golden data, and the
    full-pipeline tests.
11. **OMM.** Model, parser, the recorded fixture, and the OMM-vs-TLE test.
12. **CelesTrak client and cache.** Every rule in the cache policy, tested with a
    fake clock and fake HTTP.
13. **Coarse passes.** The pass finder and the Skyfield pass comparison.
14. **CLI.** Config loading, `sky now`, and `sky passes`.
15. **Docs.** `verification.md`, ADRs, and README status.

Then you run `sky passes` against Heavens-Above and review before Milestone 2.

## Out of scope for Milestone 1

- Root-finding, visibility, and sun position. These are Milestone 2.
- The ASP.NET Core API, web UI, and Docker Compose. These are Milestone 3.
  CLAUDE.md currently says Docker gets wired up in Milestone 1, and step 1 fixes
  that line.
- IERS data for UT1 and polar motion. Assumptions A4 and A5 show they aren't
  worth it at this accuracy level.
