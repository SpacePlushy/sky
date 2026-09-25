// The dashboard against the demo server (clock started 2026-09-24 04:00 UTC): notices, telemetry,
// the pass list, selection, and an accessibility smoke test. Desktop viewport, 1440 x 900.

import { expect, test } from "./fixtures";
import { clockTexts, getJson, ms, parseAngle, phoenix, waitForDashboard, type NowJson, type PassesJson } from "./helpers";
import { iss, mainServer } from "./servers";

test("loads offline on the simulated clock, with telemetry matching /now", async ({ page, request }) => {
    await page.goto("/");
    await expect(page.locator(".banner-notice").filter({ hasText: "Offline." })).toBeVisible();
    await expect(page.locator(".banner-notice").filter({ hasText: "Simulated clock." })).toContainText(
        `started at ${mainServer.clockStartUtc.replace("T", " ").replace("Z", "")} UTC`,
    );
    await waitForDashboard(page);

    // The page polls /now every second, so what it shows is at most about 2 s older than a /now the
    // test fetches right after reading it. The tolerance is how far each value moves in 2 s at this
    // moment (from /now at t and t + 2 s), plus half the last digit shown.
    await expect(async () => {
        const shown = await page.evaluate(() => {
            const text = (key: string): string => document.querySelector(`.row-${key} .value`)?.textContent ?? "";
            return { latitude: text("latitude"), longitude: text("longitude"), elevation: text("elevation") };
        });
        const now = await getJson<NowJson>(request, `/api/satellites/${iss}/now`);
        const later = new Date(ms(now.timeUtc) + 2000).toISOString();
        const next = await getJson<NowJson>(request, `/api/satellites/${iss}/now?at=${encodeURIComponent(later)}`);
        const wrap = (d: number): number => Math.abs(((d + 540) % 360) - 180);

        const latTolerance = Math.abs(next.position.latitudeDeg - now.position.latitudeDeg) + 0.005;
        const lonTolerance = wrap(next.position.longitudeDeg - now.position.longitudeDeg) + 0.005;
        const elTolerance = Math.abs(next.look.elevationDeg - now.look.elevationDeg) + 0.05;
        expect(Math.abs(parseAngle(shown.latitude) - now.position.latitudeDeg), `latitude ${shown.latitude}`).toBeLessThanOrEqual(latTolerance);
        expect(wrap(parseAngle(shown.longitude) - now.position.longitudeDeg), `longitude ${shown.longitude}`).toBeLessThanOrEqual(lonTolerance);
        expect(Math.abs(parseAngle(shown.elevation) - now.look.elevationDeg), `elevation ${shown.elevation}`).toBeLessThanOrEqual(elTolerance);
    }).toPass({ timeout: 15_000 });
});

test("lists the same passes as /passes, in Phoenix time, with visible ones marked in text", async ({ page, request }) => {
    await page.goto("/");
    await waitForDashboard(page);

    await expect(async () => {
        const data = await getJson<PassesJson>(request, `/api/satellites/${iss}/passes`);
        const nowMs = ms(data.fromUtc);
        // The page lists every pass that has not ended, as the API returns them.
        const expected = data.passes.filter((p) => ms(p.set.timeUtc) >= nowMs);
        expect(expected.length).toBeGreaterThan(10);

        const rows = await page.locator("li.pass-item").evaluateAll((items) => items.map((item) => ({
            date: item.querySelector(".cell-date .date")?.textContent ?? "",
            rise: item.querySelector(".cell-rise .time")?.textContent ?? "",
            visibleBadges: item.querySelectorAll(".badge-visible").length,
            visibleBadgeText: item.querySelector(".badge-visible")?.textContent ?? "",
            visibleText: item.querySelector(".visible-text")?.textContent ?? "",
        })));
        expect(rows).toHaveLength(expected.length);

        expected.forEach((pass, i) => {
            const row = rows[i];
            const rise = ms(pass.rise.timeUtc);
            expect(row?.date, `row ${i} date`).toBe(phoenix(Math.round(rise / 1000) * 1000).date);
            expect([...clockTexts(rise)], `row ${i} rise`).toContain(row?.rise);
            if (pass.visible.length > 0) {
                expect(row?.visibleBadges, `row ${i} is visible`).toBe(1);
                expect(row?.visibleBadgeText).toBe("Visible");
                const start = pass.visible[0]?.start.timeUtc ?? "";
                expect([...clockTexts(ms(start))].some((c) => row?.visibleText.startsWith(c) === true), `row ${i} visible part ${row?.visibleText}`).toBe(true);
            } else {
                expect(row?.visibleBadges, `row ${i} is not visible`).toBe(0);
                expect(row?.visibleText).toMatch(/^Not visible \(/);
            }
        });
    }).toPass({ timeout: 15_000 });
});

test("selecting a pass by keyboard and by click redraws the sky plot, and the choice survives a refresh", async ({ page }) => {
    // The page refreshes the pass list on a 5-minute timer; a fake browser clock fast-forwards it.
    await page.clock.install();
    let passesRequests = 0;
    let refreshing = false;
    await page.route(`**/api/satellites/${iss}/passes?*`, async (route) => {
        passesRequests += 1;
        const response = await route.fetch();
        if (!refreshing) {
            await route.fulfill({ response });
            return;
        }
        // The refreshed list is the API's with two changes. Its last pass (a week out) is gone, so
        // the test can see when the page has taken the new list in. And every rise is 1 ms later:
        // two requests at different moments can differ by that much (Brent's tolerance; measured
        // on this data, 2 of 26 rises), and the choice and keyboard focus must survive it.
        const body = (await response.json()) as { passes: { rise: { timeUtc: string } }[] };
        const passes = body.passes.slice(0, -1).map((p) => ({ ...p, rise: { ...p.rise, timeUtc: new Date(ms(p.rise.timeUtc) + 1).toISOString() } }));
        await route.fulfill({ response, json: { ...body, passes } });
    });

    await page.goto("/");
    await waitForDashboard(page);
    const caption = page.locator("#sky-caption");
    const buttons = page.locator("button.pass");
    const count = await buttons.count();
    const riseOf = async (i: number): Promise<number> => Number(await buttons.nth(i).getAttribute("data-rise"));
    const expectPlotted = async (i: number): Promise<void> => {
        const rise = await riseOf(i);
        await expect(buttons.nth(i)).toHaveAttribute("aria-pressed", "true");
        await expect(page.locator('button.pass[aria-pressed="true"]')).toHaveCount(1);
        await expect(buttons.nth(i).locator(".badge-plotted")).toHaveText("On sky plot");
        const { date, clock } = phoenix(Math.round(rise / 1000) * 1000);
        await expect(caption).toContainText(`${date}: rises ${clock}`);
    };

    // Keyboard: Enter on one row, then Tab to the next and Space.
    await buttons.nth(2).focus();
    await page.keyboard.press("Enter");
    await expectPlotted(2);
    await expect(buttons.nth(2)).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(buttons.nth(3)).toBeFocused();
    await page.keyboard.press("Space");
    await expectPlotted(3);

    // Click.
    await buttons.nth(4).click();
    await expectPlotted(4);
    const chosenRise = await riseOf(4);
    const captionBefore = await caption.textContent();

    // Refresh: past the 5-minute timer. The last row leaves only when the page takes in the new list.
    const lastRise = await riseOf(count - 1);
    refreshing = true;
    const requestsBefore = passesRequests;
    const refreshed = page.waitForResponse((r) => new URL(r.url()).pathname === `/api/satellites/${iss}/passes` && r.ok());
    await page.clock.fastForward(5 * 60_000 + 5_000);
    await refreshed;
    await expect(page.locator(`button.pass[data-rise="${lastRise}"]`)).toHaveCount(0);
    expect(passesRequests).toBe(requestsBefore + 1);

    const pressed = page.locator('button.pass[aria-pressed="true"]');
    await expect(pressed).toHaveCount(1);
    expect(Number(await pressed.getAttribute("data-rise"))).toBe(chosenRise + 1);
    expect([...clockTexts(chosenRise)]).toContain(await pressed.locator(".cell-rise .time").textContent());
    await expect(pressed).toBeFocused();
    // The caption still describes that pass: the same date and rise (within the 1 ms), peak, and set.
    const captionAfter = (await caption.textContent()) ?? "";
    const { date } = phoenix(Math.round(chosenRise / 1000) * 1000);
    expect([...clockTexts(chosenRise)].some((c) => captionAfter.startsWith(`${date}: rises ${c} `))).toBe(true);
    const tail = (text: string): string => text.slice(text.indexOf(", highest"));
    expect(tail(captionAfter)).toBe(tail(captionBefore ?? ""));
});

test("the map and sky plot have accessible names, and Tab reaches the controls and the pass rows", async ({ page }) => {
    await page.goto("/");
    await waitForDashboard(page);
    await expect(page.getByRole("img", { name: /^World map with the satellite's ground track/ })).toBeVisible();
    await expect(page.getByRole("img", { name: /^Sky plot of the selected pass\. .+: rises \d\d:\d\d:\d\d/ })).toBeVisible();

    const reached: string[] = [];
    for (let i = 0; i < 40; i++) {
        await page.keyboard.press("Tab");
        const focused = await page.evaluate(() => {
            const el = document.activeElement;
            if (el === null || el === document.body) {
                return "body";
            }
            return el.id !== "" ? `#${el.id}` : el.matches("button.pass") ? "pass" : el.tagName.toLowerCase();
        });
        reached.push(focused);
        if (focused === "pass") {
            break;
        }
    }
    expect(reached).toEqual(expect.arrayContaining(["#satellite", "#calendar-link", "#alerts-toggle", "#alerts-lead", "#alerts-visible-only"]));
    expect(reached.at(-1)).toBe("pass");
    // And on through the rows.
    await page.keyboard.press("Tab");
    await expect(page.locator("button.pass").nth(1)).toBeFocused();
});
