// Number and angle formatting. Negative numbers use the minus sign (U+2212), which screen readers
// read as "minus" and which is as wide as a digit in tabular figures.

export const minus = "−";

/** A fixed number of decimals, with a real minus sign and never a negative zero. */
export function fixed(value: number, digits: number): string {
    if (!Number.isFinite(value)) {
        return "—";
    }
    const text = value.toFixed(digits);
    const isZero = Number(text) === 0;
    if (isZero) {
        return text.replace("-", "");
    }
    return text.replace("-", minus);
}

/** Always signed: +3.215, −3.215, or 0.000. */
export function signed(value: number, digits: number): string {
    const text = fixed(value, digits);
    return text.startsWith(minus) || Number(value.toFixed(digits)) === 0 ? text : `+${text}`;
}

const groupedFormats = new Map<number, Intl.NumberFormat>();

/** With thousands separators: 2,345.6. */
export function grouped(value: number, digits: number): string {
    if (!Number.isFinite(value)) {
        return "—";
    }
    let format = groupedFormats.get(digits);
    if (format === undefined) {
        format = new Intl.NumberFormat("en-US", { minimumFractionDigits: digits, maximumFractionDigits: digits, numberingSystem: "latn" });
        groupedFormats.set(digits, format);
    }
    const text = format.format(value);
    return Number(value.toFixed(digits)) === 0 ? text.replace("-", "") : text.replace("-", minus);
}

/** An angle in [0, 360). */
export function normalizeDegrees(degrees: number): number {
    const d = degrees % 360;
    return d < 0 ? d + 360 : d;
}

/** A longitude in [-180, 180). */
export function normalizeLongitude(degrees: number): number {
    return normalizeDegrees(degrees + 180) - 180;
}

/** 33.45° N */
export function latitude(degrees: number, digits = 2): string {
    const text = fixed(Math.abs(degrees), digits);
    const hemisphere = Number(text) === 0 ? "" : degrees > 0 ? " N" : " S";
    return `${text}°${hemisphere}`;
}

/** 112.10° W */
export function longitude(degrees: number, digits = 2): string {
    const lon = normalizeLongitude(degrees);
    const text = fixed(Math.abs(lon), digits);
    const value = Number(text);
    const hemisphere = value === 0 || value === 180 ? "" : lon > 0 ? " E" : " W";
    return `${text}°${hemisphere}`;
}

const compassPoints = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"] as const;

/** The nearest of 16 compass points to an azimuth (degrees clockwise from north). */
export function compassPoint(azimuth: number): string {
    const index = Math.round(normalizeDegrees(azimuth) / 22.5) % 16;
    return compassPoints[index] ?? "N";
}

/** NNW 338° */
export function azimuthLabel(azimuth: number): string {
    const degrees = Math.round(normalizeDegrees(azimuth)) % 360;
    return `${compassPoint(azimuth)} ${degrees}°`;
}
