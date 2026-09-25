// Helpers for the end-to-end tests: the API's JSON (only the fields the tests read), time
// formatting written independently of the page's code, and waits for the dashboard's data.

import { expect, type APIRequestContext, type Page } from "@playwright/test";

export interface EventJson {
    readonly timeUtc: string;
    readonly azimuthDeg: number;
    readonly elevationDeg: number;
}

export interface VisibleJson {
    readonly start: EventJson;
    readonly highest: EventJson;
    readonly end: EventJson;
}

export interface PassJson {
    readonly rise: EventJson;
    readonly culmination: EventJson;
    readonly set: EventJson;
    readonly visible: readonly VisibleJson[];
}

export interface PassesJson {
    readonly fromUtc: string;
    readonly toUtc: string;
    readonly passes: readonly PassJson[];
}

export interface NowJson {
    readonly timeUtc: string;
    readonly satellite: { readonly id: number; readonly name: string };
    readonly position: { readonly latitudeDeg: number; readonly longitudeDeg: number };
    readonly look: { readonly elevationDeg: number };
}

export interface ConfigJson {
    readonly observer: { readonly name: string; readonly latitudeDeg: number; readonly longitudeDeg: number; readonly timeZone: string };
    readonly offline: boolean;
    readonly clockSimulated: boolean;
    readonly serverTimeUtc: string;
}

/** GETs JSON from the API, failing the test on any error status. */
export async function getJson<T>(request: APIRequestContext, path: string): Promise<T> {
    const response = await request.get(path);
    expect(response.ok(), `GET ${path} answered ${response.status()}`).toBe(true);
    return (await response.json()) as T;
}

export function ms(utc: string): number {
    const t = Date.parse(utc);
    if (!Number.isFinite(t) || !utc.endsWith("Z")) {
        throw new Error(`Not a UTC instant: ${utc}`);
    }
    return t;
}

// America/Phoenix wall-clock fields from the test runner's own ICU, not from the page's code. The
// en-US short names ("Thu", "Sep") are the ones the page uses.
const phoenixFields = new Intl.DateTimeFormat("en-US", {
    timeZone: "America/Phoenix",
    hourCycle: "h23",
    weekday: "short",
    day: "numeric",
    month: "short",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
});

/** An instant as the page shows it in Phoenix: clock "20:07:26" and date "Thu 24 Sep". */
export function phoenix(t: number): { clock: string; date: string } {
    const f: Record<string, string> = {};
    for (const part of phoenixFields.formatToParts(t)) {
        f[part.type] = part.value;
    }
    const field = (name: string): string => {
        const value = f[name];
        if (value === undefined) {
            throw new Error(`Intl gave no ${name}`);
        }
        return value;
    };
    return { clock: `${field("hour")}:${field("minute")}:${field("second")}`, date: `${field("weekday")} ${field("day")} ${field("month")}` };
}

/** To the nearest second, halves up, as the page and the CLI round pass times. */
export function roundToSecond(t: number): number {
    return Math.round(t / 1000) * 1000;
}

/**
 * The Phoenix clock texts an event time may show as. Pass times are refined by Brent's method to
 * 1 ms, so two searches started at different instants (the page's request and the test's) can put
 * the same event up to about 1 ms apart. When that straddles a half second, the rounded second
 * differs by one; allowing ±2 ms covers exactly that case and nothing more.
 */
export function clockTexts(t: number): Set<string> {
    return new Set([t - 2, t, t + 2].map((x) => phoenix(roundToSecond(x)).clock));
}

/** The calendar's UTC form of an event time (RFC 5545 form 2, rounded to the second), with the same ±2 ms allowance. */
export function icsTimes(t: number): Set<string> {
    return new Set([t - 2, t, t + 2].map((x) => new Date(roundToSecond(x)).toISOString().replace(/[-:]/g, "").replace(/\.\d{3}Z$/, "Z")));
}

/** An angle as the telemetry shows it: "33.45° N", "112.10° W", "−12.3°" (U+2212 minus). */
export function parseAngle(text: string): number {
    const match = /^([−-]?)(\d+(?:\.\d+)?)°(?:\s*([NSEW]))?$/.exec(text.trim());
    if (match === null) {
        throw new Error(`Not an angle: ${JSON.stringify(text)}`);
    }
    const [, minus, digits, hemisphere] = match;
    const negative = (minus !== undefined && minus !== "") !== (hemisphere === "S" || hemisphere === "W");
    return (negative ? -1 : 1) * Number(digits);
}

/** Waits until the dashboard shows telemetry, a pass list, and a sky plot caption. */
export async function waitForDashboard(page: Page): Promise<void> {
    await expect(page.locator(".row-latitude .value")).not.toHaveText("—", { timeout: 20_000 });
    await expect(page.locator("button.pass").first()).toBeVisible({ timeout: 20_000 });
    await expect(page.locator("#sky-caption")).not.toBeEmpty();
}
