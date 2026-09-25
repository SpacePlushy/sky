# ADR 0004: The container never contacts CelesTrak

**Status:** Accepted, 2026-09-25 (Milestone 3). Self-reviewed while the owner was away; see
`docs/plans/milestone-3-proposal.md`.

## Context

The dashboard runs as an ASP.NET Core API, locally with `dotnet run` or in Docker. CelesTrak allows
one download of each group per 2-hour update from a public IP address, and the CLI and the API
share that address. ADR 0002's request history lives in the cache folder, and in Milestone 3 an
exclusive file lock coordinates processes that share the folder.

A container breaks both halves of that. Its cache is either a volume, which is a second request
history on the same public address, or a bind mount of the host's folder, where file locks are not
reliably shared across the virtual machine that runs Linux containers on macOS and Windows
(Docker Desktop's VirtioFS and Colima's shares have both had `flock` gaps). Either way the CLI and
the container could each request the same group within 2 hours, and the second request gets a 403,
which counts toward the 50-errors firewall.

## Decision

The Docker image is offline: `CelesTrak:Offline` is fixed to true in the image and in
`compose.yaml`. It serves one of two read-only cache folders:

- **The recorded demo data** (the default): the committed `stations` response from 2026-09-24,
  with the clock started at 2026-09-24 04:00 UTC, so the dashboard works with no network.
- **The host CLI's cache**, bind-mounted read-only with `SKY_CACHE_DIR`, with the simulated clock
  cleared. The CLI stays the one process that fetches; the container picks up new files within a
  minute.

The API may fetch only when it runs on the host, where the lock works on one filesystem.

A simulated clock (`Clock:StartUtc`) is allowed only with offline mode, so a false instant can
never reach the request history.

## Consequences

- `docker compose up` works anywhere with no network and cannot break CelesTrak's rules.
- Live data in the container needs one CLI run on the host first, and another when the data ages.
- The image contains no observer location: `.dockerignore` excludes the local settings file, the
  API never publishes it, and CI plants sentinel files and scans the exported image and build stage for them. A real location reaches
  the container only at run time, from a gitignored `.env.observer` file.
