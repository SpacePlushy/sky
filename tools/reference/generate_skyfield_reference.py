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
"""Generate independent reference data for Sky's orbital pipeline with Skyfield.

Skyfield runs Vallado's C++ SGP4 through python-sgp4 and converts frames with its own
code, so it shares no code with Sky. The output is committed to
tests/Sky.Orbital.Tests/Data/Skyfield/, so `dotnet test` never needs Python.

Regenerate from the repo root with:

    uv run tools/reference/generate_skyfield_reference.py

Pinned versions and the exclude-newer date make the dependency set reproducible. Skyfield's
built-in time tables supply UT1-UTC; polar motion is off (Skyfield's default), matching
Sky's assumption A4, so the Earth-fixed frame here is the same pseudo Earth-fixed frame
Sky uses.
"""

import json
import math
import platform
import sys
from datetime import datetime, timedelta, timezone
from fractions import Fraction
from pathlib import Path

import numpy
import sgp4
import sgp4.api
import skyfield
from skyfield.api import EarthSatellite, load, wgs84
from skyfield.framelib import itrs

# ISS (ZARYA) from CelesTrak GROUP=stations&FORMAT=JSON, downloaded 2026-09-24.
ISS_OMM = {
    "OBJECT_NAME": "ISS (ZARYA)", "OBJECT_ID": "1998-067A", "EPOCH": "2026-09-24T03:24:21.452544",
    "MEAN_MOTION": 15.49258637, "ECCENTRICITY": 0.00046914, "INCLINATION": 51.6318,
    "RA_OF_ASC_NODE": 170.3464, "ARG_OF_PERICENTER": 174.6338, "MEAN_ANOMALY": 185.4701,
    "EPHEMERIS_TYPE": 0, "CLASSIFICATION_TYPE": "U", "NORAD_CAT_ID": 25544, "ELEMENT_SET_NO": 999,
    "REV_AT_EPOCH": 58709, "BSTAR": 0.00018115501, "MEAN_MOTION_DOT": 9.634e-05, "MEAN_MOTION_DDOT": 0,
}

# Sky's default observer: Arizona State Capitol. Height is above the WGS-84 ellipsoid.
OBSERVER = {"name": "Arizona State Capitol, Phoenix", "latitude_deg": 33.4478, "longitude_deg": -112.0972, "height_m": 331.0}

OUTPUT = Path(__file__).resolve().parents[2] / "tests/Sky.Orbital.Tests/Data/Skyfield/iss-phoenix-2026-09-24.json"

UTC = timezone.utc
EPOCH = datetime.fromisoformat(ISS_OMM["EPOCH"]).replace(tzinfo=UTC)
SAMPLE_START = datetime(2026, 9, 24, 4, 0, 0, tzinfo=UTC)


def set_exact_epoch(model, epoch_string):
    """Give python-sgp4 the OMM epoch exactly, and return how far off its own value was.

    python-sgp4's OMM loader (sgp4.omm.initialize) computes the epoch as float seconds since
    1949 divided by 86400, which stores this epoch 0.36 microseconds late. SGP4 measures time
    from the stored epoch, so every position Skyfield computes would sit 0.36 microseconds
    back along the orbit: 2.8 mm for the ISS. Sky reads the epoch exactly. Setting the stored
    two-part Julian date to the exact value makes every reference quantity in this file derive
    from the epoch the OMM record states. The SGP4 and frame math are untouched.
    """
    stated = datetime.fromisoformat(epoch_string)
    midnight = datetime(stated.year, stated.month, stated.day)
    jd_midnight = 2433281.5 + (midnight - datetime(1949, 12, 31)).days
    fraction = Fraction(stated.hour * 3600 + stated.minute * 60 + stated.second, 86400) + Fraction(stated.microsecond, 86400 * 10**6)
    exact = Fraction(jd_midnight) + fraction

    stored = Fraction(model.jdsatepoch) + Fraction(model.jdsatepochF)
    rounding_us = float((stored - exact) * 86400 * 10**6)
    if model.jdsatepoch != jd_midnight or abs(rounding_us) > 1.0:
        sys.exit(f"Unexpected python-sgp4 epoch: {model.jdsatepoch} + {model.jdsatepochF} is {rounding_us} us from the OMM epoch.")

    model.jdsatepochF = float(fraction)
    residual_us = float((Fraction(model.jdsatepoch) + Fraction(model.jdsatepochF) - exact) * 86400 * 10**6)
    if abs(residual_us) > 1e-3:
        sys.exit(f"Could not set the exact epoch: {residual_us} us remains.")
    return rounding_us


def converged_geodetic(x, y, z):
    """Geodetic latitude and height from Skyfield's own fixed-point formula, run to convergence.

    Skyfield's geographic_position_of (toposlib.Geoid._compute_latitude) stops after three
    iterations, which leaves up to 2e-8 degrees of latitude error at the ISS's altitude, about
    2 mm. Iterating the same formula with the same WGS-84 constants until it stops changing
    gives the exact solution of the geodetic equation, which is the right reference for Sky's
    closed-form conversion. Skyfield's three-iteration values are kept alongside for comparison.
    """
    a = wgs84.radius.km
    f = 1.0 / wgs84.inverse_flattening
    e2 = f * (2.0 - f)
    rho = math.hypot(x, y)
    lat = math.atan2(z, rho)
    for _ in range(100):
        sin_lat = math.sin(lat)
        a_c = a / math.sqrt(1.0 - e2 * sin_lat * sin_lat)
        hyp = z + a_c * e2 * sin_lat
        updated = math.atan2(hyp, rho)
        if abs(updated - lat) <= 1e-16:
            lat = updated
            break
        lat = updated
    else:
        sys.exit(f"Geodetic latitude did not converge for {(x, y, z)}.")
    sin_lat = math.sin(lat)
    a_c = a / math.sqrt(1.0 - e2 * sin_lat * sin_lat)
    hyp = z + a_c * e2 * sin_lat
    return math.degrees(lat), math.sqrt(hyp * hyp + rho * rho) - a_c


def parse_utc(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


def utc_text(instant):
    return instant.isoformat().replace("+00:00", "Z")


def find_refined_passes(ts, sat, observer, start, end):
    """Complete passes above 10 degrees: rise and set refined to under a microsecond, peaks to tens of microseconds."""
    origin = start

    def at(seconds):
        # Seconds since the start, as a float, keep sub-nanosecond resolution over 7 days.
        return ts.utc(origin.year, origin.month, origin.day, origin.hour, origin.minute, origin.second + seconds)

    def look(seconds):
        alt, az, _ = (sat - observer).at(at(seconds)).altaz()
        return float(alt.degrees), float(az.degrees)

    def elevation(seconds):
        return look(seconds)[0]

    def instant(seconds):
        return origin + timedelta(seconds=seconds)

    def bisect(lo, hi, rising):
        # Invariant: elevation(lo) is on the "before" side of 10 degrees, elevation(hi) after it.
        if (elevation(lo) >= 10.0) == rising or (elevation(hi) >= 10.0) != rising:
            sys.exit(f"Bracket {lo}..{hi} s does not straddle the 10 degree crossing.")
        for _ in range(60):
            mid = (lo + hi) / 2
            above = elevation(mid) >= 10.0
            if above == rising:
                hi = mid
            else:
                lo = mid
            if hi - lo < 1e-7:
                break
        return hi if rising else lo

    def golden_max(lo, hi):
        ratio = (math.sqrt(5) - 1) / 2
        a, b = lo, hi
        c, d = b - ratio * (b - a), a + ratio * (b - a)
        fc, fd = elevation(c), elevation(d)
        while b - a > 1e-5:
            if fc >= fd:
                b, d, fd = d, c, fc
                c = b - ratio * (b - a)
                fc = elevation(c)
            else:
                a, c, fc = c, d, fd
                d = a + ratio * (b - a)
                fd = elevation(d)
        return (a + b) / 2

    def event(seconds):
        elev, az = look(seconds)
        return {"utc": utc_text(instant(seconds)), "azimuth_deg": az, "elevation_deg": elev}

    t0 = ts.from_datetime(start)
    t1 = ts.from_datetime(end)
    times, kinds = sat.find_events(observer, t0, t1, altitude_degrees=10.0)
    passes, current = [], {}
    for t, kind in zip(times, kinds):
        seconds = (t.utc_datetime() - origin).total_seconds()
        if kind == 0:
            current = {"rise_guess": seconds}
        elif kind == 1 and "rise_guess" in current:
            current["culmination_guess"] = seconds
        elif kind == 2 and "culmination_guess" in current:
            rise = bisect(current["rise_guess"] - 2.0, current["rise_guess"] + 2.0, rising=True)
            set_ = bisect(seconds - 2.0, seconds + 2.0, rising=False)
            peak = golden_max(current["culmination_guess"] - 2.0, current["culmination_guess"] + 2.0)
            for guess, refined in ((current["rise_guess"], rise), (seconds, set_), (current["culmination_guess"], peak)):
                if abs(guess - refined) > 1.0:
                    sys.exit(f"Refined event moved {refined - guess} s from find_events; expected under 1 s.")
            passes.append({
                "rise": event(rise),
                "culmination": event(peak),
                "set": event(set_),
                "find_events": {
                    "rise_utc": utc_text(instant(current["rise_guess"])),
                    "culmination_utc": utc_text(instant(current["culmination_guess"])),
                    "set_utc": utc_text(instant(seconds)),
                },
            })
            current = {}
    return passes


def vector(xyz):
    return [float(c) for c in xyz]


def subpoint_record(subpoint, itrs_km):
    latitude, height = converged_geodetic(*(float(c) for c in itrs_km))
    truncation = abs(latitude - float(subpoint.latitude.degrees))
    if truncation > 1e-7:
        sys.exit(f"Skyfield's latitude is {truncation} degrees from the converged value; expected under 1e-7.")
    return {
        "latitude_deg": float(subpoint.latitude.degrees),
        "longitude_deg": float(subpoint.longitude.degrees),
        "height_km": float(subpoint.elevation.km),
        "latitude_converged_deg": latitude,
        "height_converged_km": height,
    }


def state_record(ts, sat, observer, instant):
    t = ts.from_datetime(instant)
    minutes = (instant - EPOCH).total_seconds() / 60.0
    error, teme_r, teme_v = sat.model.sgp4_tsince(minutes)
    if error:
        raise RuntimeError(f"SGP4 error {error} at {instant}")

    geocentric = sat.at(t)
    itrs_r, itrs_v = geocentric.frame_xyz_and_velocity(itrs)
    subpoint = wgs84.geographic_position_of(geocentric)
    topocentric = (sat - observer).at(t)
    elevation, azimuth, distance, _, _, range_rate = topocentric.frame_latlon_and_rates(observer)

    return {
        "utc": instant.isoformat().replace("+00:00", "Z"),
        "ut1_minus_utc_s": float(t.dut1),
        "teme_km": vector(teme_r),
        "teme_km_s": vector(teme_v),
        "earth_fixed_km": vector(itrs_r.km),
        "earth_fixed_km_s": vector(itrs_v.km_per_s),
        "subpoint": subpoint_record(subpoint, itrs_r.km),
        "look": {
            "azimuth_deg": float(azimuth.degrees),
            "elevation_deg": float(elevation.degrees),
            "range_km": float(distance.km),
            "range_rate_km_s": float(range_rate.km_per_s),
        },
    }


def main():
    ts = load.timescale()
    if ts.polar_motion_table is not None:
        sys.exit("Polar motion must be off to match Sky's assumption A4.")

    sat = EarthSatellite.from_omm(ts, ISS_OMM)
    epoch_rounding_us = set_exact_epoch(sat.model, ISS_OMM["EPOCH"])
    observer = wgs84.latlon(OBSERVER["latitude_deg"], OBSERVER["longitude_deg"], elevation_m=OBSERVER["height_m"])

    # 100 instants over 3 days, every 43 min 12 s. Whole seconds, so every instant is exact in both
    # Python datetimes and .NET ticks.
    instants = [SAMPLE_START + timedelta(seconds=2592 * k) for k in range(100)]

    # Pass events above 10 degrees over 7 days, for the pass-finding tests. These use a second
    # timescale with UT1 = UTC (constant Delta-T = TT - UTC = 32.184 s + 37 s), which is the
    # assumption Sky's production path makes, so pass comparisons have no UT1 difference.
    # find_events stops refining once its bracket is under half a second, so each event is then
    # refined with Skyfield's own altitude function: bisection for rise and set, golden-section
    # search for the peak. find_events' own times are kept alongside for transparency.
    ts_utc = load.timescale(delta_t=69.184)
    if abs(float(ts_utc.utc(2026, 9, 24).dut1)) > 1e-9:
        sys.exit("The UT1 = UTC timescale is not UT1 = UTC.")
    sat_utc = EarthSatellite.from_omm(ts_utc, ISS_OMM)
    set_exact_epoch(sat_utc.model, ISS_OMM["EPOCH"])
    passes = find_refined_passes(ts_utc, sat_utc, observer, SAMPLE_START, SAMPLE_START + timedelta(days=7))

    # Dense samples every 20 s through the highest pass in the first 3 days, from 2 minutes
    # before rise to 2 minutes after set. This covers elevations from the horizon to the peak.
    early = [p for p in passes if parse_utc(p["rise"]["utc"]) < SAMPLE_START + timedelta(days=3)]
    highest = max(early, key=lambda p: p["culmination"]["elevation_deg"])
    rise = parse_utc(highest["rise"]["utc"]).replace(microsecond=0) - timedelta(minutes=2)
    end = parse_utc(highest["set"]["utc"]) + timedelta(minutes=2)
    k = 0
    while rise + timedelta(seconds=20 * k) <= end:
        instants.append(rise + timedelta(seconds=20 * k))
        k += 1

    data = {
        "generator": "tools/reference/generate_skyfield_reference.py",
        "generated_utc": datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "versions": {
            "python": platform.python_version(),
            "skyfield": skyfield.__version__,
            "sgp4": sgp4.__version__,
            "sgp4_accelerated": bool(sgp4.api.accelerated),
            "numpy": numpy.__version__,
        },
        "python_sgp4_omm_epoch_rounding_us": epoch_rounding_us,
        "notes": [
            "python-sgp4's OMM loader stores the epoch python_sgp4_omm_epoch_rounding_us late; the generator sets the exact OMM epoch before computing anything, so every value here derives from the epoch the record states.",
            "Subpoint latitude_deg and height_km are Skyfield's own values, which stop after three iterations (up to 2e-8 degrees short of convergence). latitude_converged_deg and height_converged_km run the same formula until it stops changing; Sky is compared against those.",
            "Earth-fixed means Skyfield's ITRS with polar motion off: TEME rotated by IAU-82 GMST of UT1.",
            "ut1_minus_utc_s is what Skyfield's built-in tables give for each instant, and Sky's tests feed the same value to Sky, so the comparison checks the math whatever UT1 is.",
            "Skyfield 1.55's built-in UT1-UTC for these dates (about +0.096 s) is out of date: IERS Bulletin A of 24 September 2026 (Vol. XXXIX No. 039) gives -0.013473 s observed on 2026-09-24. Real-world figures for Sky's assumption A5 use the IERS value.",
            "Look angles are geometric: no refraction, no light-time.",
            "Pass events use a second timescale with UT1 = UTC (Sky's production assumption). Each event from Skyfield's find_events (which stops within half a second) is refined with Skyfield's own altitude function: bisection for rise and set to under 1 microsecond, golden-section search for the peak, whose time is reproducible to tens of microseconds because elevation is flat there. find_events' own times are kept under find_events.",
        ],
        "elements": ISS_OMM,
        "observer": OBSERVER,
        "states": [state_record(ts, sat, observer, instant) for instant in instants],
        "passes": passes,
    }
    OUTPUT.write_text(json.dumps(data, indent=1) + "\n")
    print(f"Wrote {len(data['states'])} states and {len(passes)} passes to {OUTPUT}")
    print(f"Highest pass in the first 3 days peaks at {highest['culmination']['elevation_deg']:.2f} degrees")
    truncation = max(abs(s["subpoint"]["latitude_deg"] - s["subpoint"]["latitude_converged_deg"]) for s in data["states"])
    print(f"Skyfield's three-iteration latitude is at most {truncation:.2e} degrees from the converged value")
    dut1 = [s["ut1_minus_utc_s"] for s in data["states"]]
    print(f"UT1-UTC ranges from {min(dut1):.5f} to {max(dut1):.5f} s; sgp4 accelerated: {sgp4.api.accelerated}")
    print(f"python-sgp4 stored the OMM epoch {epoch_rounding_us:.4f} microseconds late; the exact epoch was used")


if __name__ == "__main__":
    main()
