# Sky Over Phoenix

A mission-control-style dashboard that tracks satellites in real time and
predicts visible passes over a ground observer. The default observer is
Phoenix, Arizona.

It pulls orbital elements from [CelesTrak](https://celestrak.org), propagates
orbits with SGP4, and shows where satellites are now, what's coming overhead
next, and when it's worth going outside to look.

> **Status:** Milestone 1 (orbital core) in progress. Nothing runnable yet.

## Planned stack

- **Backend:** .NET 10, ASP.NET Core minimal API
- **Frontend:** TypeScript + Vite, a 2D world map for ground tracks, and a
  polar sky plot for passes
- **Tests:** xUnit for orbital math and services, Playwright for end-to-end
- **CI:** GitHub Actions
- **Run:** Docker Compose, one command

## Roadmap

1. **Orbital core.** Fetch GP data, SGP4 propagation, coordinate transforms,
   validation against Vallado's published test cases.
2. **Pass prediction.** Rise, culmination, and set times refined by
   root-finding, plus visibility flags.
3. **Dashboard.** Live map, telemetry panel, 7-day pass table, sky plot.
4. **Alerts and polish.** Optional pass notifications, full README, E2E tests.

## Why I built this

_TODO: Frankie to write._

## License

[MIT](LICENSE)
