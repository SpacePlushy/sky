# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "skyfield==1.55",
#   "sgp4==2.27",
#   "numpy==2.5.3",
# ]
# [tool.uv]
# exclude-newer = "2026-09-25T00:00:00Z"
# ///
"""Generate independent reference data for Sky's Sun, shadow, twilight, and visibility.

Milestone 2 companion to generate_skyfield_reference.py, using the same ISS element set and
observer. The Sun comes from JPL's DE421 ephemeris through Skyfield, which shares no code with
Sky's Meeus-based Sun. The Earth's shadow is computed here with its own method, a line-ellipsoid
quadratic, independently of Sky's stretched-sphere closest-approach function. The output is
committed to tests/Sky.Orbital.Tests/Data/Skyfield/, so `dotnet test` never needs Python.

Regenerate from the repo root with:

    uv run tools/reference/generate_skyfield_visibility_reference.py

The first run downloads de421.bsp (about 17 MB) from JPL into tools/reference/.cache/, which is
gitignored, and checks its SHA-256. No request goes to CelesTrak.
"""

import hashlib
import json
import math
import platform
import random
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy
import sgp4
import skyfield
from skyfield.api import EarthSatellite, Loader, wgs84
from skyfield.framelib import itrs

sys.path.insert(0, str(Path(__file__).resolve().parent))
from generate_skyfield_reference import ISS_OMM, OBSERVER, SAMPLE_START, set_exact_epoch, utc_text  # noqa: E402

HERE = Path(__file__).resolve().parent
CACHE = HERE / ".cache"
DE421_SHA256 = "a20a7139da04cbc462454634918e9a9ca69127044e2cc9d4f9c16e238d2deedc"  # recorded at the first download, 2026-09-25
OUTPUT = HERE.parents[1] / "tests/Sky.Orbital.Tests/Data/Skyfield/iss-phoenix-visibility-2026-09-24.json"
M1_REFERENCE = HERE.parents[1] / "tests/Sky.Orbital.Tests/Data/Skyfield/iss-phoenix-2026-09-24.json"

UTC = timezone.utc
WEEK = timedelta(days=7)
TWILIGHT_DEG = -6.0

# WGS-84, as Sky uses it.
A_KM = 6378.137
F = 1.0 / 298.257223563
B_KM = A_KM * (1.0 - F)


def check_ephemeris(path):
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if DE421_SHA256 is None:
        print(f"de421.bsp SHA-256 is {digest}; record it in DE421_SHA256.")
    elif digest != DE421_SHA256:
        sys.exit(f"de421.bsp has SHA-256 {digest}, expected {DE421_SHA256}.")
    return digest


def sunlit_by_quadratic(s, sun):
    """Whether the segment from the satellite s to the Sun's center avoids the WGS-84 ellipsoid.

    Points on the segment are s + t (sun - s) for t in [0, 1]. Substituting into
    x^2/a^2 + y^2/a^2 + z^2/b^2 = 1 gives a quadratic in t; the Sun is hidden when a root lies in
    [0, 1]. A satellite inside the ellipsoid is in shadow. This is a different method from Sky's,
    which stretches z and measures the line's closest approach. Works on 3-vectors or 3xN arrays.
    """
    s = numpy.asarray(s, dtype=float)
    sun = numpy.asarray(sun, dtype=float)
    d = sun - s
    w = numpy.array([1.0 / (A_KM * A_KM), 1.0 / (A_KM * A_KM), 1.0 / (B_KM * B_KM)]).reshape((3,) + (1,) * (s.ndim - 1))
    qa = (w * d * d).sum(axis=0)
    qb = 2.0 * (w * s * d).sum(axis=0)
    qc = (w * s * s).sum(axis=0) - 1.0
    disc = qb * qb - 4.0 * qa * qc
    root = numpy.sqrt(numpy.maximum(disc, 0.0))
    t1 = (-qb - root) / (2.0 * qa)
    t2 = (-qb + root) / (2.0 * qa)
    hidden = (disc >= 0) & (t2 >= 0.0) & (t1 <= 1.0)
    return ~((qc < 0) | hidden)


class Model:
    def __init__(self, loader):
        self.ts = loader.timescale(delta_t=69.184)  # UT1 = UTC; TT = UTC + 69.184 s
        if abs(float(self.ts.utc(2026, 9, 24).dut1)) > 1e-9:
            sys.exit("The UT1 = UTC timescale is not UT1 = UTC.")
        if self.ts.polar_motion_table is not None:
            sys.exit("Polar motion must be off to match Sky's assumption A4.")
        self.eph = loader("de421.bsp")
        self.earth = self.eph["earth"]
        self.sun = self.eph["sun"]
        self.sat = EarthSatellite.from_omm(self.ts, ISS_OMM)
        set_exact_epoch(self.sat.model, ISS_OMM["EPOCH"])
        self.observer = wgs84.latlon(OBSERVER["latitude_deg"], OBSERVER["longitude_deg"], elevation_m=OBSERVER["height_m"])
        self.site = self.earth + self.observer

    def at(self, instant, seconds=0.0):
        return self.ts.utc(instant.year, instant.month, instant.day, instant.hour, instant.minute,
                           instant.second + instant.microsecond / 1e6 + seconds)

    def sun_itrs_km(self, t):
        """The Sun's apparent geocentric position (light time, aberration, deflection) in Skyfield's ITRS."""
        return self.earth.at(t).observe(self.sun).apparent().frame_xyz(itrs).km

    def sun_altitude_deg(self, t):
        """Geometric (unrefracted) apparent altitude of the Sun's center at the observer, with parallax."""
        alt, _, _ = self.site.at(t).observe(self.sun).apparent().altaz()
        return alt.degrees

    def sat_itrs_km(self, t):
        return self.sat.at(t).frame_xyz(itrs).km

    def sunlit(self, t):
        return sunlit_by_quadratic(self.sat_itrs_km(t), self.sun_itrs_km(t))

    def sunlit_skyfield_sphere(self, t):
        return self.sat.at(t).is_sunlit(self.eph)

    def look(self, t):
        alt, az, _ = (self.sat - self.observer).at(t).altaz()
        return float(alt.degrees), float(az.degrees)


def transitions(model, start, end, step_s, state, label_on, label_off):
    """Every change of a boolean state, scanned every step_s seconds and bisected to 1 microsecond.

    The scan evaluates the whole grid at once (state accepts an array Time); bisection then works
    on single instants.
    """
    total = (end - start).total_seconds()
    grid = numpy.arange(0.0, total + step_s / 2, step_s)
    grid[-1] = min(grid[-1], total)
    states = numpy.asarray(state(model.at(start, grid)), dtype=bool)
    events = []
    for k in numpy.nonzero(states[1:] != states[:-1])[0]:
        lo, hi = float(grid[k]), float(grid[k + 1])
        before = bool(states[k])
        while hi - lo > 1e-6:
            mid = (lo + hi) / 2
            if bool(state(model.at(start, mid))) == before:
                lo = mid
            else:
                hi = mid
        at = (lo + hi) / 2
        events.append({"utc": utc_text(start + timedelta(seconds=at)), "kind": label_off if before else label_on})
    return events


def golden_max(f, lo, hi):
    ratio = (math.sqrt(5) - 1) / 2
    a, b = lo, hi
    c, d = b - ratio * (b - a), a + ratio * (b - a)
    fc, fd = f(c), f(d)
    while b - a > 1e-5:
        if fc >= fd:
            b, d, fd = d, c, fc
            c = b - ratio * (b - a)
            fc = f(c)
        else:
            a, c, fc = c, d, fd
            d = a + ratio * (b - a)
            fd = f(d)
    return (a + b) / 2


def parse(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


def visible_windows(model, passes, shadow_events, twilight_events):
    """Each pass's visible parts: sunlit (the quadratic test) and the Sun below -6 degrees."""
    changes = [(parse(e["utc"]), e["kind"]) for e in shadow_events + twilight_events]
    result = []
    for p in passes:
        rise, set_ = parse(p["rise"]["utc"]), parse(p["set"]["utc"])
        bounds = [(rise, "rise")] + sorted((t, k) for t, k in changes if rise < t < set_) + [(set_, "set")]

        def visible(t):
            tt = model.at(t)
            return bool(model.sunlit(tt)) and float(model.sun_altitude_deg(tt)) < TWILIGHT_DEG

        windows = []
        i = 0
        while i < len(bounds) - 1:
            mid = bounds[i][0] + (bounds[i + 1][0] - bounds[i][0]) / 2
            if not visible(mid):
                i += 1
                continue
            first = i
            while i < len(bounds) - 1 and visible(bounds[i][0] + (bounds[i + 1][0] - bounds[i][0]) / 2):
                i += 1
            start, end = bounds[first][0], bounds[i][0]
            span = (end - start).total_seconds()
            best = golden_max(lambda x: model.look(model.at(start, x))[0], 0.0, span)
            candidates = [0.0, span, best]
            top = max(candidates, key=lambda x: model.look(model.at(start, x))[0])
            elev, az = model.look(model.at(start, top))
            windows.append({
                "start_utc": utc_text(start), "starts_because": bounds[first][1],
                "end_utc": utc_text(end), "ends_because": bounds[i][1],
                "highest": {"utc": utc_text(start + timedelta(seconds=top)), "azimuth_deg": az, "elevation_deg": elev},
            })
        result.append({"rise_utc": p["rise"]["utc"], "set_utc": p["set"]["utc"], "windows": windows})
    return result


def main():
    CACHE.mkdir(exist_ok=True)
    loader = Loader(str(CACHE), verbose=False)
    model = Model(loader)
    digest = check_ephemeris(CACHE / "de421.bsp")

    # The Sun every 10 minutes through the ISS week, in the Earth-fixed frame and from the observer.
    week = [SAMPLE_START + timedelta(minutes=10 * k) for k in range(7 * 144)]

    # And at 400 seeded random instants from 1950 to 2049, inside DE421's span, to measure Meeus's
    # method away from 2026. These are apparent right ascension and declination of date against a
    # Terrestrial Time Julian date, compared with Meeus directly: the Earth-fixed frame would bring
    # in Earth rotation, and this timescale's constant TT - UT1 only matches UTC from 2017 on.
    rng = random.Random(2026)
    first_jd, last_jd = 2433282.5, 2469807.5  # 1950-01-01 and 2049-12-31, 0h TT
    century = []
    for _ in range(400):
        whole = first_jd + math.floor(rng.random() * (last_jd - first_jd))
        fraction = round(rng.random() * 86400) / 86400.0
        century.append((whole, fraction))
    century.sort()

    def sun_record(instant):
        t = model.at(instant)
        return {
            "utc": utc_text(instant),
            "earth_fixed_km": [float(c) for c in model.sun_itrs_km(t)],
            "altitude_deg": float(model.sun_altitude_deg(t)),
        }

    def sun_place_record(whole, fraction):
        t = model.ts.tt_jd(whole, fraction)
        ra, dec, distance = model.earth.at(t).observe(model.sun).apparent().radec(epoch="date")
        # Equation of the equinoxes, GAST - GMST, from IAU 2000A nutation with complementary terms.
        eqeq_hours = (float(t.gast) - float(t.gmst) + 12.0) % 24.0 - 12.0
        return {
            "tt_jd_whole": whole,
            "tt_jd_fraction": fraction,
            "right_ascension_deg": float(ra._degrees),
            "declination_deg": float(dec.degrees),
            "distance_au": float(distance.au),
            "equation_of_equinoxes_rad": eqeq_hours * math.pi / 12.0,
        }

    end = SAMPLE_START + WEEK
    shadow = transitions(model, SAMPLE_START, end, 10.0, model.sunlit, "leaves_shadow", "enters_shadow")
    sphere = transitions(model, SAMPLE_START, end, 10.0, model.sunlit_skyfield_sphere, "leaves_shadow", "enters_shadow")
    twilight = transitions(model, SAMPLE_START, end, 60.0, lambda t: model.sun_altitude_deg(t) < TWILIGHT_DEG, "sky_darkens", "sky_brightens")

    # Sun vectors at each shadow transition, so Sky's shadow geometry can be checked with the
    # reference Sun, independently of Sky's own Sun.
    for e in shadow:
        e["sun_earth_fixed_km"] = [float(c) for c in model.sun_itrs_km(model.at(parse(e["utc"])))]

    passes = json.loads(M1_REFERENCE.read_text())["passes"]
    windows = visible_windows(model, passes, shadow, twilight)

    data = {
        "generator": "tools/reference/generate_skyfield_visibility_reference.py",
        "generated_utc": datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "versions": {
            "python": platform.python_version(),
            "skyfield": skyfield.__version__,
            "sgp4": sgp4.__version__,
            "numpy": numpy.__version__,
            "ephemeris": "de421.bsp",
            "ephemeris_sha256": digest,
        },
        "notes": [
            "Timescale: UT1 = UTC and TT = UTC + 69.184 s, constant. UT1 = UTC is Sky's production assumption A5, so no UT1 difference enters the comparison; TT is the true 2026 value.",
            "Sun: apparent geocentric position from DE421 (light time, aberration, gravitational deflection, IAU 2000A nutation), in Skyfield's ITRS with polar motion off: the frame Sky's satellites are in.",
            "sun_century: the Sun's apparent right ascension and declination of date (true equator and equinox) and distance, at Terrestrial Time Julian dates from 1950 to 2049, for comparison with Meeus's apparent place directly, with no Earth rotation involved. equation_of_equinoxes_rad is Skyfield's GAST - GMST (IAU 2000A nutation, complementary terms included) at the same instant.",
            "Sun altitude: the Sun's center seen from the observer, apparent, geometric (no refraction), so it includes parallax.",
            "Shadow: 'shadow' transitions use this script's line-ellipsoid quadratic on WGS-84 with DE421's apparent Sun; 'skyfield_sphere_shadow' uses Skyfield's is_sunlit, a sphere with the geometric Sun (about 0.0057 degrees from the apparent one), for comparison only. Both scan every 10 s (an ISS eclipse lasts about 35 minutes) and bisect to 1 microsecond.",
            "Twilight: the Sun's center crossing -6 degrees at the observer, scanned every 60 s and bisected to 1 microsecond.",
            "Visible windows: the Milestone 1 reference passes (10 degrees), split where the satellite is sunlit by the quadratic test and the Sun is below -6 degrees. The highest point is found by golden-section search on Skyfield's altitude.",
        ],
        "elements": ISS_OMM,
        "observer": OBSERVER,
        "sun_week": [sun_record(i) for i in week],
        "sun_century": [sun_place_record(w, f) for w, f in century],
        "shadow": shadow,
        "skyfield_sphere_shadow": sphere,
        "twilight": twilight,
        "passes": windows,
    }
    OUTPUT.write_text(json.dumps(data, indent=1) + "\n")
    visible = sum(1 for p in windows if p["windows"])
    print(f"Wrote {len(week)} + {len(century)} Sun samples, {len(shadow)} shadow and {len(twilight)} twilight transitions, "
          f"{visible} of {len(windows)} passes visible, to {OUTPUT}")


if __name__ == "__main__":
    main()
