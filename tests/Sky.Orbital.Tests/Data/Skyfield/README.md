# Skyfield reference data

`iss-phoenix-2026-09-24.json` is independent reference output for Sky's full pipeline:
SGP4, Earth rotation, geodetic subpoint, look angles, and pass events. Skyfield runs
Vallado's C++ SGP4 through python-sgp4 and converts frames with its own code, so it shares
no code with Sky.

| Input | Value |
|---|---|
| Elements | ISS (ZARYA), NORAD 25544, epoch 2026-09-24T03:24:21.452544 UTC, from CelesTrak `GROUP=stations`, downloaded 2026-09-24 |
| Observer | Arizona State Capitol, Phoenix: 33.4478°, −112.0972°, 331 m above the WGS-84 ellipsoid |
| States | 133 instants over 3 days, including every 20 s through a 68.8° pass |
| Passes | All 25 complete passes above 10° in 7 days |
| Software | Python 3.14.7, Skyfield 1.55, sgp4 2.27 (C++ accelerated), numpy 2.5.3 |

Regenerate it from the repository root:

```bash
uv run tools/reference/generate_skyfield_reference.py
```

The script pins its dependencies and needs no network access. Regenerating reproduces every
value exactly; only `generated_utc` changes. SHA-256 of the committed file:
`36d7d7a9121b2bddef949422183f7fa58f0c9f3e379288052f1ad906476587a6`.

The file's `notes` list what the generator corrects in Skyfield and python-sgp4 before
writing: the OMM epoch rounding, the three-iteration geodetic latitude, and the half-second
precision of `find_events`. `docs/verification.md` describes each one.
