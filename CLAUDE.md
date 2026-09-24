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

1. **Orbital core.** CelesTrak GP fetch (JSON/OMM) with local caching, SGP4
   via an established library, TEME to ECEF to geodetic and topocentric
   transforms, Vallado verification cases as xUnit tests, and a CLI that prints
   the ISS position and next 5 passes.
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
- **Observer privacy.** Committed config defaults to a public Phoenix
  landmark. Real coordinates go only in a gitignored `appsettings.Local.json`.
  The `.gitignore` also excludes `*.local.json` and `.env*`. Check
  `git status` before every commit.

## Commands

Run from the repo root.

```bash
dotnet build Sky.slnx                             # build everything
dotnet test --solution Sky.slnx                   # run all tests
dotnet format Sky.slnx --verify-no-changes        # lint; CI fails on any diff
dotnet format Sky.slnx                            # fix formatting
```

The CLI arrives later in Milestone 1. The API, web app, and `docker compose up`
arrive in Milestone 3.

## Repo layout

- `src/Sky.Sgp4` is Vallado's reference SGP4, kept as close to upstream as
  possible. Do not edit its math. Formatting and analyzers skip it on purpose.
  See its `NOTICE.md`.
- `src/Sky.Orbital` is pure math: no I/O, no network, no clock access.
- `tests/*` mirror `src/*`. Reference data lives next to the tests that use it.
- `docs/plans` holds approved milestone plans. `docs/adr` records decisions.

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
