// Pass alerts: which events get a browser notification, when, and never twice. Pure logic: the
// caller passes the server's time, the pass list, and the options. Nothing here reads a clock, the
// DOM, the Notification API, or storage, so every rule is unit-tested with plain numbers.
//
// The rules:
// - One event per upcoming pass. A pass with a visible part alerts for its first visible part's
//   start; a pass with none alerts for its rise, and only when "visible passes only" is off.
// - The alert is due at the event's start minus the lead time, on the server's clock, and stays
//   due until the event starts. A page opened inside that window alerts at once; one opened after
//   the start does not alert at all.
// - An event that has alerted never alerts again. It is remembered by satellite and start time
//   (the key), and also matches a later event of the same satellite whose start or rise moved by
//   less than sameRiseToleranceMs, which is what new elements do to the same pass.
// - A remembered alert whose start is further ahead than the longest lead plus that tolerance is
//   forgotten. No alert is ever shown earlier than its start minus the longest lead, so such an
//   entry means the clock went backwards: the demo's simulated clock restarted and is replaying
//   the same passes, which must alert again.

import { azimuthLabel, fixed } from "./format";
import type { Pass } from "./model";
import { sameRiseToleranceMs } from "./passes";
import { roundToSecond, type ZoneFormat } from "./time";

export const leadChoices = [5, 10, 15] as const;
export type LeadMinutes = (typeof leadChoices)[number];

export interface AlertOptions {
    readonly leadMinutes: LeadMinutes;
    readonly visibleOnly: boolean;
}

export interface AlertPreferences extends AlertOptions {
    readonly enabled: boolean;
}

export const defaultPreferences: AlertPreferences = { enabled: false, leadMinutes: 10, visibleOnly: true };

export function isLeadMinutes(value: unknown): value is LeadMinutes {
    return leadChoices.some((choice) => choice === value);
}

/** Stored preferences, field by field, with the default for anything missing or invalid. */
export function parsePreferences(text: string | null): AlertPreferences {
    if (text === null) {
        return defaultPreferences;
    }
    let value: unknown;
    try {
        value = JSON.parse(text);
    } catch {
        return defaultPreferences;
    }
    if (typeof value !== "object" || value === null) {
        return defaultPreferences;
    }
    const enabled = "enabled" in value && typeof value.enabled === "boolean" ? value.enabled : defaultPreferences.enabled;
    const leadMinutes = "leadMinutes" in value && isLeadMinutes(value.leadMinutes) ? value.leadMinutes : defaultPreferences.leadMinutes;
    const visibleOnly = "visibleOnly" in value && typeof value.visibleOnly === "boolean" ? value.visibleOnly : defaultPreferences.visibleOnly;
    return { enabled, leadMinutes, visibleOnly };
}

/** One event an alert is planned for. Times are milliseconds since the epoch, UTC. */
export interface AlertEvent {
    /** The satellite and the event's start, which identify the alert: "25544@2026-09-25T03:07:26.123Z". */
    readonly key: string;
    readonly satelliteId: number;
    /** A visible part's start, or a rise for a pass with no visible part. */
    readonly kind: "visible" | "rise";
    readonly riseMs: number;
    readonly startMs: number;
    /** When the alert is due: the start minus the lead time. */
    readonly alertMs: number;
    /** Where the satellite appears at the start. */
    readonly azimuthDeg: number;
    /** The highest elevation of the visible part, or of the pass for a rise. */
    readonly maxElevationDeg: number;
}

const minuteMs = 60_000;

export function alertKey(satelliteId: number, startMs: number): string {
    return `${satelliteId}@${new Date(startMs).toISOString()}`;
}

/**
 * The notification tag for an event: the satellite and the minute its start falls in,
 * "25544@2026-09-25T03:07Z". Two tabs can list the same pass with starts a millisecond apart (two
 * searches from different moments), and they still compute the same tag, so a copy from a second
 * tab replaces the first instead of stacking. Only a start that straddles a whole minute gets two
 * tags; the cross-tab lock in notifier.ts keeps a second copy from being made at all.
 */
export function alertTag(satelliteId: number, startMs: number): string {
    const minute = new Date(Math.floor(startMs / minuteMs) * minuteMs).toISOString().slice(0, 16);
    return `${satelliteId}@${minute}Z`;
}

/** The event a pass alerts for with these options, or null when it does not alert. */
export function alertEvent(satelliteId: number, pass: Pass, options: AlertOptions): AlertEvent | null {
    const part = pass.visible[0];
    if (part === undefined && options.visibleOnly) {
        return null;
    }
    const start = part === undefined ? pass.rise : part.start;
    return {
        key: alertKey(satelliteId, start.t),
        satelliteId,
        kind: part === undefined ? "rise" : "visible",
        riseMs: pass.rise.t,
        startMs: start.t,
        alertMs: start.t - options.leadMinutes * minuteMs,
        azimuthDeg: start.azimuthDeg,
        maxElevationDeg: part === undefined ? pass.culmination.elevationDeg : part.highest.elevationDeg,
    };
}

/**
 * The alerts for a pass list: one per pass whose event has not started by `nowMs`, in order of
 * start. Called again whenever the passes, the satellite, the options, or the time change, so the
 * plan always follows the latest list.
 */
export function planAlerts(satelliteId: number, passes: readonly Pass[], options: AlertOptions, nowMs: number): AlertEvent[] {
    const plan: AlertEvent[] = [];
    for (const pass of passes) {
        const event = alertEvent(satelliteId, pass, options);
        if (event !== null && event.startMs > nowMs) {
            plan.push(event);
        }
    }
    return plan.sort((a, b) => a.startMs - b.startMs);
}

/** What the log keeps about an alert that was shown. */
export interface NotifiedEntry {
    readonly satelliteId: number;
    readonly riseMs: number;
    readonly startMs: number;
}

/** How long a shown alert is remembered after its event started. Passes are listed a week ahead. */
const rememberMs = 8 * 24 * 60 * minuteMs;
/**
 * How far ahead of now a shown alert's start can be: the longest lead, plus the tolerance by which
 * the same pass's start may differ between tabs and refreshes. Anything further ahead was logged
 * by a clock that has since gone backwards.
 */
export const aheadLimitMs = Math.max(...leadChoices) * minuteMs + sameRiseToleranceMs;
/** At most this many entries are kept, the latest by start. */
const maximumEntries = 500;

function isEntry(value: unknown): value is NotifiedEntry {
    return typeof value === "object" && value !== null &&
        "satelliteId" in value && typeof value.satelliteId === "number" && Number.isFinite(value.satelliteId) &&
        "riseMs" in value && typeof value.riseMs === "number" && Number.isFinite(value.riseMs) &&
        "startMs" in value && typeof value.startMs === "number" && Number.isFinite(value.startMs);
}

/** The alerts already shown, so that none is shown twice. */
export class NotifiedLog {
    private entries: NotifiedEntry[];

    constructor(entries: readonly NotifiedEntry[] = []) {
        this.entries = [...entries];
    }

    /** A log from its stored JSON; anything unreadable is dropped, never thrown. */
    static parse(text: string | null): NotifiedLog {
        if (text === null) {
            return new NotifiedLog();
        }
        try {
            const value: unknown = JSON.parse(text);
            return new NotifiedLog(Array.isArray(value) ? value.filter(isEntry) : []);
        } catch {
            return new NotifiedLog();
        }
    }

    /** Both logs' alerts, each pass once. */
    static merge(a: NotifiedLog, b: NotifiedLog): NotifiedLog {
        const merged = new NotifiedLog(a.entries);
        for (const entry of b.entries) {
            if (!merged.matches(entry)) {
                merged.entries.push(entry);
            }
        }
        return merged;
    }

    get size(): number {
        return this.entries.length;
    }

    private matches(other: NotifiedEntry): boolean {
        return this.entries.some((e) => e.satelliteId === other.satelliteId &&
            (Math.abs(e.startMs - other.startMs) <= sameRiseToleranceMs || Math.abs(e.riseMs - other.riseMs) <= sameRiseToleranceMs));
    }

    /** Whether this event, or the same pass with a start or rise moved by new elements, was shown. */
    has(event: AlertEvent): boolean {
        return this.matches(event);
    }

    add(event: AlertEvent): void {
        if (!this.matches(event)) {
            this.entries.push({ satelliteId: event.satelliteId, riseMs: event.riseMs, startMs: event.startMs });
        }
    }

    /** Forgets the entry `add` made for this event, for an alert the browser could not show after all. */
    remove(event: AlertEvent): void {
        this.entries = this.entries.filter((e) => !(e.satelliteId === event.satelliteId && e.riseMs === event.riseMs && e.startMs === event.startMs));
    }

    /**
     * Forgets alerts for events that started long ago, and those further ahead than any alert can
     * be shown (aheadLimitMs), which a clock that went backwards left behind. Keeps the log bounded.
     * True when it forgot any.
     */
    prune(nowMs: number): boolean {
        const before = this.entries.length;
        this.entries = this.entries
            .filter((e) => e.startMs >= nowMs - rememberMs && e.startMs <= nowMs + aheadLimitMs)
            .sort((a, b) => a.startMs - b.startMs)
            .slice(-maximumEntries);
        return this.entries.length < before;
    }

    toJSON(): NotifiedEntry[] {
        return [...this.entries];
    }
}

/** The planned alerts to show now: due (the alert time has come, the event has not started) and never shown. */
export function dueAlerts(plan: readonly AlertEvent[], nowMs: number, log: NotifiedLog): AlertEvent[] {
    return plan.filter((event) => event.alertMs <= nowMs && nowMs < event.startMs && !log.has(event));
}

/** The next alert still to come after `nowMs`, for the status line. */
export function nextAlert(plan: readonly AlertEvent[], nowMs: number, log: NotifiedLog): AlertEvent | undefined {
    return plan.find((event) => event.alertMs > nowMs && !log.has(event));
}

/** Whole minutes to the start, rounded up and at least 1, so it never says "in 0 min". */
export function minutesUntil(startMs: number, nowMs: number): number {
    return Math.max(1, Math.ceil((startMs - nowMs) / minuteMs));
}

/**
 * The notification's text. The title names the satellite and says how soon; the body gives the
 * start in the observer's zone, where to look, and how high it gets:
 * "ISS (ZARYA) visible in 15 min" / "Thu 24 Sep 20:07:26 America/Phoenix (UTC-7), look NW 315°, up to 62°".
 */
export function alertMessage(event: AlertEvent, satelliteName: string, nowMs: number, zone: ZoneFormat): { title: string; body: string } {
    const minutes = minutesUntil(event.startMs, nowMs);
    const verb = event.kind === "visible" ? "visible" : "rises";
    const start = roundToSecond(event.startMs);
    return {
        title: `${satelliteName} ${verb} in ${minutes} min`,
        body: `${zone.dateTime(start)} ${zone.timeZone} (${zone.offsetLabel(start)}), look ${azimuthLabel(event.azimuthDeg)}, up to ${fixed(event.maxElevationDeg, 0)}°`,
    };
}
