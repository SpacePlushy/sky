import { describe, expect, it } from "vitest";
import { azimuthLabel, compassPoint, fixed, grouped, latitude, longitude, minus, normalizeLongitude, signed } from "./format";

describe("compass points", () => {
    it("names the 16 points, each covering 22.5° centered on its direction", () => {
        const names = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];
        names.forEach((name, i) => {
            expect(compassPoint(i * 22.5)).toBe(name);
            expect(compassPoint(i * 22.5 + 11)).toBe(name);
            expect(compassPoint(i * 22.5 - 11)).toBe(name);
        });
        expect(compassPoint(359.9)).toBe("N");
        expect(compassPoint(-10)).toBe("N");
        expect(compassPoint(720 + 90)).toBe("E");
    });

    it("labels an azimuth with its point and whole degrees", () => {
        expect(azimuthLabel(337.6)).toBe("NNW 338°");
        expect(azimuthLabel(359.6)).toBe("N 0°");
        expect(azimuthLabel(130.2)).toBe("SE 130°");
    });
});

describe("numbers", () => {
    it("uses a real minus sign and never shows negative zero", () => {
        expect(fixed(-12.34, 1)).toBe(`${minus}12.3`);
        expect(fixed(-0.04, 1)).toBe("0.0");
        expect(fixed(Number.NaN, 1)).toBe("—");
        expect(signed(3.2, 1)).toBe("+3.2");
        expect(signed(-3.2, 1)).toBe(`${minus}3.2`);
        expect(signed(0.0004, 3)).toBe("0.000");
        expect(grouped(2345.67, 1)).toBe("2,345.7");
        expect(grouped(-2345.67, 1)).toBe(`${minus}2,345.7`);
    });

    it("gives hemispheres", () => {
        expect(latitude(33.4478)).toBe("33.45° N");
        expect(latitude(-51.6)).toBe("51.60° S");
        expect(latitude(0.001)).toBe("0.00°");
        expect(longitude(-112.0972)).toBe("112.10° W");
        expect(longitude(190)).toBe("170.00° W");
        expect(longitude(45)).toBe("45.00° E");
        expect(normalizeLongitude(180)).toBe(-180);
        expect(normalizeLongitude(-181)).toBe(179);
    });
});
