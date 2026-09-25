import { describe, expect, it } from "vitest";
import { ApiError, api, describeFailure, getJson, isAbort, type Fetch } from "./api";

// Problem details as ASP.NET Core writes them for this API (TypedResults.Problem), RFC 9457.
const unknownSatellite = JSON.stringify({
    type: "https://tools.ietf.org/html/rfc9110#section-15.5.5",
    title: "Unknown satellite",
    status: 404,
    detail: "NORAD 99999 is not in stations.",
});

function respond(status: number, body: string, contentType: string, statusText = ""): Fetch {
    return () => Promise.resolve(new Response(body, { status, statusText, headers: { "Content-Type": contentType } }));
}

async function failure(promise: Promise<unknown>): Promise<ApiError> {
    try {
        await promise;
    } catch (error) {
        if (error instanceof ApiError) {
            return error;
        }
        throw error;
    }
    throw new Error("expected the request to fail");
}

describe("describeFailure", () => {
    it("reads the title and detail of problem details", () => {
        expect(describeFailure(404, "Not Found", "application/problem+json", unknownSatellite)).toEqual({
            title: "Unknown satellite",
            detail: "NORAD 99999 is not in stations.",
        });
    });

    it("accepts media type parameters and plain JSON", () => {
        expect(describeFailure(404, "", "application/problem+json; charset=utf-8", unknownSatellite).title).toBe("Unknown satellite");
        expect(describeFailure(404, "", "application/json", unknownSatellite).title).toBe("Unknown satellite");
    });

    it("has no detail when the problem gives none or an empty one", () => {
        const noDetail = JSON.stringify({ title: "Not found", status: 404 });
        expect(describeFailure(404, "", "application/problem+json", noDetail)).toEqual({ title: "Not found", detail: null });
        const emptyDetail = JSON.stringify({ title: "Not found", detail: "  " });
        expect(describeFailure(404, "", "application/problem+json", emptyDetail).detail).toBeNull();
    });

    it("keeps the offline 503's explanation", () => {
        const body = JSON.stringify({ title: "No orbital data", status: 503, detail: "No element sets are available. Offline mode: nothing is cached for stations." });
        expect(describeFailure(503, "Service Unavailable", "application/problem+json", body)).toEqual({
            title: "No orbital data",
            detail: "No element sets are available. Offline mode: nothing is cached for stations.",
        });
    });

    it("falls back to the status when the body is not problem details", () => {
        expect(describeFailure(500, "Internal Server Error", "application/problem+json", "<html>oops</html>")).toEqual({ title: "HTTP 500 Internal Server Error", detail: null });
        expect(describeFailure(500, "", "application/json", JSON.stringify({ message: "no title" }))).toEqual({ title: "HTTP 500", detail: null });
        expect(describeFailure(500, "", "application/json", JSON.stringify({ title: 42 }))).toEqual({ title: "HTTP 500", detail: null });
        expect(describeFailure(418, "", null, "")).toEqual({ title: "HTTP 418", detail: null });
    });

    it("says the API is unreachable when a proxy answers 502 or 504 without problem details", () => {
        expect(describeFailure(502, "Bad Gateway", "text/html", "<html></html>")).toEqual({ title: "Cannot reach the API", detail: "The server answered 502 Bad Gateway." });
        expect(describeFailure(504, "", "text/plain", "")).toEqual({ title: "Cannot reach the API", detail: "The server answered 504." });
    });
});

describe("getJson", () => {
    it("returns the parsed body of a success", async () => {
        await expect(getJson("/api/x", undefined, respond(200, "{\"a\":1}", "application/json"))).resolves.toEqual({ a: 1 });
    });

    it("throws an ApiError with the problem's status, title, and detail", async () => {
        const error = await failure(getJson("/api/satellites/99999/now", undefined, respond(404, unknownSatellite, "application/problem+json", "Not Found")));
        expect(error.status).toBe(404);
        expect(error.title).toBe("Unknown satellite");
        expect(error.detail).toBe("NORAD 99999 is not in stations.");
        expect(error.path).toBe("/api/satellites/99999/now");
        expect(error.message).toBe("Unknown satellite: NORAD 99999 is not in stations.");
    });

    it("reports a network failure as status 0", async () => {
        const offline: Fetch = () => Promise.reject(new TypeError("fetch failed"));
        const error = await failure(getJson("/api/config", undefined, offline));
        expect(error.status).toBe(0);
        expect(error.title).toBe("Cannot reach the API");
        expect(error.detail).toBe("fetch failed");
    });

    it("reports a success that is not JSON", async () => {
        const error = await failure(getJson("/api/config", undefined, respond(200, "<!doctype html>", "text/html")));
        expect(error.title).toBe("Unexpected response");
    });

    it("rethrows cancellation instead of reporting it", async () => {
        const controller = new AbortController();
        const hang: Fetch = (_input, init) => new Promise((_resolve, reject) => {
            init?.signal?.addEventListener("abort", () => {
                reject(new DOMException("The operation was aborted.", "AbortError"));
            });
        });
        const pending = getJson("/api/satellites/25544/now", controller.signal, hang);
        controller.abort();
        const error: unknown = await pending.catch((e: unknown) => e);
        expect(error).not.toBeInstanceOf(ApiError);
        expect(isAbort(error)).toBe(true);
    });
});

/** A copy of an object without one key. */
function without(o: object, key: string): Record<string, unknown> {
    return Object.fromEntries(Object.entries(o).filter(([k]) => k !== key));
}

describe("api", () => {
    const nowBody = () => ({
        timeUtc: "2026-09-24T04:00:00Z",
        satellite: { id: 25544, name: "ISS (ZARYA)", epochUtc: "2026-09-23T19:37:41.123Z", ageDays: 0.35, periodMinutes: 92.9 },
        position: { latitudeDeg: 12.5, longitudeDeg: -100.25, altitudeKm: 418.2, inertialSpeedKmS: 7.66 },
        look: { azimuthDeg: 200, elevationDeg: -30, rangeKm: 5000, rangeRateKmS: -3.1 },
        sunlit: true,
        sun: { elevationDeg: -20, subsolarLatitudeDeg: -0.9, subsolarLongitudeDeg: 118.2 },
        footprintRadiusDeg: 20.1,
        visibilityRadiusDeg: 12.3,
        currentPass: null,
        warnings: [],
    });

    it("reads a /now response into instants and numbers", async () => {
        const now = await api.now(25544, undefined, respond(200, JSON.stringify(nowBody()), "application/json"));
        // 2026-09-24T04:00:00Z = 20,720 days after the epoch plus 4 hours.
        expect(now.t).toBe(20_720 * 86_400_000 + 4 * 3_600_000);
        expect(now.satellite.epoch).toBe(20_719 * 86_400_000 + 19 * 3_600_000 + 37 * 60_000 + 41_123);
        expect(now.currentPass).toBeNull();
        expect(now.position.longitudeDeg).toBe(-100.25);
    });

    it("names the field when a response breaks the contract", async () => {
        const body = nowBody();
        const broken = { ...body, satellite: without(body.satellite, "id") };
        const error = await failure(api.now(25544, undefined, respond(200, JSON.stringify(broken), "application/json")));
        expect(error.title).toBe("Unexpected response");
        expect(error.detail).toContain("now.satellite.id");
    });

    it("names the object when a nested object is missing", async () => {
        const broken = without(nowBody(), "position");
        const error = await failure(api.now(25544, undefined, respond(200, JSON.stringify(broken), "application/json")));
        expect(error.title).toBe("Unexpected response");
        expect(error.detail).toContain("now.position is not an object");
    });

    it("rejects an instant without a Z", async () => {
        const body = { observer: { name: "x", latitudeDeg: 0, longitudeDeg: 0, heightM: 0, timeZone: "UTC" }, minimumElevationDeg: 10, satellites: [25544], offline: true, clockSimulated: true, serverTimeUtc: "2026-09-24T04:00:00" };
        const error = await failure(api.config(undefined, respond(200, JSON.stringify(body), "application/json")));
        expect(error.detail).toContain("config.serverTimeUtc");
    });
});
