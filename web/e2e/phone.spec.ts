// A phone-sized viewport: nothing scrolls sideways, and passes are stacked cards, not a table.

import { expect, test } from "./fixtures";
import { waitForDashboard } from "./helpers";

test.use({ viewport: { width: 375, height: 812 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 });

test("fits 375 px without sideways scrolling and shows passes as cards", async ({ page }) => {
    await page.goto("/");
    await waitForDashboard(page);

    const widths = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, client: document.documentElement.clientWidth }));
    expect(widths.client).toBe(375);
    expect(widths.scroll).toBeLessThanOrEqual(widths.client);

    // Cards: the table header is hidden, each pass is a two-column grid with visible field labels,
    // and no card is wider than the screen.
    await expect(page.locator(".pass-header")).toBeHidden();
    const first = page.locator("button.pass").first();
    await expect(first.locator(".cell-rise .label")).toBeVisible();
    await expect(first.locator(".cell-rise .label")).toHaveText("Rise");
    const layout = await page.locator("button.pass").evaluateAll((buttons) => buttons.map((b) => ({
        columns: getComputedStyle(b).gridTemplateColumns.split(" ").length,
        right: b.getBoundingClientRect().right,
    })));
    expect(layout.length).toBeGreaterThan(10);
    for (const card of layout) {
        expect(card.columns).toBe(2);
        expect(card.right).toBeLessThanOrEqual(375);
    }

    // The pass tools fit too.
    for (const id of ["calendar-link", "alerts-toggle", "alerts-lead", "alerts-status"]) {
        const box = await page.locator(`#${id}`).boundingBox();
        expect(box, id).not.toBeNull();
        expect((box?.x ?? 0) + (box?.width ?? 0), id).toBeLessThanOrEqual(375);
    }
});
