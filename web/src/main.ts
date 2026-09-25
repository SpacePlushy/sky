import "./styles.css";
import { App } from "./app";

function fail(error: unknown): void {
    const banner = document.getElementById("banners");
    if (banner !== null) {
        const message = document.createElement("div");
        message.className = "banner banner-error";
        message.textContent = `The dashboard failed to start: ${error instanceof Error ? error.message : String(error)}`;
        banner.replaceChildren(message);
    }
}

try {
    new App().start().catch(fail);
} catch (error) {
    fail(error);
}
