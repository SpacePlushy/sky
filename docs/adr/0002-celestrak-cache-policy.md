# ADR 0002: CelesTrak cache policy

**Status:** Accepted, 2026-09-24

## Context

CelesTrak's usage policy (https://celestrak.org/usage-policy.php), checked 2026-09-24:

- GP data updates every 2 hours. Download each dataset at most once per update. Since
  2026-03-26, a repeat download inside the window returns HTTP 403.
- 50 responses of 301, 403, or 404 within 2 hours firewall the client's IP address.
- Automated clients must stop querying on any non-200 response and report it to a person.
- Responses carry no `Last-Modified`, `ETag`, or `Cache-Control` headers.

## Decision

`GpCache` decides whether a request may be made at all. `CelesTrakClient` makes exactly one
request per call and never retries.

| Rule | Value |
|---|---|
| Refresh | On demand, when cached data is over 6 hours old |
| Minimum interval | 2 hours after the last request for that group, even when forced. The attempt is recorded on disk before the request is sent, so an interrupted run still counts. |
| Any non-200 answer, or a 200 without valid element sets | Block that group until a person runs `sky unblock`; report the answer word for word; keep serving cached data. The status is read before the body, so a non-200 whose body is lost still blocks. |
| No answer at all (network failure or timeout), or a 200 whose body is lost | Back off 2, 4, 8, 16, then 24 hours. Any answer from CelesTrak resets the count. |
| Timeouts | 30 s for the status and headers, then 30 s more for the body |
| Redirects | Not followed, so a 301 surfaces as an error |
| Writes | Only validated responses are written, via a temporary file and rename |
| State | On disk next to the data, so the rules hold across restarts. A relative cache directory resolves against the settings folder, so every run shares one state. |
| Unreadable state file | Treated as blocked, since it may have recorded a block, with its modification time as the last request |
| Clock moved back | A last request recorded in the future counts from now and is saved, so the group is not locked out until that date. Data downloaded in the future counts as due. |
| Stale data | Warn when the newest epoch is over 3 days old |

The CLI searches groups in order and stops at the first that holds the requested satellite,
so it never downloads a group it does not need.

## Consequences

- No process can exceed CelesTrak's limits, even across restarts or interrupted runs, and none
  can silently accumulate errors.
- A CelesTrak outage that returns 5xx needs a person to run `sky unblock`. That is deliberate:
  the policy asks for a human in the loop on any non-200 answer.
- Two processes sharing one cache directory on one filesystem, such as the CLI and the dashboard
  API, are coordinated by an exclusive lock on a `.lock` file there (added in Milestone 3). A
  process that cannot get the lock within 2 minutes serves cached data instead of requesting. The
  lock does not reach across a container boundary, so the Docker image never requests at all;
  see [ADR 0004](0004-offline-container.md).
