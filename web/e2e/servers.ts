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
 * For the notification test: 16 min 26 s before the first evening visible ISS pass, which starts at
 * 03:07:26 UTC (20:07:26 in Phoenix). With a 15-minute lead its alert is due about 1.5 minutes after
 * the server starts.
 *
 * It starts within 17 minutes (the longest lead plus the same-pass tolerance) of that start on
 * purpose. The page forgets a shown alert whose start is further ahead than that, because only a
 * clock that went backwards (a restarted simulated clock) can produce one. The test jumps the page's
 * clock forward and the next /now answer takes it back, which is such a step backwards; starting
 * inside the window means it never looks like a replay.
 */
export const alertServer = server(5096, "2026-09-25T02:51:00Z");

/** The ISS, the dashboard's featured satellite. */
export const iss = 25544;

/**
 * A satellite with passes but no visible part in the week from the main server's start
 * (HRC MONOBLOCK CAMERA in the demo data), so the API has no calendar file for it. Tests check that
 * against the API before relying on it.
 */
export const noVisiblePasses = 66052;
