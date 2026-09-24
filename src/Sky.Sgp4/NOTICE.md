# Sky.Sgp4 provenance

`SGP4Lib.cs` is David Vallado's reference implementation of the SGP4 orbit
propagator, as published with:

> D. A. Vallado, P. Crawford, R. Hujsak, and T. S. Kelso, "Revisiting Spacetrack
> Report #3," AIAA/AAS Astrodynamics Specialist Conference, AIAA 2006-6753, 2006.

Main page: https://celestrak.org/publications/AIAA/2006-6753/

## Source

| Item | Value |
|---|---|
| Package | `AIAA-2006-6753.zip` from the main page above |
| Package `Last-Modified` | Wed, 10 May 2023 23:17:06 GMT |
| Package size | 1,248,284 bytes |
| Package SHA-256 | `3642043b706c76be87cf012db3f22e04da6b80498d00f515e51879e0ffadc115` |
| File in package | `sgp4/cs/SGP4Lib/SGP4Lib/SGP4Lib.cs`, dated 2022-02-09 |
| Upstream file SHA-256 | `e95873daa30874f0cd2b064862ca0038f4dfff9651b74cf6c0b09b66478f3259` |
| Version string in file | `SGP4 Version 2020-03-12` |
| Downloaded | 2026-09-24 |

The file was committed byte-for-byte in the commit titled "Import Vallado's
reference SGP4 source unchanged". Every later change is listed below and can be
seen with `git log -p -- src/Sky.Sgp4/SGP4Lib.cs`.

Nothing here comes from the `CelesTrak/fundamentals-of-astrodynamics` GitHub
repository, which has been AGPL-3.0 licensed since 2025-02-01.

## Terms

The package FAQ (https://celestrak.org/publications/AIAA/2006-6753/faq.php)
states: "There is no license associated with the code and you may use it for any
purpose—personal or commercial—as you wish." It asks that users cite the source
in documentation and source code and link to the main page. This file, the
header comment in `SGP4Lib.cs`, and the project README do that.

## Changes from upstream

The arithmetic is unmodified. These are the only changes:

1. **Added** a citation header comment at the top of the file, as the FAQ asks.
2. **Removed** `using System.Drawing;` and `using System.Windows.Forms;`, which
   are not available on .NET 10 outside Windows.
3. **Removed** `twoline2rv`, the TLE reader. It parses numbers with the current
   culture, so it fails on machines that use a comma as the decimal separator.
   It also contains the interactive Windows Forms and console input paths. Sky
   reads TLEs with its own culture-invariant parser in `Sky.Orbital`, which is
   checked against Vallado's verification output.
4. **Removed** the utility routines `sgn`, `mag`, `cross`, `dot`, `angle`,
   `asinh`, `newtonnu`, `rv2coe`, `jday`, `days2mdhms`, and `invjday`. Nothing
   that remains calls them.
5. **Removed** the `InputBox` class, a Windows Forms dialog.

6. **Restored** the five early `return` statements in `sgp4` that follow error
   codes 1, 2, 3, 4, and 6. The C++ version in the same package returns at each
   of these points ("sgp4fix add return"). The C# version has them commented
   out, so execution continued and the final decay check overwrote the real
   error code with 6. Vallado's verification set exposes this: satellite 33333
   must report error 4 and satellite 33334 must report error 3, and without the
   returns both report 6. Each restored line carries a `// Sky:` comment.

What remains is `elsetrec`, `gravconsttype`, `getgravconst`, `gstime`,
`initl`, `dscom`, `dpper`, `dsinit`, `dspace`, `sgp4init`, and `sgp4`.

## Known differences from the C++ version, left as-is

A statement-by-statement comparison of every remaining function against
`sgp4/cpp/SGP4/SGP4/SGP4.cpp` from the same package found only these
differences, none of which affect improved mode `'i'`:

- **`initl` in AFSPC mode `'a'`.** The C# code uses the older 1970-based
  sidereal-time formula for `gsto` in mode `'a'`. The C++ code computes that
  value but then always uses `gstime`. This changes resonant deep-space orbits
  by nanometers to micrometers in mode `'a'` only. Sky uses mode `'i'`, which
  is the mode Vallado's reference output was generated in.
- **Cosmetic differences.** C# uses `%` where C++ uses `fmod` (the semantics are
  identical for doubles), and C# zero-initializes some locals and the output
  vectors.

## Verification

`tests/Sky.Orbital.Tests/Propagation/Sgp4VerificationTests.cs` runs all 33
cases in Vallado's `SGP4-VER.TLE` and compares 666 states against the
reference output `tcppver.out` to 2e-7 km and km/s, the same bound
python-sgp4 uses. It also checks all seven published error cases.
