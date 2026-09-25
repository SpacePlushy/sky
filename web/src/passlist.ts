// The pass list: a table on wide screens and stacked cards on narrow ones, from the same markup.
// Each pass is a button, so the list works from the keyboard; the selected pass is drawn on the
// sky plot. Passes are keyed by rise time, never by position in the list.

import { h } from "./dom";
import { azimuthLabel, fixed } from "./format";
import type { Pass, Passes } from "./model";
import { describeVisiblePart, findByRise, notVisibleReason, passStatus } from "./passes";
import { formatDuration, roundToSecond, type ZoneFormat } from "./time";

export interface PassListView {
    readonly passes: Passes | null;
    readonly nowMs: number;
    readonly selected: Pass | undefined;
    readonly zone: ZoneFormat;
    readonly minimumElevationDeg: number;
    readonly error: boolean;
}

const versions = new WeakMap<Passes, number>();
let nextVersion = 1;

function versionOf(passes: Passes): number {
    let v = versions.get(passes);
    if (v === undefined) {
        v = nextVersion++;
        versions.set(passes, v);
    }
    return v;
}

export class PassList {
    private readonly container: HTMLElement;
    private readonly note: HTMLElement;
    private readonly onSelect: (riseMs: number) => void;
    private signature = "";

    constructor(container: HTMLElement, note: HTMLElement, onSelect: (riseMs: number) => void) {
        this.container = container;
        this.note = note;
        this.onSelect = onSelect;
    }

    /** Rebuilds the list only when something visible changed, keeping keyboard focus on its pass. */
    render(view: PassListView): void {
        const { passes, nowMs, selected, zone } = view;
        const listed = passes === null ? [] : passes.passes.filter((p) => passStatus(p, nowMs) !== "ended");
        const signature = [
            passes === null ? (view.error ? "error" : "loading") : versionOf(passes),
            zone.timeZone,
            selected?.rise.t ?? "none",
            listed.map((p) => `${p.rise.t}:${passStatus(p, nowMs)}`).join(","),
        ].join("|");
        if (signature === this.signature) {
            return;
        }
        this.signature = signature;

        const focused = document.activeElement instanceof HTMLElement && this.container.contains(document.activeElement)
            ? document.activeElement.dataset.rise
            : undefined;

        this.note.replaceChildren(...this.notes(view, listed));

        if (passes === null) {
            this.container.replaceChildren(h("p", { class: "empty" }, view.error ? "Passes could not be loaded." : "Loading passes…"));
            return;
        }
        if (listed.length === 0) {
            this.container.replaceChildren(h("p", { class: "empty" }, `No passes above ${fixed(view.minimumElevationDeg, 0)}° before ${zone.dateTime(passes.to)}.`));
            return;
        }

        // Where the zone's offset changes inside the list (daylight saving), a wall-clock time alone
        // can be ambiguous, so every time then carries its offset.
        const offsets = new Set(listed.flatMap((p) => [zone.offsetLabel(p.rise.t), zone.offsetLabel(p.set.t)]));
        const showOffsets = offsets.size > 1;

        const header = h(
            "div",
            { class: "pass-header", "aria-hidden": "true" },
            h("span", {}, "Date"),
            h("span", {}, "Rise"),
            h("span", {}, "Peak"),
            h("span", {}, "Set"),
            h("span", {}, "Duration"),
            h("span", {}, "Visible"),
        );
        const list = h("ol", { class: "pass-list" });
        for (const pass of listed) {
            list.append(this.row(pass, view, showOffsets));
        }
        this.container.replaceChildren(header, list);

        // A refresh can move a rise by a millisecond, so focus follows the same pass by the same
        // tolerance the selection uses, not by an exact match.
        if (focused !== undefined) {
            const target = findByRise(listed, Number(focused));
            if (target !== undefined) {
                this.container.querySelector<HTMLButtonElement>(`button[data-rise="${target.rise.t}"]`)?.focus();
            }
        }
    }

    private notes(view: PassListView, listed: readonly Pass[]): (Node | string)[] {
        const { passes, zone } = view;
        const parts: string[] = [];
        const first = listed[0];
        const last = listed[listed.length - 1];
        const from = first?.rise.t ?? passes?.from ?? view.nowMs;
        const to = last?.set.t ?? passes?.to ?? view.nowMs;
        const a = zone.offsetLabel(from);
        const b = zone.offsetLabel(to);
        parts.push(`Times in ${zone.timeZone} (${a === b ? a : `${a}, then ${b} after the daylight saving change; each time shows its offset`}).`);
        parts.push(`Rise and set at ${fixed(view.minimumElevationDeg, 0)}° elevation. Visible means sunlit while the Sun is more than 6° below the horizon.`);
        if (passes !== null && passes.aboveMinimumAtStartSince !== null) {
            parts.push(`Already above ${fixed(view.minimumElevationDeg, 0)}° since before ${zone.dateTime(passes.aboveMinimumAtStartSince)}; that pass has no rise to list.`);
        }
        if (passes !== null && passes.aboveMinimumAtEndUntil !== null) {
            parts.push(`Still above ${fixed(view.minimumElevationDeg, 0)}° at ${zone.dateTime(passes.aboveMinimumAtEndUntil)}; that pass has no set to list.`);
        }
        if (passes !== null && passes.stoppedAt !== null) {
            parts.push(`SGP4 stopped at ${zone.dateTime(passes.stoppedAt)} (${passes.stoppedBy ?? "error"}); no passes can be predicted after that.`);
        }
        return [parts.join(" ")];
    }

    private row(pass: Pass, view: PassListView, showOffsets: boolean): HTMLLIElement {
        const { zone, nowMs, selected } = view;
        const status = passStatus(pass, nowMs);
        const isVisible = pass.visible.length > 0;
        const isSelected = selected?.rise.t === pass.rise.t;

        const rise = roundToSecond(pass.rise.t);
        const peak = roundToSecond(pass.culmination.t);
        const set = roundToSecond(pass.set.t);
        const riseDay = zone.isoDate(rise);
        // A time on a later local day than the rise says which day.
        const time = (ms: number): string => {
            const day = zone.isoDate(ms) === riseDay ? "" : ` (${zone.weekday(ms)})`;
            return `${zone.clock(ms)}${showOffsets ? ` ${zone.offsetLabel(ms)}` : ""}${day}`;
        };
        const label = (text: string): HTMLElement => h("span", { class: "label" }, text);

        const badges = h("span", { class: "badges" });
        if (status === "inProgress") {
            badges.append(h("span", { class: "badge badge-live" }, "In progress"));
        }
        if (isVisible) {
            badges.append(h("span", { class: "badge badge-visible" }, "Visible"));
        }
        if (isSelected) {
            badges.append(h("span", { class: "badge badge-plotted" }, "On sky plot"));
        }

        const visibleText = isVisible
            ? pass.visible.map((part) => describeVisiblePart(part, time)).join("; ")
            : `Not visible (${notVisibleReason(pass.path)})`;

        const button = h(
            "button",
            {
                type: "button",
                class: "pass",
                "data-rise": String(pass.rise.t),
                "aria-pressed": String(isSelected),
            },
            h("span", { class: "cell cell-date" }, label("Date"), h("span", { class: "date" }, zone.date(rise)), badges),
            h("span", { class: "cell cell-rise" }, label("Rise"), h("span", { class: "time" }, time(rise)), h("span", { class: "sub" }, azimuthLabel(pass.rise.azimuthDeg))),
            h("span", { class: "cell cell-peak" }, label("Peak"), h("span", { class: "time" }, time(peak)), h("span", { class: "sub" }, `${fixed(pass.culmination.elevationDeg, 1)}°`)),
            h("span", { class: "cell cell-set" }, label("Set"), h("span", { class: "time" }, time(set)), h("span", { class: "sub" }, azimuthLabel(pass.set.azimuthDeg))),
            h("span", { class: "cell cell-duration" }, label("Duration"), h("span", { class: "time" }, formatDuration(set - rise))),
            h("span", { class: "cell cell-visible" }, label("Visible"), h("span", { class: "visible-text" }, visibleText)),
        );
        button.addEventListener("click", () => {
            this.onSelect(pass.rise.t);
        });

        const classes = ["pass-item"];
        if (isVisible) {
            classes.push("is-visible");
        }
        if (status === "inProgress") {
            classes.push("is-live");
        }
        if (isSelected) {
            classes.push("is-selected");
        }
        return h("li", { class: classes.join(" ") }, button);
    }
}
