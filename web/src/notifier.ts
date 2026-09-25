// Showing due pass alerts as browser notifications, once across every open dashboard tab.
//
// Every tab works out the same plan each second (alerts.ts), so several tabs can find one alert
// due at the same moment. Showing is therefore serialized across tabs with the Web Locks API: while
// holding one exclusive lock, a tab re-reads the shown-alert log from storage, merges it into its
// own, works out again what is due, logs that, writes the log back, and only then shows it. The
// next tab to get the lock reads the log and finds nothing left to show. A cheap check comes first
// (the tab's log with storage's merged in, no writing), so the lock is requested only when
// something looks due. A browser without Web Locks takes the same steps without the lock.
//
// The log is pruned at each check and again after each merge. When that forgets an entry (a
// restarted simulated clock left one further ahead than any alert can be), the log is written
// back under the lock too, so the stale entry cannot come back from storage later, when the
// replayed pass is close enough for it to look genuine.
//
// Chrome for Android has a Notification constructor that always throws a TypeError ("Illegal
// constructor": pages there may notify only through a service worker). The first TypeError marks
// the constructor unusable for the rest of the page's life, and that alert is taken back out of
// the log, since it was never shown.
//
// Nothing here touches the DOM or browser globals: storage, the Notification constructor, and the
// lock manager are handed in, so the unit tests drive it with stubs.

import { alertMessage, alertTag, dueAlerts, nextAlert, NotifiedLog, type AlertEvent } from "./alerts";
import type { ZoneFormat } from "./time";

/** The localStorage key of the shown-alert log, shared by every tab. */
export const notifiedKey = "sky.alerts.notified";
/** The Web Locks name that serializes showing across tabs. */
export const lockName = "sky.alerts.show";

/** What the page does with a notification it created. */
export interface ShownNotification {
    onclick: ((event: Event) => unknown) | null;
    close(): void;
}

export type NotificationConstructor = new (title: string, options?: NotificationOptions) => ShownNotification;

/** The part of the Web Locks API (navigator.locks) used here. */
export interface Locks {
    request(name: string, options: { mode: "exclusive" }, callback: () => void): Promise<unknown>;
}

export interface NotifierEnvironment {
    /** localStorage access that never throws (storage.ts). */
    readonly read: (key: string) => string | null;
    readonly write: (key: string, value: string) => boolean;
    /** The Notification constructor, when the page may use it. */
    readonly notification: () => NotificationConstructor | null;
    /** navigator.locks, or null when the browser has no Web Locks API. */
    readonly locks: () => Locks | null;
    /** Brings the dashboard's window forward, when a notification is clicked. */
    readonly focus: () => void;
}

/** What to alert for now: the selected satellite's plan, on the server's clock. */
export interface AlertContext {
    readonly plan: readonly AlertEvent[];
    readonly nowMs: number;
    readonly satelliteName: string;
    readonly zone: ZoneFormat;
}

export class AlertNotifier {
    private readonly env: NotifierEnvironment;
    private log: NotifiedLog;
    private unusable = false;
    private failureText: string | null = null;
    /** A lock request is queued; it reads the latest context when granted, so none is added. */
    private waiting = false;

    constructor(env: NotifierEnvironment) {
        this.env = env;
        this.log = NotifiedLog.parse(env.read(notifiedKey));
    }

    /** Whether the browser refused to construct a notification (Chrome for Android). Never resets. */
    get cannotShow(): boolean {
        return this.unusable;
    }

    /** Why the last notification could not be shown, or null after one was. */
    get failure(): string | null {
        return this.failureText;
    }

    /** The next alert still to come after `nowMs` that has not been shown. */
    next(plan: readonly AlertEvent[], nowMs: number): AlertEvent | undefined {
        return nextAlert(plan, nowMs, this.log);
    }

    /**
     * Shows what is due, once across tabs. `current` gives the context, or null when alerts are off;
     * it is called again once the lock is granted, so a late grant still uses the latest plan and
     * options. Settles when the showing is done.
     */
    async check(current: () => AlertContext | null): Promise<void> {
        const context = current();
        if (context === null || this.unusable) {
            return;
        }
        // What other tabs have shown, and what the clock rules out; nothing is written here.
        this.log = NotifiedLog.merge(this.log, NotifiedLog.parse(this.env.read(notifiedKey)));
        const forgot = this.log.prune(context.nowMs);
        if (!forgot && dueAlerts(context.plan, context.nowMs, this.log).length === 0) {
            return;
        }
        const locks = this.env.locks();
        if (locks === null) {
            this.deliver(current);
            return;
        }
        if (this.waiting) {
            return;
        }
        this.waiting = true;
        const lock = { granted: false };
        try {
            await locks.request(lockName, { mode: "exclusive" }, () => {
                lock.granted = true;
                this.deliver(current);
            });
        } catch (error) {
            if (lock.granted) {
                throw error;
            }
            // The lock was refused (the document is no longer fully active, say): better to show
            // without it than not at all.
            this.deliver(current);
        } finally {
            this.waiting = false;
        }
    }

    /** Merges the stored log, stores it pruned with what is still due added, then shows that. */
    private deliver(current: () => AlertContext | null): void {
        const context = current();
        if (context === null || this.unusable) {
            return;
        }
        // Another tab may have shown some already: fold in what storage remembers.
        this.log = NotifiedLog.merge(this.log, NotifiedLog.parse(this.env.read(notifiedKey)));
        this.log.prune(context.nowMs);
        const due = dueAlerts(context.plan, context.nowMs, this.log);
        // Logged and stored before they are shown, so the next tab to get the lock finds them, and
        // a browser that refuses one is not asked again every second.
        for (const event of due) {
            this.log.add(event);
        }
        this.save();
        let taken = false;
        for (const event of due) {
            if (!this.show(event, context)) {
                this.log.remove(event);
                taken = true;
            }
        }
        if (taken) {
            this.save();
        }
    }

    private save(): void {
        this.env.write(notifiedKey, JSON.stringify(this.log));
    }

    /** Shows one alert. False when the constructor is unusable, so the alert was never shown. */
    private show(event: AlertEvent, context: AlertContext): boolean {
        if (this.unusable) {
            return false;
        }
        const api = this.env.notification();
        if (api === null) {
            return true;
        }
        const { title, body } = alertMessage(event, context.satelliteName, context.nowMs, context.zone);
        try {
            const notification = new api(title, { body, tag: alertTag(event.satelliteId, event.startMs) });
            notification.onclick = () => {
                this.env.focus();
                notification.close();
            };
            this.failureText = null;
            return true;
        } catch (error) {
            if (error instanceof TypeError) {
                this.unusable = true;
                return false;
            }
            this.failureText = `The browser would not show a notification (${error instanceof Error ? error.message : String(error)}). Use the calendar file instead.`;
            return true;
        }
    }
}
