// The telemetry panel: where the satellite is, how it looks from the observer, and when it next
// comes up. Values update from /now once a second; countdowns tick on the server's clock.

import { h, setText } from "./dom";
import { azimuthLabel, compassPoint, fixed, grouped, latitude, longitude, signed } from "./format";
import { twilightSunElevationDeg } from "./geo";
import type { Now, Pass, Passes } from "./model";
import { passStatus } from "./passes";
import { formatCountdown, roundToSecond, type ZoneFormat } from "./time";

const rows = [
    ["latitude", "Latitude"],
    ["longitude", "Longitude"],
    ["altitude", "Altitude"],
    ["speed", "Inertial speed"],
    ["azimuth", "Azimuth"],
    ["elevation", "Elevation"],
    ["range", "Range"],
    ["rangeRate", "Range rate"],
    ["sunlight", "Satellite"],
    ["sun", "Sun elevation"],
    ["elements", "Elements"],
    ["nextPass", "Next pass"],
    ["nextVisible", "Next visible"],
] as const;

type RowKey = (typeof rows)[number][0];

export interface CountdownView {
    readonly nowMs: number;
    readonly zone: ZoneFormat;
    /** The pass /now reported as in progress, if any. */
    readonly currentPass: Pass | null;
    readonly passes: Passes | null;
    readonly minimumElevationDeg: number;
}

export class Telemetry {
    private readonly values = new Map<RowKey, HTMLElement>();
    private readonly notes = new Map<RowKey, HTMLElement>();

    constructor(list: HTMLElement) {
        for (const [key, label] of rows) {
            const value = h("span", { class: "value" }, "—");
            const note = h("span", { class: "note" });
            list.append(h("div", { class: `row row-${key}` }, h("dt", {}, label), h("dd", {}, value, note)));
            this.values.set(key, value);
            this.notes.set(key, note);
        }
    }

    private set(key: RowKey, value: string, note = ""): void {
        const v = this.values.get(key);
        const n = this.notes.get(key);
        if (v !== undefined) {
            setText(v, value);
        }
        if (n !== undefined) {
            setText(n, note);
        }
    }

    /** The /now values, or dashes while there are none. */
    update(now: Now | null, zone: ZoneFormat): void {
        if (now === null) {
            for (const [key] of rows) {
                if (key !== "nextPass" && key !== "nextVisible") {
                    this.set(key, "—");
                }
            }
            return;
        }
        const { position: p, look, sun } = now;
        this.set("latitude", latitude(p.latitudeDeg));
        this.set("longitude", longitude(p.longitudeDeg));
        this.set("altitude", `${grouped(p.altitudeKm, 1)} km`);
        this.set("speed", `${fixed(p.inertialSpeedKmS, 3)} km/s`);
        this.set("azimuth", `${fixed(look.azimuthDeg, 1)}°`, compassPoint(look.azimuthDeg));
        this.set("elevation", `${fixed(look.elevationDeg, 1)}°`, look.elevationDeg < 0 ? "below the horizon" : "above the horizon");
        this.set("range", `${grouped(look.rangeKm, 1)} km`);
        this.set("rangeRate", `${signed(look.rangeRateKmS, 3)} km/s`, look.rangeRateKmS < 0 ? "approaching" : "receding");
        this.set("sunlight", now.sunlit ? "Sunlit" : "In Earth's shadow");
        this.set("sun", `${fixed(sun.elevationDeg, 1)}°`, sun.elevationDeg < twilightSunElevationDeg ? "dark enough" : "too bright");
        // An epoch after "now" happens with a simulated clock set before the data was recorded.
        const age = now.satellite.ageDays;
        // Rounded once, so the clock and its offset name the same instant near a DST change.
        const epoch = roundToSecond(now.satellite.epoch);
        this.set(
            "elements",
            age >= 0 ? `${fixed(age, 2)} days old` : `${fixed(-age, 2)} days ahead of the clock`,
            `epoch ${zone.dateTime(epoch)} ${zone.offsetLabel(epoch)}`,
        );
    }

    /** The countdowns, once a second on the server's clock. */
    tick(view: CountdownView): void {
        const { nowMs, zone, currentPass, passes } = view;
        const listed = passes?.passes ?? [];
        const inProgress = (currentPass !== null && passStatus(currentPass, nowMs) === "inProgress" ? currentPass : undefined)
            ?? listed.find((p) => passStatus(p, nowMs) === "inProgress");

        if (inProgress !== undefined) {
            this.set("nextPass", `In progress, sets in ${formatCountdown(inProgress.set.t - nowMs)}`, `sets ${zone.clock(roundToSecond(inProgress.set.t))} in the ${azimuthLabel(inProgress.set.azimuthDeg)}`);
        } else if (passes === null) {
            this.set("nextPass", "—");
        } else {
            const next = listed.find((p) => passStatus(p, nowMs) === "upcoming");
            if (next === undefined) {
                this.set("nextPass", "None", `none above ${fixed(view.minimumElevationDeg, 0)}° before ${zone.dateTime(passes.to)}`);
            } else {
                const rise = roundToSecond(next.rise.t);
                this.set(
                    "nextPass",
                    `Rises in ${formatCountdown(next.rise.t - nowMs)}`,
                    `${zone.dateTime(rise)} in the ${azimuthLabel(next.rise.azimuthDeg)}, up to ${fixed(next.culmination.elevationDeg, 1)}°`,
                );
            }
        }

        if (passes === null) {
            this.set("nextVisible", "—");
            return;
        }
        for (const pass of listed) {
            for (const part of pass.visible) {
                if (part.end.t < nowMs) {
                    continue;
                }
                if (part.start.t <= nowMs) {
                    this.set("nextVisible", `Visible now, for ${formatCountdown(part.end.t - nowMs)}`, `until ${zone.clock(roundToSecond(part.end.t))}, up to ${fixed(part.highest.elevationDeg, 1)}°`);
                } else {
                    this.set(
                        "nextVisible",
                        `In ${formatCountdown(part.start.t - nowMs)}`,
                        `${zone.dateTime(roundToSecond(part.start.t))}, up to ${fixed(part.highest.elevationDeg, 1)}°`,
                    );
                }
                return;
            }
        }
        this.set("nextVisible", "None", `no visible pass before ${zone.dateTime(passes.to)}`);
    }
}
