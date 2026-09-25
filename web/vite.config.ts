import type { Plugin } from "vite";
import { defineConfig } from "vitest/config";

// The API the dashboard reads. `npm run dev` proxies /api to it, so the page and the API share one
// origin in development exactly as they do when the API serves the built files.
const api = "http://localhost:5080";

// A Content Security Policy for the built page: scripts, styles, images, and requests may come
// only from the page's own origin. It is how the page proves it makes no other network requests
// (no fonts, CDNs, map tiles, or analytics). Added at build time only, because the development
// server injects styles and a websocket that a strict policy would block.
const contentSecurityPolicy = [
    "default-src 'self'",
    "script-src 'self'",
    "style-src 'self'",
    "img-src 'self'",
    "font-src 'none'",
    "connect-src 'self'",
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'none'",
].join("; ");

function csp(): Plugin {
    return {
        name: "sky-content-security-policy",
        apply: "build",
        transformIndexHtml: () => [
            {
                tag: "meta",
                attrs: { "http-equiv": "Content-Security-Policy", content: contentSecurityPolicy },
                injectTo: "head-prepend",
            },
        ],
    };
}

export default defineConfig({
    plugins: [csp()],
    server: {
        port: 5173,
        strictPort: true,
        proxy: { "/api": { target: api } },
    },
    preview: {
        port: 4173,
        strictPort: true,
        proxy: { "/api": { target: api } },
    },
    build: {
        outDir: "dist",
        emptyOutDir: true,
        target: "es2022",
    },
    test: {
        include: ["src/**/*.test.ts"],
        environment: "node",
        // Vitest empties CSS imports unless asked; the palette test reads styles.css as text.
        css: { include: [/styles\.css/] },
    },
});
