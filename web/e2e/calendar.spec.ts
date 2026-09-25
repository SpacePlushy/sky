// The calendar link downloads the API's iCalendar export of the selected satellite's visible passes.

import { readFile } from "node:fs/promises";
import { expect, test } from "./fixtures";
import { getJson, icsTimes, ms, waitForDashboard, type PassesJson } from "./helpers";
import { iss } from "./servers";

/** RFC 5545 §3.1: a CRLF followed by a space or tab continues the previous line. */
function unfold(text: string): string[] {
    return text.replace(/\r\n[ \t]/g, "").split("\r\n");
}

test("the calendar link downloads one event with an alarm per visible part", async ({ page, request }) => {
    await page.goto("/");
    await waitForDashboard(page);

    const link = page.getByRole("link", { name: "Add visible passes to your calendar (.ics, alarm 10 min before)" });
    await expect(link).toHaveAttribute("href", `/api/satellites/${iss}/passes.ics?days=7&visibleOnly=true&alarm=10`);
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
