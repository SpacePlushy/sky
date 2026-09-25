// The calendar link downloads the API's iCalendar export of the selected satellite's visible passes.
// It is offered only when the loaded pass list has a visible part: the API has no file otherwise.

import { readFile } from "node:fs/promises";
import { expect, test } from "./fixtures";
import { getJson, icsTimes, ms, waitForDashboard, type PassesJson } from "./helpers";
import { iss, noVisiblePasses } from "./servers";

const nothingToAdd = "No visible passes in the next 7 days, so there is nothing to add to a calendar.";

/** RFC 5545 §3.1: a CRLF followed by a space or tab continues the previous line. */
function unfold(text: string): string[] {
    return text.replace(/\r\n[ \t]/g, "").split("\r\n");
}

test("the calendar link downloads one event with an alarm per visible part", async ({ page, request }) => {
    await page.goto("/");
    await waitForDashboard(page);

    const link = page.getByRole("link", { name: "Add visible passes to your calendar (.ics, alarm 10 min before)" });
    await expect(link).toHaveAttribute("href", `/api/satellites/${iss}/passes.ics?days=7&visibleOnly=true&alarm=10`);
    // The alarm claim is qualified beside the link, and the link points to it.
    await expect(page.locator("#calendar-alarm")).toHaveText(
        "The file has a 10-minute alarm on each visible pass. Apple Calendar and Outlook keep the alarm; " +
        "Google Calendar ignores alarms in imported files and uses its own default notifications.",
    );
    await expect(link).toHaveAttribute("aria-describedby", "calendar-alarm");
    await expect(page.locator("#calendar-none")).toBeHidden();
    const [download] = await Promise.all([page.waitForEvent("download"), link.click()]);
    expect(download.suggestedFilename()).toBe(`sky-${iss}-passes.ics`);
    const text = await readFile(await download.path(), "utf8");
    const data = await getJson<PassesJson>(request, `/api/satellites/${iss}/passes?days=7`);

    // Every line ends in CRLF, and no physical line is longer than 75 octets.
    expect(text.endsWith("\r\n")).toBe(true);
    expect(/[^\r]\n/.test(text)).toBe(false);
    for (const line of text.split("\r\n")) {
        expect(Buffer.byteLength(line, "utf8"), line).toBeLessThanOrEqual(75);
    }

    const lines = unfold(text).filter((line) => line !== "");
    expect(lines[0]).toBe("BEGIN:VCALENDAR");
    expect(lines.at(-1)).toBe("END:VCALENDAR");
    expect(lines).toContain("VERSION:2.0");

    const events: string[][] = [];
    let current: string[] | null = null;
    for (const line of lines) {
        if (line === "BEGIN:VEVENT") {
            current = [];
        } else if (line === "END:VEVENT") {
            events.push(current ?? []);
            current = null;
        } else {
            current?.push(line);
        }
    }

    const parts = data.passes.flatMap((p) => p.visible);
    expect(parts.length).toBeGreaterThan(0);
    expect(events).toHaveLength(parts.length);
    events.forEach((event, i) => {
        const start = event.find((l) => l.startsWith("DTSTART"));
        expect(start, `event ${i}`).toMatch(/^DTSTART:\d{8}T\d{6}Z$/);
        expect(event.find((l) => l.startsWith("DTEND")), `event ${i}`).toMatch(/^DTEND:\d{8}T\d{6}Z$/);
        const part = parts[i];
        expect([...icsTimes(ms(part?.start.timeUtc ?? ""))], `event ${i} start`).toContain(start?.slice("DTSTART:".length));
        expect(event).toEqual(expect.arrayContaining(["BEGIN:VALARM", "ACTION:DISPLAY", "TRIGGER:-PT10M", "END:VALARM"]));
    });
});

test("the calendar link follows the selected satellite", async ({ page, request }) => {
    await page.goto("/");
    await waitForDashboard(page);
    const satellites = await getJson<{ id: number }[]>(request, "/api/satellites");
    const other = satellites.find((s) => s.id !== iss);
    expect(other).toBeDefined();
    await page.locator("#satellite").selectOption(String(other?.id));
    await expect(page.locator("#calendar-link")).toHaveAttribute("href", `/api/satellites/${other?.id}/passes.ics?days=7&visibleOnly=true&alarm=10`);
});

test("the calendar link waits for the pass list, and is not offered when it fails to load", async ({ page }) => {
    // The ISS pass list is held until the test lets it through.
    let release = (): void => undefined;
    const held = new Promise<void>((resolve) => {
        release = resolve;
    });
    await page.route(`**/api/satellites/${iss}/passes?*`, async (route) => {
        await held;
        await route.continue();
    });

    await page.goto("/");
    await expect(page.locator(".row-latitude .value")).not.toHaveText("—", { timeout: 20_000 });
    await expect(page.locator("#passes")).toHaveText("Loading passes…");
    await expect(page.locator("#calendar-link")).toBeHidden();
    await expect(page.locator("#calendar-none")).toBeHidden();

    release();
    await waitForDashboard(page);
    await expect(page.locator("#calendar-link")).toBeVisible();

    // A failed load: another satellite's list fails, and nothing is offered for it.
    const satellites = await page.locator("#satellite option").evaluateAll((options) => options.map((o) => o.getAttribute("value") ?? ""));
    const other = satellites.find((id) => id !== "" && id !== String(iss));
    if (other === undefined) {
        throw new Error("no other satellite to choose");
    }
    await page.route(`**/api/satellites/${other}/passes?*`, (route) =>
        route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ title: "Service unavailable", detail: "Failed by the test." }) }));
    await page.locator("#satellite").selectOption(other);
    await expect(page.locator("#passes")).toHaveText("Passes could not be loaded.");
    await expect(page.locator("#calendar-link")).toBeHidden();
    await expect(page.locator("#calendar-none")).toBeHidden();
});

test("says there is nothing to add for a satellite with no visible pass, for which the API has no file", async ({ page, request }) => {
    const data = await getJson<PassesJson>(request, `/api/satellites/${noVisiblePasses}/passes?days=7`);
    expect(data.passes.length).toBeGreaterThan(0);
    expect(data.passes.flatMap((p) => p.visible)).toEqual([]);
    const file = await request.get(`/api/satellites/${noVisiblePasses}/passes.ics?days=7&visibleOnly=true&alarm=10`);
    expect(file.status()).toBe(404);
    expect(file.headers()["content-type"]).toContain("application/problem+json");

    await page.goto(`/?sat=${noVisiblePasses}`);
    await waitForDashboard(page);
    await expect(page.locator("#calendar-none")).toHaveText(nothingToAdd);
    await expect(page.locator("#calendar-none")).toBeVisible();
    await expect(page.locator("#calendar-link")).toBeHidden();

    // A satellite with visible passes brings the link back, once its list has loaded.
    await page.locator("#satellite").selectOption(String(iss));
    await expect(page.locator("#calendar-link")).toBeVisible();
    await expect(page.locator("#calendar-none")).toBeHidden();
});
