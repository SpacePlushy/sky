// The API client. Errors come back as RFC 9457 problem details (application/problem+json) with a
// title and usually a detail; the page shows both. Anything else that is not a success becomes an
// ApiError too, so callers handle one kind of failure.

import {
    ContractError,
    readConfig,
    readNow,
    readPasses,
    readSatellites,
    readTrack,
    type Config,
    type Now,
    type Passes,
    type SatelliteSummary,
    type Track,
} from "./model";

/** A failed API request: the HTTP status (0 when no response arrived), a title, and a detail. */
export class ApiError extends Error {
    readonly status: number;
    readonly title: string;
    readonly detail: string | null;
    readonly path: string;

    constructor(status: number, title: string, detail: string | null, path: string) {
        super(detail === null ? title : `${title}: ${detail}`);
        this.name = "ApiError";
        this.status = status;
        this.title = title;
        this.detail = detail;
        this.path = path;
    }
}

/** The title and detail of a failed response, from its problem details when it has them. */
export function describeFailure(status: number, statusText: string, contentType: string | null, body: string): { title: string; detail: string | null } {
    const mediaType = (contentType ?? "").split(";")[0]?.trim().toLowerCase() ?? "";
    if (mediaType === "application/problem+json" || mediaType === "application/json") {
        try {
            const problem: unknown = JSON.parse(body);
            if (typeof problem === "object" && problem !== null && "title" in problem && typeof problem.title === "string" && problem.title.trim() !== "") {
                const detail = "detail" in problem && typeof problem.detail === "string" && problem.detail.trim() !== "" ? problem.detail : null;
                return { title: problem.title, detail };
            }
        } catch {
            // Not JSON after all: described below from the status alone.
        }
    }
    // No problem details, most likely from a proxy in front of the API (Vite's, in development)
    // when the API is not running.
    if (status === 502 || status === 504) {
        return { title: "Cannot reach the API", detail: `The server answered ${status}${statusText === "" ? "" : ` ${statusText}`}.` };
    }
    return { title: `HTTP ${status}${statusText === "" ? "" : ` ${statusText}`}`, detail: null };
}

export function isAbort(error: unknown): boolean {
    return error instanceof DOMException && error.name === "AbortError";
}

export type Fetch = (input: string, init?: RequestInit) => Promise<Response>;

/**
 * GETs JSON from the API. Throws ApiError for any failure except cancellation, which rethrows the
 * AbortError so callers can tell "no longer wanted" from "failed".
 */
export async function getJson(path: string, signal?: AbortSignal, fetchImpl: Fetch = fetch): Promise<unknown> {
    let response: Response;
    try {
        response = await fetchImpl(path, { headers: { Accept: "application/json" }, ...(signal === undefined ? {} : { signal }) });
    } catch (error) {
        if (isAbort(error) || signal?.aborted === true) {
            throw error;
        }
        throw new ApiError(0, "Cannot reach the API", error instanceof Error ? error.message : String(error), path);
    }

    let body: string;
    try {
        body = await response.text();
    } catch (error) {
        if (isAbort(error) || signal?.aborted === true) {
            throw error;
        }
        throw new ApiError(response.status, "The response was cut off", error instanceof Error ? error.message : String(error), path);
    }

    if (!response.ok) {
        const { title, detail } = describeFailure(response.status, response.statusText, response.headers.get("Content-Type"), body);
        throw new ApiError(response.status, title, detail, path);
    }

    try {
        return JSON.parse(body) as unknown;
    } catch {
        throw new ApiError(response.status, "Unexpected response", `${path} did not return JSON.`, path);
    }
}

/** GETs and reads one response, reporting a contract mismatch as an ApiError. */
async function get<T>(path: string, read: (value: unknown) => T, signal?: AbortSignal, fetchImpl?: Fetch): Promise<T> {
    const value = await getJson(path, signal, fetchImpl);
    try {
        return read(value);
    } catch (error) {
        if (error instanceof ContractError) {
            throw new ApiError(200, "Unexpected response", `${path}: ${error.message}.`, path);
        }
        throw error;
    }
}

export interface CalendarOptions {
    /** Days ahead, 1 to 10, as for /passes. */
    readonly days: number;
    /** Only the visible parts of passes, or every pass. */
    readonly visibleOnly: boolean;
    /** Minutes before each event its alarm goes off, 0 to 120. */
    readonly alarmMinutes: number;
}

/**
 * The same-origin path of a satellite's iCalendar export, within the limits the API enforces:
 * /api/satellites/25544/passes.ics?days=7&visibleOnly=true&alarm=10. The alarm is always sent,
 * so the link's label never depends on the API's default.
 */
export function calendarPath(id: number, options: CalendarOptions): string {
    const { days, visibleOnly, alarmMinutes } = options;
    if (!Number.isSafeInteger(id) || id <= 0) {
        throw new RangeError(`Not a catalog number: ${id}`);
    }
    if (!Number.isInteger(days) || days < 1 || days > 10) {
        throw new RangeError(`days must be a whole number from 1 to 10: ${days}`);
    }
    if (!Number.isInteger(alarmMinutes) || alarmMinutes < 0 || alarmMinutes > 120) {
        throw new RangeError(`alarm must be a whole number of minutes from 0 to 120: ${alarmMinutes}`);
    }
    const query = new URLSearchParams({ days: String(days), visibleOnly: String(visibleOnly), alarm: String(alarmMinutes) });
    return `/api/satellites/${id}/passes.ics?${query.toString()}`;
}

export const api = {
    config: (signal?: AbortSignal, fetchImpl?: Fetch): Promise<Config> => get("/api/config", readConfig, signal, fetchImpl),
    satellites: (signal?: AbortSignal, fetchImpl?: Fetch): Promise<SatelliteSummary[]> => get("/api/satellites", readSatellites, signal, fetchImpl),
    now: (id: number, signal?: AbortSignal, fetchImpl?: Fetch): Promise<Now> => get(`/api/satellites/${id}/now`, readNow, signal, fetchImpl),
    track: (id: number, signal?: AbortSignal, fetchImpl?: Fetch): Promise<Track> => get(`/api/satellites/${id}/track`, readTrack, signal, fetchImpl),
    passes: (id: number, days: number, signal?: AbortSignal, fetchImpl?: Fetch): Promise<Passes> =>
        get(`/api/satellites/${id}/passes?days=${days}`, readPasses, signal, fetchImpl),
};
