// The pass-alert control: the on/off button, lead time, and "visible passes only" choice, and the
// browser notifications themselves. What to alert for and when is decided in alerts.ts from the
// server's clock; this module only asks for permission, stores the choices, and shows what is due.
//
// Notifications come from this page, so they work only while a dashboard tab is open; the control
// says so. In a background tab the browser may run the page's timers only once a minute, so an
// alert can come up to a minute late there.

import { alertMessage, dueAlerts, isLeadMinutes, nextAlert, NotifiedLog, parsePreferences, planAlerts, type AlertEvent, type AlertPreferences } from "./alerts";
import { byId, setText } from "./dom";
import type { Pass } from "./model";
import { readStored, writeStored } from "./storage";
import { roundToSecond, type ZoneFormat } from "./time";

const preferencesKey = "sky.alerts.preferences";
const notifiedKey = "sky.alerts.notified";

export interface AlertView {
    readonly satellite: { readonly id: number; readonly name: string } | null;
    readonly passes: readonly Pass[] | null;
    /** The server's time, or null before the page has synced to it. Never the browser's clock. */
    readonly nowMs: number | null;
    readonly zone: ZoneFormat;
}

/** A view with everything needed to plan alerts. */
interface ReadyView {
    readonly satellite: { readonly id: number; readonly name: string };
    readonly passes: readonly Pass[];
    readonly nowMs: number;
    readonly zone: ZoneFormat;
}

function ready(view: AlertView | null): ReadyView | null {
    const satellite = view?.satellite ?? null;
    const passes = view?.passes ?? null;
    const nowMs = view?.nowMs ?? null;
    return view === null || satellite === null || passes === null || nowMs === null ? null : { satellite, passes, nowMs, zone: view.zone };
}

type Permission = NotificationPermission | "unsupported";

/** The Notification API, when this page can use it (a secure context such as localhost or HTTPS). */
function notificationApi(): typeof Notification | null {
    try {
        return "Notification" in window && window.isSecureContext ? window.Notification : null;
    } catch {
        return null;
    }
}

function currentPermission(): Permission {
    const api = notificationApi();
    if (api === null) {
        return "unsupported";
    }
    try {
        return api.permission;
    } catch {
        return "unsupported";
    }
}

export class AlertControl {
    private prefs: AlertPreferences;
    private log: NotifiedLog;
    private view: AlertView | null = null;
    private requesting = false;
    private failure: string | null = null;

    private readonly toggle = byId("alerts-toggle", HTMLButtonElement);
    private readonly state = byId("alerts-state", HTMLElement);
    private readonly lead = byId("alerts-lead", HTMLSelectElement);
    private readonly visibleOnly = byId("alerts-visible-only", HTMLInputElement);
    private readonly status = byId("alerts-status", HTMLElement);

    constructor() {
        this.prefs = parsePreferences(readStored(preferencesKey));
        this.log = NotifiedLog.parse(readStored(notifiedKey));
        this.lead.value = String(this.prefs.leadMinutes);
        this.visibleOnly.checked = this.prefs.visibleOnly;
        this.toggle.addEventListener("click", () => {
            void this.toggleAlerts();
        });
        this.lead.addEventListener("change", () => {
            const minutes = Number(this.lead.value);
            if (isLeadMinutes(minutes)) {
                this.save({ ...this.prefs, leadMinutes: minutes });
            }
        });
        this.visibleOnly.addEventListener("change", () => {
            this.save({ ...this.prefs, visibleOnly: this.visibleOnly.checked });
        });
        this.render();
    }

    /**
     * Shows the alerts that are due and refreshes the status. The app calls it every second and
     * whenever the passes or the satellite change; the plan is worked out afresh each time, so it
     * always follows the latest pass list and options.
     */
    update(view: AlertView): void {
        this.view = view;
        this.check();
        this.render();
    }

    private get active(): boolean {
        return this.prefs.enabled && currentPermission() === "granted";
    }

    private plan(view: ReadyView): AlertEvent[] {
        return planAlerts(view.satellite.id, view.passes, this.prefs, view.nowMs);
    }

    private check(): void {
        const view = ready(this.view);
        if (!this.active || view === null) {
            return;
        }
        const plan = this.plan(view);
        if (dueAlerts(plan, view.nowMs, this.log).length === 0) {
            return;
        }
        // Another dashboard tab may have shown it already: fold in what storage remembers first.
        this.log = NotifiedLog.merge(this.log, NotifiedLog.parse(readStored(notifiedKey)));
        for (const event of dueAlerts(plan, view.nowMs, this.log)) {
            // Logged before it is shown, so a browser that refuses is not asked again every second.
            this.log.add(event);
            this.show(event, view.satellite.name, view.nowMs, view.zone);
        }
        this.log.prune(view.nowMs);
        writeStored(notifiedKey, JSON.stringify(this.log));
    }

    private show(event: AlertEvent, satelliteName: string, nowMs: number, zone: ZoneFormat): void {
        const api = notificationApi();
        if (api === null) {
            return;
        }
        const { title, body } = alertMessage(event, satelliteName, nowMs, zone);
        try {
            // The tag makes a second copy (from another tab) replace the first instead of stacking.
            const notification = new api(title, { body, tag: event.key });
            notification.onclick = () => {
                window.focus();
                notification.close();
            };
            this.failure = null;
        } catch (error) {
            this.failure = `The browser would not show a notification (${error instanceof Error ? error.message : String(error)}). The calendar file works instead.`;
        }
    }

    private async toggleAlerts(): Promise<void> {
        if (this.requesting) {
            return;
        }
        if (this.active) {
            this.save({ ...this.prefs, enabled: false });
            return;
        }
        const api = notificationApi();
        if (api === null) {
            this.render();
            return;
        }
        let permission = currentPermission();
        if (permission !== "granted") {
            this.requesting = true;
            this.render();
            try {
                permission = await api.requestPermission();
            } catch {
                permission = currentPermission();
            } finally {
                this.requesting = false;
            }
        }
        this.save({ ...this.prefs, enabled: permission === "granted" });
    }

    private save(prefs: AlertPreferences): void {
        this.prefs = prefs;
        writeStored(preferencesKey, JSON.stringify(prefs));
        this.check();
        this.render();
    }

    private render(): void {
        const permission = currentPermission();
        const on = this.active;
        const state = permission === "unsupported" ? "Unavailable" : permission === "denied" ? "Blocked" : on ? "On" : "Off";
        setText(this.state, state);
        if (this.state.dataset.state !== state.toLowerCase()) {
            this.state.dataset.state = state.toLowerCase();
        }
        setText(this.toggle, on ? "Turn off alerts" : "Turn on alerts");
        this.toggle.disabled = permission === "unsupported" || this.requesting;
        setText(this.status, this.statusText(permission, on));
    }

    private statusText(permission: Permission, on: boolean): string {
        const what = this.prefs.visibleOnly ? "visible pass" : "pass";
        if (permission === "unsupported") {
            return "This browser cannot show notifications for this page (they need HTTPS or localhost). The calendar file works instead.";
        }
        if (permission === "denied") {
            return "Notifications are blocked for this page. Allow them in the browser's site settings, then turn alerts on.";
        }
        if (this.requesting) {
            return "Waiting for the browser's permission…";
        }
        if (this.failure !== null) {
            return this.failure;
        }
        if (!on) {
            return `A notification ${this.prefs.leadMinutes} min before each ${what}, once you turn alerts on.`;
        }
        const view = ready(this.view);
        if (view === null) {
            return "On. Waiting for the pass list.";
        }
        const next = nextAlert(this.plan(view), view.nowMs, this.log);
        if (next === undefined) {
            return `On. No ${what} left in the list to alert for.`;
        }
        const zone = view.zone;
        const alertAt = roundToSecond(next.alertMs);
        const verb = next.kind === "visible" ? "is visible" : "rises";
        return `On. Next alert ${zone.date(alertAt)} ${zone.clock(alertAt)}, ${this.prefs.leadMinutes} min before ${view.satellite.name} ${verb} at ${zone.clock(roundToSecond(next.startMs))}.`;
    }
}
