// The two offline API servers the end-to-end tests and the screenshots run against. Both serve the
// committed demo cache (deploy/demo-cache, the recorded CelesTrak response) and never contact
// CelesTrak; they differ only in when their simulated clock starts.

/** The public landmark the committed settings use. Screenshots refuse to run for any other observer. */
export const publicObserver = {
    name: "Arizona State Capitol, Phoenix",
    latitudeDeg: 33.4478,
    longitudeDeg: -112.0972,
    heightM: 331,
    timeZone: "America/Phoenix",
} as const;

export interface ApiServer {
    readonly port: number;
    /** SKY_Clock__StartUtc: the server's clock starts here and runs at real speed. */
    readonly clockStartUtc: string;
    readonly url: string;
}

function server(port: number, clockStartUtc: string): ApiServer {
    return { port, clockStartUtc, url: `http://127.0.0.1:${port}` };
}

/** The demo setup, as the Docker image runs it: the clock starts at 2026-09-24 04:00 UTC. */
export const mainServer = server(5095, "2026-09-24T04:00:00Z");

/**
 * For the notification test: 17 min 26 s before the first evening visible ISS pass, which starts at
 * 03:07:26 UTC (20:07:26 in Phoenix). With a 15-minute lead its alert is due about 2.5 minutes after
 * the server starts.
 */
export const alertServer = server(5096, "2026-09-25T02:50:00Z");

/** The ISS, the dashboard's featured satellite. */
export const iss = 25544;
