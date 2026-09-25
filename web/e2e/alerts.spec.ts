// Browser notifications, against the server whose clock starts 17 min 26 s before the first
// evening visible ISS pass. window.Notification is replaced by a recorder, so the test sees every
// notification the page creates, across reloads (sessionStorage), without the operating system.

import type { Page } from "@playwright/test";
import { expect, test } from "./fixtures";
import { getJson, ms, phoenix, roundToSecond, waitForDashboard, type ConfigJson, type PassesJson } from "./helpers";
import { alertServer, iss, mainServer } from "./servers";

interface Shown {
    readonly title: string;
    readonly body: string;
    readonly tag: string;
}

/**
 * Replaces window.Notification before the page's scripts run. Permission starts as "default"; the
 * first requestPermission() asks the real API (which the test context has granted, or the answer
 * given) and remembers the result for the rest of the tab's life.
 */
function recordNotifications(answer: string | null): void {
    const read = <T,>(key: string, fallback: T): T => {
        try {
            const text = sessionStorage.getItem(key);
            return text === null ? fallback : (JSON.parse(text) as T);
        } catch {
            return fallback;
        }
    };
    const write = (key: string, value: unknown): void => {
        sessionStorage.setItem(key, JSON.stringify(value));
    };
    const Real = window.Notification;
    class RecordingNotification {
        onclick: (() => void) | null = null;
        constructor(title: string, options?: NotificationOptions) {
            write("e2e.notifications", [...read<Shown[]>("e2e.notifications", []), { title, body: options?.body ?? "", tag: options?.tag ?? "" }]);
        }
        close(): void {
            // Nothing to close.
        }
        static get permission(): NotificationPermission {
            return read<NotificationPermission>("e2e.permission", "default");
        }
        static async requestPermission(): Promise<NotificationPermission> {
            write("e2e.permissionRequests", read("e2e.permissionRequests", 0) + 1);
            const result = (answer as NotificationPermission | null) ?? (await Real.requestPermission());
            write("e2e.permission", result);
            return result;
        }
    }
    Object.defineProperty(window, "Notification", { value: RecordingNotification, configurable: true, writable: true });
}

async function recorded(page: Page): Promise<{ shown: Shown[]; requests: number }> {
    return page.evaluate(() => ({
        shown: JSON.parse(sessionStorage.getItem("e2e.notifications") ?? "[]") as Shown[],
        requests: Number(sessionStorage.getItem("e2e.permissionRequests") ?? "0"),
    }));
}

test.describe("on the alert server", () => {
    test.use({ baseURL: alertServer.url });

    test("one notification for the first visible pass, at start minus the lead, never repeated", async ({ page, context, request }) => {
        test.setTimeout(120_000);
        await context.grantPermissions(["notifications"], { origin: alertServer.url });
        await page.addInitScript(recordNotifications, null);
        await page.clock.install();
        await page.goto("/");
        await waitForDashboard(page);

        // Off until asked; the lead and the choice can be set first.
        await expect(page.locator("#alerts-state")).toHaveText("Off");
        await expect(page.locator(".alerts-scope")).toHaveText(/only work while this tab is open/);
        await page.getByLabel("Lead time").selectOption("15");
        await expect(page.getByLabel("Visible passes only")).toBeChecked();
        await page.getByRole("button", { name: "Turn on alerts" }).click();
        await expect(page.locator("#alerts-state")).toHaveText("On");
        await expect(page.getByRole("button", { name: "Turn off alerts" })).toBeVisible();
        expect((await recorded(page)).requests).toBe(1);

        // The first visible part still ahead, from the API, and its alert time on the server's clock.
        const config = await getJson<ConfigJson>(request, "/api/config");
        const data = await getJson<PassesJson>(request, `/api/satellites/${iss}/passes`);
        const serverNow = ms(config.serverTimeUtc);
        const start = data.passes.flatMap((p) => p.visible).map((v) => ms(v.start.timeUtc)).find((t) => t > serverNow);
        if (start === undefined) {
            throw new Error("no visible pass ahead");
        }
        const alertAt = start - 15 * 60_000;
        expect(alertAt - serverNow, `the ${alertServer.port} server must be started fresh: its alert time has passed`).toBeGreaterThan(10_000);
        // On a fresh server this is the 03:07:26 UTC pass, 20:07:26 in Phoenix.
        expect(roundToSecond(start)).toBe(Date.parse("2026-09-25T03:07:26Z"));
        const startsAt = phoenix(roundToSecond(start));
        await expect(page.locator("#alerts-status")).toContainText(`Next alert ${phoenix(roundToSecond(alertAt)).date} ${phoenix(roundToSecond(alertAt)).clock}, 15 min before ISS (ZARYA) is visible at ${startsAt.clock}.`);
        expect((await recorded(page)).shown).toEqual([]);

        const forThisPass = (shown: Shown[]): Shown[] => shown.filter((n) => Math.abs(Date.parse(n.tag.split("@")[1] ?? "") - start) < 1000);

        /**
         * Jumps the page's clock so the server time it computes is `offset` ms from the alert time.
         * A jump moves only the browser's clock, so the page's estimate of the server's clock jumps
         * with it until the next /now answer puts it back; wait for that first, so each jump starts
         * from the server's real time.
         */
        const jumpToAlert = async (offset: number): Promise<void> => {
            await expect.poll(async () => {
                const server = ms((await getJson<ConfigJson>(request, "/api/config")).serverTimeUtc);
                const shown = (await page.locator("#clock-utc").textContent()) ?? "";
                const seconds = (clock: string): number => clock.split(":").reduce((sum, part) => sum * 60 + Number(part), 0);
                const apart = Math.abs(seconds(shown) - seconds(new Date(server).toISOString().slice(11, 19)));
                return Math.min(apart, 86_400 - apart);
            }, { message: "the page's clock follows the server's again" }).toBeLessThanOrEqual(2);
            const now = ms((await getJson<ConfigJson>(request, "/api/config")).serverTimeUtc);
            const jump = alertAt + offset - now;
            if (offset < 0) {
                expect(jump, "there is still time before the alert").toBeGreaterThan(0);
            }
            // After the alert time the server's own clock is already in the window: no jump needed.
            await page.clock.fastForward(Math.max(1, jump));
        };

        // Three seconds before the alert time: nothing.
        await jumpToAlert(-3_000);
        await page.clock.runFor(1_000);
        expect(forThisPass((await recorded(page)).shown)).toEqual([]);

        // Five seconds after it: exactly one, naming the satellite and the Phoenix start time.
        await jumpToAlert(5_000);
        await expect.poll(async () => forThisPass((await recorded(page)).shown)).toHaveLength(1);
        const [shown] = forThisPass((await recorded(page)).shown);
        expect(shown?.title).toBe("ISS (ZARYA) visible in 15 min");
        expect(shown?.body).toContain(`${startsAt.date} ${startsAt.clock} America/Phoenix (UTC-7)`);
        expect(shown?.body).toMatch(/look [NESW]{1,3} \d+°, up to \d+°$/);

        // Ticking on inside the window, and a pass-list refresh: still one.
        const refreshed = page.waitForResponse((r) => new URL(r.url()).pathname === `/api/satellites/${iss}/passes` && r.ok());
        await page.clock.fastForward(5 * 60_000 + 5_000);
        await refreshed;
        await jumpToAlert(20_000);
        await page.clock.runFor(3_000);
        expect(forThisPass((await recorded(page)).shown)).toHaveLength(1);

        // A reload remembers it: alerts are still on, and the pass does not alert again.
        await page.reload();
        await waitForDashboard(page);
        await expect(page.locator("#alerts-state")).toHaveText("On");
        await jumpToAlert(30_000);
        await page.clock.runFor(3_000);
        const { shown: all, requests } = await recorded(page);
        expect(forThisPass(all)).toHaveLength(1);
        expect(new Set(all.map((n) => n.tag)).size).toBe(all.length);
        expect(requests).toBe(1);
    });
});

test("shows Blocked when the browser denies notifications", async ({ page }) => {
    await page.addInitScript(recordNotifications, "denied");
    await page.goto(mainServer.url);
    await waitForDashboard(page);
    await page.getByRole("button", { name: "Turn on alerts" }).click();
    await expect(page.locator("#alerts-state")).toHaveText("Blocked");
    await expect(page.locator("#alerts-status")).toContainText("Notifications are blocked for this page.");
    expect((await recorded(page)).shown).toEqual([]);
});
