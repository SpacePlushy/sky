// End-to-end tests and screenshots: Chromium against the real API, offline.
//
// Playwright starts two API servers from the repo root with the committed demo cache
// (deploy/demo-cache) and CelesTrak:Offline, so nothing ever contacts CelesTrak. Each serves the
// built dashboard from web/dist on its own origin, as the Docker image does. The first server's
// command builds the dashboard and the API; servers start one after the other, so the second
// reuses that build. See e2e/servers.ts for the two simulated clocks.
//
// Locally a server already listening on a port is reused (it must serve a current build); on CI
// both always start fresh.

import path from "node:path";
import { defineConfig, devices } from "@playwright/test";
import { alertServer, mainServer, publicObserver, type ApiServer } from "./e2e/servers";

const ci = (process.env.CI ?? "") !== "";
const repo = path.resolve(import.meta.dirname, "..");

function apiServer(server: ApiServer, build: boolean) {
    const run = `dotnet run --project src/Sky.Api --no-build --urls ${server.url}`;
    return {
        name: `API ${server.port}`,
        command: build ? `npm --prefix web run build && dotnet build src/Sky.Api --nologo && ${run}` : run,
        cwd: repo,
        url: `${server.url}/api/health`,
        reuseExistingServer: !ci,
        timeout: 240_000,
        stdout: "ignore" as const,
        stderr: "pipe" as const,
        env: {
            SKY_CelesTrak__Offline: "true",
            SKY_CelesTrak__Groups: "stations",
            SKY_CelesTrak__CacheDirectory: path.join(repo, "deploy", "demo-cache"),
            SKY_Clock__StartUtc: server.clockStartUtc,
            SKY_Observer__Name: publicObserver.name,
            SKY_Observer__LatitudeDegrees: String(publicObserver.latitudeDeg),
            SKY_Observer__LongitudeDegrees: String(publicObserver.longitudeDeg),
            SKY_Observer__HeightMeters: String(publicObserver.heightM),
            SKY_Observer__TimeZone: publicObserver.timeZone,
            ASPNETCORE_WEBROOT: path.join(repo, "web", "dist"),
            DOTNET_CLI_TELEMETRY_OPTOUT: "1",
            DOTNET_NOLOGO: "1",
        },
    };
}

export default defineConfig({
    testDir: "e2e",
    outputDir: "test-results",
    fullyParallel: true,
    forbidOnly: ci,
    // A flaky test is a bug to fix, not to retry.
    retries: 0,
    ...(ci ? { workers: 2 } : {}),
    reporter: ci ? [["list"], ["html", { open: "never" }]] : "list",
    use: {
        baseURL: mainServer.url,
        trace: "retain-on-failure",
        screenshot: "only-on-failure",
    },
    // Chromium's new headless mode ("chromium" channel), not the separate headless shell: the shell
    // reports Notification.permission as "denied" even when it is granted, which a real browser never does.
    projects: [
        {
            name: "chromium",
            testMatch: /.*\.spec\.ts$/,
            use: { ...devices["Desktop Chrome"], channel: "chromium", viewport: { width: 1440, height: 900 } },
        },
        {
            // `npm run screenshots`: writes docs/images/*.png, so it never runs with the tests.
            name: "screenshots",
            testMatch: /screenshots\.ts$/,
            use: { ...devices["Desktop Chrome"], channel: "chromium" },
        },
    ],
    webServer: [apiServer(mainServer, true), apiServer(alertServer, false)],
});
