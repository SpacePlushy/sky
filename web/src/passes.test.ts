import { describe, expect, it } from "vitest";
import type { Pass, SkyPoint, VisiblePart } from "./model";
import { defaultPass, describeVisiblePart, findByRise, hasVisiblePass, notVisibleReason, passStatus, sameRiseToleranceMs, selectedPass, splitPathByVisibility } from "./passes";
import { ZoneFormat } from "./time";

const minute = 60_000;
// 2026-09-25T02:00:00Z, 19:00:00 on Thursday 24 September in Phoenix (UTC-7).
const base = 20_721 * 86_400_000 + 2 * 3_600_000;

function event(t: number, elevationDeg = 10, azimuthDeg = 0): { t: number; azimuthDeg: number; elevationDeg: number } {
    return { t, azimuthDeg, elevationDeg };
}

function pass(riseMinute: number, visible: VisiblePart[] = [], sunlit = true): Pass {
    const rise = base + riseMinute * minute;
    const path: SkyPoint[] = [];
    for (let t = rise; t <= rise + 6 * minute; t += 10_000) {
        path.push({ ...event(t, 20), sunlit });
    }
    return { rise: event(rise), culmination: event(rise + 3 * minute, 45), set: event(rise + 6 * minute), peakUncertaintyDeg: 0, visible, path };
}

function part(start: number, end: number, endsBecause = "set", highest = 14.54): VisiblePart {
    return { start: event(start), startsBecause: "rise", highest: event((start + end) / 2, highest), end: event(end), endsBecause };
}

describe("pass status", () => {
    it("is upcoming before rise, in progress from rise to set, and ended after", () => {
        const p = pass(0);
        expect(passStatus(p, base - 1)).toBe("upcoming");
        expect(passStatus(p, base)).toBe("inProgress");
        expect(passStatus(p, base + 6 * minute)).toBe("inProgress");
        expect(passStatus(p, base + 6 * minute + 1)).toBe("ended");
    });
});

describe("default selection", () => {
    const early = pass(0);
    const visible = pass(100, [part(base + 101 * minute, base + 104 * minute)]);
    const later = pass(200);

    it("prefers the pass in progress", () => {
        expect(defaultPass([early, visible, later], base + minute)).toBe(early);
    });

    it("else the next visible pass", () => {
        expect(defaultPass([early, visible, later], base + 10 * minute)).toBe(visible);
    });

    it("else the next pass", () => {
        expect(defaultPass([early, later], base + 10 * minute)).toBe(later);
        expect(defaultPass([early], base + 10 * minute)).toBeUndefined();
    });
});

describe("something for the calendar", () => {
    const dim = pass(0);
    const bright = pass(95, [part(base + 96 * minute, base + 99 * minute)]);

    it("is a visible part among the passes that have not ended", () => {
        expect(hasVisiblePass([dim, bright], base - minute)).toBe(true);
        expect(hasVisiblePass([dim, pass(190)], base - minute)).toBe(false);
        expect(hasVisiblePass([], base)).toBe(false);
    });

    it("counts a pass in progress, even once its visible part is over, as the API's export does, but not one that has set", () => {
        // bright rises at +95 min, is visible from +96 to +99, and sets at +101.
        expect(hasVisiblePass([dim, bright], base + 100 * minute)).toBe(true);
        expect(hasVisiblePass([dim, bright], base + 101 * minute)).toBe(true);
        expect(hasVisiblePass([dim, bright], base + 101 * minute + 1)).toBe(false);
    });
});

describe("selection by rise time", () => {
    const a = pass(0);
    const b = pass(100);
    const c = pass(200);

    it("finds the chosen pass wherever it sits in the list", () => {
        expect(findByRise([a, b, c], b.rise.t)).toBe(b);
        expect(findByRise([b, c], b.rise.t)).toBe(b);
        expect(findByRise([c, b], b.rise.t)).toBe(b);
    });

    it("follows a pass whose rise moved by seconds with new elements, but not to another pass", () => {
        expect(findByRise([a, b, c], b.rise.t + 4_000)).toBe(b);
        expect(findByRise([a, c], b.rise.t)).toBeUndefined();
        expect(sameRiseToleranceMs).toBeLessThan(90 * minute);
    });

    it("keeps the user's choice and falls back to the default when it is gone", () => {
        expect(selectedPass([a, b, c], c.rise.t, base + minute)).toBe(c);
        expect(selectedPass([a, b], c.rise.t, base + minute)).toBe(a);
        expect(selectedPass([a, b, c], null, base + minute)).toBe(a);
    });
});

describe("visible part text", () => {
    const phoenix = new ZoneFormat("America/Phoenix");

    it("reads like the CLI, times rounded to the nearest second in the observer's zone", () => {
        // 19:20:16.6 rounds to 19:20:17; 19:23:02.4 rounds to 19:23:02.
        const start = base + 20 * minute + 16_600;
        const end = base + 23 * minute + 2_400;
        expect(describeVisiblePart(part(start, end, "entersShadow"), (ms) => phoenix.clock(ms))).toBe("19:20:17–19:23:02, up to 14.5°, then into shadow");
        expect(describeVisiblePart(part(start, end, "set"), (ms) => phoenix.clock(ms))).toBe("19:20:17–19:23:02, up to 14.5°");
        expect(describeVisiblePart(part(start, end, "skyBrightens"), (ms) => phoenix.clock(ms))).toBe("19:20:17–19:23:02, up to 14.5°, then the sky brightens");
    });

    it("gives each time the offset of its own rounded instant across a daylight saving change", () => {
        // Denver falls back at 2026-11-01 08:00 UTC (02:00 MDT becomes 01:00 MST). A part starting
        // at 07:59:59.6 UTC rounds to 08:00:00, which is 01:00:00 MST: its offset must be UTC-7, the
        // rounded instant's, not UTC-6, the raw one's.
        const denver = new ZoneFormat("America/Denver");
        const format = (ms: number): string => `${denver.clock(ms)} ${denver.offsetLabel(ms)}`;
        const start = Date.parse("2026-11-01T07:59:59.600Z");
        const end = Date.parse("2026-11-01T08:03:00.200Z");
        expect(describeVisiblePart(part(start, end, "set"), format)).toBe("01:00:00 UTC-7–01:03:00 UTC-7, up to 14.5°");
    });

    it("explains a pass with no visible part from its path", () => {
        expect(notVisibleReason(pass(0, [], false).path)).toBe("in Earth's shadow");
        expect(notVisibleReason(pass(0, [], true).path)).toBe("sky too bright");
        const mixed = pass(0).path.map((p, i) => ({ ...p, sunlit: i < 10 }));
        expect(notVisibleReason(mixed)).toBe("in shadow or sky too bright");
    });
});

describe("path split by visibility", () => {
    it("starts and ends each visible run at the visible part's exact ends, sharing boundary points", () => {
        const p = pass(0);
        // The API puts each visible part's ends into the path; here 00:01:05 and 00:02:35.
        const start = base + 65_000;
        const end = base + 155_000;
        const path = [...p.path, { ...event(start, 30), sunlit: true }, { ...event(end, 30), sunlit: true }].sort((x, y) => x.t - y.t);
        const runs = splitPathByVisibility(path, [part(start, end)]);
        expect(runs.map((r) => r.visible)).toEqual([false, true, false]);
        expect(runs[1]?.points[0]?.t).toBe(start);
        expect(runs[1]?.points.at(-1)?.t).toBe(end);
        expect(runs[0]?.points.at(-1)).toBe(runs[1]?.points[0]);
        expect(runs[1]?.points.at(-1)).toBe(runs[2]?.points[0]);
        const count = runs.reduce((n, r) => n + r.points.length, 0) - (runs.length - 1);
        expect(count).toBe(path.length);
    });

    it("is one invisible run when nothing is visible", () => {
        const p = pass(0);
        const runs = splitPathByVisibility(p.path, []);
        expect(runs).toHaveLength(1);
        expect(runs[0]?.visible).toBe(false);
        expect(runs[0]?.points).toHaveLength(p.path.length);
    });
});
