import { describe, expect, it } from "vitest";
import { ServerClock, ZoneFormat, clockOffset, formatCountdown, formatDuration, offsetLabel, parseUtc, roundToSecond } from "./time";

// Expected instants are written as literals derived by hand, not with Date, so the parser is
// checked against arithmetic rather than against itself.
//
// 2026-09-24T00:00:00Z: 1970 to 2025 is 56 years with 14 leap days (1972 to 2024), 20,454 days;
// Jan 1 to Sep 24, 2026 adds 243 + 23 = 266; 20,720 days × 86,400,000 ms.
const sep24 = 1_790_208_000_000;
const hour = 3_600_000;

describe("parseUtc", () => {
    it("reads the API's instants, with 0 to 3 fractional digits", () => {
        expect(parseUtc("2026-09-24T00:00:00Z")).toBe(sep24);
        expect(parseUtc("2026-09-24T04:00:00Z")).toBe(sep24 + 4 * hour);
        expect(parseUtc("2026-09-24T04:00:00.5Z")).toBe(sep24 + 4 * hour + 500);
        expect(parseUtc("2026-09-24T04:00:00.12Z")).toBe(sep24 + 4 * hour + 120);
        expect(parseUtc("2026-09-24T04:00:00.123Z")).toBe(sep24 + 4 * hour + 123);
    });

    it("rejects anything that is not a UTC instant", () => {
        for (const bad of ["2026-09-24T04:00:00", "2026-09-24T04:00:00+00:00", "2026-09-24 04:00:00Z", "2026-02-30T00:00:00Z", "2026-09-24T24:00:00Z", "", "now"]) {
            expect(() => parseUtc(bad), bad).toThrow();
        }
    });
});

describe("roundToSecond", () => {
    it("rounds to the nearest second, halves up, as the CLI does", () => {
        expect(roundToSecond(sep24 + 1499)).toBe(sep24 + 1000);
        expect(roundToSecond(sep24 + 1500)).toBe(sep24 + 2000);
        expect(roundToSecond(sep24 + 999)).toBe(sep24 + 1000);
    });
});

describe("ZoneFormat in America/Phoenix", () => {
    const phoenix = new ZoneFormat("America/Phoenix");

    it("is UTC-7 all year: Arizona keeps standard time", () => {
        // 2026-07-01T12:00:00Z (summer) and 2026-01-15T12:00:00Z (winter) both read 05:00:00.
        // Jul 1 is day 181 of 2026 (index 181 from Jan 1), Jan 15 is index 14.
        const july1Noon = sep24 - 85 * 24 * hour + 12 * hour; // Sep 24 is index 266; 266 - 181 = 85
        const jan15Noon = sep24 - 252 * 24 * hour + 12 * hour; // 266 - 14 = 252
        expect(phoenix.clock(july1Noon)).toBe("05:00:00");
        expect(phoenix.clock(jan15Noon)).toBe("05:00:00");
        expect(phoenix.offsetLabel(july1Noon)).toBe("UTC-7");
        expect(phoenix.offsetLabel(jan15Noon)).toBe("UTC-7");
        expect(phoenix.offsetMinutes(july1Noon)).toBe(-420);
    });

    it("shows a UTC instant just after midnight UTC on the observer's own date", () => {
        // 2026-09-25T06:30:00Z is 23:30 on Thursday the 24th in Phoenix; 07:00Z is midnight.
        // 2026-09-24 is a Thursday: Jan 1, 2026 was a Thursday and index 266 is 38 weeks later.
        const t = sep24 + 24 * hour + 6.5 * hour;
        expect(phoenix.clock(t)).toBe("23:30:00");
        expect(phoenix.date(t)).toBe("Thu 24 Sep");
        expect(phoenix.isoDate(t)).toBe("2026-09-24");
        expect(phoenix.dateTime(t)).toBe("Thu 24 Sep 23:30:00");
        const midnight = sep24 + 24 * hour + 7 * hour;
        expect(phoenix.dateTime(midnight)).toBe("Fri 25 Sep 00:00:00");
        expect(phoenix.weekday(midnight)).toBe("Fri");
        expect(phoenix.clockMinutes(midnight + 90_000)).toBe("00:01");
    });

    it("drops milliseconds instead of rounding them into the next second", () => {
        expect(phoenix.clock(sep24 + 4 * hour + 999)).toBe("21:00:00");
    });
});

describe("ZoneFormat in America/Denver across daylight saving changes", () => {
    const denver = new ZoneFormat("America/Denver");

    it("falls back at 2026-11-01 08:00Z: 01:59:59 MDT is followed by 01:00:00 MST", () => {
        // DST ends on the first Sunday of November at 02:00 local daylight time (UTC-6), which is
        // 08:00Z. Nov 1, 2026 is index 304: 38 days after Sep 24.
        const fallBack = sep24 + 38 * 24 * hour + 8 * hour;
        expect(denver.clock(fallBack - 1000)).toBe("01:59:59");
        expect(denver.offsetLabel(fallBack - 1000)).toBe("UTC-6");
        expect(denver.clock(fallBack)).toBe("01:00:00");
        expect(denver.offsetLabel(fallBack)).toBe("UTC-7");

        // 01:30 happens twice; only the offset tells the two apart.
        expect(denver.clock(fallBack - hour / 2)).toBe("01:30:00");
        expect(denver.clock(fallBack + hour / 2)).toBe("01:30:00");
        expect(denver.offsetLabel(fallBack - hour / 2)).not.toBe(denver.offsetLabel(fallBack + hour / 2));
        expect(denver.date(fallBack)).toBe("Sun 1 Nov");
    });

    it("springs forward at 2026-03-08 09:00Z: 01:59:59 MST is followed by 03:00:00 MDT", () => {
        // DST starts on the second Sunday of March at 02:00 local standard time (UTC-7), which is
        // 09:00Z. Mar 8, 2026 is index 66: 200 days before Sep 24.
        const springForward = sep24 - 200 * 24 * hour + 9 * hour;
        expect(denver.clock(springForward - 1000)).toBe("01:59:59");
        expect(denver.offsetLabel(springForward - 1000)).toBe("UTC-7");
        expect(denver.clock(springForward)).toBe("03:00:00");
        expect(denver.offsetLabel(springForward)).toBe("UTC-6");
        expect(denver.date(springForward)).toBe("Sun 8 Mar");
    });
});

describe("offset labels", () => {
    it("reads whole and fractional offsets", () => {
        expect(offsetLabel(0)).toBe("UTC");
        expect(offsetLabel(-420)).toBe("UTC-7");
        expect(offsetLabel(330)).toBe("UTC+5:30");
        expect(offsetLabel(-570)).toBe("UTC-9:30");
        expect(new ZoneFormat("Asia/Kolkata").offsetLabel(sep24)).toBe("UTC+5:30");
        expect(new ZoneFormat("Asia/Kathmandu").offsetLabel(sep24)).toBe("UTC+5:45");
        expect(new ZoneFormat("UTC").offsetLabel(sep24)).toBe("UTC");
    });

    it("rejects a zone the time zone database does not have", () => {
        expect(() => new ZoneFormat("America/Nowhere")).toThrow(RangeError);
    });
});

describe("durations", () => {
    it("counts down in whole seconds, rounded up, with units once it passes an hour", () => {
        expect(formatCountdown(0)).toBe("00:00");
        expect(formatCountdown(-5000)).toBe("00:00");
        expect(formatCountdown(1)).toBe("00:01");
        expect(formatCountdown(59_000)).toBe("00:59");
        expect(formatCountdown(61_000)).toBe("01:01");
        expect(formatCountdown(3_599_000)).toBe("59:59");
        expect(formatCountdown(3_599_001)).toBe("1h 00m 00s");
        expect(formatCountdown(3_723_000)).toBe("1h 02m 03s");
        expect(formatCountdown(83_179_000)).toBe("23h 06m 19s");
        expect(formatCountdown(90_061_000)).toBe("1d 01h 01m 01s");
    });

    it("gives pass durations to the nearest second", () => {
        expect(formatDuration(548_000)).toBe("9m 08s");
        expect(formatDuration(548_499)).toBe("9m 08s");
        expect(formatDuration(548_500)).toBe("9m 09s");
        expect(formatDuration(3_723_000)).toBe("1h 02m 03s");
    });
});

describe("server clock", () => {
    it("takes the offset from the midpoint of the request", () => {
        // The server stamped 1000 somewhere between browser times 0 and 100: offset 1000 - 50.
        expect(clockOffset(1000, 0, 100)).toBe(950);
        expect(clockOffset(1000, 1000, 1000)).toBe(0);
    });

    it("runs a simulated clock at the browser's rate from the server's stamp", () => {
        // Browser at 2026-09-25T03:00:00Z, server simulated at 2026-09-24T04:00:00Z.
        const browserStart = sep24 + 27 * hour;
        const server = sep24 + 4 * hour;
        let local = browserStart;
        const clock = new ServerClock(() => local);
        expect(clock.isSynced).toBe(false);

        clock.update(server, browserStart, browserStart + 40);
        // Stamped at the midpoint (browser + 20 ms); the stamp is truncated, so +0.5 ms on average.
        expect(clock.offsetMs).toBeCloseTo(server - (browserStart + 20) + 0.5, 6);
        local = browserStart + 1020;
        expect(clock.now()).toBeCloseTo(server + 1000.5, 6);
        expect(clock.isSynced).toBe(true);
    });

    it("keeps the tightest estimate and ignores a looser one that agrees with it", () => {
        let local = 0;
        const clock = new ServerClock(() => local);
        clock.update(10_050, 0, 100); // offset 10,000 ± 50 (plus the stamp's half millisecond)
        clock.update(10_210, 200, 210); // offset 10,005 ± 5: tighter, taken
        expect(clock.offsetMs).toBeCloseTo(10_005.5, 6);
        clock.update(10_320, 300, 340); // offset 10,000 ± 20: looser and consistent, ignored
        expect(clock.offsetMs).toBeCloseTo(10_005.5, 6);
        local = 1000;
        expect(clock.now()).toBeCloseTo(11_005.5, 6);
    });

    it("takes a new estimate that cannot agree with the kept one: the server's clock was reset", () => {
        const clock = new ServerClock(() => 0);
        clock.update(10_005, 0, 10);
        clock.update(90_000, 1000, 1100); // 79,000 ms away with ±50: a restart, taken
        expect(clock.offsetMs).toBeCloseTo(90_000 - 1050 + 0.5, 6);
    });

    it("lets an old estimate's uncertainty grow at 100 ppm so newer ones replace it", () => {
        const clock = new ServerClock(() => 0);
        clock.update(10_005, 0, 10); // ±5.5 ms at browser time 10
        expect(clock.uncertaintyMs(10)).toBeCloseTo(5.5, 9);
        // An hour later the kept estimate is good to 5.5 + 3,600,000 × 1e-4 = 365.5 ms.
        expect(clock.uncertaintyMs(10 + hour)).toBeCloseTo(365.5, 9);
        clock.update(10_000 + hour + 60, hour, hour + 40); // ±20.5 ms, now tighter: taken
        expect(clock.offsetMs).toBeCloseTo(10_040.5, 6);
    });
});
