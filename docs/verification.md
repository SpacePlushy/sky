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
| Improved vs AFSPC mode, ISS | Sky, both modes, every minute for 7 days | Identical | Identical |
| Improved vs AFSPC mode, low-inclination deep space | Vallado run 23599 | Must differ, proving the mode reaches SGP4 | Differs by up to 1.2 km |
| OMM path vs TLE path | Vallado case 00005 written as OMM | Identical elements | Identical |
| Real CelesTrak ISS record, end to end | Skyfield's propagation of the same record | 2×10⁻⁷ km | 0.0008 mm |

### Time and Earth rotation

| Check | Reference | Tolerance | Measured |
|---|---|---|---|
| GMST at J2000 | IAU-82 defining constant, 280.46061837504° | 10⁻⁹° | Within |
| GMST, 1992-08-20 12:14 UT1 | Vallado, Example 3-5: 152.5787878° | 10⁻⁶° | Within |
| GMST rate at J2000 | Exact rational derivative of IAU-82: 7.29211585530659×10⁻⁵ rad/s | 10⁻¹⁸ rad/s | Within |
| GMST rounding noise, 1970–2070 | Rate × 1 s over 2,000 random one-second steps | 5×10⁻¹³ rad | 2×10⁻¹³ rad |
| TEME to Earth-fixed | Vallado 2006 Appendix C (Rev 2), forward to ITRF with published polar motion | 0.1 mm, 0.1 mm/s | 0.05 mm, 0.066 mm/s |
| Velocity is the derivative of position | Straight-line TEME motion, 2,000 random states | 10⁻⁸ km/s | 5×10⁻¹⁰ km/s |
| Rotation preserves length and z | 2,000 random states | 10⁻⁹ km | Within |

The 0.066 mm/s in the Appendix C velocity is expected, not error. See
[ADR 0003](adr/0003-earth-rotation-rate.md).

### Geodetic and look angles

| Check | Reference | Tolerance | Measured |
|---|---|---|---|
| Zero-height points on the ellipsoid | x²/a² + y²/a² + z²/b² = 1 | 10⁻¹⁴ | Within |
| Height along the geodetic normal | Definition of geodetic coordinates | 10⁻⁹ km | Within |
| Normal perpendicular to the surface | Numerical tangents | 10⁻⁹ | Within |
| Inverse on a global grid, −0.5 km to GEO | Round trip | 10⁻¹²°, 1 µm | 2×10⁻¹⁴° |
| 20,000 random points, 6,000–50,000 km | Round trip | 1 µm | 0.034 µm |
| ECEF to geodetic | Vallado, Example 3-3 | 10⁻⁶°, 0.1 m | 34.352495151°, 46.446416857°, 5085.2187311 km |
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

### Pass finding (Milestone 1, coarse)

The finder samples every 10 s. Its bounds follow from the step and from the curvature of the
elevation curve, measured in Skyfield's data as at most 0.0202°/s² at the 68.8° peak.

| Check against Skyfield's 25 passes over 7 days | Bound | Measured |
|---|---|---|
| Rise is late by | 0 to 10 s | 0.21 to 9.98 s |
| Set is early by | 0 to 10 s | 0.23 to 9.77 s |
| Peak time | Within 10 s | Within 4.99 s |
| Peak elevation is low by | 0 to 0.6° | −0.0012° to 0.31° |
| Passes whose peak clears 30° | Exactly Skyfield's 12 | 12 |

The −0.0012° is the reference's UT1 offset, allowed for by a 0.01° margin. Milestone 2
replaces this finder with root-finding.

### CelesTrak data and policy

| Check | How |
|---|---|
| OMM parsing | A real `GROUP=stations` response, 22 records, including 6-digit catalog numbers |
| Refuses SGP4-XP (ephemeris type 4) | SGP4 would propagate those elements to wrong positions without error |
| Every cache rule | 21 tests with a fake clock and a scripted fake server that records each request |

The cache rules are in [ADR 0002](adr/0002-celestrak-cache-policy.md).

## Assumptions

| # | Assumption | Effect | How it is checked |
|---|---|---|---|
| A1 | SGP4 uses WGS-72 constants. | None: the elements were fitted with them. | Vallado verification set |
| A2 | SGP4 runs in improved mode `'i'`. | None for near-Earth orbits: all 14 near-Earth Vallado runs are bit-identical in both modes. | Mode tests |
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
- **Milestone 1 pass times are coarse**, as bounded above.

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
3. **Vallado 2006 Appendix C holds UT1 as one rounded double.** JD(UTC) + ΔUT1/86400,
   rounded twice, lands 22.79 µs after the exact instant: 13.2 mm at that radius. The test
   feeds Sky the same double, and a separate test shows Sky's split Julian date keeps the
   exact instant.
4. **python-sgp4 2.27 stores OMM epochs up to about 0.4 µs late.** It divides float seconds
   by 86400: 0.3641 µs here, which is 2.79 mm along track. The reference generator sets the
   exact epoch and records the rounding.
5. **Skyfield 1.55's geodetic latitude stops after three iterations**, up to 1.99×10⁻⁸°
   short of convergence. The reference file keeps Skyfield's value and adds the same formula
   run to convergence; Sky matches that to 3×10⁻¹²°.
6. **Skyfield 1.55's built-in UT1 − UTC is out of date** for September 2026: +0.096 s,
   against −0.0135 s observed by IERS. The tests give Sky Skyfield's own value, so they check
   the math either way.

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
- **GMST** removes rounding noise the first version had (about 10⁻¹¹ rad).

## Reference data

| Data | Source | Regenerate |
|---|---|---|
| `tests/Sky.Orbital.Tests/Data/Vallado/` | CelesTrak AIAA 2006-6753 package; `tcppver.out` from python-sgp4 | Fixed; checksums in its README |
| `tests/Sky.Orbital.Tests/Data/Skyfield/` | Skyfield 1.55, sgp4 2.27, numpy 2.5.3 | `uv run tools/reference/generate_skyfield_reference.py` |
| `tests/Sky.CelesTrak.Tests/Fixtures/` | A real CelesTrak response, byte for byte | Fixed; checksum in its README |
