import { describe, expect, it } from "vitest";
import { blend, contrastRatio, parseHex, relativeLuminance, type Rgb } from "./contrast";
import { contrastPairs, opacity, palette, type Blend, type ColorName } from "./palette";
import css from "./styles.css?raw";

function kebab(name: string): string {
    return name.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`);
}

function color(name: ColorName): Rgb {
    return parseHex(palette[name]);
}

function background(bg: ColorName | Blend): { rgb: Rgb; name: string } {
    if (typeof bg === "string") {
        return { rgb: color(bg), name: bg };
    }
    return {
        rgb: blend(color(bg.color), opacity[bg.alpha], color(bg.under)),
        name: `${bg.color} at ${opacity[bg.alpha]} over ${bg.under}`,
    };
}

describe("WCAG contrast formula", () => {
    // Anchors published by the W3C and WebAIM, so the implementation is checked against values it
    // did not produce.
    it("gives 21:1 for black on white and 1:1 for a color on itself", () => {
        expect(contrastRatio([0, 0, 0], [255, 255, 255])).toBeCloseTo(21, 12);
        expect(contrastRatio([18, 52, 86], [18, 52, 86])).toBe(1);
    });

    it("puts #767676 just above 4.5:1 on white and #777777 just below", () => {
        // WebAIM's contrast checker: #767676 on #FFFFFF is 4.54:1, #777777 is 4.48:1.
        expect(contrastRatio(parseHex("#767676"), parseHex("#ffffff"))).toBeCloseTo(4.54, 2);
        expect(contrastRatio(parseHex("#777777"), parseHex("#ffffff"))).toBeCloseTo(4.48, 2);
    });

    it("gives the relative luminance of pure and mid colors", () => {
        expect(relativeLuminance([255, 255, 255])).toBeCloseTo(1, 12);
        expect(relativeLuminance([0, 0, 0])).toBe(0);
        // sRGB 128/255 linearizes to 0.21586; gray has that luminance since the weights sum to 1.
        expect(relativeLuminance([128, 128, 128])).toBeCloseTo(0.21586, 5);
        expect(relativeLuminance([255, 0, 0])).toBeCloseTo(0.2126, 12);
        expect(relativeLuminance([0, 255, 0])).toBeCloseTo(0.7152, 12);
        expect(relativeLuminance([0, 0, 255])).toBeCloseTo(0.0722, 12);
    });

    it("is symmetric in its arguments", () => {
        expect(contrastRatio(parseHex("#3dd3ec"), parseHex("#0b1017"))).toBe(contrastRatio(parseHex("#0b1017"), parseHex("#3dd3ec")));
    });

    it("blends per channel", () => {
        expect(blend([255, 255, 255], 0.5, [0, 0, 0])).toEqual([127.5, 127.5, 127.5]);
        expect(blend([10, 20, 30], 0, [1, 2, 3])).toEqual([1, 2, 3]);
    });
});

describe("palette", () => {
    it.each(contrastPairs.map((p) => [p.fg, background(p.bg).name, p.min, p.use, p] as const))(
        "%s on %s meets %s:1 (%s)",
        (_fg, _bg, _min, _use, pair) => {
            const ratio = contrastRatio(color(pair.fg), background(pair.bg).rgb);
            expect(ratio).toBeGreaterThanOrEqual(pair.min);
        },
    );

    it("declares the same colors and opacities as styles.css", () => {
        const declared = new Map([...css.matchAll(/--([a-z-]+):\s*([^;]+);/g)].map((m) => [m[1] ?? "", (m[2] ?? "").trim()]));
        for (const [name, hex] of Object.entries(palette)) {
            expect(declared.get(kebab(name)), `--${kebab(name)}`).toBe(hex);
        }
        for (const [name, alpha] of Object.entries(opacity)) {
            expect(Number(declared.get(`${kebab(name)}-opacity`)), `--${kebab(name)}-opacity`).toBe(alpha);
        }
        // And the other way: every color the stylesheet declares is in the palette.
        const hexes = [...declared.entries()].filter(([, value]) => value.startsWith("#"));
        const known = new Set(Object.keys(palette).map(kebab));
        for (const [name] of hexes) {
            expect(known.has(name), `--${name} is not in palette.ts`).toBe(true);
        }
    });

    it("uses no color literal outside the custom properties", () => {
        const body = css.replace(/--[a-z-]+:\s*#[0-9a-f]{6};/gi, "").replace(/\/\*[\s\S]*?\*\//g, "");
        expect(body.match(/#[0-9a-f]{3,8}\b/gi) ?? []).toEqual([]);
    });
});
