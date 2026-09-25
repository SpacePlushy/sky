Milestone 3 of Sky Over Phoenix: the dashboard. The plan, and the multi-agent plan review that stood in for the owner's approval, are in `docs/plans/milestone-3-proposal.md`; the verification record is `docs/verification.md`. This branch is based on `milestone-2`; merge that first.

## What's in it

- **Sky.Settings.** The CLI's settings, shared with the API, with three new validated keys: `CelesTrak:Offline`, `Dashboard:Satellites`, and `Clock:StartUtc` (allowed only offline, so a simulated time never reaches the request history).
- **Cache coordination.** An exclusive lock file coordinates every process sharing the cache folder, so the CLI and the API together still make at most one request per group per 2 hours. Offline mode reads only. A refusal cut off mid-body is still recorded as a block.
- **Sky.Api.** An ASP.NET Core minimal API over the orbital core: config, health, satellites, and per-satellite now, track, and passes (with visible parts and sky-plot paths). Times are UTC with a Z. Errors are RFC 9457 problem details. It accepts only loopback Host headers by default, so a DNS-rebinding page cannot read the observer's location.
- **The dashboard** (`web/`). TypeScript and Vite with no UI framework: a world map with the ground track, night side, and footprints (d3-geo and Natural Earth), telemetry, a 7-day pass table, and a polar sky plot. "Now" comes from the server's clock. The built page allows only its own origin.
- **Docker.** `docker compose up --build` serves it on 127.0.0.1:8080. The image never contacts CelesTrak (ADR 0004): it shows recorded demo data, or reads the host CLI's cache read-only.

## Verification

| Check | Result |
|---|---|
| .NET tests | 374, all passing, offline |
| Dashboard unit tests (Vitest) | 134, all passing; six deliberately introduced bugs were each caught |
| `/now` against Skyfield | Within Milestone 1's pipeline bounds |
| `/passes`, `/track`, sky paths | Equal to the orbital core, value for value |
| The Sun's subpoint | Within 0.0115° of DE421 |
| Palette contrast | Every text pair at least 7.37:1, every graphic at least 3.47:1 (WCAG AA) |
| The Docker image | CI plants sentinel private files and scans the exported image and build stage; then runs it offline |

## Review notes

- The plan had a multi-agent review (37 confirmed findings), and the built code had another (22 confirmed findings, about 18 distinct). All are fixed, and the fixes are listed in the plan and in these commits' messages. The most important: an aborted browser request could cancel a CelesTrak download after it was counted, a relative cache path gave the CLI and the API separate request histories, and the API accepted any Host header.
- Screenshots and tests use the public Arizona State Capitol location only; the screenshot script refuses to run otherwise.

## Test plan

- [x] `dotnet test --solution Sky.slnx`: 374 passed
- [x] `dotnet format Sky.slnx --verify-no-changes`: clean
- [x] `npm run typecheck`, `lint`, `test`, `build` in `web/`: clean, 134 passed
- [x] CI green: build and test on ubuntu, macOS, and Windows; web; Docker
- [x] `docker compose up --build` checked by hand at 1440 and 375 px
- [ ] Owner review

🤖 Generated with [Claude Code](https://claude.com/claude-code)
