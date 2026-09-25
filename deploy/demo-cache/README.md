# Demo cache

`stations.json` is the recorded CelesTrak `GROUP=stations` response from
`tests/Sky.CelesTrak.Tests/Fixtures/stations-2026-09-24.json`, byte for byte, named as the cache
expects. The Docker image serves it, offline, with the clock started at 2026-09-24 04:00 UTC, so
`docker compose up` shows a working dashboard with no network access and no request to CelesTrak.
