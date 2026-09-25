# CLAUDE.md

Guidance for Claude Code (and any other contributor) working in this repo.

## Project

**Sky Over Phoenix** is a satellite ground-station dashboard. It fetches GP
orbital elements from CelesTrak, propagates them with SGP4, and predicts
visible passes over a configurable observer (default: Phoenix, AZ).

It is a portfolio project. Correctness matters as much as looks, and the
verification story must be strong enough to explain to a stranger.

## How we work

- **One milestone at a time.** A milestone is done when its tests pass and
  the owner has reviewed it. Do not start the next one without an OK.
- **Propose before building.** For each milestone, propose structure and plan
  first and wait for approval before writing code.
- **Small, focused commits** with clear imperative messages.
- **State assumptions.** Whenever you assume something about orbital mechanics
  or a data source, say so explicitly and note how it is verified (test case,
  reference tool, or published source).
- If a different technology choice is clearly better than the stack below,
  explain why before switching.

## Milestones

1. **Orbital core.** Done, in review. Plan: `docs/plans/milestone-1-proposal.md`.
   CelesTrak GP fetch (JSON/OMM) with a policy-enforcing cache, Vallado's
   reference SGP4 (ADR 0001), TEME to Earth-fixed to geodetic and topocentric,
   and a CLI that prints the ISS position and next 5 passes (rise and set on a
   10 s grid, peaks refined to 0.1 s).
2. **Pass prediction.** Rise, max elevation, and set with root-finding
   refinement. Visibility means satellite sunlit and sun below -6 degrees at
   the observer. Target accuracy is a few seconds against reference tools.
3. **Dashboard.** Live map with ground track, telemetry panel, 7-day pass
   table, polar sky plot. Dark mission-control look, mobile-friendly.
4. **Alerts and polish.** Optional notifications, hiring-manager README,
   Playwright E2E tests.

## Stack

| Layer    | Choice                                   |
| -------- | ---------------------------------------- |
| Backend  | .NET 10, ASP.NET Core minimal API        |
| Frontend | TypeScript + Vite, no paid map APIs      |
| Tests    | xUnit (math, services), Playwright (E2E) |
| CI       | GitHub Actions: build, test, lint        |
| Run      | Docker Compose                           |

## Conventions

- **Never write SGP4 from scratch.** Use an established library or a port of
  Vallado's reference implementation.
- **Respect CelesTrak.** Follow their current usage guidelines, cache GP data
  locally, and never request more often than allowed.
- **Time zones are IANA names** (`America/Phoenix`) everywhere. Never
  hardcode UTC offsets. Arizona does not observe DST, and the code must not
  depend on that fact.
- **Internal times are UTC.** Convert to the observer's zone only at the edge
  (API response formatting or UI).
- **No secrets in the repo.** No API keys or tokens in committed files.
- **No network in tests.** Tests use recorded fixtures and fake clocks. Never
  point a test, script, or experiment at live CelesTrak; the cache enforces its
  2-hour rule, and repeated requests get the IP address firewalled.
- **Observer privacy.** Committed config defaults to a public Phoenix
  landmark. Real coordinates go only in a gitignored `appsettings.Local.json`.
  The `.gitignore` also excludes `*.local.json` and `.env*`. Check
  `git status` before every commit.

## Verification rules

The owner's bar is that the math is correct, with no errors and no band-aid fixes.

- Every math function gets a test against an independent reference (a
  published worked example, or Skyfield where no usable example exists) **and**
  invariant tests over seeded random inputs (round trips, derivatives,
  symmetries).
- Tolerances come from error analysis written in the test comment, decided
  before the test first runs. Never loosen a tolerance to make a test pass.
- When a test fails, find the root cause and measure it. If a reference source
  is at fault, fix the comparison at its source and record the finding in
  `docs/verification.md`.
- Report measured worst cases, not just "within tolerance".

## Commands

Run from the repo root.

```bash
dotnet build Sky.slnx                             # build everything
dotnet test --solution Sky.slnx                   # run all tests
dotnet format Sky.slnx --verify-no-changes        # lint; CI fails on any diff
dotnet format Sky.slnx                            # fix formatting
dotnet run --project src/Sky.Cli -- now           # ISS position now
dotnet run --project src/Sky.Cli -- passes        # next 5 ISS passes
uv run tools/reference/generate_skyfield_reference.py   # regenerate Skyfield data
```

The API, web app, and `docker compose up` arrive in Milestone 3.

## Repo layout

- `src/Sky.Sgp4` is Vallado's reference SGP4, kept as close to upstream as
  possible. Do not edit its math. Formatting and analyzers skip it on purpose.
  See its `NOTICE.md`.
- `src/Sky.Orbital` is pure math: no I/O, no network, no clock access.
- `src/Sky.CelesTrak` is OMM parsing, the HTTP client, and the policy cache
  (ADR 0002).
- `src/Sky.Cli` is the `sky` command-line tool and its settings.
- `tests/*` mirror `src/*`. Reference data lives next to the tests that use it,
  with provenance and checksums in a README beside it.
- `tools/reference` generates independent reference data with Skyfield.
- `docs/verification.md` is the verification record. `docs/plans` holds
  approved milestone plans. `docs/adr` records decisions.

## Build settings

- `Directory.Build.props` applies nullable, warnings as errors, and the
  recommended analyzer set to every project except `Sky.Sgp4`.
- `Directory.Packages.props` holds every NuGet version. Project files never
  carry a `Version` attribute.
- `global.json` pins the .NET 10 SDK and selects Microsoft Testing Platform as
  the test runner. xUnit v3 needs it.

## Local toolchain (macOS)

Installed with Homebrew formulae. No casks, no sudo, no Docker Desktop.

```bash
brew install dotnet colima docker docker-compose
```

| Tool           | Verified version                          |
| -------------- | ----------------------------------------- |
| .NET SDK       | 10.0.401 (ASP.NET Core runtime 10.0.12)   |
| Node / npm     | 24.21 / 11.19                             |
| Docker CLI     | 29.8.1, with Compose 5.5.1                |
| Colima         | 0.10.3 (Linux VM that runs the containers)|

Two one-time setup steps, already done on the owner's machine:

- `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec` is exported in
  `~/.zprofile`. Without it, built apphosts cannot find the runtime.
- `~/.docker/config.json` sets `cliPluginsExtraDirs` to
  `/opt/homebrew/lib/docker/cli-plugins` so `docker compose` works.

Start the container runtime before any Docker command:

```bash
colima start
```
