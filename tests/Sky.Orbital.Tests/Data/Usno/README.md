# USNO civil twilight

Published civil-twilight times for Sky's default observer, from the U.S. Naval Observatory's
Astronomical Applications API, as a reference independent of both Sky and Skyfield. USNO defines
civil twilight as the Sun's center geometrically 6° below the horizon, the same definition Sky uses
(assumption A12), and prints times rounded to the minute.

| File | Request |
|---|---|
| `phoenix-2026-03-20.json` | `https://aa.usno.navy.mil/api/rstt/oneday?date=2026-03-20&coords=33.4478,-112.0972&tz=-7` |
| `phoenix-2026-06-21.json` | same, `date=2026-06-21` |
| `phoenix-2026-09-24.json` | same, `date=2026-09-24` |
| `phoenix-2026-12-21.json` | same, `date=2026-12-21` |

Fetched 2026-09-25 with `curl`, API version 4.0.1, byte for byte. SHA-256:

```text
d64cfee522e34f7a43023d4f08c38e0ee12e1c1f68dc13b5316bd2f564375f3b  phoenix-2026-03-20.json
1cc6054297ab2cdfce245fc01e6be132ee219bd0273b74fe09723a53ae137da0  phoenix-2026-06-21.json
055270e32119f7ccbe38ae8d69584249025689c80a7369b890a71963c746a18b  phoenix-2026-09-24.json
790ddd295f68b1a3f6f5aad4b205a7e16c9fee3e74c834ad8f79cc8f684b8ed9  phoenix-2026-12-21.json
```

The dates are the 2026 equinoxes and solstices and the ISS reference date, so the Sun's elevation
rate at −6° spans its yearly range.
