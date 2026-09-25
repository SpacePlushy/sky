// Time. Every instant the API sends is UTC, ISO 8601 with a Z and at most three fractional digits.
// Inside the page an instant is a number: milliseconds since 1970-01-01T00:00:00Z. It is shown in
// the observer's IANA zone only at the edge, with Intl.DateTimeFormat, so the page never depends
// on a zone's offset or daylight saving rules, only on the browser's time zone database.
//
// "Now" is the server's clock, which can be simulated: see ServerClock.

const isoUtc = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?Z$/;

/** Milliseconds since the epoch for a UTC instant such as 2026-09-24T04:00:00.123Z. */
export function parseUtc(text: string): number {
    const match = isoUtc.exec(text);
    if (match === null) {
        throw new Error(`Not a UTC instant: ${JSON.stringify(text)}`);
    }
    const [year, month, day, hour, minute, second] = match.slice(1, 7).map(Number);
    if (year === undefined || month === undefined || day === undefined || hour === undefined || minute === undefined || second === undefined) {
        throw new Error(`Not a UTC instant: ${JSON.stringify(text)}`);
    }
    // Digits past the millisecond are dropped; the API sends at most three.
    const fraction = match[7] ?? "";
    const milliseconds = Number(fraction.padEnd(3, "0").slice(0, 3));
    const date = new Date(0);
    date.setUTCFullYear(year, month - 1, day);
    date.setUTCHours(hour, minute, second, milliseconds);
    // Date rolls invalid fields over (February 30 becomes March 2); reject them instead.
    if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day ||
        date.getUTCHours() !== hour || date.getUTCMinutes() !== minute || date.getUTCSeconds() !== second) {
        throw new Error(`Not a valid UTC instant: ${JSON.stringify(text)}`);
    }
    return date.getTime();
}

/** The nearest whole second, halves rounding up, as the CLI prints pass times. */
export function roundToSecond(ms: number): number {
    return Math.round(ms / 1000) * 1000;
}

/** The wall-clock fields of an instant in a zone. */
export interface WallClock {
    readonly year: number;
    readonly month: number; // 1 to 12
    readonly day: number;
    readonly hour: number; // 0 to 23
    readonly minute: number;
    readonly second: number;
}

const weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"] as const;
const months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"] as const;

function two(n: number): string {
    return String(n).padStart(2, "0");
}

/**
 * Formats instants in one IANA zone. Intl.DateTimeFormat does the zone conversion; the text is
 * assembled here from its numeric fields, so it does not change with the browser's locale data
 * (en-GB, for one, abbreviates September as "Sept" in newer releases).
 */
export class ZoneFormat {
    readonly timeZone: string;
    private readonly parts: Intl.DateTimeFormat;

    /** Throws RangeError if the browser does not know the zone. */
    constructor(timeZone: string) {
        this.timeZone = timeZone;
        this.parts = new Intl.DateTimeFormat("en-GB", {
            timeZone,
            numberingSystem: "latn",
            calendar: "gregory",
            hourCycle: "h23",
            year: "numeric",
            month: "2-digit",
            day: "2-digit",
            hour: "2-digit",
            minute: "2-digit",
            second: "2-digit",
        });
    }

    /** The wall clock in this zone at an instant, to the second (milliseconds are dropped). */
    wallClock(ms: number): WallClock {
        const fields: Record<string, number> = {};
        for (const part of this.parts.formatToParts(ms)) {
            if (part.type !== "literal") {
                fields[part.type] = Number(part.value);
            }
        }
        const read = (name: string): number => {
            const value = fields[name];
            if (value === undefined || !Number.isInteger(value)) {
                throw new Error(`Intl.DateTimeFormat gave no ${name} for ${this.timeZone}`);
            }
            return value;
        };
        return { year: read("year"), month: read("month"), day: read("day"), hour: read("hour"), minute: read("minute"), second: read("second") };
    }

    /** 19:20:17 */
    clock(ms: number): string {
        const w = this.wallClock(ms);
        return `${two(w.hour)}:${two(w.minute)}:${two(w.second)}`;
    }

    /** 19:20 */
    clockMinutes(ms: number): string {
        const w = this.wallClock(ms);
        return `${two(w.hour)}:${two(w.minute)}`;
    }

    /** Thu 24 Sep */
    date(ms: number): string {
        const w = this.wallClock(ms);
        return `${weekdays[weekdayOf(w)]} ${w.day} ${months[w.month - 1] ?? "?"}`;
    }

    /** Thu */
    weekday(ms: number): string {
        return weekdays[weekdayOf(this.wallClock(ms))];
    }

    /** Thu 24 Sep 19:20:17 */
    dateTime(ms: number): string {
        return `${this.date(ms)} ${this.clock(ms)}`;
    }

    /** 2026-09-24: the calendar date in this zone, for comparing days. */
    isoDate(ms: number): string {
        const w = this.wallClock(ms);
        return `${w.year}-${two(w.month)}-${two(w.day)}`;
    }

    /** The zone's offset from UTC at an instant, in minutes (-420 for UTC-7). */
    offsetMinutes(ms: number): number {
        const w = this.wallClock(ms);
        const wallAsUtc = Date.UTC(w.year, w.month - 1, w.day, w.hour, w.minute, w.second);
        const wholeSecond = Math.floor(ms / 1000) * 1000;
        return Math.round((wallAsUtc - wholeSecond) / 60000);
    }

    /** The offset as a short label: UTC-7, UTC+5:30, or UTC. */
    offsetLabel(ms: number): string {
        return offsetLabel(this.offsetMinutes(ms));
    }
}

function weekdayOf(w: WallClock): 0 | 1 | 2 | 3 | 4 | 5 | 6 {
    return new Date(Date.UTC(w.year, w.month - 1, w.day)).getUTCDay() as 0 | 1 | 2 | 3 | 4 | 5 | 6;
}

export function offsetLabel(minutes: number): string {
    if (minutes === 0) {
        return "UTC";
    }
    const sign = minutes < 0 ? "-" : "+";
    const abs = Math.abs(minutes);
    const hours = Math.floor(abs / 60);
    const rest = abs % 60;
    return rest === 0 ? `UTC${sign}${hours}` : `UTC${sign}${hours}:${two(rest)}`;
}

const formats = new Map<string, ZoneFormat>();

/** A cached ZoneFormat per zone. Throws RangeError for a zone the browser does not know. */
export function zoneFormat(timeZone: string): ZoneFormat {
    let format = formats.get(timeZone);
    if (format === undefined) {
        format = new ZoneFormat(timeZone);
        formats.set(timeZone, format);
    }
    return format;
}

/**
 * A countdown to an event, in whole seconds rounded up so it reads 00:00 only when the event is
 * here. Under an hour it is mm:ss (04:07); longer ones spell out their units (1h 02m 03s,
 * 2d 01h 02m 03s) so they cannot be mistaken for a time of day.
 */
export function formatCountdown(ms: number): string {
    const total = Math.max(0, Math.ceil(ms / 1000));
    const days = Math.floor(total / 86400);
    const hours = Math.floor((total % 86400) / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const seconds = total % 60;
    if (days > 0) {
        return `${days}d ${two(hours)}h ${two(minutes)}m ${two(seconds)}s`;
    }
    return hours > 0 ? `${hours}h ${two(minutes)}m ${two(seconds)}s` : `${two(minutes)}:${two(seconds)}`;
}

/** A duration to the nearest second: 9m 08s, or 1h 02m 03s. */
export function formatDuration(ms: number): string {
    const total = Math.max(0, Math.round(ms / 1000));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const seconds = total % 60;
    return hours > 0 ? `${hours}h ${two(minutes)}m ${two(seconds)}s` : `${minutes}m ${two(seconds)}s`;
}

/**
 * The offset of the server's clock from the browser's, from one request: the server stamped
 * `serverMs` at some moment between `sentMs` and `receivedMs` on the browser's clock, so the offset
 * is `serverMs` minus the midpoint, within half the round trip either way.
 */
export function clockOffset(serverMs: number, sentMs: number, receivedMs: number): number {
    return serverMs - (sentMs + receivedMs) / 2;
}

/**
 * How fast an offset's uncertainty grows with age, as a fraction of elapsed time: 100 ppm, well
 * above the drift of a working computer clock.
 */
const driftRate = 1e-4;

/** The API truncates instants to the millisecond, so a stamp can be up to 1 ms early. */
const stampResolutionMs = 1;

/**
 * The server's clock, as seen from the browser. The browser's own clock never defines "now": the
 * server's can be simulated (Clock:StartUtc), and every response states the instant it used.
 *
 * Each response gives an offset good to within half its round trip. The clock keeps the tightest
 * estimate, letting its uncertainty grow with age at the drift rate, and takes a new one when it
 * is tighter, or when it cannot agree with the kept one (the server restarted with a new
 * simulated start, or the browser's clock was set).
 */
export class ServerClock {
    private offset = 0;
    private halfWindow = Number.POSITIVE_INFINITY;
    private sampledAt = 0;
    private synced = false;
    private readonly local: () => number;

    constructor(local: () => number = Date.now) {
        this.local = local;
    }

    get isSynced(): boolean {
        return this.synced;
    }

    /** Server time minus browser time, in milliseconds. */
    get offsetMs(): number {
        return this.offset;
    }

    /** How far the offset can be off, in milliseconds, at browser time `at`. */
    uncertaintyMs(at: number = this.local()): number {
        return this.halfWindow + Math.abs(at - this.sampledAt) * driftRate;
    }

    /** Folds in one response: the server's stamp, and when the request left and the response came back. */
    update(serverMs: number, sentMs: number, receivedMs: number): void {
        const estimate = clockOffset(serverMs, sentMs, receivedMs) + stampResolutionMs / 2;
        const half = Math.max(0, receivedMs - sentMs) / 2 + stampResolutionMs / 2;
        if (this.synced) {
            const kept = this.uncertaintyMs(sentMs);
            const consistent = Math.abs(estimate - this.offset) <= kept + half;
            if (consistent && half > kept) {
                return;
            }
        }
        this.offset = estimate;
        this.halfWindow = half;
        this.sampledAt = receivedMs;
        this.synced = true;
    }

    /** The server's time now, in milliseconds since the epoch. */
    now(): number {
        return this.local() + this.offset;
    }
}
