# Handoff: where the project stands, and how to resume it

Everything needed to review, run, and continue this project, on this machine or a fresh clone.
Written 2026-09-25 (UTC) at the end of an unattended session. Update it whenever work moves
between machines or sessions.

## Where things stand

All four milestones are built, each on its own branch, stacked in order. Nothing is merged and no
pull request is open: `main` still holds only the initial scaffold.

| Branch | Built on | What it adds | Checks at its head |
|---|---|---|---|
| `milestone-1` | `main` | Orbital core and the `sky` CLI | 257 .NET tests; CI green on Linux, macOS, Windows |
| `milestone-2` | `milestone-1` | Pass prediction by root-finding, and visibility | 328 .NET tests; CI green |
| `milestone-3` | `milestone-2` | The API, the dashboard, and Docker | 376 .NET and 134 dashboard tests; CI green, including the web and Docker jobs |
| `milestone-4` | `milestone-3` | Pass alerts, the calendar export, browser tests, the README | 400 .NET, 189 dashboard unit, and 14 browser tests; CI runs them all |

**How Milestones 2 to 4 were built.** The owner asked, on 2026-09-25, for the whole project to be
finished while they were away for about 20 hours. So the per-milestone approval step was replaced:
each plan in [docs/plans](../plans) had a multi-agent review (several expert lenses, with a skeptic
checking every finding), and each milestone's code had an adversarial review of the same kind.
Every confirmed finding was fixed on the branch before the next milestone; the plans record what
the reviews changed. Every decision is still open for the owner to overturn.

**No live CelesTrak request has been made from these branches.** The first live run in this
session was blocked by the session's permission settings, so the code has been exercised only
against recorded responses. The CelesTrak client has changed since the one live run (on the old
machine, on 2026-09-25 01:12 UTC, before commit `a9e4bb1`). The first live run is the owner's; see
step 6 below.

## Next steps, in order

1. **Review the milestones in order.** Each has a pull request description ready:
   [milestone-1-pr.md](milestone-1-pr.md), [milestone-2-pr.md](milestone-2-pr.md),
   [milestone-3-pr.md](milestone-3-pr.md), [milestone-4-pr.md](milestone-4-pr.md). Fix anything
   raised with a test that fails first, on the branch it belongs to, then rebase the branches
   above it.
2. **Make the first live run**, after reading step 6: `sky now`, then `sky passes --visible`.
   Compare a few passes with Heavens-Above for your location if you like; Milestone 2's
   same-element-set comparison agreed within 1.1 s.
3. **Open the pull requests when you are ready**, one at a time, merging each before opening the
   next. Use a merge commit, not a squash, so the next branch's history still matches:

   ```bash
   gh pr create --base main --head milestone-1 --title "Milestone 1: orbital core and sky CLI" --body-file docs/handoff/milestone-1-pr.md
   # after it is merged:
   gh pr create --base main --head milestone-2 --title "Milestone 2: pass prediction and visibility" --body-file docs/handoff/milestone-2-pr.md
   gh pr create --base main --head milestone-3 --title "Milestone 3: dashboard" --body-file docs/handoff/milestone-3-pr.md
   gh pr create --base main --head milestone-4 --title "Milestone 4: alerts, browser tests, and polish" --body-file docs/handoff/milestone-4-pr.md
   ```

   The owner merges; do not enable auto-merge. Before each, update its description's test counts
   if review changed the code.
4. **Write the README's "Why I built this"**, which is left for the owner.

## Set up a machine

### 1. Install the tools

| Tool | Needed for | macOS (Homebrew) | Windows (winget) | Linux |
|---|---|---|---|---|
| .NET 10 SDK, 10.0.100 or later (not .NET 11; `global.json` refuses it) | Build, test, run | `brew install dotnet@10` | `winget install Microsoft.DotNet.SDK.10` | Your distribution's `dotnet-sdk-10.0` package |
| Node.js 24 or later | The dashboard | `brew install node` | `winget install OpenJS.NodeJS` | https://nodejs.org |
| Docker, with Compose | `docker compose up` | Docker Desktop, or `brew install colima docker docker-compose` | Docker Desktop | Docker Engine |
| Git | Everything | Xcode Command Line Tools | `winget install Git.Git` | Your distribution's `git` package |
| GitHub CLI | Signing git in to GitHub for `git push` over HTTPS, and the pull requests | `brew install gh` | `winget install GitHub.cli` | https://cli.github.com |
| uv | Only to regenerate the Skyfield reference data | `brew install uv` | `winget install astral-sh.uv` | https://docs.astral.sh/uv |

Python is not needed to run any test. On macOS, Homebrew's .NET also needs `DOTNET_ROOT`, or
built programs cannot find the runtime. Add it once, then open a new terminal:

```bash
echo 'export DOTNET_ROOT="$(brew --prefix dotnet@10)/libexec"' >> ~/.zprofile
```

`dotnet --version` must print 10.0.x. Sign in to GitHub with `gh auth login`, choosing HTTPS and
yes to authenticating Git (or run `gh auth setup-git` afterwards), so `git push` works.

### 2. Clone and switch to the newest branch

```bash
git clone https://github.com/SpacePlushy/sky.git
cd sky
git switch milestone-4
```

### 3. Set the commit identity for this repository

```bash
git config user.name "Frankie Palmisano"
git config user.email "171470977+SpacePlushy@users.noreply.github.com"
```

### 4. Verify the clone

```bash
dotnet build Sky.slnx
dotnet test --solution Sky.slnx                   # 400 pass, offline
dotnet format Sky.slnx --verify-no-changes
cd web
npm ci
npm run typecheck && npm run lint && npm test && npm run build   # 189 unit tests
npx playwright install chromium
npm run e2e                                       # 14 browser tests, offline
```

### 5. Your location (optional)

The committed default observer is the Arizona State Capitol. For your real location, copy
`src/Sky.Cli/appsettings.Local.example.json` to `src/Sky.Cli/appsettings.Local.json` and edit it;
the CLI and the API both read it. It is gitignored, never published, and never in the Docker
image; `git status` must not list it. For the container, put `SKY_Observer__*` variables in a
gitignored `.env.observer` file instead.

### 6. The CelesTrak cache (read before the first live run)

A new machine's cache is empty, so its first `sky now` or `sky passes` downloads the `stations`
group. CelesTrak refuses a repeat download from the same public IP address until its data next
updates, every 2 hours, with HTTP 403, and Sky then blocks that group until someone runs
`sky unblock`. Before the first run on a network another machine has used within 2 hours, copy
that machine's `stations.json` and `stations.state.json`, or wait out the 2 hours.

**A refused run still makes a second request.** The committed setting is
`CelesTrak:Groups = stations,visual`, and Sky tries the groups in order until one holds the
satellite, so a refused `stations` is followed at once by a `visual` download, and the block shows
only as a warning on stderr. For the first run, put `{ "CelesTrak": { "Groups": "stations" } }` in
the gitignored local settings file.

| System | Cache folder |
|---|---|
| macOS | `~/Library/Application Support/sky/celestrak` |
| Linux | `~/.local/share/sky/celestrak`, or `$XDG_DATA_HOME/sky/celestrak` |
| Windows | `%LOCALAPPDATA%\sky\celestrak` |

The CLI and the API run with `dotnet run` share that folder and its lock, so together they still
make at most one request per group per 2 hours. The Docker image never requests at all
([ADR 0004](../adr/0004-offline-container.md)): it shows recorded data, or the CLI's cache
read-only. If a group gets blocked, read the error, then `dotnet run --project src/Sky.Cli --
unblock stations`; the next request is still allowed only 2 hours after the refused one.

### 7. Run it

```bash
dotnet run --project src/Sky.Cli -- now
dotnet run --project src/Sky.Cli -- passes --visible
docker compose up --build          # the dashboard, offline demo, on http://localhost:8080
```

## What does not travel with a clone

| Item | Where it lives | On a new machine |
|---|---|---|
| Commit identity | This repository's local git config | Step 3 |
| `DOTNET_ROOT` for Homebrew .NET | `~/.zprofile` | Step 1 |
| Your location | The gitignored local settings file | Step 5 |
| CelesTrak cache and request history | The cache folder above | Step 6 |
| Dashboard dependencies and browsers | `web/node_modules`, Playwright's browser cache | Step 4 |
| DE421 ephemeris for the reference generator | `tools/reference/.cache` | Downloaded on the generator's first run |
| GitHub CLI sign-in | The keychain | `gh auth login` |
| Claude Code memory and session history | The machine that ran the session | This file and `CLAUDE.md` replace them |

## Resuming with Claude Code

Open Claude Code in the clone after `git switch milestone-4`: `CLAUDE.md` is read once when a
session starts, and `main`'s predates all of this. Then say, for example:

> Read docs/handoff/README.md and pick up where we left off. I am reviewing Milestone 1.

Working agreements, beyond what `CLAUDE.md` says:

- **Correct math comes first.** The owner's words: "no bandaid fixes" and "I dont want any
  errors". Every result is checked against an independent reference, and docs report measured
  worst cases.
- **Wait for the owner's word** before opening pull requests and before new work. The unattended
  run that built Milestones 2 to 4 was a one-time instruction, not a standing approval.
- **Never contact CelesTrak from a test, script, screenshot, or experiment.** Use offline mode
  with `deploy/demo-cache` and a simulated clock (see `web/README.md`).
- **Multi-agent workflows** are a per-session setting; they are off in a new session unless the
  owner turns them on.

## The milestones in brief

- **1. Orbital core.** Vallado's SGP4 (ADR 0001), TEME to geodetic and look angles, the CelesTrak
  policy cache (ADR 0002), the Earth rotation rate (ADR 0003), and the `sky` CLI.
- **2. Pass prediction.** Brent's root-finding for rise, peak, and set; the Sun by Meeus; the
  Earth's shadow on the WGS-84 ellipsoid; visibility. Checked against Skyfield with DE421, USNO's
  civil twilight, and Heavens-Above on the same element set.
- **3. Dashboard.** `Sky.Settings`, cross-process cache coordination, `Sky.Api`, the TypeScript
  dashboard, and an offline Docker image (ADR 0004).
- **4. Alerts and polish.** The iCalendar export with alarms, browser notifications, the Playwright
  suite, and the README with screenshots.

Ten problems found in reference sources, and every change from each plan, are in
[docs/verification.md](../verification.md).

## Loose ends

- The README's "Why I built this" is for the owner to write.
- The "Local toolchain (macOS)" table in `CLAUDE.md` describes the old machine (Colima, Node 24).
  The machine that built Milestones 2 to 4 uses Docker Desktop and Node 26 with .NET 10.0.401.
- The first live CelesTrak run, as above.
