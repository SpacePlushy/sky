// Times follow the observer's zone, never the browser's: the browser here runs in Tokyo (UTC+9,
// 16 hours ahead of Phoenix) with a German locale.

import { expect, test } from "./fixtures";
import { clockTexts, getJson, ms, phoenix, waitForDashboard, type PassesJson } from "./helpers";
import { iss } from "./servers";

test.use({ timezoneId: "Asia/Tokyo", locale: "de-DE" });

test("shows America/Phoenix times in a browser set to Asia/Tokyo and de-DE", async ({ page, request }) => {
    await page.goto("/");
    await waitForDashboard(page);

    // The emulation took effect.
    const browser = await page.evaluate(() => ({ zone: Intl.DateTimeFormat().resolvedOptions().timeZone, language: navigator.language }));
    expect(browser).toEqual({ zone: "Asia/Tokyo", language: "de-DE" });

    await expect(page.locator("#clock-zone")).toHaveText("UTC-7");
    await expect(page.locator("#passes-note")).toContainText("Times in America/Phoenix (UTC-7).");

    // The observer clock is the UTC clock minus 7 hours (both read from one rendering).
    const clocks = await page.evaluate(() => ({
        local: document.getElementById("clock-local")?.textContent ?? "",
        utc: document.getElementById("clock-utc")?.textContent ?? "",
    }));
    const hour = (text: string): number => Number(text.slice(0, 2));
    expect(clocks.local.slice(2)).toBe(clocks.utc.slice(2));
    expect((hour(clocks.utc) - hour(clocks.local) + 24) % 24).toBe(7);

    // Pass rise times and the sky plot caption are Phoenix times, in the page's own format.
    await expect(async () => {
        const data = await getJson<PassesJson>(request, `/api/satellites/${iss}/passes`);
        const nowMs = ms(data.fromUtc);
        const expected = data.passes.filter((p) => ms(p.set.timeUtc) >= nowMs);
        const rises = await page.locator("li.pass-item .cell-rise .time").allTextContents();
        expect(rises).toHaveLength(expected.length);
        expected.forEach((pass, i) => {
            expect([...clockTexts(ms(pass.rise.timeUtc))], `row ${i}`).toContain(rises[i]);
        });
    }).toPass({ timeout: 15_000 });

    // The plotted pass and the caption, read from one rendering: the pass plotted by default can
    // change between two reads, when the pass in progress at the server's start ends 63 s in.
    await expect(async () => {
        const shown = await page.evaluate(() => ({
            rise: Number(document.querySelector('button.pass[aria-pressed="true"]')?.getAttribute("data-rise")),
            caption: document.getElementById("sky-caption")?.textContent ?? "",
        }));
        expect(Number.isFinite(shown.rise) && shown.rise > 0, "a pass is plotted").toBe(true);
        const { date, clock } = phoenix(Math.round(shown.rise / 1000) * 1000);
        expect(shown.caption).toContain(`${date}: rises ${clock}`);
    }).toPass({ timeout: 15_000 });
    // Not the German or Japanese forms of that date.
    await expect(page.locator("#sky-caption")).not.toContainText(/Do\.|Fr\.|月|日/);
});
