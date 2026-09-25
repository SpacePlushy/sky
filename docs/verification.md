# Verification

How Sky shows that its orbital math is right, how closely, and against what.

Every tolerance below was set from analysis before the test first ran. When a test failed,
the cause was found and fixed at its source; no tolerance was loosened to make a test pass.
The measured column is the worst case over all inputs in the test.

The suite runs on every push and pull request, on Linux, macOS, and Windows, because each
ships a different math library. It also runs weekly with no code change, so a runtime or
operating-system update that shifts floating-point results is caught.

```bash
dotnet test --solution Sky.slnx
```

## What is checked

### SGP4 propagation

| Check | Reference | Tolerance | Measured |
|---|---|---|---|
| 33 runs, 666 states | Vallado's `SGP4-VER.TLE` and `tcppver.out` (AIAA 2006-6753) | 2×10⁻⁷ km and km/s | 0.117 mm, 0.0005 mm/s |
| 7 runs that must fail | Same files | Exact error code, at the exact step | All 7 exact |
| Improved vs AFSPC mode, near-Earth | All 9 near-Earth Vallado runs over their published schedules, and the ISS every minute for 7 days | Bit-identical | Bit-identical |
| Improved vs AFSPC mode, low-inclination deep space | Vallado run 16, satellite 23599 | Must differ, proving the mode reaches SGP4 | Differs by up to 0.964 km |
| OMM path vs TLE path | Vallado case 00005 written as OMM | Identical elements | Identical |
| Real CelesTrak ISS record, end to end | Skyfield's propagation of the same record | 2×10⁻⁷ km | 0.0008 mm |

### Time and Earth rotation

| Check | Reference | Tolerance | Measured |
|---|---|---|---|
| GMST at J2000 | IAU-82 constant, 67,310.54841 s of time = 280.460618375° | 10⁻¹⁰° | Within |
| GMST, 1992-08-20 12:14 UT1 | Vallado, Example 3-5: 152.5787878° | 10⁻⁶° | Within |
| GMST rate at J2000 | Exact rational derivative of IAU-82: 7.29211585530659×10⁻⁵ rad/s | 10⁻¹⁸ rad/s | Within |
| GMST rounding noise, 1970–2070 | Rate × 1 s over 2,000 random one-second steps | 5×10⁻¹³ rad | 2×10⁻¹³ rad |
| TEME to Earth-fixed | Vallado 2006 Appendix C (Rev 2), forward to ITRF with published polar motion | 0.1 mm, 0.1 mm/s per component | 0.05 mm, 0.066 mm/s |
| Velocity is the derivative of position | Straight-line TEME motion, 2,000 random states | 10⁻⁸ km/s | 5×10⁻¹⁰ km/s |
| Rotation preserves length and z | 2,000 random states | 10⁻⁹ km | Within |

The Appendix C velocity difference is expected, not error: 0.067 mm/s in all, 0.066 mm/s of it
in x. See [ADR 0003](adr/0003-earth-rotation-rate.md).

### Geodetic and look angles

| Check | Reference | Tolerance | Measured |
|---|---|---|---|
| Zero-height points on the ellipsoid | x²/a² + y²/a² + z²/b² = 1 | 10⁻¹⁴ | Within |
| Height along the geodetic normal | Definition of geodetic coordinates | 10⁻⁹ km | Within |
| Normal perpendicular to the surface | Numerical tangents | 10⁻⁹ | Within |
| Inverse on a global grid, −0.5 km to GEO | Round trip | 10⁻¹²°, 1 µm | 2×10⁻¹⁴° |
| 20,000 random points, 6,000–50,000 km | Round trip | 1 µm | 0.034 µm |
| ECEF to geodetic against a different algorithm | Fixed-point iteration run to convergence, at Vallado Example 3-3's input and 2,000 random points | 10⁻¹¹°, 1 µm | Within |
| Look angles at known azimuth and elevation | Exact geometry, 1,800 placements | 10⁻⁹° | Within |
| Due north on the meridian; east/west mirror | Symmetry, no axis formula | 10⁻⁹° | Within |
| Range rate is the derivative of range | Straight-line motion, 2,000 cases | 10⁻⁹ km/s | 4.5×10⁻¹¹ km/s |

### Full pipeline against Skyfield

Skyfield 1.55 runs Vallado's C++ SGP4 through python-sgp4 2.27 and converts frames with its
own code, so it shares no code with Sky. The comparison uses the ISS over the default
Phoenix observer at 133 instants across 3 days, including every 20 s through a 68.8° pass.

| Check | Tolerance | Measured |
|---|---|---|
| TEME position | 2×10⁻⁷ km | 0.0008 mm |
| Earth-fixed position | 1 mm | 0.0007 mm |
| Earth-fixed velocity | 0.1 mm/s | 0.058 mm/s |
| Subpoint latitude and longitude | 10⁻⁸° | 7×10⁻¹²° |
| Azimuth and elevation | 10⁻⁶° | 5×10⁻¹¹° |
| Range | 1 mm | 0.0006 mm |
| Range rate | 0.1 mm/s | 0.044 mm/s |

Both velocity figures match the prediction exactly: Skyfield subtracts Earth rotation at the
IERS nominal rate, which differs from Sky's GMST rate by 8.6×10⁻¹² rad/s.

### Pass finding (Milestone 1)

Rise and set come from 10 s samples. Elevation near the zenith changes about 1° per second, so
a 10 s grid alone could miss an overhead peak by up to 5°. The finder therefore refines every
local maximum among the samples in 0.1 s steps, within one coarse step either side, and reports
the highest. A satellite in a high orbit can have two or more maxima in one pass, so refining
only the best sample could miss the true peak.

Each pass carries its own peak-elevation uncertainty (`SatellitePass.PeakElevationUncertaintyDegrees`):
the fastest the line of sight can turn near the reported peak, times 0.05 s. That rate is the
satellite's speed relative to the observer, plus an allowance for its change, over the closest
range it can reach. It comes from states the search has already propagated, so it holds for
any orbit, element age, or search length. `sky passes` prints the largest among the passes it
lists, rounded up.

A grazing pass can peak above the minimum while no 10 s sample does. The finder also refines
every maximum below the minimum that the same rate bound says could reach it, and reports the
pass if the refined peak does. Rise and set move to the peak where needed, so rise, peak, and
set are always in order.

The reference events are Skyfield's, computed with UT1 = UTC as Sky's production path is.
Each was refined with Skyfield's own altitude function, rise and set to under a microsecond and
peaks to tens of microseconds, because `find_events` stops at half a second. Its times were up
to 0.22 s from the true events.

| Check | Bound | Measured |
|---|---|---|
| Rise is late by, over 25 passes in 7 days | 0 to 10 s | 0.238 to 9.993 s |
| Set is early by | 0 to 10 s | 0.209 to 9.551 s |
| Peak time | Within 0.1 s | Within 0.046 s |
| Peak elevation is low by | 0 to each pass's uncertainty, 0.017° to 0.047° here | 0 to 0.00003° |
| ISS directly overhead, true peak midway between 0.1 s samples, at 3 grid phases | 90° at the overhead instant, within 0.1 s and the pass's uncertainty | 0.0495° low against 0.0496°, 0.050 s off |
| Overhead passes of the ISS and of HRC MONOBLOCK CAMERA, the lowest perigee in `stations`, with elements 1, 15, and 29 days old | Each pass's uncertainty | Worst: HRC at 29 days, 0.1210° low against 0.1216° |
| Grazing passes: minimum 0.02°, 0.05°, and 0.2° below each of the 25 ISS peaks | Every pass found, in order, peak within 0.1 s | All 75 |
| Molniya pass with maxima of 79.4° and 85.1°, 7.7 hours apart | The higher maximum, against a brute-force 0.1 s sweep of the whole pass | Same sample, 85.1038° |
| Passes whose peak clears 30° | Exactly Skyfield's 12 | 12 |
| A decaying satellite | Search stops and reports the SGP4 error | Reports `Decayed` |

In the overhead cases, the worst case for sampling, each uncertainty covers the measured
shortfall and exceeds it by less than 0.5%, so the printed figure is both safe and tight.

Searches start on the whole second at or before the clock, so printed rise and set times are
exact, peaks print to tenths of a second, and a pass rising within the current second is still
found. A pass already in progress when a search starts is not listed; `sky now` reports it as
in progress. Milestone 2 replaces the 10 s rise and set grid with root-finding.

### CelesTrak data and policy

| Check | How |
|---|---|
| OMM parsing | A real `GROUP=stations` response, 22 records, including 6-digit catalog numbers |
| Refuses SGP4-XP (ephemeris type 4) | SGP4 would propagate those elements to wrong positions without error |
| Every cache rule | 27 test cases with a fake clock and a scripted fake server that records each request. They include a run interrupted mid-request, an unreadable state file (treated as blocked), a response body lost after the status arrived, a network backoff reset by any CelesTrak answer, and timestamps from the future |

The cache rules are in [ADR 0002](adr/0002-celestrak-cache-policy.md).

### Command line and settings

| Check | How |
|---|---|
| Settings | Out-of-range values, Windows time-zone IDs, and unknown keys such as `Observer:Latitude` are rejected with a message; unknown keys list the known ones |
| Relative `CelesTrak:CacheDirectory` | Resolves against the settings folder, never the working directory, so every run shares one request history and one 2-hour rule |
| `--min-elevation` | Parsed with the invariant culture: `30.5` means the same under a German locale, and `1,5`, `NaN`, and values outside 0 to below 90 are errors |
| Daylight-saving zones | When the offset changes inside the window, every printed time carries its UTC offset; checked in `America/Denver` across the November change |
| Search start | A clock at 04:00:02.332 gives rises on the 04:00:02 grid |
| Cache wiring | `--refresh` and `sky unblock` checked end to end against the fake server |

## Assumptions

| # | Assumption | Effect | How it is checked |
|---|---|---|---|
| A1 | SGP4 uses WGS-72 constants. | None: the elements were fitted with them. | Vallado verification set |
| A2 | SGP4 runs in improved mode `'i'`. | None for near-Earth orbits: all 9 near-Earth Vallado runs are bit-identical in both modes. | Mode tests |
| A3 | TEME to Earth-fixed is one GMST (IAU-82) rotation. | This is the frame's definition. | Appendix C |
| A4 | Polar motion is ignored. | Pole-offset angle × radius: 11–12 m in low orbit today, 17.9 m in the Appendix C example at 10,208 km. | Appendix C bound |
| A5 | UT1 is taken as UTC. | IERS Bulletin A (24 Sep 2026) gives UT1 − UTC = −0.0135 s, about 7 m at ISS radius. By definition it never exceeds 0.9 s, about 450 m. | Per-sample bound against Skyfield |
| A6 | Geodetic output uses WGS-84. | This is the GPS and mapping standard. | Definitional and round-trip tests |
| A7 | Observer height is ellipsoid height. | Phoenix's geoid is about 30 m below the ellipsoid: under 0.004° of elevation. | Documented in settings |
| A8 | Elevation is geometric, with no refraction. | Refraction lifts objects about 0.5° at the horizon and 0.1° at 10°. | Documented in CLI output |

## Known limits

- **SGP4 velocity is not exactly the derivative of SGP4 position.** For the ISS the gap peaks
  at 2.24×10⁻⁵ km/s (22 mm/s), identical in python-sgp4. SGP4 approximates some
  short-period terms in its velocity. Range rate inherits this, so range rate is verified on
  exact trajectories, and a characterization test bounds the SGP4 gap at 3 cm/s.
- **SGP4 itself is accurate to about a kilometer at epoch**, degrading by kilometers per day
  of element age. Every figure above measures Sky's implementation, not SGP4's physics.
- **Milestone 1 rise and set times are coarse**, as bounded above, and a pass already in
  progress is not listed by `sky passes`.

## Issues found in reference sources

These came to light because tests failed. Each was traced to its source and measured.

1. **Vallado's C# SGP4 overwrites error codes.** Its `return` statements after errors 1, 2,
   3, 4, and 6 are commented out, so the final decay check turns codes 3 and 4 into 6. The
   C++ version in the same package returns. Sky restores the five returns; see
   `src/Sky.Sgp4/NOTICE.md`.
2. **SGP4's epoch arithmetic matters at the millimeter level.** Upstream forms the epoch as
   Julian date of midnight plus fraction minus 2433281.5, which rounds by up to about 20 µs.
   For the very eccentric deep-space satellite 23333 that moved results by 4 mm. Sky
   reproduces the arithmetic exactly.
3. **Vallado 2006 Appendix C holds UT1 as one double.** Its printed Julian date,
   2453101.82740678310, is the nearest double to the published UT1 (07:51:27.9460471, from
   UTC 07:51:28.386009 and UT1 − UTC = −0.4399619 s). A double near JD 2.45 million resolves
   only 40 µs, and this one is 14.69 µs late: 8.5 mm at that radius. The tests feed Sky the
   same double, and a separate test shows Sky's split Julian date keeps the exact instant.
4. **python-sgp4 2.27 stores OMM epochs up to about 0.4 µs late.** It divides float seconds
   by 86400: 0.3641 µs here, which is 2.79 mm along track. The reference generator sets the
   exact epoch and records the rounding.
5. **Skyfield 1.55's geodetic latitude stops after three iterations**, up to 1.99×10⁻⁸°
   short of convergence. The reference file keeps Skyfield's value and adds the same formula
   run to convergence; Sky matches that to 3×10⁻¹²°.
6. **Skyfield 1.55's built-in UT1 − UTC is out of date** for September 2026: +0.096 s,
   against −0.0135 s observed by IERS. The tests give Sky Skyfield's own value, so they check
   the math either way.
7. **Skyfield's `find_events` stops refining at half a second.** Its rise, set, and peak
   times were up to 0.22 s from the true events. The generator refines them with Skyfield's
   own altitude function.

## Changes from the approved plan

- **Polar-motion bound** is pole angle × radius instead of a flat 15 m, which was too tight
  for the Appendix C satellite at 10,208 km.
- **Range rate** is verified on exact trajectories instead of against the derivative of SGP4
  range, because of the SGP4 velocity limit above.
- **Earth-fixed velocity** uses the GMST rate rather than Vallado's inertial rate; see
  [ADR 0003](adr/0003-earth-rotation-rate.md).
- **CelesTrak 5xx responses** block the group like any other non-200, instead of backing off,
  because CelesTrak's policy requires stopping on any non-200.
- **OMM vs TLE** uses Vallado case 00005 written as OMM, and the real ISS record against
  Skyfield, instead of a paired download, which would cost two CelesTrak requests.
- **GMST** removes rounding noise the first version had (about 5×10⁻¹¹ rad).
- **Pass peaks** are refined in 0.1 s steps. Decision D3 had the whole finder on a 10 s grid,
  but that cannot meet the plan's own "peak within about 1°" for passes above about 78°.
  Rise and set stay on the 10 s grid.
- **Look angles** are not checked against Vallado Example 7-1. That example's published answer
  is an inertial vector produced through the full IAU-76/FK5 reduction, which Sky does not
  implement. Look angles are checked instead by exact geometry and against Skyfield, to
  5×10⁻¹¹°.
- **Setting names carry their units.** The plan's `SKY_Observer__Latitude` is
  `SKY_Observer__LatitudeDegrees`. Unknown keys are rejected, so the old name fails with a
  message instead of being ignored.
- **Peak uncertainty is per pass**, computed from the propagated states, instead of one bound
  per satellite from its elements. The per-satellite bound assumed a week of drag and failed
  for decaying objects over the CLI's 30-day window.
- **The UT1 = UTC check** uses a per-sample bound derived from the Earth's rotation over
  UT1 − UTC, instead of the plan's flat 50 m and 20″, which assumed today's −0.015 s.
- **ECEF to geodetic is not checked against Vallado Example 3-3's printed answer.** The book
  uses R = 6378.1363 km rather than WGS-84's 6378.137 km, which moves that height by 0.7 m, so
  it cannot anchor a WGS-84 check at the plan's 0.1 m. The conversion is checked instead
  against an independent fixed-point solver at Example 3-3's input and 2,000 random points.
- **The SGP4 check covers 666 states, not the plan's 667.** `tcppver.out` has 667 state lines,
  but the single line for satellite 33334, which fails at initialization, is a stale copy of
  the previous state and is ignored, as python-sgp4's own tests do.
- **CI runs on Linux, macOS, and Windows, weekly, and on demand**, not only on ubuntu-latest,
  because each system ships a different math library.
- **The cache keeps two files per group**: `{group}.json`, the raw response, and
  `{group}.state.json`, the request history and any block. The plan had one file that also
  stored the newest element epoch; the epoch is read from the data instead.
- **`sky unblock` was added** so a person clears a block after reading why it happened
  (ADR 0002). The plan listed only `now` and `passes`.
- **The CLI has its own test project**, `tests/Sky.Cli.Tests`, for settings, commands,
  formatting, and the cache wiring. The plan said the CLI held no logic worth testing; the
  settings validation and time-zone handling turned out to need tests.

## Reference data

| Data | Source | Regenerate |
|---|---|---|
| `tests/Sky.Orbital.Tests/Data/Vallado/` | CelesTrak AIAA 2006-6753 package; `tcppver.out` from python-sgp4 | Fixed; checksums in its README |
| `tests/Sky.Orbital.Tests/Data/Skyfield/` | Skyfield 1.55, sgp4 2.27, numpy 2.5.3 | `uv run tools/reference/generate_skyfield_reference.py`; checksum in its README |
| `tests/Sky.CelesTrak.Tests/Fixtures/` | A real CelesTrak response, byte for byte | Fixed; checksum in its README |
