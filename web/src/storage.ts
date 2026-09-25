// Browser storage that never throws. localStorage can be missing, blocked by site settings, full,
// or throw on access in private windows; the page then keeps its settings for this visit only.

/** The stored text for a key, or null when there is none or storage cannot be read. */
export function readStored(key: string): string | null {
    try {
        return window.localStorage.getItem(key);
    } catch {
        return null;
    }
}

/** Stores text under a key; returns false when storage cannot be written. */
export function writeStored(key: string, value: string): boolean {
    try {
        window.localStorage.setItem(key, value);
        return true;
    } catch {
        return false;
    }
}
