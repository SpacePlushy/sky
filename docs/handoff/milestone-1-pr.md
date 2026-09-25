Milestone 1 of Sky Over Phoenix: the orbital core and a `sky` command-line tool. The plan is in `docs/plans/milestone-1-proposal.md`, and the full verification record is in `docs/verification.md`.

## What's in it

- **SGP4.** Vallado's reference C# SGP4 (AIAA 2006-6753), trimmed to the propagation code. It restores five early returns that the C# port had commented out; the C++ reference has them. Provenance and every change are in `src/Sky.Sgp4/NOTICE.md` and ADR 0001.
- **Time and frames.** Split Julian dates, IAU-82 GMST and its exact rate, TEME to Earth-fixed (ADR 0003), WGS-84 geodetic by Vermeille's closed form, and topocentric look angles with range rate.
- **Pass finding.** Rise and set on a 10 s grid. Every local elevation maximum is refined in 0.1 s steps, and each pass carries its own peak-elevation uncertainty. Grazing and multi-maximum passes are handled. Milestone 2 replaces the rise and set grid with root-finding.
- **CelesTrak.** OMM JSON parsing, and a disk cache that enforces the usage policy (ADR 0002). It allows one request per group every 2 hours, and the attempt is saved before the request is sent. Any non-200 answer blocks the group until `sky unblock`. Network failures back off from 2 to 24 hours.
- **CLI.** `sky now`, `sky passes`, and `sky unblock`. Settings are layered: committed Phoenix defaults, then a gitignored local file, then `SKY_` environment variables. Unknown keys and non-IANA time zones are rejected.

## Verification

All 257 tests run offline. CI runs them on Linux, macOS, and Windows, and weekly.

| Check | Worst case measured |
|---|---|
| Vallado's 666 SGP4 verification states | 0.117 mm |
| Vallado's 7 expected SGP4 error codes | All exact |
| Full pipeline against Skyfield: position, look angles | 0.0008 mm, 5e-11° |
| Pass peaks against refined Skyfield events | Within 0.046 s |
| Overhead worst case, lowest-perigee object, 29-day-old elements | 0.1210° low against a stated 0.1216° |

Seven problems in the reference sources surfaced along the way. Each was traced and measured, and all seven are listed in `docs/verification.md`.

## Review notes

- Two review rounds and a multi-agent adversarial review ran on this branch. The adversarial review's confirmed findings are fixed in the commits from e547629 onward.
- Changes from the approved plan are listed in `docs/verification.md` under "Changes from the approved plan".
- The observer default is the Arizona State Capitol. Real coordinates belong in the gitignored `appsettings.Local.json`.

## Test plan

- [x] `dotnet test --solution Sky.slnx`: 257 passed
- [x] `dotnet format Sky.slnx --verify-no-changes`: clean
- [x] CI green on ubuntu, macOS, and Windows
- [ ] Owner review

🤖 Generated with [Claude Code](https://claude.com/claude-code)
