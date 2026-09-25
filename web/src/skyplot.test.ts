import { describe, expect, it } from "vitest";
import { skyXY } from "./skyplot";

// A plot centered at (100, 100) with a 90-unit horizon radius, so one degree of elevation is one
// unit of radius. SVG y grows downward: "up" is smaller y. Lying on your back with your head to
// the north, east is on your left, so azimuth 90 plots to the left.
const cx = 100;
const cy = 100;
const R = 90;

function at(azimuth: number, elevation: number): [number, number] {
    return skyXY(azimuth, elevation, cx, cy, R);
}

function distanceFromCenter([x, y]: [number, number]): number {
    return Math.hypot(x - cx, y - cy);
}

describe("sky plot projection", () => {
    it("puts north up, east left, south down, and west right", () => {
        const north = at(0, 0);
        expect(north[0]).toBeCloseTo(cx, 12);
        expect(north[1]).toBeCloseTo(cy - R, 12);

        const east = at(90, 0);
        expect(east[0]).toBeCloseTo(cx - R, 12);
        expect(east[1]).toBeCloseTo(cy, 12);

        const south = at(180, 0);
        expect(south[0]).toBeCloseTo(cx, 12);
        expect(south[1]).toBeCloseTo(cy + R, 12);

        const west = at(270, 0);
        expect(west[0]).toBeCloseTo(cx + R, 12);
        expect(west[1]).toBeCloseTo(cy, 12);
    });

    it("puts the northeast up and to the left, between north and east", () => {
        const [x, y] = at(45, 0);
        expect(x).toBeCloseTo(cx - R / Math.SQRT2, 12);
        expect(y).toBeCloseTo(cy - R / Math.SQRT2, 12);
    });

    it("puts the zenith at the center, whatever the azimuth", () => {
        for (const az of [0, 37, 90, 211, 359]) {
            const [x, y] = at(az, 90);
            expect(x).toBeCloseTo(cx, 12);
            expect(y).toBeCloseTo(cy, 12);
        }
    });

    it("puts the horizon on the rim and 30° at two thirds of the radius", () => {
        for (const az of [0, 45, 123.4, 270, 300]) {
            expect(distanceFromCenter(at(az, 0))).toBeCloseTo(R, 12);
            expect(distanceFromCenter(at(az, 30))).toBeCloseTo((2 / 3) * R, 12);
            expect(distanceFromCenter(at(az, 60))).toBeCloseTo((1 / 3) * R, 12);
        }
    });

    it("is linear in elevation", () => {
        for (let el = 0; el <= 90; el += 7.5) {
            expect(distanceFromCenter(at(200, el))).toBeCloseTo(R - el, 12);
        }
    });
});
