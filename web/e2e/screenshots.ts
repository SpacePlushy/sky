// `npm run screenshots`: the README's screenshots, from the demo server (clock started
// 2026-09-24 04:00 UTC) once its data has loaded. Written to docs/images/.
//
// Privacy guard: before anything is captured, /api/config must show the public Capitol location
// the committed settings use. Any other observer (a real location from a local settings file)
// stops the run with no file written.

import path from "node:path";
import type { APIRequestContext, Page } from "@playwright/test";
import { expect, test } from "./fixtures";
import { getJson, waitForDashboard, type ConfigJson } from "./helpers";
import { mainServer, publicObserver } from "./servers";

const images = path.resolve(import.meta.dirname, "..", "..", "docs", "images");

async function requirePublicObserver(request: APIRequestContext): Promise<void> {
    const config = await getJson<ConfigJson>(request, `${mainServer.url}/api/config`);
    const { name, latitudeDeg, longitudeDeg } = config.observer;
    if (name !== publicObserver.name || latitudeDeg !== publicObserver.latitudeDeg || longitudeDeg !== publicObserver.longitudeDeg) {
        throw new Error(`Refusing to take screenshots: the API's observer is not the public ${publicObserver.name} location.`);
    }
    expect(config.offline, "the API is offline").toBe(true);
    expect(config.clockSimulated, "the API's clock is simulated").toBe(true);
}

async function ready(page: Page): Promise<void> {
    await page.goto(mainServer.url);
    await waitForDashboard(page);
    await expect(page.locator(".map-track path").first()).toBeAttached();
    await expect(page.locator("#alerts-state")).toHaveText("Off");
}

test.describe("desktop", () => {
    test.use({ viewport: { width: 1440, height: 900 } });

    test("dashboard-desktop.png", async ({ page, request }) => {
        await requirePublicObserver(request);
        await ready(page);
        await page.screenshot({ path: path.join(images, "dashboard-desktop.png"), fullPage: true, animations: "disabled", caret: "hide" });
    });
});

test.describe("phone", () => {
    test.use({ viewport: { width: 375, height: 812 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 });

    test("dashboard-mobile.png", async ({ page, request }) => {
        await requirePublicObserver(request);
        await ready(page);
        await page.screenshot({ path: path.join(images, "dashboard-mobile.png"), fullPage: false, animations: "disabled", caret: "hide" });
    });
});
