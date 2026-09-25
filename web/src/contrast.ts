// WCAG 2.x contrast (https://www.w3.org/TR/WCAG22/#dfn-contrast-ratio).

/** Red, green, and blue from 0 to 255. */
export type Rgb = readonly [number, number, number];

export function parseHex(hex: string): Rgb {
    const match = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex);
    if (match?.[1] === undefined || match[2] === undefined || match[3] === undefined) {
        throw new Error(`Not a #rrggbb color: ${hex}`);
    }
    return [parseInt(match[1], 16), parseInt(match[2], 16), parseInt(match[3], 16)];
}

/**
 * Relative luminance: each sRGB channel linearized, then weighted 0.2126, 0.7152, 0.0722. The
 * threshold is the sRGB standard's 0.04045; WCAG 2.x printed 0.03928, and no 8-bit channel value
 * falls between the two, so both give the same result here.
 */
export function relativeLuminance([r, g, b]: Rgb): number {
    const linear = (channel: number): number => {
        const c = channel / 255;
        return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

/** (L1 + 0.05) / (L2 + 0.05), lighter over darker: from 1 to 21. */
export function contrastRatio(a: Rgb, b: Rgb): number {
    const la = relativeLuminance(a);
    const lb = relativeLuminance(b);
    return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

/** `top` at `alpha` over `under`, blended per channel in sRGB as browsers composite by default. */
export function blend(top: Rgb, alpha: number, under: Rgb): Rgb {
    const mix = (t: number, u: number): number => alpha * t + (1 - alpha) * u;
    return [mix(top[0], under[0]), mix(top[1], under[1]), mix(top[2], under[2])];
}
