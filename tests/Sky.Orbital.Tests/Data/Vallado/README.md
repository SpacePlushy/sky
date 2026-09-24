# Vallado SGP4 verification data

These files come from the verification set published with Vallado, Crawford,
Hujsak, and Kelso, "Revisiting Spacetrack Report #3," AIAA 2006-6753, 2006.
Main page: https://celestrak.org/publications/AIAA/2006-6753/

| File | What it is | Source | SHA-256 |
|---|---|---|---|
| `SGP4-VER.TLE` | 33 test runs over 32 satellites. Line 2 of each element set carries start, stop, and step times in minutes from epoch after column 69. | `AIAA-2006-6753.zip`, file `sgp4/cpp/testsgp4/TestSGP4/SGP4-VER.TLE`. Byte-identical to the copy in python-sgp4. | `d246d1d9d768ace445a38a965713fa9ba52d80fd8a41a0502ff83d7acffe2881` |
| `tcppver.out` | Output of Vallado's C++ driver: minutes since epoch, TEME position in km, and TEME velocity in km/s, followed by osculating elements and a date. Generated with WGS-72 constants in improved mode `'i'`. | python-sgp4 (MIT), commit `8126f773923c2902719d45aab8bdb5cce86cf39d`, file `sgp4/tcppver.out`. The current CelesTrak package no longer ships this file. | `687bf28dbe52df86e8e60ab5cb4a08d1aa3dbcaf4e63b1f7ab95f044fbe3833b` |

Both files were downloaded on 2026-09-24. The CelesTrak FAQ states there is no
license on the code and asks for citation. python-sgp4 redistributes both files
under the MIT license.

## Known quirks

- Seven runs end early on purpose, to exercise SGP4's error codes. The driver
  prints nothing for the failing step, so the output simply stops.
- Satellite 33334 fails at initialization. Its single output line is a stale copy
  of the previous satellite's last state and must be ignored. python-sgp4's test
  suite handles it the same way.
- Satellite 20413 appears twice. The second run starts about 3.5 years after
  epoch and ends in decay.
