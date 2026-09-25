// Pass logic for the table and the sky plot: status, which pass is selected, and how a pass's
// visible parts read.

import type { Pass, SkyPoint, VisiblePart } from "./model";
import { fixed } from "./format";
import { roundToSecond } from "./time";

export type PassStatus = "ended" | "inProgress" | "upcoming";

export function passStatus(pass: Pass, nowMs: number): PassStatus {
    if (nowMs > pass.set.t) {
        return "ended";
    }
    return nowMs >= pass.rise.t ? "inProgress" : "upcoming";
}

/**
 * The pass to show when the user has not chosen one: the pass in progress, else the next visible
 * pass, else the next pass.
 */
export function defaultPass(passes: readonly Pass[], nowMs: number): Pass | undefined {
    return passes.find((p) => passStatus(p, nowMs) === "inProgress")
        ?? passes.find((p) => passStatus(p, nowMs) === "upcoming" && p.visible.length > 0)
        ?? passes.find((p) => passStatus(p, nowMs) === "upcoming");
}

/**
 * How far a chosen pass's rise may move and still be the same pass. A refetch with the same
 * elements can move a rise by a millisecond (the root-finding tolerance, from a different search
 * start); new elements move it by seconds. Passes of one satellite over one observer rise at least
 * an orbit apart, far more than this.
 */
export const sameRiseToleranceMs = 120_000;

/**
 * The pass the user chose, found by its rise time rather than its place in the list, so a refresh
 * that drops an ended pass keeps the choice. Undefined when the pass is no longer listed.
 */
export function findByRise(passes: readonly Pass[], riseMs: number): Pass | undefined {
    let best: Pass | undefined;
    let bestGap = Number.POSITIVE_INFINITY;
    for (const pass of passes) {
        const gap = Math.abs(pass.rise.t - riseMs);
        if (gap < bestGap) {
            best = pass;
            bestGap = gap;
        }
    }
    return bestGap <= sameRiseToleranceMs ? best : undefined;
}

/** The pass the sky plot shows: the user's choice while it is listed, else the default. */
export function selectedPass(passes: readonly Pass[], chosenRiseMs: number | null, nowMs: number): Pass | undefined {
    const chosen = chosenRiseMs === null ? undefined : findByRise(passes, chosenRiseMs);
    return chosen ?? defaultPass(passes, nowMs);
}

const endings: Readonly<Record<string, string>> = {
    entersShadow: ", then into shadow",
    skyBrightens: ", then the sky brightens",
};

/**
 * One visible part as a phrase: "19:20:17–19:23:02, up to 14.5°, then into shadow". Each time is
 * rounded to the second once and handed to `format`, which prints it as the rest of the row does
 * (with its UTC offset near a daylight saving change, and its day when that differs), so a clock
 * and its offset always come from the same instant. It ends as the CLI's does: nothing when the
 * satellite sets, and the reason when it vanishes earlier.
 */
export function describeVisiblePart(part: VisiblePart, format: (ms: number) => string): string {
    const start = format(roundToSecond(part.start.t));
    const end = format(roundToSecond(part.end.t));
    return `${start}–${end}, up to ${fixed(part.highest.elevationDeg, 1)}°${endings[part.endsBecause] ?? ""}`;
}

/**
 * Why a pass with no visible part is not visible, read from its path's sunlit flags: if the
 * satellite was never sunlit, the Earth's shadow; if it was sunlit at some point but no part is
 * visible, then the sky was too bright whenever it was sunlit (a sunlit moment with a dark sky
 * would be a visible part, and the API lists every one).
 */
export function notVisibleReason(path: readonly SkyPoint[]): string {
    const sunlit = path.filter((p) => p.sunlit).length;
    if (sunlit === 0) {
        return "in Earth's shadow";
    }
    return sunlit === path.length ? "sky too bright" : "in shadow or sky too bright";
}

/** Whether a sky-path point lies in one of the pass's visible parts (their ends included). */
export function inVisiblePart(point: SkyPoint, parts: readonly VisiblePart[]): boolean {
    return parts.some((part) => point.t >= part.start.t && point.t <= part.end.t);
}

/**
 * The path cut into runs inside and outside the visible parts. Each visible run starts and ends at
 * a visible part's exact ends (the API includes them in the path), and runs share their end points.
 */
export function splitPathByVisibility(path: readonly SkyPoint[], parts: readonly VisiblePart[]): { visible: boolean; points: SkyPoint[] }[] {
    const runs: { visible: boolean; points: SkyPoint[] }[] = [];
    for (let i = 0; i + 1 < path.length; i++) {
        const a = path[i];
        const b = path[i + 1];
        if (a === undefined || b === undefined) {
            continue;
        }
        // A stretch is visible when both ends are inside the same visible part.
        const visible = parts.some((part) => a.t >= part.start.t && b.t <= part.end.t);
        const current = runs[runs.length - 1];
        if (current?.visible === visible) {
            current.points.push(b);
        } else {
            runs.push({ visible, points: [a, b] });
        }
    }
    return runs;
}
