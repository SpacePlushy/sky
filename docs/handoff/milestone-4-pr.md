Milestone 4 of Sky Over Phoenix: alerts, browser tests, and polish. The plan and the code review that stood in for the owner's approval are in `docs/plans/milestone-4-proposal.md`; the verification record is `docs/verification.md`. This branch is based on `milestone-3`; merge that first.

## What's in it

- **Calendar export.** `GET /api/satellites/{id}/passes.ics` gives an RFC 5545 calendar of visible passes (or all passes), each with a display alarm before it starts. Times are UTC. UIDs come from the revolution number at the pass's peak, so importing a later export updates events instead of duplicating them. Each event carries the element epoch and any warning. Apple Calendar and Outlook keep the alarm; Google Calendar ignores imported alarms and uses its own.
- **Browser alerts.** Opt-in notifications 5, 10, or 15 minutes before a visible pass (or any pass), on the server's clock, while a dashboard tab is open. They are de-duplicated across tabs with a Web Lock, survive a restarted demo clock, and report browsers that cannot show them.
- **Playwright end-to-end tests.** 14 tests drive the built dashboard in Chromium against the real API, offline, and fail on any request that leaves the machine. They run in CI.
- **README for a hiring manager**, with screenshots made by a script that refuses to run unless the observer is the public Arizona State Capitol.

## Verification

| Check | Result |
|---|---|
| .NET tests | 400, all passing, offline |
| Dashboard unit tests | 189, all passing |
| Browser tests (Playwright, Chromium) | 14, all passing, run twice locally and in CI |
| Calendar format | RFC 5545 folding, escaping, UTC times, `DTSTAMP`, alarms, checked directly |
| Calendar events | Equal to the core's visible parts; UIDs identical after a 10 ms or 30 s shift of the elements (the previous key fails) |
| Alerts | Exactly one per pass, across tabs and after a clock restart; each rule unit-tested, and the regressions checked by breaking the code |

## Review notes

- The built milestone had an adversarial multi-agent review; its 17 confirmed findings are fixed in the last commits of this branch and listed in the plan. Two stated limits remain: a duplicate alert across tabs is very unlikely rather than impossible in Chromium (and would replace, not add), and a replayed demo pass alerts again only if a tab checks within about 26 seconds of the restart.
- No live CelesTrak request was made on this branch; see `docs/handoff/README.md`.

## Test plan

- [x] `dotnet test --solution Sky.slnx`: 400 passed
- [x] `dotnet format Sky.slnx --verify-no-changes`: clean
- [x] `npm run typecheck`, `lint`, `test` (189), `build`, `e2e` (14) in `web/`
- [x] CI green: build and test on ubuntu, macOS, and Windows; web; end to end; Docker
- [ ] Owner review
- [ ] Import the calendar file into the owner's calendar app and check the alarm

🤖 Generated with [Claude Code](https://claude.com/claude-code)
