// The dashboard's colors. styles.css declares the same values as CSS custom properties (a test
// checks the two agree), and palette.test.ts checks every pair below against its WCAG 2.x
// contrast minimum. Color never carries meaning alone: every colored state also has text, a
// dash pattern, a shape, or a line width.

/** Every color, as #rrggbb. The CSS name is the key in kebab case: `mutedText` is `--muted-text`. */
export const palette = {
    // Page and panels
    bg: "#05070b",
    panel: "#0b1017",
    raised: "#131b26",
    border: "#1f2b39",

    // Text
    text: "#e6edf3",
    muted: "#9aabbd",

    // The two accents: live data, and visible passes
    accent: "#3dd3ec",
    visible: "#f5b942",
    visibleBg: "#211906",

    // Banners
    noticeText: "#9ddcf2",
    noticeBg: "#08202a",
    warnText: "#ffcf70",
    warnBg: "#241a05",
    errorText: "#ffa198",
    errorBg: "#2a0e0e",

    // Map
    ocean: "#070c13",
    land: "#1a2531",
    landEdge: "#5b7490",
    graticule: "#1b2735",
    trackPast: "#7aa7b8",
    trackShadow: "#99a9ff",
    observer: "#f2f5f8",
    shade: "#000000",

    // Sky plot
    skyBg: "#0d151f",
    skyRing: "#27384a",
    skyRim: "#6f8398",
    skyPath: "#b7c4d1",
} as const;

export type ColorName = keyof typeof palette;

/**
 * Translucent layers on the map, as alpha over whatever lies below. Night layers are black; the
 * footprint fill is the accent. styles.css declares them as `--night-opacity` and so on.
 */
export const opacity = {
    night: 0.5,
    dark: 0.35,
    footprintFill: 0.1,
} as const;

export type OpacityName = keyof typeof opacity;

/** A background that is a blend: `color` at `alpha` over `under`. */
export interface Blend {
    readonly color: ColorName;
    readonly alpha: OpacityName;
    readonly under: ColorName;
}

export interface ContrastPair {
    readonly fg: ColorName;
    readonly bg: ColorName | Blend;
    /** 4.5 for text, 3 for large text and for graphics (WCAG 2.x SC 1.4.3 and 1.4.11). */
    readonly min: 4.5 | 3;
    readonly use: string;
}

// The map's lightest backgrounds: the footprint's accent tint over ocean and over land. The night
// layers only darken what is under them, so light lines have more contrast there, and the
// coastline is drawn above the night layers.
const oceanInFootprint: Blend = { color: "accent", alpha: "footprintFill", under: "ocean" };
const landInFootprint: Blend = { color: "accent", alpha: "footprintFill", under: "land" };
const mapBackgrounds: readonly (ColorName | Blend)[] = ["ocean", "land", oceanInFootprint, landInFootprint];

function onMap(fg: ColorName, use: string): ContrastPair[] {
    return mapBackgrounds.map((bg) => ({ fg, bg, min: 3, use }));
}

/** Every foreground and background the page puts together, with the ratio each must meet. */
export const contrastPairs: readonly ContrastPair[] = [
    // Text on the page, panels, and raised rows
    { fg: "text", bg: "bg", min: 4.5, use: "body text" },
    { fg: "text", bg: "panel", min: 4.5, use: "panel text" },
    { fg: "text", bg: "raised", min: 4.5, use: "selected or hovered pass row" },
    { fg: "muted", bg: "bg", min: 4.5, use: "footer and secondary text" },
    { fg: "muted", bg: "panel", min: 4.5, use: "labels" },
    { fg: "muted", bg: "raised", min: 4.5, use: "labels in a selected row" },
    { fg: "accent", bg: "bg", min: 4.5, use: "live values in the header" },
    { fg: "accent", bg: "panel", min: 4.5, use: "live telemetry values" },
    { fg: "accent", bg: "raised", min: 4.5, use: "in-progress label in a selected row" },
    { fg: "visible", bg: "panel", min: 4.5, use: "visible-pass text" },
    { fg: "visible", bg: "raised", min: 4.5, use: "visible-pass text in a selected row" },
    { fg: "visible", bg: "visibleBg", min: 4.5, use: "visible-pass text on its row tint" },
    { fg: "text", bg: "visibleBg", min: 4.5, use: "text in a visible-pass row" },
    { fg: "muted", bg: "visibleBg", min: 4.5, use: "labels in a visible-pass row" },
    { fg: "bg", bg: "visible", min: 4.5, use: "VISIBLE badge" },
    { fg: "bg", bg: "accent", min: 4.5, use: "IN PROGRESS badge" },
    { fg: "accent", bg: "raised", min: 4.5, use: "PLOTTED badge on the selected row" },

    // Banners
    { fg: "noticeText", bg: "noticeBg", min: 4.5, use: "offline and simulated-clock notice" },
    { fg: "warnText", bg: "warnBg", min: 4.5, use: "API warnings" },
    { fg: "errorText", bg: "errorBg", min: 4.5, use: "API errors" },
    { fg: "text", bg: "noticeBg", min: 4.5, use: "notice body text" },
    { fg: "text", bg: "warnBg", min: 4.5, use: "warning body text" },
    { fg: "text", bg: "errorBg", min: 4.5, use: "error body text" },

    // Controls
    { fg: "accent", bg: "panel", min: 3, use: "focus ring and selected-row bar" },
    { fg: "accent", bg: "raised", min: 3, use: "focus ring on a selected row" },
    { fg: "accent", bg: "bg", min: 3, use: "focus ring on the page" },
    { fg: "muted", bg: "panel", min: 3, use: "select box, button, and alert-state borders" },
    { fg: "text", bg: "raised", min: 4.5, use: "button text" },
    { fg: "muted", bg: "raised", min: 4.5, use: "disabled button text" },

    // Pass tools
    { fg: "accent", bg: "panel", min: 4.5, use: "calendar link and ON alert state" },
    { fg: "muted", bg: "panel", min: 4.5, use: "OFF alert state" },
    { fg: "errorText", bg: "panel", min: 4.5, use: "BLOCKED and UNAVAILABLE alert states" },

    // Map graphics
    ...onMap("accent", "future track in sunlight, satellite, visibility footprint"),
    ...onMap("trackShadow", "future track in shadow"),
    ...onMap("trackPast", "past track"),
    ...onMap("observer", "observer marker"),
    ...onMap("muted", "horizon footprint"),
    { fg: "landEdge", bg: "ocean", min: 3, use: "coastline" },
    { fg: "landEdge", bg: oceanInFootprint, min: 3, use: "coastline inside the footprint" },

    // Sky plot graphics and labels
    { fg: "skyPath", bg: "skyBg", min: 3, use: "pass path" },
    { fg: "visible", bg: "skyBg", min: 3, use: "visible part of the path" },
    { fg: "accent", bg: "skyBg", min: 3, use: "satellite now" },
    { fg: "skyRim", bg: "panel", min: 3, use: "horizon circle against the panel" },
    { fg: "skyRim", bg: "skyBg", min: 3, use: "horizon circle against the sky" },
    { fg: "muted", bg: "skyBg", min: 4.5, use: "elevation ring labels" },
    { fg: "text", bg: "skyBg", min: 4.5, use: "peak elevation label" },
    { fg: "text", bg: "panel", min: 4.5, use: "compass labels" },
];
