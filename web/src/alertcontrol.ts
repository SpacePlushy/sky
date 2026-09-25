// The pass-alert control: the on/off button, lead time, and "visible passes only" choice. What to
// alert for and when is decided in alerts.ts from the server's clock, and notifier.ts shows what is
// due, once across tabs; this module asks for permission, stores the choices, and shows the state.
//
// Notifications come from this page, so they work only while a dashboard tab is open; the control
// says so. In a background tab the browser may run the page's timers only once a minute, so an
// alert can come up to a minute late there.
//
// The choices are shared by every open tab through localStorage: a change in one tab reaches the
// others through the "storage" event, and each check reads them again before anything is shown.

import { isLeadMinutes, parsePreferences, planAlerts, type AlertEvent, type AlertPreferences } from "./alerts";
import { byId, setText } from "./dom";
import type { Pass } from "./model";
import { AlertNotifier, type AlertContext, type Locks } from "./notifier";
import { readStored, writeStored } from "./storage";
import { roundToSecond, type ZoneFormat } from "./time";

const preferencesKey = "sky.alerts.preferences";

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

/** "unusable": the browser has a Notification constructor that always throws (Chrome for Android). */
type Permission = NotificationPermission | "unsupported" | "unusable";

/** The Notification API, when this page can use it (a secure context such as localhost or HTTPS). */
function notificationApi(): typeof Notification | null {
    try {
        return "Notification" in window && window.isSecureContext ? window.Notification : null;
    } catch {
        return null;
    }
}

/** The Web Locks API, when the browser has it (it too needs a secure context). */
function lockManager(): Locks | null {
    try {
        return "locks" in navigator ? navigator.locks : null;
    } catch {
        return null;
    }
}

function samePreferences(a: AlertPreferences, b: AlertPreferences): boolean {
    return a.enabled === b.enabled && a.leadMinutes === b.leadMinutes && a.visibleOnly === b.visibleOnly;
}

export class AlertControl {
    private prefs: AlertPreferences;
    /**
     * Whether the choices in effect are the stored ones. False after a write failed: this visit's
     * choices then stand, and reading storage again would undo them.
     */
    private prefsStored = true;
    private readonly notifier = new AlertNotifier({
        read: readStored,
        write: writeStored,
        notification: notificationApi,
        locks: lockManager,
        focus: () => {
            window.focus();
        },
    });
    private view: AlertView | null = null;
    private requesting = false;

    private readonly toggle = byId("alerts-toggle", HTMLButtonElement);
    private readonly state = byId("alerts-state", HTMLElement);
    private readonly lead = byId("alerts-lead", HTMLSelectElement);
    private readonly visibleOnly = byId("alerts-visible-only", HTMLInputElement);
    private readonly status = byId("alerts-status", HTMLElement);

    constructor() {
        this.prefs = parsePreferences(readStored(preferencesKey));
        this.showPreferences();
        this.toggle.addEventListener("click", () => {
            void this.toggleAlerts();
        });
        // Another tab changed the choices (key null: it cleared storage).
        window.addEventListener("storage", (event) => {
            if ((event.key === null || event.key === preferencesKey) && this.syncPreferences()) {
                this.render();
            }
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

    private permission(): Permission {
        const api = notificationApi();
        if (api === null) {
            return "unsupported";
        }
        if (this.notifier.cannotShow) {
            return "unusable";
        }
        try {
            return api.permission;
        } catch {
            return "unsupported";
        }
    }

    private get active(): boolean {
        return this.prefs.enabled && this.permission() === "granted";
    }

    private plan(view: ReadyView): AlertEvent[] {
        return planAlerts(view.satellite.id, view.passes, this.prefs, view.nowMs);
    }

    /** Puts the choices in the controls, touching only what differs. */
    private showPreferences(): void {
        const lead = String(this.prefs.leadMinutes);
        if (this.lead.value !== lead) {
            this.lead.value = lead;
        }
        if (this.visibleOnly.checked !== this.prefs.visibleOnly) {
            this.visibleOnly.checked = this.prefs.visibleOnly;
        }
    }

    /** Reads the stored choices again, which another tab may have changed. True when they changed here. */
    private syncPreferences(): boolean {
        if (!this.prefsStored) {
            return false;
        }
        const stored = parsePreferences(readStored(preferencesKey));
        if (samePreferences(stored, this.prefs)) {
            return false;
        }
        this.prefs = stored;
        this.showPreferences();
        return true;
    }

    /** What to alert for now, with the choices read afresh; null when alerts are off or not ready. */
    private context(): AlertContext | null {
        this.syncPreferences();
        const view = ready(this.view);
        if (!this.active || view === null) {
            return null;
        }
        return { plan: this.plan(view), nowMs: view.nowMs, satelliteName: view.satellite.name, zone: view.zone };
    }

    private check(): void {
        void this.notifier.check(() => this.context()).then(() => {
            this.render();
        });
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
        if (api === null || this.notifier.cannotShow) {
            this.render();
            return;
        }
        let permission = this.permission();
        if (permission !== "granted") {
            this.requesting = true;
            this.render();
            try {
                permission = await api.requestPermission();
            } catch {
                permission = this.permission();
            } finally {
                this.requesting = false;
            }
        }
        this.save({ ...this.prefs, enabled: permission === "granted" });
    }

    private save(prefs: AlertPreferences): void {
        this.prefs = prefs;
        this.prefsStored = writeStored(preferencesKey, JSON.stringify(prefs));
        this.check();
        this.render();
    }

    private render(): void {
        const permission = this.permission();
        const on = this.active;
        const unavailable = permission === "unsupported" || permission === "unusable";
        const state = unavailable ? "Unavailable" : permission === "denied" ? "Blocked" : on ? "On" : "Off";
        setText(this.state, state);
        if (this.state.dataset.state !== state.toLowerCase()) {
            this.state.dataset.state = state.toLowerCase();
        }
        setText(this.toggle, on ? "Turn off alerts" : "Turn on alerts");
        // Natively disabled only when alerts cannot work at all. While the browser asks for
        // permission the button stays focusable (a disabled one would drop keyboard focus to the
        // page) and is marked aria-disabled; toggleAlerts ignores presses meanwhile.
        if (this.toggle.disabled !== unavailable) {
            this.toggle.disabled = unavailable;
        }
        if (this.requesting && !unavailable) {
            this.toggle.setAttribute("aria-disabled", "true");
        } else {
            this.toggle.removeAttribute("aria-disabled");
        }
        setText(this.status, this.statusText(permission, on));
    }

    private statusText(permission: Permission, on: boolean): string {
        const what = this.prefs.visibleOnly ? "visible pass" : "pass";
        if (permission === "unsupported") {
            return "This browser cannot show notifications for this page (they need HTTPS or localhost). Use the calendar file instead.";
        }
        if (permission === "unusable") {
            return "This browser does not let a web page show notifications itself (Chrome on Android allows them only from a service worker). Use the calendar file instead.";
        }
        if (permission === "denied") {
            return "Notifications are blocked for this page. Allow them in the browser's site settings, then turn alerts on.";
        }
        if (this.requesting) {
            return "Waiting for the browser's permission…";
        }
        const failure = this.notifier.failure;
        if (failure !== null) {
            return failure;
        }
        if (!on) {
            return `A notification ${this.prefs.leadMinutes} min before each ${what}, once you turn alerts on.`;
        }
        const view = ready(this.view);
        if (view === null) {
            return "On. Waiting for the pass list.";
        }
        const next = this.notifier.next(this.plan(view), view.nowMs);
        if (next === undefined) {
            return `On. No ${what} left in the list to alert for.`;
        }
        const zone = view.zone;
        const alertAt = roundToSecond(next.alertMs);
        const verb = next.kind === "visible" ? "is visible" : "rises";
        return `On. Next alert ${zone.date(alertAt)} ${zone.clock(alertAt)}, ${this.prefs.leadMinutes} min before ${view.satellite.name} ${verb} at ${zone.clock(roundToSecond(next.startMs))}.`;
    }
}
