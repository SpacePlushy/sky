// The dashboard's controller: loads the configuration and satellite list, then polls /now once a
// second and refreshes the track and passes every few minutes for the selected satellite.
//
// Races: every request for a satellite belongs to a Scope with its own AbortController. Choosing
// another satellite aborts the old scope, and a response is used only if its scope is still the
// current one, so a late answer for the previous satellite can never be drawn.

import { ApiError, api, isAbort } from "./api";
import { byId, h, setText } from "./dom";
import { WorldMap } from "./map";
import type { Config, Now, Passes, SatelliteSummary, Track } from "./model";
import { PassList } from "./passlist";
import { selectedPass, passStatus } from "./passes";
import { SkyPlot } from "./skyplot";
import { Telemetry } from "./telemetry";
import { ServerClock, zoneFormat, type ZoneFormat } from "./time";

/** How often each resource is refreshed, and retried after a failure, in milliseconds. */
const refresh = {
    track: 2 * 60_000,
    passes: 5 * 60_000,
    retry: 15_000,
} as const;

const passDays = 7;

type Source = "config" | "satellites" | "now" | "track" | "passes";

interface Scope {
    readonly id: number;
    readonly controller: AbortController;
    nowBusy: boolean;
    trackBusy: boolean;
    passesBusy: boolean;
    /** Browser-clock times (Date.now()) when the track and passes are due. */
    trackDue: number;
    passesDue: number;
}

interface Banner {
    readonly kind: "notice" | "warning" | "error";
    readonly title: string;
    readonly detail: string;
}

function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

function errorText(error: unknown): { title: string; detail: string } {
    if (error instanceof ApiError) {
        return { title: error.title, detail: error.detail ?? "" };
    }
    return { title: "Unexpected error", detail: error instanceof Error ? error.message : String(error) };
}

export class App {
    private readonly clock = new ServerClock();
    private zone: ZoneFormat = zoneFormat("UTC");
    private readonly utc: ZoneFormat = zoneFormat("UTC");
    private zoneProblem: string | null = null;

    private config: Config | null = null;
    private satellites: SatelliteSummary[] = [];
    private scope: Scope | null = null;
    private now: Now | null = null;
    private track: Track | null = null;
    private passes: Passes | null = null;
    /** The rise time of the pass the user chose, or null to follow the default. */
    private chosenRise: number | null = null;
    private readonly errors = new Map<Source, unknown>();
    private configLoadedAt: number | null = null;
    private bannerSignature = "";
    private timer: ReturnType<typeof setTimeout> | undefined;

    private readonly map: WorldMap;
    private readonly telemetry: Telemetry;
    private readonly passList: PassList;
    private readonly skyPlot: SkyPlot;
    private readonly select = byId("satellite", HTMLSelectElement);
    private readonly observerName = byId("observer-name", HTMLElement);
    private readonly localClock = byId("clock-local", HTMLElement);
    private readonly localZone = byId("clock-zone", HTMLElement);
    private readonly localDate = byId("clock-date", HTMLElement);
    private readonly utcClock = byId("clock-utc", HTMLElement);
    private readonly banners = byId("banners", HTMLElement);
    private readonly skyCaption = byId("sky-caption", HTMLElement);

    constructor() {
        this.map = new WorldMap(byId("map", HTMLElement));
        this.telemetry = new Telemetry(byId("telemetry", HTMLElement));
        this.passList = new PassList(byId("passes", HTMLElement), byId("passes-note", HTMLElement), (rise) => {
            this.chosenRise = rise;
            this.renderPasses();
        });
        this.skyPlot = new SkyPlot(byId("skyplot", HTMLElement));
        this.select.addEventListener("change", () => {
            const id = Number(this.select.value);
            if (Number.isInteger(id)) {
                this.choose(id);
            }
        });
        document.addEventListener("visibilitychange", () => {
            if (!document.hidden) {
                void this.pollNow();
            }
        });
    }

    async start(): Promise<void> {
        this.renderAll();
        this.schedule();
        this.config = await this.retry("config", async () => {
            const sent = Date.now();
            const config = await api.config();
            this.clock.update(config.serverTime, sent, Date.now());
            return config;
        });
        this.configLoadedAt = this.config.serverTime;
        try {
            this.zone = zoneFormat(this.config.observer.timeZone);
        } catch {
            this.zoneProblem = `This browser does not know the time zone ${this.config.observer.timeZone}, so times are shown in UTC.`;
        }
        setText(this.observerName, `Observer: ${this.config.observer.name}`);

        this.satellites = await this.retry("satellites", () => api.satellites());
        this.fillSelector();
        const requested = Number(new URLSearchParams(location.search).get("sat"));
        const configured = this.config.satellites[0];
        const known = (id: number | undefined): id is number => id !== undefined && this.satellites.some((s) => s.id === id);
        const first = known(requested) ? requested : known(configured) ? configured : (this.satellites[0]?.id ?? configured);
        if (first !== undefined) {
            this.choose(first);
        } else {
            this.renderAll();
        }
    }

    /** Runs a request until it succeeds, showing each failure. */
    private async retry<T>(source: Source, request: () => Promise<T>): Promise<T> {
        for (;;) {
            try {
                const value = await request();
                this.errors.delete(source);
                this.renderBanners();
                return value;
            } catch (error) {
                this.errors.set(source, error);
                this.renderBanners();
                await sleep(refresh.retry / 3);
            }
        }
    }

    private fillSelector(): void {
        const featured = this.satellites.filter((s) => s.featured);
        const others = this.satellites.filter((s) => !s.featured);
        const option = (s: SatelliteSummary): HTMLOptionElement => h("option", { value: String(s.id) }, `${s.name} (${s.id})`);
        const groups: HTMLElement[] = [];
        if (featured.length > 0) {
            groups.push(h("optgroup", { label: "Featured" }, ...featured.map(option)));
        }
        if (others.length > 0) {
            const names = [...new Set(others.map((s) => s.group))].join(", ");
            groups.push(h("optgroup", { label: `Also in ${names}` }, ...others.map(option)));
        }
        this.select.replaceChildren(...groups);
        this.select.disabled = this.satellites.length === 0;
    }

    /** Switches to a satellite: cancels everything in flight for the old one and starts afresh. */
    private choose(id: number): void {
        this.scope?.controller.abort();
        this.scope = { id, controller: new AbortController(), nowBusy: false, trackBusy: false, passesBusy: false, trackDue: 0, passesDue: 0 };
        this.now = null;
        this.track = null;
        this.passes = null;
        this.chosenRise = null;
        for (const source of ["now", "track", "passes"] as const) {
            this.errors.delete(source);
        }
        this.select.value = String(id);
        const name = this.satellites.find((s) => s.id === id)?.name ?? `NORAD ${id}`;
        document.title = `${name} · Sky Over Phoenix`;
        const url = new URL(location.href);
        url.searchParams.set("sat", String(id));
        history.replaceState(null, "", url);
        this.renderAll();
        void this.pollNow();
        void this.loadTrack();
        void this.loadPasses();
    }

    /** Ticks just after each whole second of the server's clock, so the displayed seconds step evenly. */
    private schedule(): void {
        clearTimeout(this.timer);
        const ms = this.clock.now();
        const delay = 1000 - (((ms % 1000) + 1000) % 1000) + 15;
        this.timer = setTimeout(() => {
            this.tick();
            this.schedule();
        }, delay);
    }

    private tick(): void {
        this.renderClock();
        this.renderCountdowns();
        this.renderPasses();
        void this.pollNow();
        const scope = this.scope;
        if (scope !== null) {
            if (Date.now() >= scope.trackDue) {
                void this.loadTrack();
            }
            if (Date.now() >= scope.passesDue) {
                void this.loadPasses();
            }
        }
    }

    /** One /now request, skipped while the previous one is still in flight or the tab is hidden. */
    private async pollNow(): Promise<void> {
        const scope = this.scope;
        if (scope === null || scope.nowBusy || document.hidden) {
            return;
        }
        scope.nowBusy = true;
        const sent = Date.now();
        try {
            const now = await api.now(scope.id, scope.controller.signal);
            const received = Date.now();
            if (scope !== this.scope || now.satellite.id !== scope.id) {
                return;
            }
            this.clock.update(now.t, sent, received);
            this.now = now;
            this.errors.delete("now");
            this.renderNow();
        } catch (error) {
            if (isAbort(error) || scope !== this.scope) {
                return;
            }
            this.errors.set("now", error);
        } finally {
            scope.nowBusy = false;
            if (scope === this.scope) {
                this.renderBanners();
            }
        }
    }

    private async loadTrack(): Promise<void> {
        const scope = this.scope;
        if (scope === null || scope.trackBusy) {
            return;
        }
        scope.trackBusy = true;
        scope.trackDue = Date.now() + refresh.track;
        try {
            const track = await api.track(scope.id, scope.controller.signal);
            if (scope !== this.scope) {
                return;
            }
            this.track = track;
            this.errors.delete("track");
            this.renderMap();
        } catch (error) {
            if (isAbort(error) || scope !== this.scope) {
                return;
            }
            scope.trackDue = Date.now() + refresh.retry;
            this.errors.set("track", error);
        } finally {
            scope.trackBusy = false;
            if (scope === this.scope) {
                this.renderBanners();
            }
        }
    }

    private async loadPasses(): Promise<void> {
        const scope = this.scope;
        if (scope === null || scope.passesBusy) {
            return;
        }
        scope.passesBusy = true;
        scope.passesDue = Date.now() + refresh.passes;
        try {
            const passes = await api.passes(scope.id, passDays, scope.controller.signal);
            if (scope !== this.scope) {
                return;
            }
            this.passes = passes;
            this.errors.delete("passes");
            this.renderPasses();
            this.renderCountdowns();
        } catch (error) {
            if (isAbort(error) || scope !== this.scope) {
                return;
            }
            scope.passesDue = Date.now() + refresh.retry;
            this.errors.set("passes", error);
            this.renderPasses();
        } finally {
            scope.passesBusy = false;
            if (scope === this.scope) {
                this.renderBanners();
            }
        }
    }

    private renderAll(): void {
        this.renderClock();
        this.renderNow();
        this.renderPasses();
        this.renderCountdowns();
        this.renderBanners();
    }

    private renderNow(): void {
        this.telemetry.update(this.now, this.zone);
        this.renderMap();
        this.renderSkyPlot();
        this.renderCountdowns();
    }

    private renderMap(): void {
        const observer = this.config?.observer ?? null;
        this.map.update({ observer, now: this.now, track: this.track?.points ?? null });
    }

    private renderClock(): void {
        if (!this.clock.isSynced) {
            return;
        }
        const ms = this.clock.now();
        setText(this.localClock, this.zone.clock(ms));
        setText(this.localZone, this.zone.offsetLabel(ms));
        setText(this.localDate, this.zone.date(ms));
        setText(this.utcClock, this.utc.clock(ms));
    }

    private renderCountdowns(): void {
        if (!this.clock.isSynced) {
            return;
        }
        this.telemetry.tick({
            nowMs: this.clock.now(),
            zone: this.zone,
            currentPass: this.now?.currentPass ?? null,
            passes: this.passes,
            minimumElevationDeg: this.config?.minimumElevationDeg ?? 0,
        });
    }

    private currentSelection(nowMs: number): ReturnType<typeof selectedPass> {
        const listed = this.passes?.passes ?? [];
        const selected = selectedPass(listed.filter((p) => passStatus(p, nowMs) !== "ended"), this.chosenRise, nowMs);
        // Without a list (not loaded, or failed), show the pass /now says is in progress.
        return selected ?? (this.now?.currentPass !== null && this.now?.currentPass !== undefined && passStatus(this.now.currentPass, nowMs) === "inProgress" ? this.now.currentPass : undefined);
    }

    private renderPasses(): void {
        const nowMs = this.clock.now();
        this.passList.render({
            passes: this.passes,
            nowMs,
            selected: this.currentSelection(nowMs),
            zone: this.zone,
            minimumElevationDeg: this.config?.minimumElevationDeg ?? 0,
            error: this.errors.has("passes"),
        });
        this.renderSkyPlot();
    }

    private renderSkyPlot(): void {
        const nowMs = this.clock.now();
        const pass = this.currentSelection(nowMs) ?? null;
        const inProgress = pass !== null && this.now !== null && passStatus(pass, this.now.t) === "inProgress";
        const caption = this.skyPlot.render({
            pass,
            now: inProgress && this.now !== null ? this.now.look : null,
            minimumElevationDeg: this.config?.minimumElevationDeg ?? 0,
            zone: this.zone,
        });
        setText(this.skyCaption, caption);
    }

    private renderBanners(): void {
        const banners: Banner[] = [];
        const config = this.config;
        if (config?.offline === true) {
            banners.push({ kind: "notice", title: "Offline", detail: "Showing cached orbital data; the API does not contact CelesTrak." });
        }
        if (config?.clockSimulated === true) {
            const start = config.clockStart ?? this.configLoadedAt;
            banners.push({
                kind: "notice",
                title: "Simulated clock",
                detail: start === null
                    ? "The server's clock was started at a fixed instant and runs at real speed; every time here follows it."
                    : `The server's clock started at ${this.utc.isoDate(start)} ${this.utc.clock(start)} UTC and runs at real speed, ` +
                      "to match the recorded data; every time here follows it.",
            });
        }
        if (this.zoneProblem !== null) {
            banners.push({ kind: "warning", title: "Time zone", detail: this.zoneProblem });
        }
        // Every endpoint repeats the element-set and cache warnings, each with figures from its own
        // moment, so the same warning would show two or three times with different numbers. /now's are
        // the freshest; from the track, only what /now cannot say (where SGP4 stopped the track).
        const shared = this.now !== null ? this.now.warnings : (this.passes?.warnings ?? []);
        const trackOnly = (this.track?.warnings ?? []).filter((w) => w.includes("the track stops there"));
        const warnings = new Set([...shared, ...trackOnly]);
        for (const warning of warnings) {
            banners.push({ kind: "warning", title: "Warning", detail: warning });
        }
        const seen = new Set<string>();
        for (const [source, error] of this.errors) {
            const { title, detail } = errorText(error);
            const key = `${title}|${detail}`;
            if (!seen.has(key)) {
                seen.add(key);
                banners.push({ kind: "error", title, detail: detail === "" ? `The ${source} request failed.` : detail });
            }
        }

        const signature = JSON.stringify(banners);
        if (signature === this.bannerSignature) {
            return;
        }
        this.bannerSignature = signature;
        this.banners.replaceChildren(
            ...banners.map((b) => h("div", { class: `banner banner-${b.kind}` }, h("strong", {}, `${b.title}. `), b.detail)),
        );
    }
}
