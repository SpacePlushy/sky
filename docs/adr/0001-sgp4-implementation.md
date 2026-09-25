# ADR 0001: SGP4 from Vallado's reference code

**Status:** Accepted, 2026-09-24

## Context

Sky must propagate CelesTrak element sets with SGP4, verified against Vallado's published
test cases, without writing SGP4 from scratch. Live data must come in as OMM, because 5-digit
catalog numbers ran out on 2026-07-11 and new objects cannot be written as TLEs.

## Options considered

| Option | Why not |
|---|---|
| SGP.NET 1.6.0 | Hard-codes WGS-84 constants and has no opsmode switch, so it cannot match Vallado's verification set. |
| One_Sgp4 1.1.0 | Stale since 2024; OMM input is XML only and mishandles the epoch year; SGP4 error codes are never reported. |
| Zeptomoby OrbitTools | Free edition is based on the 1980 report and licensed for non-commercial use only. |
| IO.Astrodynamics | Wraps NASA's SPICE toolkit with native binaries; OMM must be converted to TLE first. |
| Vallado's C# on GitHub | AGPL-3.0 since 2025-02-01, and built for .NET Framework 4.8 with Windows Forms. |

## Decision

Adapt the C# SGP4 from the AIAA 2006-6753 package on CelesTrak (2023-05-10). Its FAQ says there
is no license on the code and asks only for citation. Keep it in its own project, `Sky.Sgp4`:

1. The upstream file is committed byte for byte, with its checksum.
2. A second commit removes only non-propagation code (Windows Forms, the TLE reader, and
   unused utilities).
3. A third commit restores the five early `return` statements the C++ version has, which the
   C# version had commented out. Without them, error codes 3 and 4 are overwritten with 6.

The arithmetic is untouched. `NOTICE.md` in the project lists every change. `Sky.Orbital`
wraps the code in a typed API that takes OMM-style mean elements directly and returns a TEME
state or a named error.

## Consequences

- Sky reproduces all 666 of Vallado's reference states within 0.12 mm and all 7 published
  error codes.
- Sky owns this code. The propagation math upstream has not changed since 2015.
- The upstream TLE reader was culture-dependent, so Sky has its own culture-invariant parser,
  verified by the same reference output.
