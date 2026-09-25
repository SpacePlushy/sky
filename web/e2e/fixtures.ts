// The test function every spec uses. Two automatic checks wrap each test:
// - No request leaves the machine: any request from the browser to a host other than 127.0.0.1
//   fails the test (data: and blob: URLs never touch the network and are ignored).
// - No uncaught exception in the page.

import { test as base, expect } from "@playwright/test";

interface Guards {
    offHostRequests: string[];
    pageErrors: string[];
}

export const test = base.extend<Guards>({
    offHostRequests: [
        async ({ context }, use) => {
            const offHost: string[] = [];
            context.on("request", (request) => {
                const url = new URL(request.url());
                if (url.protocol !== "data:" && url.protocol !== "blob:" && url.hostname !== "127.0.0.1") {
                    offHost.push(request.url());
                }
            });
            await use(offHost);
            expect(offHost, "requests to a host other than 127.0.0.1").toEqual([]);
        },
        { auto: true },
    ],
    pageErrors: [
        async ({ page }, use) => {
            const errors: string[] = [];
            page.on("pageerror", (error) => errors.push(error.message));
            await use(errors);
            expect(errors, "uncaught exceptions in the page").toEqual([]);
        },
        { auto: true },
    ],
});

export { expect };
