import { geoEquirectangular, geoPath } from "d3-geo";
import { describe, expect, it } from "vitest";
import { segmentFeature, segmentTrack, type TrackSample, type TrackSegment } from "./track";

const step = 30_000;

function sample(i: number, sunlit: boolean, lon = i * 2): TrackSample {
    return { t: i * step, latitudeDeg: i, longitudeDeg: lon, sunlit };
}

/** The segments' points in order with each shared boundary counted once. */
function joined(segments: readonly TrackSegment[]): TrackSample[] {
    const out: TrackSample[] = [];
    for (const segment of segments) {
        for (const [i, p] of segment.points.entries()) {
            if (i === 0 && out.length > 0) {
                expect(p).toBe(out[out.length - 1]);
                continue;
            }
            out.push(p);
        }
    }
    return out;
}

/** A small seeded generator (mulberry32), so the random cases are the same on every run. */
function random(seed: number): () => number {
    let a = seed >>> 0;
    return () => {
        a = (a + 0x6d2b79f5) >>> 0;
        let t = a;
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

describe("track segmentation", () => {
    it("keeps a track with one style as one run", () => {
        const points = [0, 1, 2, 3].map((i) => sample(i, true));
        const segments = segmentTrack(points, -1);
        expect(segments).toHaveLength(1);
        expect(segments[0]).toMatchObject({ past: false, sunlit: true });
        expect(segments[0]?.points).toEqual(points);
    });

    it("splits where sunlight changes and shares the boundary point", () => {
        // Sunlit at samples 0-2, shadow at 3-5. The stretch from 2 to 3 is held at sample 2's
        // state, so the sunlit run ends at sample 3, where the shadow run starts.
        const points = [true, true, true, false, false, false].map((s, i) => sample(i, s));
        const segments = segmentTrack(points, -1);
        expect(segments.map((s) => s.sunlit)).toEqual([true, false]);
        expect(segments[0]?.points.map((p) => p.t)).toEqual([0, 1, 2, 3].map((i) => i * step));
        expect(segments[1]?.points.map((p) => p.t)).toEqual([3, 4, 5].map((i) => i * step));
        expect(segments[1]?.points[0]).toBe(segments[0]?.points[3]);
    });

    it("splits at now through the satellite's own position", () => {
        const points = [0, 1, 2, 3, 4].map((i) => sample(i, true));
        const now = { t: 2.5 * step, latitudeDeg: 2.5, longitudeDeg: 5, sunlit: true };
        const segments = segmentTrack(points, now.t, now);
        expect(segments.map((s) => s.past)).toEqual([true, false]);
        expect(segments[0]?.points.at(-1)).toBe(now);
        expect(segments[1]?.points[0]).toBe(now);
        expect(joined(segments)).toEqual([...points.slice(0, 3), now, ...points.slice(3)]);
    });

    it("without a position for now, holds the stretch across now as past", () => {
        const points = [0, 1, 2, 3].map((i) => sample(i, true));
        const segments = segmentTrack(points, 1.5 * step);
        expect(segments.map((s) => s.past)).toEqual([true, false]);
        expect(segments[0]?.points.map((p) => p.t)).toEqual([0, step, 2 * step]);
        expect(segments[1]?.points.map((p) => p.t)).toEqual([2 * step, 3 * step]);
    });

    it("ignores a position for now outside the track", () => {
        const points = [0, 1, 2].map((i) => sample(i, true));
        const late = { t: 10 * step, latitudeDeg: 0, longitudeDeg: 0, sunlit: true };
        expect(joined(segmentTrack(points, late.t, late))).toEqual(points);
    });

    it("sorts by time and keeps the first of two samples at the same time", () => {
        const a = sample(0, true);
        const b = sample(1, true);
        const c = sample(2, true);
        const duplicate = { ...b, latitudeDeg: 99 };
        expect(joined(segmentTrack([c, a, b, duplicate], -1))).toEqual([a, b, c]);
    });

    it("handles empty and single-point tracks", () => {
        expect(segmentTrack([], 0)).toEqual([]);
        const only = sample(0, false);
        expect(segmentTrack([only], step)).toEqual([{ past: true, sunlit: false, points: [only] }]);
    });

    it("keeps every point, in order, with shared boundaries, over seeded random tracks", () => {
        const next = random(20260924);
        for (let run = 0; run < 200; run++) {
            const n = 2 + Math.floor(next() * 60);
            let sunlit = next() < 0.5;
            const points: TrackSample[] = [];
            for (let i = 0; i < n; i++) {
                if (next() < 0.15) {
                    sunlit = !sunlit;
                }
                points.push(sample(i, sunlit, next() * 360 - 180));
            }
            const nowT = (next() * (n + 2) - 1) * step;
            const nowPoint = next() < 0.5 ? { t: nowT, latitudeDeg: 0, longitudeDeg: 0, sunlit: next() < 0.5 } : undefined;
            const shuffled = [...points].sort(() => next() - 0.5);
            const segments = segmentTrack(shuffled, nowT, nowPoint);

            // Every point, and the now point when it falls inside the track, in time order.
            const first = points[0];
            const last = points[points.length - 1];
            const inside = nowPoint !== undefined && first !== undefined && last !== undefined &&
                nowPoint.t >= first.t && nowPoint.t <= last.t && !points.some((p) => p.t === nowPoint.t);
            const expected = inside ? [...points, nowPoint].sort((a, b) => a.t - b.t) : points;
            expect(joined(segments)).toEqual(expected);

            for (const [i, segment] of segments.entries()) {
                expect(segment.points.length).toBeGreaterThanOrEqual(2);
                // Every stretch in a run starts at a sample with the run's style.
                for (const p of segment.points.slice(0, -1)) {
                    expect(p.sunlit).toBe(segment.sunlit);
                    expect(p.t < nowT).toBe(segment.past);
                }
                const previous = segments[i - 1];
                if (previous !== undefined) {
                    expect(previous.past !== segment.past || previous.sunlit !== segment.sunlit).toBe(true);
                }
            }
        }
    });
});

describe("antimeridian", () => {
    it("leaves longitudes alone and lets d3.geoPath cut the line at ±180°", () => {
        // 170° E to 170° W is 20° of longitude eastward across the antimeridian, not 340° back
        // across the map.
        const segment: TrackSegment = {
            past: false,
            sunlit: true,
            points: [
                { t: 0, latitudeDeg: 10, longitudeDeg: 170, sunlit: true },
                { t: step, latitudeDeg: 12, longitudeDeg: -170, sunlit: true },
            ],
        };
        const feature = segmentFeature(segment);
        expect(feature.geometry.coordinates).toEqual([[170, 10], [-170, 12]]);
        expect(feature.properties).toEqual({ past: false, sunlit: true });

        const width = 720;
        const projection = geoEquirectangular().fitExtent([[0, 0], [width, width / 2]], { type: "Sphere" });
        const d = geoPath(projection)(feature) ?? "";
        const pieces = d.split("M").filter((s) => s !== "");
        expect(pieces).toHaveLength(2);
        // Each piece stays within 10° of longitude (20 px at 2 px per degree) of its edge.
        for (const piece of pieces) {
            const xs = [...piece.matchAll(/(-?[\d.]+),(-?[\d.]+)/g)].map((m) => Number(m[1]));
            const nearLeft = xs.every((x) => x <= 20 + 1e-6);
            const nearRight = xs.every((x) => x >= width - 20 - 1e-6);
            expect(nearLeft || nearRight).toBe(true);
        }
    });
});
