# Milestone 2 proposal: Pass prediction

**Status:** Built 2026-09-25 without the owner's approval. The owner asked for the whole project to
be finished while they were away for about 20 hours. In place of approval, a multi-agent review
(four expert lenses, each finding checked by a skeptic) went over this plan, and its confirmed
findings changed the build; they are listed under "Plan review" at the end. Every decision below
is open for the owner to overturn. What was built, and every difference from this plan, is in
`docs/verification.md`.
**Date:** 2026-09-25. External facts were checked on this date.

## Goal

Turn Milestone 1's coarse pass list into predictions a person can go outside with: rise,
peak, and set to well under a second, and whether each pass can actually be seen.

### Done when

- `dotnet test` passes locally and in CI on all three systems.
- Every row of the verification table below passes at its stated tolerance.
- `sky passes` prints rise, peak, and set to the second, and each pass's visible part.
- A pass already in progress when the search starts is listed, with its real rise time.
- `sky now` says whether the satellite is sunlit and how high the Sun is at the observer.
- `sky passes` agrees with Heavens-Above for the same observer: rise and set within a few
  seconds when both use the same element set, after the documented model differences.
- `docs/verification.md` lists every new assumption, its size, and how it is checked.

## Decisions

| # | Decision | Choice | Main alternative |
|---|---|---|---|
| D1 | Sun position | Meeus, *Astronomical Algorithms* (2nd ed.), chapter 25, low-accuracy method: 0.01°, with the published Example 25.a as its anchor | A JPL ephemeris file: 17 MB or more to ship, for accuracy nothing here needs |
| D2 | Earth's shadow | Geometric: the Sun's center hidden by the WGS-84 ellipsoid, no atmosphere, no penumbra | A sphere (Skyfield's model) or a penumbra model |
| D3 | Dark enough to see | Sun's geometric center below −6° at the observer (civil twilight) | Nautical twilight (−12°) |
| D4 | Root-finding | Brent's method for rise, set, and every visibility boundary; Brent's minimizer for the peak | Bisection, which is slower and gains nothing in robustness here, since every root is already bracketed |
| D5 | Finding every pass | Keep Milestone 1's 10 s scan and its rate bound for grazing passes; refine from there | Adaptive steps: faster, but a new proof of completeness for little gain at 60,000 samples a week |
| D6 | Passes in progress | Search back from the start, up to 1 day, for the real rise | Keep leaving them out |
| D7 | Refraction | Stay geometric (assumption A8). Measure the effect against Heavens-Above and report it | Add a standard refraction model |
| D8 | Downloads | JPL's `de421.bsp` (17 MB, for the Skyfield reference generator only, never committed, SHA-256 checked) | None |

## Sun position (D1)

Visibility needs the Sun's direction twice: from the observer, for twilight, and from the
satellite, for the shadow. Both need only a few thousandths of a degree.

**What 0.01° costs.** At twilight in Phoenix the Sun's elevation changes by at most about
0.25° per minute, so 0.01° moves a twilight boundary by at most about 3 s. It tilts the
shadow by 0.01°, which moves the shadow's edge at the ISS by at most about 0.4 km, a fraction
of a second at 7 km/s unless the orbit grazes the shadow. That is inside the milestone's "few
seconds" target, so a full ephemeris is not worth shipping.

**The method.** Meeus's low-accuracy Sun gives the apparent right ascension and declination
referred to the true equinox of date, and the distance. Sky rotates that vector into the
Earth-fixed frame with Greenwich *apparent* sidereal time: its own GMST plus the equation of
the equinoxes, computed from the same one-term nutation Meeus uses. That keeps the vector in
the pseudo Earth-fixed frame the satellite is in, so the two can be subtracted.

**Time scale.** Meeus's formulas take Terrestrial Time; Sky passes UTC. The Sun moves about
0.04° an hour, so the 69 s between the two costs 0.0008°. Sky accepts that rather than carry
a leap-second table (assumption A10).

## Earth's shadow (D2)

A satellite is sunlit when the straight line from it to the Sun's center does not pass
through the Earth. Sky models the Earth as the WGS-84 ellipsoid: scaling the z axis by a/b
turns it into a sphere of radius a, where the test is exact geometry. The shadow function is
continuous and signed, so Brent's method can find its roots:

- If the Sun is on the satellite's side of the Earth (the line heads away from the center),
  the function is the satellite's distance from the center minus a, which is positive.
- Otherwise it is the line's closest approach to the center minus a.

Both branches give the same value where they meet, so the function has no jump.

**What the model leaves out.** The Sun is a disk, not a point: the ISS crosses the penumbra in
about 8 s, and Sky reports the moment the Sun's center disappears, the middle of that fade.
The atmosphere dims and bends light near the edge too. Both move the moment a person stops
seeing the satellite by a few seconds, and they are stated as assumption A11 instead of
modeled.

**Why not a sphere.** The polar radius is 21 km shorter than the equatorial. A sphere of the
equatorial radius makes high-latitude shadows too large by up to 21 km, a few seconds for the
ISS. The ellipsoid is exact at the same cost. Skyfield's `is_sunlit` uses a sphere, so the
comparison against Skyfield reports the difference and its bound instead of pretending the
models agree.

## Pass finding with root-finding (D4, D5, D6)

`PassFinder` replaces `CoarsePassFinder`.

1. **Scan** every 10 s, as now. Milestone 1's rate bound still finds grazing passes that clear
   the minimum between samples.
2. **Rise and set.** Each is bracketed by a sample below the minimum and one at or above it.
   Brent's method finds the crossing to 1 ms.
3. **Peak.** Every local maximum among the samples is refined with Brent's minimizer across
   one step either side, to 1 ms in time. The highest wins. Elevation is flat at a peak, so
   the elevation is then exact to about 10⁻⁹°. The per-pass peak uncertainty from Milestone 1
   is no longer needed and goes away. *Review: not at the zenith, where elevation has a corner;
   the per-pass uncertainty stays, at about 0.0002°.*
4. **In progress at the start.** If the first sample is already above the minimum, the finder
   scans backward in 10 s steps for up to 1 day to find the rise, then refines it. A pass
   still up at the end of the window is followed forward the same way. A satellite that stays
   above the minimum for the whole extension, such as a geostationary one, has no rise or set;
   the result says so instead of inventing one.
5. **Visibility.** Inside each pass, Sky finds where the shadow function and the Sun's
   elevation plus 6° change sign, on 10 s samples of the pass, and refines each change with
   Brent's method. The visible part of a pass is where the satellite is sunlit and the Sun is
   below −6°. A pass can have more than one visible part, for example when the satellite
   leaves the shadow mid-pass; each is reported with its highest elevation.

**Brent's method** (Brent 1973, as in *Numerical Recipes* §9.3 and §10.3) is written in Sky
rather than taken from a package. It is about 60 lines per routine, and the tests check it
against functions with known roots and minima, including ones built to defeat the method's
interpolation steps.

## Reference data

The Skyfield generator gains a second output file for Milestone 2, built from the same ISS
element set and observer, so Milestone 1's reference file is unchanged:

- The Sun's apparent position from JPL's DE421 ephemeris, in Skyfield's Earth-fixed frame,
  and its altitude at the observer, at the 133 existing instants plus every 10 minutes over
  the 7 days.
- Every sunlit and shadow transition of the ISS in the 7 days, found with an ellipsoid shadow
  function written in the generator, independently of Sky, and refined by bisection.
  Skyfield's own spherical `is_sunlit` transitions are recorded beside them.
- Every civil-twilight crossing at the observer in the 7 days, refined by bisection on
  Skyfield's Sun altitude.
- Each pass's visible parts, from those two.

DE421 is fetched once by Skyfield from JPL and checked against a SHA-256 recorded in the
script. The generated JSON is committed, so `dotnet test` still needs no network.

## Verification plan

Tolerances come from analysis written here, before any code runs.

| What | Reference | Tolerance | Why |
|---|---|---|---|
| Sun, Meeus Example 25.a | Published: α = 198.38083°, δ = −7.78507°, R = 0.99766 AU at 1992-10-13 00:00 TD | 10⁻⁵° and 10⁻⁵ AU | Printed to that precision; Sky runs the same formulas |
| Sun direction | DE421 through Skyfield, 1,000+ instants over 7 days and 20 years (1950 to 2050 sampled) | 0.017° | Meeus's stated 0.01°, plus up to 0.006° from the one-term nutation, plus 0.0008° from TT = UTC. *Review: the nutation was counted twice; built with 0.0115°.* |
| Sun elevation at the observer | Same | 0.017° | Same error budget; parallax (0.002°) is included on both sides. *Built with 0.0115°.* |
| Shadow function | The generator's independent ellipsoid function, given Skyfield's Sun | 10⁻⁶ km | Same geometry, different code |
| Shadow transitions with Skyfield's Sun | The generator's refined transitions | 2 ms | 1 ms root tolerance on each side |
| Shadow transitions with Sky's Sun | Same | Per transition: the Sun-direction bound's effect on the shadow function, over the function's rate at the transition | The size depends on how steeply the orbit cuts the shadow, so each transition gets its own bound |
| Shadow model against Skyfield's sphere | Skyfield `is_sunlit` | Reported, not asserted | Different models by design (D2) |
| Rise and set | The refined Skyfield events of Milestone 1, 25 passes | 2 ms | 1 ms root tolerance each side; the two pipelines agree to 5×10⁻¹¹° |
| Peak time and elevation | Same | 0.05 s, 10⁻⁶° | The peak is flat: 10⁻⁶° of elevation spans tens of ms, so time is loose and elevation tight |
| Twilight crossings | Generator's refined crossings | Per crossing: 0.017° over the Sun's elevation rate there | Same Sun bound |
| Visible parts of each pass | Generator's visible parts | Each boundary within the bound of the event that makes it | Composition of the rows above |
| Root-finder | Functions with exact roots and minima, including adversarial ones | 1 ms, and a maximum iteration count | Brent guarantees convergence within a bracket |
| Invariants | Seeded random observers and times | Rise < peak < set; elevation at rise and set equals the minimum to 10⁻⁶°; no two passes overlap; every sample between set and the next rise is below the minimum | Properties of a correct finder |
| Passes in progress | Searches starting inside each of the 25 passes | Same rise as a search that started before it | By construction |
| Heavens-Above | Its pass list for the default observer, with the same element set epoch where possible | Recorded, not asserted | Different elements and models; the figures go into verification.md |

## CLI

```text
sky now    [--sat 25544]                                # adds: sunlit or in shadow; Sun elevation
sky passes [--sat 25544] [--count 5] [--visible]        # rise, peak, set to the second; visible parts
```

`--visible` lists only passes with a visible part. Times stay in the observer's IANA zone,
converted only when printed.

## Assumptions added

| # | Assumption | Effect | How it is checked |
|---|---|---|---|
| A9 | Sun position from Meeus's low-accuracy method with one-term nutation | Under 0.017°: at most about 4 s on a twilight boundary, usually under 1 s on a shadow boundary | DE421 comparison, Example 25.a |
| A10 | UTC is used for Terrestrial Time in the Sun formulas | 0.0008° | Included in A9's bound |
| A11 | Geometric shadow: the Sun's center, WGS-84 ellipsoid, no atmosphere | The ISS's 8 s penumbra is split at its middle; the atmosphere moves the fade by a few seconds | Documented; Skyfield's sphere reported beside it |
| A12 | Twilight is the Sun's geometric center at −6° | The standard definition of civil twilight | Definition |

## Build order

Each step is one or two small commits, tests first.

1. **Brent's methods.** Root and minimum, with their tests.
2. **Sun.** Meeus's method, Example 25.a, and apparent sidereal time.
3. **Reference data.** Generator extension, DE421 checksum, the new JSON, Sun comparisons.
4. **Shadow.** The function, exact-geometry tests, and transitions against the generator.
5. **Pass finder.** Root-finding for rise, peak, and set; passes in progress; invariants;
   Skyfield comparison at 2 ms.
6. **Visibility.** Visible parts of each pass, twilight, and their comparisons.
7. **CLI.** New columns, `--visible`, and `sky now` additions.
8. **Heavens-Above.** A live comparison, recorded in `docs/verification.md`.
9. **Docs.** `verification.md`, README status, CLAUDE.md milestone line.

## Out of scope

- Brightness (magnitude). It needs each satellite's intrinsic brightness, which CelesTrak's GP
  data does not carry.
- Refraction, penumbra, and atmospheric dimming, beyond stating their size.
- The API and dashboard, which are Milestone 3.

## Plan review

The review confirmed 35 findings, many overlapping. These changed what was built:

- **Sun bound.** Meeus's 0.01° already includes his one-term nutation, so the plan counted it twice.
  The Earth-fixed bound is 0.0115° (0.01° + 0.0006° equation of the equinoxes + 0.00004° GMST
  models + 0.0008° TT = UTC), and the equation of the equinoxes and Meeus Example 12.a are checked
  on their own, because the combined bound could hide a missing or sign-flipped one.
- **Time scales.** The 1950 to 2049 samples compare apparent places at Terrestrial Time; an
  Earth-fixed comparison there would mix in up to 13 s of UT1 − UTC.
- **Peak uncertainty kept**, since the zenith's corner defeats "exact to 10⁻⁹°".
- **Dips below the minimum** between two samples now split a pass, the mirror of the grazing case.
- **Visibility sign changes** get a rate-bound check, so a short eclipse between samples is found.
- **Event bounds** use each event's own rate, not the fastest, which the plan's "about 3 s" did.
- **Crossing invariants** use 1.1×10⁻³° from the 1 ms root tolerance, not 10⁻⁶°.
- **Published anchors** were added for twilight (USNO) and sidereal time (Meeus 12.a), and the
  Heavens-Above comparison became a test once its element set proved to be the reference one.
- **The penumbra's length** depends on the beta angle; A11 says so.
