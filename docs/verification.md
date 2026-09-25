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

### The Sun (Milestone 2)

Sky computes the Sun with Meeus's low-accuracy method (*Astronomical Algorithms*, chapter 25) and
rotates it to Earth-fixed with apparent sidereal time. The reference is JPL's DE421 ephemeris
through Skyfield, which shares no code with Sky, generated with UT1 = UTC so Earth rotation is the
same on both sides. The bound for the Earth-fixed direction is Meeus's stated 0.01° for the
apparent place (his one-term nutation included), plus 0.0006° for the one-term equation of the
equinoxes against IAU 2000A, 0.00004° for IAU-82 against IAU-2006 GMST, and 0.0008° for passing UTC
as Terrestrial Time: 0.0115°.

| Check | Reference | Tolerance | Measured |
|---|---|---|---|
| Apparent right ascension, declination, distance | Meeus Example 25.a (published) | 10⁻⁵° and 10⁻⁵ AU, the printed digits | Within |
| Mean sidereal time, 1987 April 10 0h UT | Meeus Example 12.a: 13h10m46.3668s | 10⁻⁴ s | Within |
| Apparent sidereal time, same date | Meeus Example 12.a with full nutation: 13h10m46.1351s | 0.14 s, the omitted nutation terms | Within |
| Apparent place, 400 instants 1950 to 2049, at the same Terrestrial Time | DE421 | 0.010° | 0.0092° |
| Distance, same instants | DE421 | 10⁻⁴ AU | 7.5×10⁻⁵ AU |
| Equation of the equinoxes, same instants | Skyfield's GAST − GMST (IAU 2000A) | 2.1″ | Within |
| Earth-fixed direction, every 10 min for 7 days | DE421 | 0.0115° | 0.0028° |
| Elevation at the observer, same instants | DE421, apparent, no refraction | 0.0115° | 0.0026° |
| Declination within the obliquity, distance between perihelion and aphelion | 5,000 random instants, 1950 to 2050 | Physical ranges | Within |
| Subsolar longitude at 12:00 UTC | The equation of time, ±16.5 min | ±4.2° every day of 2026 | Within |

### The Earth's shadow (Milestone 2)

A satellite is sunlit when the line from it to the Sun's center misses the WGS-84 ellipsoid.
Stretching z by a/b turns the ellipsoid into a sphere, so the test is exact, and the signed
function Sky root-finds is zero exactly where the line grazes the ellipsoid. The reference computes
the same geometry by a different method, a line-ellipsoid quadratic, with DE421's Sun.

| Check | Reference | Tolerance | Measured |
|---|---|---|---|
| Behind and beside the Earth; the equatorial plane | Exact line distance, \|s × sun\| / \|sun − s\|, 2,000 cases | 10⁻⁷ km | Within |
| Shadow edge over a pole | At the polar radius b, not a | ±1 m | Within |
| Lines built tangent to the ellipsoid | 2,000 random tangent points and directions | Zero to 10⁻⁷ km | Within |
| Sunlit or not | A brute-force 0.5 km march toward the Sun, 600 cases | Identical | Identical |
| Continuity where the function's two branches meet | 5,000 pairs | Lipschitz in the stretched space | Within |
| Sky's function at the reference's 217 transitions, given the reference Sun | Line-ellipsoid quadratic, bisected to 1 µs | 2×10⁻⁵ km, and the right sign change | 2.1×10⁻⁶ km |
| Transitions with Sky's own Sun, 7 days | Same | Per transition: 0.0115° of Sun direction over the function's rate there, plus 1 ms: 0.76 to 1.06 s | 16 ms (median 10 ms) |
| Skyfield's `is_sunlit` | A sphere and the geometric Sun | The models' difference over the rate | Up to 8.0 s apart |

The last row is not a check of Sky: Skyfield's sphere puts high-latitude shadow edges up to 21 km
from the ellipsoid's.

### Pass finding (Milestone 2)

Milestone 2 replaces Milestone 1's finder, which put rise and set on a 10 s grid. The 10 s scan and
its rate bound still find every pass, including grazing ones; then Brent's method finds rise and set
to 1 ms and Brent's minimizer finds each peak. A dip below the minimum hidden between two samples
splits a pass, by the same rate bound. A pass already up when the search starts is followed back for
its real rise, and one still up at the end forward for its set, up to a day each way.

Near the zenith elevation has a corner, not a smooth top, so each pass still carries its own
peak-elevation uncertainty (`SatellitePass.PeakElevationUncertaintyDegrees`): the line-of-sight rate
times the distance within which the minimizer places the peak. It is about 0.0002° for an overhead
ISS pass.

| Check | Bound | Measured |
|---|---|---|
| Rise, 25 passes in 7 days, against Skyfield's refined events | 1 ms + 10 µs | 0.23 ms |
| Set | 1 ms + 10 µs | 0.20 ms |
| Peak time | 0.3 ms | 0.072 ms |
| Peak elevation is low by | 0 to each pass's uncertainty, up to 0.00019° here | 2.4×10⁻¹¹° |
| ISS directly overhead, 6 grid phases | 90° within the pass's uncertainty (under 0.0003°), time within 0.21 ms | Within |
| Overhead ISS and HRC MONOBLOCK CAMERA, elements 1, 15, and 29 days old | Each pass's uncertainty | Within |
| Grazing passes: minimum 0.02°, 0.05°, and 0.2° below each of the 25 peaks | Every pass found; rise and set on the minimum within 1.1×10⁻³°; peak within 10 ms | All 75 |
| A 6 s dip below the minimum between two samples of a Molniya pass | Split into two passes, each ending on the minimum | Split; the test fails with the check disabled |
| Molniya pass with maxima of 79.4° and 85.1° | The higher maximum, at least as high as a brute-force 0.1 s sweep | Same maximum |
| A search starting inside a pass (3 passes, 3 points each) | Same rise, peak, and set as a search starting before it, within 2 ms | Within |
| A geostationary satellite | No passes; reported as up for the whole extension | As bounded |
| A decaying satellite | Search stops and reports the SGP4 error | Reports `Decayed` |
| 40 random observers and minimums | Rise < peak < set, crossings on the minimum, no overlaps | All |
| Heavens-Above, same element set, 9 visible passes | Rise, set, and peak within 1.5 s (it prints truncated whole seconds) | 0.3 to 0.9 s; peaks 1.1 s |

Printed times round to the nearest second.

### Visibility (Milestone 2)

A pass is visible where the satellite is sunlit and the Sun is below −6° at the observer. Inside each
pass Sky samples both functions every 10 s and root-finds each sign change to 1 ms; a rate bound for
each function catches a crossing and return between samples.

| Check | Reference | Tolerance | Measured |
|---|---|---|---|
| Every second of every pass for 7 days | Sky's Sun and shadow functions, evaluated directly | Inside a window exactly when both conditions hold | Exact |
| Each window boundary | The function that caused it | Changes sign within ±2 ms | All |
| Hidden crossings between samples | Functions with known roots | Found to 1 ms | All |
| Civil twilight, 14 crossings in 7 days | DE421 | Per crossing: 0.0115° over the Sun's elevation rate, 4.9 s or more | 0.74 s |
| Civil twilight, 8 crossings on 4 dates across 2026 | U.S. Naval Observatory, published to the minute | 30 s of rounding plus the Sun bound | All 8 round to USNO's minute |
| Visible parts of the 25 reference passes, 7 visible | Reference windows from DE421 and the quadratic shadow | Each boundary within the bound of its cause | 0.12 s; highest point 0.039° |
| Heavens-Above, same element set | Its visible-pass table | Recorded | Ends 2.0 to 4.6 s earlier at shadow entry; counts twilight passes visible sooner |

Heavens-Above's earlier shadow ends match a fade through the penumbra, where Sky uses the Sun's
center (assumption A11). Its twilight rule, which judges visibility from sky brightness and the
satellite's magnitude, differs from this project's defined Sun-below-−6°.

A week of passes with their visibility takes about 40 ms.

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
| A8 | Elevation is geometric, with no refraction. | Refraction lifts objects about 0.5° at the horizon and 0.1° at 10°. | Documented in CLI output. Heavens-Above's 10° crossings agree with Sky's geometric ones within 1 s. |
| A9 | The Sun comes from Meeus's low-accuracy method with a one-term nutation. | Under 0.0115° of direction: under 1.1 s on an ISS shadow transition, and 0.0115° over the Sun's elevation rate on a twilight crossing (about 3 to 6 s in Phoenix). | DE421, Meeus Examples 25.a and 12.a, USNO |
| A10 | UTC is used as Terrestrial Time in the Sun formulas. | 0.0008°, inside A9's bound. | Part of A9's bound |
| A11 | The shadow is geometric: the Sun's center, the WGS-84 ellipsoid, no atmosphere. | The Sun's disk makes the ISS fade over about 8 s when its orbit crosses the shadow squarely, several times longer at a high beta angle; Sky reports the middle. Atmospheric dimming moves the fade by seconds. | Heavens-Above ends passes 2.0 to 4.6 s earlier; Skyfield's sphere is reported beside the ellipsoid |
| A12 | The sky is dark enough when the Sun's center is geometrically 6° below the horizon. | The standard definition of civil twilight. | USNO uses the same definition |

## Known limits

- **SGP4 velocity is not exactly the derivative of SGP4 position.** For the ISS the gap peaks
  at 2.24×10⁻⁵ km/s (22 mm/s), identical in python-sgp4. SGP4 approximates some
  short-period terms in its velocity. Range rate inherits this, so range rate is verified on
  exact trajectories, and a characterization test bounds the SGP4 gap at 3 cm/s.
- **SGP4 itself is accurate to about a kilometer at epoch**, degrading by kilometers per day
  of element age. Every figure above measures Sky's implementation, not SGP4's physics.
- **UT1 = UTC (A5) moves twilight by at most about 1 s**, since |UT1 − UTC| never exceeds 0.9 s;
  today's −0.0135 s moves it by 0.01 s. It does not move shadow transitions, because the satellite
  and the Sun turn with the Earth together.
- **Brightness is not computed.** It needs each satellite's intrinsic magnitude, which CelesTrak's
  GP data does not carry.

## Issues found in reference sources

These came to light while testing, most because a test failed. Each was traced to its source and measured.

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
8. **A constant-ΔT timescale is UT1 = UTC only from 2017.** The Milestone 2 generator fixes TT −
   UT1 at 69.184 s so that UT1 = UTC, as Sky assumes; that holds only while TAI − UTC is 37 s. The
   first Sun comparison over 1950 to 2049 in the Earth-fixed frame was 0.1° off in 1950, from 27 s
   of Earth rotation. The century check now compares apparent places at the same Terrestrial Time,
   with no Earth rotation involved.
9. **Heavens-Above reads `tz=MST` as Denver time.** Its pass table for Phoenix came back in UTC−6,
   the Windows zone "Mountain Standard Time" with daylight saving, not Arizona's UTC−7.
10. **Skyfield's `is_sunlit` uses a sphere and the geometric Sun**, 0.0057° from the apparent Sun
    that lights the satellite. Its shadow transitions differ from the ellipsoid's by up to 8 s, so
    it serves only as a comparison.

## Changes from the approved plan

### Milestone 1

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

### Milestone 2

- **The Sun's bound is 0.0115°, not the plan's 0.017°.** The plan review found the nutation term
  counted twice: Meeus's 0.01° already includes his one-term nutation. The equation of the
  equinoxes and Example 12.a are checked separately, since the combined bound could hide a missing
  or sign-flipped equation of the equinoxes.
- **The 1950 to 2049 Sun check compares apparent places at Terrestrial Time**, not Earth-fixed
  vectors; see issue 8.
- **The shadow function is not compared value for value.** The reference computes the shadow by a
  different method, so its values are not Sky's. Sky's function is checked to vanish at the
  reference's transitions given the reference Sun, and end to end with its own Sun.
- **The per-pass peak uncertainty stays**, at about 0.0002° instead of Milestone 1's 0.05°. The plan
  expected the peak to be exact to 10⁻⁹°, which fails at the zenith's corner.
- **Dips below the minimum split a pass**, and **visibility crossings between samples are caught by
  a rate bound**. The plan review found both gaps.
- **Rise and set are within 1 ms + 10 µs and peaks within 0.3 ms of Skyfield**, derived bounds
  tighter than the plan's 2 ms and 0.05 s.
- **Heavens-Above's rise, set, and peak are asserted**, because its published element set turned out
  to be the reference one. Two published anchors were added: Meeus Example 12.a and USNO's civil
  twilight.

## Reference data

| Data | Source | Regenerate |
|---|---|---|
| `tests/Sky.Orbital.Tests/Data/Vallado/` | CelesTrak AIAA 2006-6753 package; `tcppver.out` from python-sgp4 | Fixed; checksums in its README |
| `tests/Sky.Orbital.Tests/Data/Skyfield/` | Skyfield 1.55, sgp4 2.27, numpy 2.5.3 | `uv run tools/reference/generate_skyfield_reference.py`; checksum in its README |
| `tests/Sky.Orbital.Tests/Data/Skyfield/iss-phoenix-visibility-2026-09-24.json` | Skyfield 1.55 with JPL DE421 (SHA-256 in the generator) | `uv run tools/reference/generate_skyfield_visibility_reference.py` |
| `tests/Sky.Orbital.Tests/Data/Usno/` | U.S. Naval Observatory API, byte for byte | Fixed; checksums in its README |
| `tests/Sky.Orbital.Tests/Data/HeavensAbove/` | Values read from Heavens-Above's pass table, with the source and time | Fixed |
| `tests/Sky.CelesTrak.Tests/Fixtures/` | A real CelesTrak response, byte for byte | Fixed; checksum in its README |
