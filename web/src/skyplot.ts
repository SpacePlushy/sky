// The polar sky plot: the sky as seen lying on your back, head to the north. The zenith is at the
// center and the horizon at the rim, with elevation linear in radius. North is up and east is on
// the LEFT (south up would put east on the right; looking up mirrors a map).

import { azimuthLabel, fixed } from "./format";
import type { Pass, PassEvent } from "./model";
import { splitPathByVisibility } from "./passes";
import { roundToSecond, type ZoneFormat } from "./time";
import { svg, clear } from "./dom";

/**
 * Where an azimuth and elevation plot: r = R(90 − el)/90 from the center, at x = cx − r sin(az),
 * y = cy − r cos(az) (SVG y grows downward). Azimuth 0 is up, 90 (east) is left.
 */
export function skyXY(azimuthDeg: number, elevationDeg: number, cx: number, cy: number, radius: number): [number, number] {
    const r = (radius * (90 - elevationDeg)) / 90;
    const a = (azimuthDeg * Math.PI) / 180;
    return [cx - r * Math.sin(a), cy - r * Math.cos(a)];
}

const size = 320;
const center = size / 2;
const radius = 128;

function xy(azimuthDeg: number, elevationDeg: number): [number, number] {
    // Below the horizon cannot happen within a pass; clamp so a rounding error stays on the rim.
    return skyXY(azimuthDeg, Math.max(0, Math.min(90, elevationDeg)), center, center, radius);
}

function pathData(points: readonly { azimuthDeg: number; elevationDeg: number }[]): string {
    return points.map((p, i) => {
        const [x, y] = xy(p.azimuthDeg, p.elevationDeg);
        return `${i === 0 ? "M" : "L"}${x.toFixed(2)},${y.toFixed(2)}`;
    }).join("");
}

export interface SkyPlotView {
    readonly pass: Pass | null;
    /** The satellite's look angles now, shown when the pass is in progress. */
    readonly now: { azimuthDeg: number; elevationDeg: number } | null;
    readonly minimumElevationDeg: number;
    readonly zone: ZoneFormat;
}

/** Draws the sky plot into an <svg> and returns a sentence describing it. */
export class SkyPlot {
    private readonly root: SVGSVGElement;
    private readonly title: SVGTitleElement;
    private readonly dynamic: SVGGElement;
    private readonly minRing: SVGCircleElement;
    private readonly minLabel: SVGTextElement;

    constructor(container: HTMLElement) {
        this.root = svg("svg", { viewBox: `0 0 ${size} ${size}`, role: "img", class: "skyplot", "aria-labelledby": "skyplot-title" });
        this.title = svg("title", { id: "skyplot-title" });
        this.title.textContent = "Sky plot";
        this.root.append(this.title);

        const grid = svg("g", { class: "sky-grid" });
        grid.append(svg("circle", { cx: center, cy: center, r: radius, class: "sky-disk" }));
        for (const el of [30, 60]) {
            grid.append(svg("circle", { cx: center, cy: center, r: (radius * (90 - el)) / 90, class: "sky-ring" }));
        }
        this.minRing = svg("circle", { cx: center, cy: center, r: radius, class: "sky-min-ring" });
        grid.append(this.minRing);
        grid.append(svg("line", { x1: center, y1: center - radius, x2: center, y2: center + radius, class: "sky-ring" }));
        grid.append(svg("line", { x1: center - radius, y1: center, x2: center + radius, y2: center, class: "sky-ring" }));
        grid.append(svg("circle", { cx: center, cy: center, r: radius, class: "sky-rim" }));

        // Ring labels just east of north, inside the disk.
        for (const el of [30, 60]) {
            const [, y] = xy(0, el);
            const label = svg("text", { x: center + 4, y: y - 3, class: "sky-ring-label" });
            label.textContent = `${el}°`;
            grid.append(label);
        }
        this.minLabel = svg("text", { x: center + 4, y: center - radius, class: "sky-ring-label" });
        grid.append(this.minLabel);

        const cardinal: [string, number, number, string][] = [
            ["N", center, center - radius - 8, "middle"],
            ["S", center, center + radius + 18, "middle"],
            ["E", center - radius - 8, center + 5, "end"],
            ["W", center + radius + 8, center + 5, "start"],
        ];
        for (const [text, x, y, anchor] of cardinal) {
            const label = svg("text", { x, y, class: "sky-cardinal", "text-anchor": anchor });
            label.textContent = text;
            grid.append(label);
        }

        this.dynamic = svg("g", {});
        this.root.append(grid, this.dynamic);
        container.append(this.root);
    }

    render(view: SkyPlotView): string {
        const { pass, now, minimumElevationDeg, zone } = view;
        const r = (radius * (90 - minimumElevationDeg)) / 90;
        this.minRing.setAttribute("r", r.toFixed(2));
        this.minLabel.setAttribute("y", (center - r + 11).toFixed(2));
        this.minLabel.textContent = minimumElevationDeg > 0 && minimumElevationDeg < 25 ? `${fixed(minimumElevationDeg, 0)}°` : "";
        clear(this.dynamic);

        if (pass === null) {
            const note = svg("text", { x: center, y: center + 4, class: "sky-empty", "text-anchor": "middle" });
            note.textContent = "No pass selected";
            this.dynamic.append(note);
            this.title.textContent = "Sky plot: no pass selected.";
            return "No pass to show.";
        }

        for (const run of splitPathByVisibility(pass.path, pass.visible)) {
            this.dynamic.append(svg("path", { d: pathData(run.points), class: run.visible ? "sky-path sky-path-visible" : "sky-path" }));
        }

        const marker = (event: PassEvent, kind: "rise" | "set" | "peak"): void => {
            const [x, y] = xy(event.azimuthDeg, event.elevationDeg);
            if (kind === "rise") {
                this.dynamic.append(svg("circle", { cx: x, cy: y, r: 5, class: "sky-marker sky-rise" }));
            } else if (kind === "set") {
                this.dynamic.append(svg("rect", { x: x - 4.5, y: y - 4.5, width: 9, height: 9, class: "sky-marker sky-set" }));
            } else {
                this.dynamic.append(svg("path", { d: `M${x},${y - 6}L${x + 6},${y}L${x},${y + 6}L${x - 6},${y}Z`, class: "sky-marker sky-peak" }));
                const label = svg("text", { x: x + 9, y: y + 4, class: "sky-peak-label" });
                label.textContent = `${fixed(event.elevationDeg, 0)}°`;
                this.dynamic.append(label);
            }
        };
        marker(pass.rise, "rise");
        marker(pass.set, "set");
        marker(pass.culmination, "peak");

        if (now !== null && now.elevationDeg >= 0) {
            const [x, y] = xy(now.azimuthDeg, now.elevationDeg);
            this.dynamic.append(svg("circle", { cx: x, cy: y, r: 10, class: "sky-now-halo" }));
            this.dynamic.append(svg("circle", { cx: x, cy: y, r: 5.5, class: "sky-now" }));
        }

        const rise = roundToSecond(pass.rise.t);
        const peak = roundToSecond(pass.culmination.t);
        const set = roundToSecond(pass.set.t);
        const sentence =
            `${zone.date(rise)}: rises ${zone.clock(rise)} in the ${azimuthLabel(pass.rise.azimuthDeg)}, ` +
            `highest ${fixed(pass.culmination.elevationDeg, 1)}° at ${zone.clock(peak)} in the ${azimuthLabel(pass.culmination.azimuthDeg)}, ` +
            `sets ${zone.clock(set)} in the ${azimuthLabel(pass.set.azimuthDeg)}.` +
            (now !== null ? " The satellite is up now." : "");
        this.title.textContent = `Sky plot of the selected pass. ${sentence}`;
        return sentence;
    }
}
