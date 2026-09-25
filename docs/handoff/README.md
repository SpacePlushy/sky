# Handoff: resume on another machine

Everything needed to continue this project on a different computer, from a plain clone.
Written 2026-09-24 in Phoenix (2026-09-25 02:30 UTC). Update it whenever work moves between
machines.

## Where things stand

- **Milestone 1 is complete** on the `milestone-1` branch: the orbital core and the `sky`
  command-line tool. `main` still holds only the initial scaffold.
- **Checks pass.** All 257 tests pass, lint is clean, and CI passed on Linux, macOS, and
  Windows for commit `77f81ed`. The commit that added this folder changes only documentation.
- **The owner is reviewing the branch.** The pull request is not open yet. The owner will say
  when the review is done.

## Next steps, in order

1. **Finish the review.** Fix anything the owner raises, with a test that fails first, then
   push to `milestone-1`.
2. **Open the pull request when the owner says so.** The description is ready in
   [milestone-1-pr.md](milestone-1-pr.md). The owner merges it; do not enable auto-merge.

   ```bash
   gh pr create --base main --head milestone-1 --title "Milestone 1: orbital core and sky CLI" --body-file docs/handoff/milestone-1-pr.md
   ```

3. **Propose Milestone 2.** Write `docs/plans/milestone-2-proposal.md` and wait for approval
   before any code. The scope is in `CLAUDE.md`. Milestone 1 deliberately left these for it:
   - Rise and set come from a 10 s grid; Milestone 2 refines them by root-finding.
   - A pass already in progress when a search starts is not listed.
   - Visibility (satellite sunlit, sun below −6° at the observer) is not computed yet.
   - Elevation is geometric, with no refraction (assumption A8 in `docs/verification.md`).

## Set up the other machine

### 1. Install the tools

| Tool | Needed for | macOS (Homebrew) | Windows (winget) | Linux |
|---|---|---|---|---|
| .NET 10 SDK, 10.0.100 or later | Build, test, run | `brew install dotnet` | `winget install Microsoft.DotNet.SDK.10` | Your distribution's `dotnet-sdk-10.0` package, or https://dotnet.microsoft.com/download |
| Git | Everything | Xcode Command Line Tools | `winget install Git.Git` | Your distribution's `git` package |
| GitHub CLI | Opening the pull request | `brew install gh` | `winget install GitHub.cli` | https://cli.github.com |
| uv | Only to regenerate the Skyfield reference data | `brew install uv` | `winget install astral-sh.uv` | https://docs.astral.sh/uv |

Docker and Colima are not needed until Milestone 3. Python is not needed to run the tests.

On macOS, Homebrew's .NET also needs `DOTNET_ROOT`, or built programs cannot find the
runtime. Add it once, then open a new terminal:

```bash
echo 'export DOTNET_ROOT="$(brew --prefix dotnet)/libexec"' >> ~/.zprofile
```

Sign in to GitHub yourself with `gh auth login`.

### 2. Clone and switch to the branch

```bash
git clone https://github.com/SpacePlushy/sky.git
cd sky
git switch milestone-1
```

### 3. Set the commit identity for this repository

The identity is set per repository, not globally, so a new clone does not have it. Without
it, commits do not link to the GitHub account.

```bash
git config user.name "Frankie Palmisano"
git config user.email "171470977+SpacePlushy@users.noreply.github.com"
```

### 4. Verify the clone

```bash
dotnet build Sky.slnx
dotnet test --solution Sky.slnx
dotnet format Sky.slnx --verify-no-changes
```

Expect 257 tests passed, none failed, and no formatting changes. The tests need no network.

### 5. Your location (optional)

The committed default observer is the Arizona State Capitol. To use your real location, copy
`src/Sky.Cli/appsettings.Local.example.json` to `src/Sky.Cli/appsettings.Local.json` and edit
it. That file is gitignored and must never be committed; `git status` should not list it.
The old machine has no such file, so there is nothing to copy.

### 6. The CelesTrak cache (read before the first `sky` run)

The cache does not travel with the repository, so a new machine starts empty and its first
`sky now` or `sky passes` downloads the `stations` group. CelesTrak refuses a repeat download
from the same public IP address until its data next updates, which happens every 2 hours. It
answers with HTTP 403, and Sky then blocks that group until someone runs `sky unblock`.

The old machine last asked CelesTrak at **2026-09-25 01:12:37 UTC** (18:12 on 2026-09-24 in
Phoenix). Before the first run, do one of these:

- **Copy the cache** from the old machine: both `stations.json` and `stations.state.json`.
  The new machine then serves that data until it is 6 hours old and keeps the old machine's
  request history.
- **Wait** until 2 hours after the last request, which is 03:12 UTC on 2026-09-25.
- **Use a different network**, so the request comes from a different address.

| System | Cache folder |
|---|---|
| macOS (the old machine) | `~/Library/Application Support/sky/celestrak` |
| Linux | `~/.local/share/sky/celestrak`, or `$XDG_DATA_HOME/sky/celestrak` |
| Windows | `%LOCALAPPDATA%\sky\celestrak` |

If a group does get blocked, read the error Sky printed, then clear it:

```bash
dotnet run --project src/Sky.Cli -- unblock stations
```

### 7. Run it

```bash
dotnet run --project src/Sky.Cli -- now
dotnet run --project src/Sky.Cli -- passes
```

## What does not travel with a clone

| Item | Where it lives now | On the new machine |
|---|---|---|
| Commit identity | This repository's local git config | Step 3 |
| `DOTNET_ROOT` for Homebrew .NET | `~/.zprofile` | Step 1 |
| Your location | Nowhere yet | Step 5 |
| CelesTrak cache and request history | The macOS cache folder above | Step 6 |
| GitHub CLI sign-in | The old machine's keychain | `gh auth login` |
| Claude Code memory and session history | The old machine only | This file and `CLAUDE.md` replace them |

## Resuming with Claude Code

Open Claude Code in the clone. `CLAUDE.md` loads by itself and points here. Then say, for
example:

> Read docs/handoff/README.md and pick up where we left off. My Milestone 1 review is not done yet.

Working agreements from the Milestone 1 sessions, beyond what `CLAUDE.md` already says:

- **Correct math comes first.** The owner's words: "no bandaid fixes" and "I dont want any
  errors". Every result is checked against an independent reference, and docs report
  measured worst cases.
- **Wait for the owner's word** before opening the pull request and before starting
  Milestone 2.
- **Watch the pace.** Milestone 1 took about 3 hours, including three review passes, and the
  owner asked to finish it without another one. Agree on how much review Milestone 2 gets
  before it starts.
- **Multi-agent workflows.** The owner turned on ultracode for the final Milestone 1 review.
  It is a per-session setting, so it is off in a new session unless the owner turns it on.

## Milestone 1 in brief

- **Plan.** [docs/plans/milestone-1-proposal.md](../plans/milestone-1-proposal.md) was
  approved. Every change from it is listed in [docs/verification.md](../verification.md)
  under "Changes from the approved plan".
- **Decisions.** ADR 0001 covers the SGP4 source, ADR 0002 the CelesTrak cache policy, and
  ADR 0003 the Earth rotation rate. They are in [docs/adr](../adr).
- **Reviews.** Two review rounds, then a multi-agent adversarial review. Its confirmed
  findings are fixed in commits `e547629` through `77f81ed`.
- **Reference sources.** Seven problems in reference data and libraries surfaced and are
  documented in `docs/verification.md`.
- **Live data.** One live CelesTrak run, at 2026-09-25 01:12 UTC, succeeded end to end.
  Everything else uses recorded fixtures.

## Loose ends

- The README's "Why I built this" section is a placeholder for the owner to write.
- The "Local toolchain (macOS)" section of `CLAUDE.md` describes the old machine.
