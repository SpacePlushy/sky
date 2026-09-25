import { describe, expect, it } from "vitest";
import { alertEvent, NotifiedLog, planAlerts, type AlertEvent, type AlertOptions } from "./alerts";
import type { Pass, PassEvent, VisiblePart } from "./model";
import { AlertNotifier, lockName, notifiedKey, type AlertContext, type Locks, type NotificationConstructor, type ShownNotification } from "./notifier";
import { ZoneFormat } from "./time";

const second = 1000;
const minute = 60 * second;
const hour = 60 * minute;
// The first evening visible ISS pass in the demo data starts at 03:07:26 UTC on 25 September
// (2026-09-25 is 20,721 days after 1970-01-01).
const firstVisible = 20_721 * 86_400_000 + 3 * hour + 7 * minute + 26 * second;
const iss = 25544;
const tenVisible: AlertOptions = { leadMinutes: 10, visibleOnly: true };
const phoenix = new ZoneFormat("America/Phoenix");

function at(t: number, azimuthDeg = 315, elevationDeg = 10): PassEvent {
    return { t, azimuthDeg, elevationDeg };
}

/** A pass rising 2 minutes before `start`, visible from `start` for 3 minutes. */
function brightPass(start: number): Pass {
    const part: VisiblePart = { start: at(start), startsBecause: "rise", highest: at(start + 90 * second, 30, 62), end: at(start + 3 * minute), endsBecause: "entersShadow" };
    return { rise: at(start - 2 * minute, 200), culmination: at(start + minute, 250, 62), set: at(start + 4 * minute, 300), peakUncertaintyDeg: 0, visible: [part], path: [] };
}

function context(nowMs: number, start = firstVisible): AlertContext {
    return { plan: planAlerts(iss, [brightPass(start)], tenVisible, nowMs), nowMs, satelliteName: "ISS (ZARYA)", zone: phoenix };
}

function eventAt(start: number): AlertEvent {
    const event = alertEvent(iss, brightPass(start), tenVisible);
    if (event === null) {
        throw new Error("expected an event");
    }
    return event;
}

/** One origin's localStorage, shared by its tabs. */
class Storage {
    readonly items = new Map<string, string>();
    readonly read = (key: string): string | null => this.items.get(key) ?? null;
    readonly write = (key: string, value: string): boolean => {
        this.items.set(key, value);
        return true;
    };
    log(): NotifiedLog {
        return NotifiedLog.parse(this.read(notifiedKey));
    }
}

/** Web Locks for one origin: exclusive locks granted one at a time, in the order requested. */
class FakeLocks implements Locks {
    held = false;
    requests = 0;
    private queue: Promise<void> = Promise.resolve();

    request(name: string, options: { mode: "exclusive" }, callback: () => void): Promise<unknown> {
        expect(name).toBe(lockName);
        expect(options.mode).toBe("exclusive");
        this.requests += 1;
        const granted = this.queue.then(() => {
            this.held = true;
            try {
                callback();
            } finally {
                this.held = false;
            }
        });
        this.queue = granted.catch(() => undefined);
        return granted;
    }
}

interface Shown {
    readonly title: string;
    readonly body: string;
    readonly tag: string;
    /** Whether a tab held the lock when the notification was made. */
    readonly locked: boolean;
    /** The stored log at that moment. */
    readonly stored: NotifiedLog;
}

/** A Notification constructor that records what it is asked to show, and what was true at that moment. */
function recorder(storage: Storage, locks: FakeLocks | null): { Recording: NotificationConstructor; shown: Shown[]; clicked: string[] } {
    const shown: Shown[] = [];
    const clicked: string[] = [];
    class Recording implements ShownNotification {
        onclick: ((event: Event) => unknown) | null = null;
        constructor(title: string, options?: NotificationOptions) {
            shown.push({ title, body: options?.body ?? "", tag: options?.tag ?? "", locked: locks?.held ?? false, stored: storage.log() });
        }
        close(): void {
            clicked.push("close");
        }
    }
    return { Recording, shown, clicked };
}

function tab(storage: Storage, locks: FakeLocks | null, notification: NotificationConstructor | null, focus: () => void = () => undefined): AlertNotifier {
    return new AlertNotifier({ read: storage.read, write: storage.write, notification: () => notification, locks: () => locks, focus });
}

describe("showing across tabs", () => {
    it("shows an alert due in two tabs at once only once, from inside the lock and after storing it", async () => {
        const storage = new Storage();
        const locks = new FakeLocks();
        const { Recording, shown } = recorder(storage, locks);
        const a = tab(storage, locks, Recording);
        const b = tab(storage, locks, Recording);
        const now = firstVisible - 5 * minute;
        // The two tabs' lists put the start 1 ms apart, as two searches can.
        await Promise.all([a.check(() => context(now)), b.check(() => context(now, firstVisible + 1))]);

        expect(shown).toHaveLength(1);
        expect(shown[0]?.title).toBe("ISS (ZARYA) visible in 5 min");
        expect(shown[0]?.tag).toBe("25544@2026-09-25T03:07Z");
        expect(shown[0]?.locked).toBe(true);
        // Stored before it was shown, so the tab that gets the lock next finds it.
        expect(shown[0]?.stored.has(eventAt(firstVisible))).toBe(true);
        expect(locks.requests).toBe(2);
    });

    it("asks for the lock only when something looks due, and once while a request waits", async () => {
        const storage = new Storage();
        const locks = new FakeLocks();
        const { Recording, shown } = recorder(storage, locks);
        const a = tab(storage, locks, Recording);

        await a.check(() => context(firstVisible - 20 * minute));
        await a.check(() => null);
        expect(locks.requests).toBe(0);

        const now = firstVisible - 9 * minute;
        await Promise.all([a.check(() => context(now)), a.check(() => context(now)), a.check(() => context(now))]);
        expect(locks.requests).toBe(1);
        expect(shown).toHaveLength(1);

        // Shown: nothing is due any more, so no lock.
        await a.check(() => context(now + second));
        expect(locks.requests).toBe(1);
    });

    it("reads the context again once the lock is granted, so alerts turned off meanwhile show nothing", async () => {
        const storage = new Storage();
        const locks = new FakeLocks();
        const { Recording, shown } = recorder(storage, locks);
        const a = tab(storage, locks, Recording);
        let on = true;
        const check = a.check(() => (on ? context(firstVisible - 5 * minute) : null));
        on = false;
        await check;
        expect(locks.requests).toBe(1);
        expect(shown).toEqual([]);
    });

    it("works without the Web Locks API, still storing before showing", async () => {
        const storage = new Storage();
        const { Recording, shown } = recorder(storage, null);
        const a = tab(storage, null, Recording);
        const b = tab(storage, null, Recording);
        const now = firstVisible - 5 * minute;
        await a.check(() => context(now));
        await b.check(() => context(now));
        expect(shown).toHaveLength(1);
        expect(shown[0]?.stored.has(eventAt(firstVisible))).toBe(true);
    });

    it("shows without the lock when the browser refuses it", async () => {
        const storage = new Storage();
        const refusing: Locks = { request: () => Promise.reject(new Error("InvalidStateError")) };
        const { Recording, shown } = recorder(storage, null);
        const a = new AlertNotifier({ read: storage.read, write: storage.write, notification: () => Recording, locks: () => refusing, focus: () => undefined });
        await a.check(() => context(firstVisible - 5 * minute));
        expect(shown).toHaveLength(1);
    });

    it("gives tabs that each show the pass (storage not yet shared) one tag, though their starts are 1 ms apart", async () => {
        const { Recording, shown } = recorder(new Storage(), null);
        const now = firstVisible - 5 * minute;
        await tab(new Storage(), null, Recording).check(() => context(now));
        await tab(new Storage(), null, Recording).check(() => context(now, firstVisible + 1));
        expect(shown).toHaveLength(2);
        expect(shown[1]?.tag).toBe(shown[0]?.tag);
    });

    it("brings the dashboard forward and closes the notification when it is clicked", async () => {
        const storage = new Storage();
        const made: Kept[] = [];
        class Kept implements ShownNotification {
            onclick: ((event: Event) => unknown) | null = null;
            closed = false;
            constructor() {
                made.push(this);
            }
            close(): void {
                this.closed = true;
            }
        }
        let focused = 0;
        await tab(storage, null, Kept, () => {
            focused += 1;
        }).check(() => context(firstVisible - 5 * minute));
        expect(made).toHaveLength(1);
        made[0]?.onclick?.(new Event("click"));
        expect(focused).toBe(1);
        expect(made[0]?.closed).toBe(true);
    });
});

describe("a browser that cannot construct notifications", () => {
    // Chrome for Android: Notification and its permission exist, but pages may notify only through
    // a service worker, so the constructor always throws.
    class Illegal implements ShownNotification {
        static made = 0;
        onclick: ((event: Event) => unknown) | null = null;
        constructor() {
            Illegal.made += 1;
            throw new TypeError("Failed to construct 'Notification': Illegal constructor. Use ServiceWorkerRegistration.showNotification() instead.");
        }
        close(): void {
            // Never made.
        }
    }

    it("stops trying, says it cannot show, and does not log the alert as shown", async () => {
        Illegal.made = 0;
        const storage = new Storage();
        const locks = new FakeLocks();
        const a = tab(storage, locks, Illegal);
        const now = firstVisible - 5 * minute;
        expect(a.cannotShow).toBe(false);
        await a.check(() => context(now));
        expect(Illegal.made).toBe(1);
        expect(a.cannotShow).toBe(true);
        expect(storage.log().has(eventAt(firstVisible))).toBe(false);
        expect(a.next(context(now - 10 * minute).plan, now - 10 * minute)?.startMs).toBe(firstVisible);

        // Sticky: never asked again.
        await a.check(() => context(now + second));
        expect(Illegal.made).toBe(1);
        expect(locks.requests).toBe(1);

        // A tab that can show it still does, since nothing claims it was shown.
        const { Recording, shown } = recorder(storage, locks);
        await tab(storage, locks, Recording).check(() => context(now + 2 * second));
        expect(shown).toHaveLength(1);
    });

    it("keeps an alert logged when the browser refuses it for another reason, so it is not asked every second", async () => {
        class Refused implements ShownNotification {
            onclick: ((event: Event) => unknown) | null = null;
            constructor() {
                throw new Error("Notifications are off for this site");
            }
            close(): void {
                // Never made.
            }
        }
        const storage = new Storage();
        const a = tab(storage, new FakeLocks(), Refused);
        await a.check(() => context(firstVisible - 5 * minute));
        expect(a.cannotShow).toBe(false);
        expect(a.failure).toBe("The browser would not show a notification (Notifications are off for this site). Use the calendar file instead.");
        expect(storage.log().has(eventAt(firstVisible))).toBe(true);
    });
});

describe("a simulated clock that restarts", () => {
    const restart = firstVisible - 17 * minute - 26 * second;

    it("alerts again for the pass it replays, though storage remembers it from the last run", async () => {
        const storage = new Storage();
        const locks = new FakeLocks();
        const { Recording, shown } = recorder(storage, locks);

        // The last run showed the alert at its alert time.
        await tab(storage, locks, Recording).check(() => context(firstVisible - 10 * minute));
        expect(shown).toHaveLength(1);

        // The server restarts 17 min 26 s before the start; the page loads the stored log. The
        // entry is forgotten in storage as well, under the lock: left there, it would look genuine
        // by the alert time (10 minutes ahead) and silence the replayed alert.
        const replay = tab(storage, locks, Recording);
        await replay.check(() => context(restart));
        expect(shown).toHaveLength(1);
        expect(locks.requests).toBe(2);
        expect(storage.log().size).toBe(0);
        await replay.check(() => context(firstVisible - 10 * minute));
        expect(shown).toHaveLength(2);
    });

    it("forgets it from storage even in a tab whose own log never had it", async () => {
        const storage = new Storage();
        const locks = new FakeLocks();
        const { Recording, shown } = recorder(storage, locks);
        // Open through both runs, but another tab showed the alert in the first.
        const open = tab(storage, locks, Recording);
        await tab(storage, locks, Recording).check(() => context(firstVisible - 10 * minute));
        expect(shown).toHaveLength(1);

        await open.check(() => context(restart));
        expect(storage.log().size).toBe(0);
        await open.check(() => context(firstVisible - 10 * minute));
        expect(shown).toHaveLength(2);
    });

    it("does not alert again after a reload on a clock that runs on", async () => {
        const storage = new Storage();
        const locks = new FakeLocks();
        const { Recording, shown } = recorder(storage, locks);
        await tab(storage, locks, Recording).check(() => context(firstVisible - 10 * minute));
        await tab(storage, locks, Recording).check(() => context(firstVisible - 9 * minute));
        expect(shown).toHaveLength(1);
    });
});
