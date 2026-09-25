# ADR 0003: Earth rotation rate for Earth-fixed velocity

**Status:** Accepted, 2026-09-24

## Context

Converting a TEME velocity to the Earth-fixed frame subtracts ω × r. Three values of ω are in
common use:

| Rate (rad/s) | Used by | Meaning |
|---|---|---|
| 7.2921158553 × 10⁻⁵ | Skyfield's `TEME_to_ITRF` | The rate of IAU-82 GMST: the derivative of the rotation angle itself |
| 7.29211514670698 × 10⁻⁵ | Vallado's `teme2ecef` | Earth's inertial rotation rate |
| 7.2921150 × 10⁻⁵ | Skyfield's ITRS frame (IERS nominal) | Rounded nominal value |

The first two differ by 7.086 × 10⁻¹² rad/s, which is exactly the precession of the mean
equinox in right ascension, 46.12 arcseconds per year.

## Decision

Sky rotates TEME to Earth-fixed by GMST, so it subtracts Earth rotation at the rate of GMST. That
makes Earth-fixed velocity exactly the time derivative of Earth-fixed position, which the test
suite checks numerically to 10⁻⁸ km/s.

## Consequences

- Sky's Earth-fixed velocity differs from Vallado's worked example by 0.066 mm/s. The example
  also scales the inertial rate by (1 − LOD/86400) with LOD = 0.0015563 s, so the rates differ
  by 8.40 × 10⁻¹² rad/s, which predicts 0.0664 mm/s at the example's 7901 km. Against
  Skyfield's ITRS velocity the difference is up to 0.058 mm/s at ISS radius, also as predicted.
  Both are covered by 0.1 mm/s tolerances.
- In range rate, the choice of rate enters only through the observer's velocity term,
  Δω (ẑ × r_observer) · (unit line of sight). The satellite's own ω × (line of sight) part is
  perpendicular to the line of sight and drops out. From Phoenix that bounds the difference
  from Skyfield at 4.6 × 10⁻⁸ km/s; the measured difference is 0.044 mm/s.
