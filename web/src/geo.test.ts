import { geoContains, geoDistance } from "d3-geo";
import { describe, expect, it } from "vitest";
import { antisolarPoint, darkPolygon, footprintPolygon, nightPolygon } from "./geo";

const rad = Math.PI / 180;

/**
 * The point `distanceDeg` of arc from (lat, lon) along the initial bearing `bearingDeg`, by the
 * spherical direct formula (independent of d3): lat2 = asin(sin φ cos δ + cos φ sin δ cos θ),
 * lon2 = λ + atan2(sin θ sin δ cos φ, cos δ − sin φ sin lat2). Returned as [lon, lat].
 */
function destination(latDeg: number, lonDeg: number, bearingDeg: number, distanceDeg: number): [number, number] {
    const phi = latDeg * rad;
    const lambda = lonDeg * rad;
    const theta = bearingDeg * rad;
    const delta = distanceDeg * rad;
    const lat2 = Math.asin(Math.sin(phi) * Math.cos(delta) + Math.cos(phi) * Math.sin(delta) * Math.cos(theta));
    const lon2 = lambda + Math.atan2(Math.sin(theta) * Math.sin(delta) * Math.cos(phi), Math.cos(delta) - Math.sin(phi) * Math.sin(lat2));
    const lon = ((((lon2 / rad) + 180) % 360) + 360) % 360 - 180;
    return [lon, lat2 / rad];
}

/** The initial bearing from (lat1, lon1) to (lat2, lon2), in degrees clockwise from north. */
function initialBearing(lat1Deg: number, lon1Deg: number, lat2Deg: number, lon2Deg: number): number {
    const phi1 = lat1Deg * rad;
    const phi2 = lat2Deg * rad;
    const dLambda = (lon2Deg - lon1Deg) * rad;
    const y = Math.sin(dLambda) * Math.cos(phi2);
    const x = Math.cos(phi1) * Math.sin(phi2) - Math.sin(phi1) * Math.cos(phi2) * Math.cos(dLambda);
    return Math.atan2(y, x) / rad;
}

// Subsolar points across the year and the map: the equinox, both solstices, and points beside the
// antimeridian, where a hand-built terminator usually breaks.
const subsolarPoints: [number, number][] = [
    [0, 0],
    [23.44, -112.1],
    [-23.44, 179.5],
    [10.2, -179.9],
    [-5, 90],
    [1.2, 180],
];
const bearings = Array.from({ length: 24 }, (_, i) => i * 15 + 7);

describe("night side", () => {
    it("the direct formula agrees with d3.geoDistance, so the test points are where they claim", () => {
        for (const [lat, lon] of subsolarPoints) {
            for (const bearing of bearings) {
                const p = destination(lat, lon, bearing, 89);
                expect(geoDistance([lon, lat], p)).toBeCloseTo(89 * rad, 12);
            }
        }
    });

    it("puts the subsolar point in day and the antisolar point in night", () => {
        for (const [lat, lon] of subsolarPoints) {
            const night = nightPolygon(lat, lon);
            expect(geoContains(night, [lon, lat])).toBe(false);
            expect(geoContains(night, antisolarPoint(lat, lon))).toBe(true);
        }
    });

    it("has the terminator 90° of arc from the subsolar point: 89° is day, 91° is night", () => {
        for (const [lat, lon] of subsolarPoints) {
            const night = nightPolygon(lat, lon);
            for (const bearing of bearings) {
                expect(geoContains(night, destination(lat, lon, bearing, 89)), `89° at ${bearing}° from ${lat},${lon}`).toBe(false);
                expect(geoContains(night, destination(lat, lon, bearing, 91)), `91° at ${bearing}° from ${lat},${lon}`).toBe(true);
            }
        }
    });

    it("darkens where the Sun is more than 6° down: 95° from the subsolar point is not, 97° is", () => {
        for (const [lat, lon] of subsolarPoints) {
            const dark = darkPolygon(lat, lon);
            for (const bearing of bearings) {
                expect(geoContains(dark, destination(lat, lon, bearing, 95))).toBe(false);
                expect(geoContains(dark, destination(lat, lon, bearing, 97))).toBe(true);
            }
        }
    });

    it("finds the antisolar point opposite the subsolar point", () => {
        const [lon1, lat1] = antisolarPoint(23.44, -112.1);
        expect(lon1).toBeCloseTo(67.9, 12);
        expect(lat1).toBeCloseTo(-23.44, 12);
        const [lon2, lat2] = antisolarPoint(0, 0);
        expect(lon2).toBe(-180);
        expect(lat2).toBeCloseTo(0, 12);
        for (const [lat, lon] of subsolarPoints) {
            expect(geoDistance([lon, lat], antisolarPoint(lat, lon))).toBeCloseTo(Math.PI, 9);
        }
    });
});

describe("footprint", () => {
    // Radii like the API's: ISS horizon (about 20°) and 10° minimum (about 12°), a geostationary
    // horizon (about 81°); centers at the equator, beside the antimeridian, and near a pole.
    const cases: { lat: number; lon: number; radius: number }[] = [
        { lat: 0, lon: 0, radius: 20.1 },
        { lat: 33.4, lon: -112.1, radius: 12.3 },
        { lat: -51.6, lon: 179.2, radius: 20.1 },
        { lat: 51.6, lon: -179.8, radius: 12.3 },
        { lat: 85, lon: 30, radius: 20.1 },
        { lat: 0, lon: -75, radius: 81.3 },
    ];

    it("puts every boundary vertex at the footprint radius from the subpoint (within 1e-6 rad)", () => {
        for (const { lat, lon, radius } of cases) {
            const ring = footprintPolygon(lat, lon, radius).coordinates[0] ?? [];
            expect(ring.length).toBeGreaterThan(300);
            for (const vertex of ring) {
                expect(Math.abs(geoDistance([lon, lat], [vertex[0] ?? NaN, vertex[1] ?? NaN]) - radius * rad)).toBeLessThan(1e-6);
            }
        }
    });

    it("puts the point at the footprint radius along each vertex's bearing on that vertex (within 1e-6 rad)", () => {
        for (const { lat, lon, radius } of cases) {
            const ring = footprintPolygon(lat, lon, radius).coordinates[0] ?? [];
            for (const vertex of ring) {
                const v: [number, number] = [vertex[0] ?? NaN, vertex[1] ?? NaN];
                const p = destination(lat, lon, initialBearing(lat, lon, v[1], v[0]), radius);
                expect(geoDistance(p, v)).toBeLessThan(1e-6);
            }
        }
    });

    it("contains points just inside the radius and not those just outside", () => {
        for (const { lat, lon, radius } of cases) {
            const footprint = footprintPolygon(lat, lon, radius);
            expect(geoContains(footprint, [lon, lat])).toBe(true);
            for (const bearing of bearings) {
                expect(geoContains(footprint, destination(lat, lon, bearing, radius - 0.01))).toBe(true);
                expect(geoContains(footprint, destination(lat, lon, bearing, radius + 0.01))).toBe(false);
            }
        }
    });
});
