Milestone 2 of Sky Over Phoenix: pass prediction by root-finding, and visibility. The plan is in `docs/plans/milestone-2-proposal.md`, including the multi-agent plan review that stood in for the owner's approval, and the verification record is in `docs/verification.md`. This branch is based on `milestone-1`; merge that first.

## What's in it

- **Brent's methods.** Root and minimum finders translated step for step from Brent's 1973 procedures, tested against functions built to defeat them.
- **Pass finder.** Milestone 1's 10 s scan still finds every pass, including grazing ones. Rise and set are then found to 1 ms, and peaks to 0.2 ms. A dip below the minimum between samples splits a pass. Passes in progress at either end of the search are followed to their real rise and set. A pass longer than a day is reported with whichever end lies within reach.
- **The Sun.** Meeus's low-accuracy method, rotated to Earth-fixed with apparent sidereal time.
- **The Earth's shadow.** An exact test on the WGS-84 ellipsoid for the Sun's center, with a continuous signed function to root-find.
- **Visibility.** The visible parts of each pass, where the satellite is sunlit and the Sun is below −6°. Each part records what starts and ends it.
- **CLI.** `sky passes` prints times to the second, a Visible column, and passes in progress; `--visible` filters. `sky now` adds sunlight, the Sun's elevation, the current pass, and the next visible stretch.

## Verification

| Check | Worst case measured |
|---|---|
| Rise and set against Skyfield, 25 passes | 0.23 ms (bound 1 ms) |
| Peak time against Skyfield | 0.072 ms (bound 0.3 ms) |
| Sun direction against JPL DE421, 2026 | 0.0028° (bound 0.0115°) |
| Sun apparent place against DE421, 1950 to 2049 | 0.0092° (bound 0.010°) |
| Shadow transitions against an independent reference, 217 in a week | 16 ms (bounds 0.52 to 0.72 s) |
| Civil twilight against USNO's published times | All 8 on USNO's printed minute |
| Heavens-Above, same element set, 9 visible passes | Rise 0.3 to 1.1 s, set 0.7 to 0.8 s, peaks 0.5 to 1.1 s |

Heavens-Above ends passes 2.0 to 4.6 s earlier where the ISS enters shadow: it fades the satellite through the penumbra, and Sky uses the Sun's center (assumption A11). Its twilight rule is also more permissive than this project's −6°.

## Review notes

- The plan was reviewed by four agents with different lenses, each finding checked by a skeptic. The confirmed findings changed the build; they are listed at the end of the plan.
- The built code then had its own adversarial review (four lenses, a skeptic per finding). All 26 confirmed findings are fixed in the last commits of this branch: long-pass edge cases in the finder and CLI, tests that could not fail, and stale figures in these docs.
- No live CelesTrak request was made on this branch. The session's permission settings blocked the first live run, so the branch head has not yet run against live CelesTrak. See `docs/handoff/README.md`.
- New downloads: JPL's `de421.bsp` for the reference generator only, SHA-256 checked, gitignored.

## Test plan

- [x] `dotnet test --solution Sky.slnx`: all pass
- [x] `dotnet format Sky.slnx --verify-no-changes`: clean
- [x] CI green on ubuntu, macOS, and Windows
- [ ] Owner review
- [ ] First live run on the owner's machine (read the handoff's cache section first)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
