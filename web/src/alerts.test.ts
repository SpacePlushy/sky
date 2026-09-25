import { afterEach, describe, expect, it, vi } from "vitest";
import {
    alertEvent,
    alertKey,
    alertMessage,
    defaultPreferences,
    dueAlerts,
    minutesUntil,
    nextAlert,
    NotifiedLog,
    parsePreferences,
    planAlerts,
    type AlertOptions,
} from "./alerts";
import type { Pass, PassEvent, VisiblePart } from "./model";
import { ZoneFormat } from "./time";

const second = 1000;
const minute = 60 * second;
const hour = 60 * minute;
// 2026-09-25T00:00:00Z: 20,721 days after 1970-01-01 (2026-09-24 is day 20,720, as in api.test.ts).
const day25 = 20_721 * 86_400_000;
// The first evening visible ISS pass in the demo data starts at 03:07:26 UTC on 25 September,
// which is 20:07:26 on Thursday 24 September in Phoenix (UTC-7 all year).
const firstVisible = day25 + 3 * hour + 7 * minute + 26 * second;

const iss = 25544;
const tenVisible: AlertOptions = { leadMinutes: 10, visibleOnly: true };
const tenAll: AlertOptions = { leadMinutes: 10, visibleOnly: false };

function at(t: number, azimuthDeg = 0, elevationDeg = 10): PassEvent {
    return { t, azimuthDeg, elevationDeg };
}

/** A 6-minute pass rising at `rise`, peaking at 45° halfway, with the given visible parts. */
function pass(rise: number, visible: VisiblePart[] = []): Pass {
    return {
        rise: at(rise, 200),
        culmination: at(rise + 3 * minute, 250, 45),
        set: at(rise + 6 * minute, 300),
        peakUncertaintyDeg: 0,
        visible,
        path: [],
    };
}

/** A visible part from `start` to `end`, appearing at azimuth 315° and highest at `highest`°. */
function part(start: number, end: number, highest = 62.3): VisiblePart {
    return {
        start: at(start, 315, 10),
        startsBecause: "rise",
        highest: at((start + end) / 2, 30, highest),
        end: at(end, 60, 10),
        endsBecause: "entersShadow",
    };
}

// Three passes about an orbit apart: not visible, visible from 2 minutes after rise, not visible.
const dim = pass(firstVisible - 95 * minute);
const bright = pass(firstVisible - 2 * minute, [part(firstVisible, firstVisible + 3 * minute)]);
const later = pass(firstVisible + 93 * minute);
const passes = [dim, bright, later];

afterEach(() => {
    vi.useRealTimers();
});

describe("which events alert", () => {
    it("alerts for a visible pass at its visible part's start, not its rise", () => {
        const event = alertEvent(iss, bright, tenVisible);
        expect(event?.kind).toBe("visible");
        expect(event?.startMs).toBe(firstVisible);
        expect(event?.riseMs).toBe(bright.rise.t);
        expect(event?.azimuthDeg).toBe(315);
        expect(event?.maxElevationDeg).toBe(62.3);
    });

    it("skips passes with no visible part when visible passes only is on", () => {
        expect(alertEvent(iss, dim, tenVisible)).toBeNull();
        expect(planAlerts(iss, passes, tenVisible, dim.rise.t - hour).map((e) => e.startMs)).toEqual([firstVisible]);
    });

    it("alerts for every pass when it is off: rise for the others, still the visible start for visible ones", () => {
        const plan = planAlerts(iss, passes, tenAll, dim.rise.t - hour);
        expect(plan.map((e) => [e.kind, e.startMs])).toEqual([
            ["rise", dim.rise.t],
            ["visible", firstVisible],
            ["rise", later.rise.t],
        ]);
        // A rise alert gives the pass's peak as its maximum elevation.
        expect(plan[0]?.maxElevationDeg).toBe(45);
        expect(plan[0]?.azimuthDeg).toBe(200);
    });

    it("uses the first visible part when a pass has two", () => {
        const twice = pass(firstVisible - 2 * minute, [part(firstVisible, firstVisible + minute), part(firstVisible + 2 * minute, firstVisible + 3 * minute)]);
        expect(alertEvent(iss, twice, tenVisible)?.startMs).toBe(firstVisible);
    });

    it("leaves out events that have started, and orders the rest by start", () => {
        expect(planAlerts(iss, [later, bright, dim], tenAll, firstVisible).map((e) => e.startMs)).toEqual([later.rise.t]);
        expect(planAlerts(iss, [later, bright, dim], tenAll, firstVisible - 1).map((e) => e.startMs)).toEqual([firstVisible, later.rise.t]);
    });

    it("keys an alert by the satellite and the event's start", () => {
        expect(alertEvent(iss, bright, tenVisible)?.key).toBe("25544@2026-09-25T03:07:26.000Z");
        expect(alertKey(48274, firstVisible)).toBe("48274@2026-09-25T03:07:26.000Z");
    });
});

describe("alert times", () => {
    it("is the start minus the lead time", () => {
        for (const leadMinutes of [5, 10, 15] as const) {
            const event = alertEvent(iss, bright, { leadMinutes, visibleOnly: true });
            expect(event?.alertMs).toBe(firstVisible - leadMinutes * minute);
        }
        // 15 minutes before 03:07:26 is 02:52:26 UTC.
        expect(alertEvent(iss, bright, { leadMinutes: 15, visibleOnly: true })?.alertMs).toBe(day25 + 2 * hour + 52 * minute + 26 * second);
    });

    it("is due from the alert time until the event starts, on the time it is given", () => {
        const plan = planAlerts(iss, passes, tenVisible, firstVisible - hour);
        const log = new NotifiedLog();
        expect(dueAlerts(plan, firstVisible - 10 * minute - 1, log)).toEqual([]);
        expect(dueAlerts(plan, firstVisible - 10 * minute, log).map((e) => e.startMs)).toEqual([firstVisible]);
        expect(dueAlerts(plan, firstVisible - 1, log).map((e) => e.startMs)).toEqual([firstVisible]);
        expect(dueAlerts(plan, firstVisible, log)).toEqual([]);
    });

    it("never reads the browser's clock: the server time passed in is all that counts", () => {
        vi.useFakeTimers();
        const plan = planAlerts(iss, passes, tenVisible, firstVisible - hour);
        const log = new NotifiedLog();
        for (const browserNow of [Date.UTC(2030, 0, 1), Date.UTC(2001, 0, 1), firstVisible]) {
            vi.setSystemTime(browserNow);
            expect(dueAlerts(plan, firstVisible - 11 * minute, log)).toEqual([]);
            expect(dueAlerts(plan, firstVisible - 9 * minute, log)).toHaveLength(1);
            expect(planAlerts(iss, passes, tenVisible, firstVisible - hour)).toEqual(plan);
        }
    });

    it("names the next alert still to come", () => {
        const plan = planAlerts(iss, passes, tenAll, dim.rise.t - hour);
        const log = new NotifiedLog();
        expect(nextAlert(plan, dim.rise.t - hour, log)?.startMs).toBe(dim.rise.t);
        expect(nextAlert(plan, dim.rise.t - 5 * minute, log)?.startMs).toBe(firstVisible);
        const first = plan[1];
        if (first === undefined) {
            throw new Error("plan too short");
        }
        log.add(first);
        expect(nextAlert(plan, dim.rise.t - 5 * minute, log)?.startMs).toBe(later.rise.t);
    });
});

describe("never twice", () => {
    it("does not alert again once shown, however often it is asked", () => {
        const log = new NotifiedLog();
        const plan = planAlerts(iss, passes, tenVisible, firstVisible - hour);
        const due = dueAlerts(plan, firstVisible - 10 * minute, log);
        expect(due).toHaveLength(1);
        for (const event of due) {
            log.add(event);
        }
        for (let t = firstVisible - 10 * minute; t < firstVisible; t += 7 * second) {
            expect(dueAlerts(plan, t, log)).toEqual([]);
        }
        expect(log.size).toBe(1);
    });

    it("remembers across a reload through its stored JSON", () => {
        const log = new NotifiedLog();
        const event = alertEvent(iss, bright, tenVisible);
        if (event === null) {
            throw new Error("expected an event");
        }
        log.add(event);
        const reloaded = NotifiedLog.parse(JSON.stringify(log));
        expect(reloaded.has(event)).toBe(true);
        expect(dueAlerts([event], firstVisible - 5 * minute, reloaded)).toEqual([]);
    });

    it("drops unreadable storage instead of failing", () => {
        expect(NotifiedLog.parse(null).size).toBe(0);
        expect(NotifiedLog.parse("not json").size).toBe(0);
        expect(NotifiedLog.parse("{}").size).toBe(0);
        expect(NotifiedLog.parse(JSON.stringify([{ satelliteId: iss, riseMs: 1, startMs: 2 }, { satelliteId: "x" }, null])).size).toBe(1);
    });

    it("treats the same pass with a start moved by new elements as already shown", () => {
        const log = new NotifiedLog();
        const event = alertEvent(iss, bright, tenVisible);
        if (event === null) {
            throw new Error("expected an event");
        }
        log.add(event);
        // New elements move the same pass by seconds.
        const moved = pass(bright.rise.t + 4 * second, [part(firstVisible + 3 * second, firstVisible + 3 * minute)]);
        expect(dueAlerts(planAlerts(iss, [moved], tenVisible, firstVisible - hour), firstVisible - 5 * minute, log)).toEqual([]);
        // Or change whether a marginal pass is visible, moving its event from the visible start to the rise.
        const dimmed = pass(bright.rise.t + 2 * second);
        expect(dueAlerts(planAlerts(iss, [dimmed], tenAll, firstVisible - hour), bright.rise.t - 5 * minute, log)).toEqual([]);
    });

    it("still alerts for the next pass, and for another satellite at the same moment", () => {
        const log = new NotifiedLog();
        for (const event of planAlerts(iss, [bright], tenVisible, firstVisible - hour)) {
            log.add(event);
        }
        expect(dueAlerts(planAlerts(iss, [later], tenAll, firstVisible), later.rise.t - 5 * minute, log)).toHaveLength(1);
        expect(dueAlerts(planAlerts(48274, [bright], tenVisible, firstVisible - hour), firstVisible - 5 * minute, log)).toHaveLength(1);
    });

    it("merges what another tab stored", () => {
        const a = new NotifiedLog();
        const b = new NotifiedLog();
        const [first, second_] = planAlerts(iss, [bright, later], tenAll, firstVisible - hour);
        if (first === undefined || second_ === undefined) {
            throw new Error("expected two events");
        }
        a.add(first);
        b.add(first);
        b.add(second_);
        const merged = NotifiedLog.merge(a, b);
        expect(merged.size).toBe(2);
        expect(merged.has(second_)).toBe(true);
    });

    it("forgets events more than eight days old", () => {
        const log = new NotifiedLog([
            { satelliteId: iss, riseMs: firstVisible - 9 * 24 * hour, startMs: firstVisible - 9 * 24 * hour },
            { satelliteId: iss, riseMs: firstVisible - 24 * hour, startMs: firstVisible - 24 * hour },
        ]);
        log.prune(firstVisible);
        expect(log.toJSON().map((e) => e.startMs)).toEqual([firstVisible - 24 * hour]);
    });
});

describe("a page opened late", () => {
    it("alerts at once when opened after the alert time but before the start", () => {
        const opened = firstVisible - 7 * minute;
        const log = new NotifiedLog();
        const due = dueAlerts(planAlerts(iss, passes, tenVisible, opened), opened, log);
        expect(due.map((e) => e.startMs)).toEqual([firstVisible]);
        const event = due[0];
        if (event === undefined) {
            throw new Error("expected an alert");
        }
        expect(alertMessage(event, "ISS (ZARYA)", opened, new ZoneFormat("America/Phoenix")).title).toBe("ISS (ZARYA) visible in 7 min");
    });

    it("and once only", () => {
        const log = new NotifiedLog();
        const plan = planAlerts(iss, passes, tenVisible, firstVisible - 7 * minute);
        for (const event of dueAlerts(plan, firstVisible - 7 * minute, log)) {
            log.add(event);
        }
        expect(dueAlerts(plan, firstVisible - 6 * minute, log)).toEqual([]);
    });

    it("does not alert when opened after the start", () => {
        const opened = firstVisible + second;
        expect(dueAlerts(planAlerts(iss, passes, tenVisible, opened), opened, new NotifiedLog())).toEqual([]);
    });
});

describe("rescheduling", () => {
    it("follows a refreshed pass list: ended passes leave, new ones join, moved ones move", () => {
        const before = planAlerts(iss, passes, tenAll, dim.rise.t - hour);
        expect(before.map((e) => e.startMs)).toEqual([dim.rise.t, firstVisible, later.rise.t]);
        const next = pass(later.rise.t + 94 * minute);
        const moved = pass(bright.rise.t + 3 * second, [part(firstVisible + 3 * second, firstVisible + 3 * minute)]);
        const after = planAlerts(iss, [moved, later, next], tenAll, dim.set.t + minute);
        expect(after.map((e) => e.startMs)).toEqual([firstVisible + 3 * second, later.rise.t, next.rise.t]);
        expect(after.map((e) => e.alertMs)).toEqual([firstVisible + 3 * second - 10 * minute, later.rise.t - 10 * minute, next.rise.t - 10 * minute]);
    });

    it("follows a new lead time and a new choice of passes", () => {
        const nowMs = dim.rise.t - hour;
        expect(planAlerts(iss, passes, { leadMinutes: 15, visibleOnly: true }, nowMs).map((e) => e.alertMs)).toEqual([firstVisible - 15 * minute]);
        expect(planAlerts(iss, passes, { leadMinutes: 5, visibleOnly: true }, nowMs).map((e) => e.alertMs)).toEqual([firstVisible - 5 * minute]);
        expect(planAlerts(iss, passes, { leadMinutes: 5, visibleOnly: false }, nowMs)).toHaveLength(3);
    });

    it("makes a longer lead due at once when its alert time has already passed", () => {
        const nowMs = firstVisible - 12 * minute;
        const log = new NotifiedLog();
        expect(dueAlerts(planAlerts(iss, passes, tenVisible, nowMs), nowMs, log)).toEqual([]);
        expect(dueAlerts(planAlerts(iss, passes, { leadMinutes: 15, visibleOnly: true }, nowMs), nowMs, log)).toHaveLength(1);
    });

    it("follows the satellite", () => {
        const plan = planAlerts(48274, passes, tenVisible, firstVisible - hour);
        expect(plan.map((e) => e.key)).toEqual(["48274@2026-09-25T03:07:26.000Z"]);
    });
});

describe("notification text", () => {
    const phoenix = new ZoneFormat("America/Phoenix");

    it("names the satellite, how soon, the start in the observer's zone, where to look, and how high", () => {
        const event = alertEvent(iss, bright, { leadMinutes: 15, visibleOnly: true });
        if (event === null) {
            throw new Error("expected an event");
        }
        expect(alertMessage(event, "ISS (ZARYA)", event.alertMs, phoenix)).toEqual({
            title: "ISS (ZARYA) visible in 15 min",
            body: "Thu 24 Sep 20:07:26 America/Phoenix (UTC-7), look NW 315°, up to 62°",
        });
    });

    it("says rises for a pass with no visible part", () => {
        const event = alertEvent(iss, later, tenAll);
        if (event === null) {
            throw new Error("expected an event");
        }
        // later rises 93 minutes after 03:07:26 UTC: 04:40:26 UTC, 21:40:26 in Phoenix.
        expect(alertMessage(event, "ISS (ZARYA)", event.alertMs, phoenix)).toEqual({
            title: "ISS (ZARYA) rises in 10 min",
            body: "Thu 24 Sep 21:40:26 America/Phoenix (UTC-7), look SSW 200°, up to 45°",
        });
    });

    it("counts whole minutes up, and never says 0", () => {
        expect(minutesUntil(firstVisible, firstVisible - 15 * minute)).toBe(15);
        expect(minutesUntil(firstVisible, firstVisible - 14 * minute - 59 * second)).toBe(15);
        expect(minutesUntil(firstVisible, firstVisible - 14 * minute)).toBe(14);
        expect(minutesUntil(firstVisible, firstVisible - 30 * second)).toBe(1);
        expect(minutesUntil(firstVisible, firstVisible)).toBe(1);
    });
});

describe("preferences", () => {
    it("defaults to off, 10 minutes, and visible passes only", () => {
        expect(defaultPreferences).toEqual({ enabled: false, leadMinutes: 10, visibleOnly: true });
        expect(parsePreferences(null)).toEqual(defaultPreferences);
        expect(parsePreferences("not json")).toEqual(defaultPreferences);
        expect(parsePreferences("[]")).toEqual(defaultPreferences);
    });

    it("keeps valid stored values and replaces invalid ones field by field", () => {
        expect(parsePreferences(JSON.stringify({ enabled: true, leadMinutes: 15, visibleOnly: false }))).toEqual({ enabled: true, leadMinutes: 15, visibleOnly: false });
        expect(parsePreferences(JSON.stringify({ enabled: "yes", leadMinutes: 7, visibleOnly: false }))).toEqual({ enabled: false, leadMinutes: 10, visibleOnly: false });
    });
});
