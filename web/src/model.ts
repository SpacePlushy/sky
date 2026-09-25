// The API's JSON (src/Sky.Api/ApiModels.cs, camel case) read into the page's own types. Instants
// become milliseconds since the epoch here, at the boundary, and every field is checked, so a
// contract change fails loudly with the field's name instead of drawing NaN.

import { parseUtc } from "./time";

export interface Observer {
    readonly name: string;
    readonly latitudeDeg: number;
    readonly longitudeDeg: number;
    readonly heightM: number;
    readonly timeZone: string;
}

export interface Config {
    readonly observer: Observer;
    readonly minimumElevationDeg: number;
    readonly satellites: readonly number[];
    readonly offline: boolean;
    readonly clockSimulated: boolean;
    /** When the simulated clock started, if it is simulated. */
    readonly clockStart: number | null;
    readonly serverTime: number;
}

export interface SatelliteSummary {
    readonly id: number;
    readonly name: string;
    readonly group: string;
    readonly epoch: number;
    readonly ageDays: number;
    readonly featured: boolean;
}

/** A moment in a pass: when, and where the satellite appears. */
export interface PassEvent {
    readonly t: number;
    readonly azimuthDeg: number;
    readonly elevationDeg: number;
}

/** A visible part of a pass. The reasons are the API's strings, such as "entersShadow". */
export interface VisiblePart {
    readonly start: PassEvent;
    readonly startsBecause: string;
    readonly highest: PassEvent;
    readonly end: PassEvent;
    readonly endsBecause: string;
}

/** A sky-plot path point: every 10 s from rise, plus set and each visible part's ends. */
export interface SkyPoint extends PassEvent {
    readonly sunlit: boolean;
}

export interface Pass {
    readonly rise: PassEvent;
    readonly culmination: PassEvent;
    readonly set: PassEvent;
    readonly peakUncertaintyDeg: number;
    readonly visible: readonly VisiblePart[];
    readonly path: readonly SkyPoint[];
}

export interface Now {
    readonly t: number;
    readonly satellite: { readonly id: number; readonly name: string; readonly epoch: number; readonly ageDays: number; readonly periodMinutes: number };
    readonly position: { readonly latitudeDeg: number; readonly longitudeDeg: number; readonly altitudeKm: number; readonly inertialSpeedKmS: number };
    readonly look: { readonly azimuthDeg: number; readonly elevationDeg: number; readonly rangeKm: number; readonly rangeRateKmS: number };
    readonly sunlit: boolean;
    readonly sun: { readonly elevationDeg: number; readonly subsolarLatitudeDeg: number; readonly subsolarLongitudeDeg: number };
    readonly footprintRadiusDeg: number;
    readonly visibilityRadiusDeg: number;
    readonly currentPass: Pass | null;
    readonly warnings: readonly string[];
}

export interface TrackPoint {
    readonly t: number;
    readonly latitudeDeg: number;
    readonly longitudeDeg: number;
    readonly sunlit: boolean;
}

export interface Track {
    readonly from: number;
    readonly to: number;
    readonly stepSeconds: number;
    readonly points: readonly TrackPoint[];
    readonly warnings: readonly string[];
}

export interface Passes {
    readonly from: number;
    readonly to: number;
    readonly minimumElevationDeg: number;
    readonly passes: readonly Pass[];
    readonly aboveMinimumAtStartSince: number | null;
    readonly aboveMinimumAtEndUntil: number | null;
    readonly stoppedBy: string | null;
    readonly stoppedAt: number | null;
    readonly warnings: readonly string[];
}

/** The response did not have the shape the page expects. */
export class ContractError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "ContractError";
    }
}

type Json = Record<string, unknown>;

function object(value: unknown, path: string): Json {
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
        throw new ContractError(`${path} is not an object`);
    }
    return value as Json;
}

function number(o: Json, key: string, path: string): number {
    const value = o[key];
    if (typeof value !== "number" || !Number.isFinite(value)) {
        throw new ContractError(`${path}.${key} is not a finite number`);
    }
    return value;
}

function string(o: Json, key: string, path: string): string {
    const value = o[key];
    if (typeof value !== "string") {
        throw new ContractError(`${path}.${key} is not a string`);
    }
    return value;
}

function boolean(o: Json, key: string, path: string): boolean {
    const value = o[key];
    if (typeof value !== "boolean") {
        throw new ContractError(`${path}.${key} is not a boolean`);
    }
    return value;
}

function instant(o: Json, key: string, path: string): number {
    const text = string(o, key, path);
    try {
        return parseUtc(text);
    } catch {
        throw new ContractError(`${path}.${key} is not a UTC instant: ${text}`);
    }
}

function optionalInstant(o: Json, key: string, path: string): number | null {
    return o[key] === null || o[key] === undefined ? null : instant(o, key, path);
}

function optionalString(o: Json, key: string, path: string): string | null {
    return o[key] === null || o[key] === undefined ? null : string(o, key, path);
}

function array<T>(o: Json, key: string, path: string, item: (value: unknown, path: string) => T): T[] {
    const value = o[key];
    if (!Array.isArray(value)) {
        throw new ContractError(`${path}.${key} is not an array`);
    }
    return value.map((v: unknown, i) => item(v, `${path}.${key}[${i}]`));
}

function strings(o: Json, key: string, path: string): string[] {
    return array(o, key, path, (v, p) => {
        if (typeof v !== "string") {
            throw new ContractError(`${p} is not a string`);
        }
        return v;
    });
}

export function readConfig(value: unknown): Config {
    const o = object(value, "config");
    const obs = object(o.observer, "config.observer");
    return {
        observer: {
            name: string(obs, "name", "config.observer"),
            latitudeDeg: number(obs, "latitudeDeg", "config.observer"),
            longitudeDeg: number(obs, "longitudeDeg", "config.observer"),
            heightM: number(obs, "heightM", "config.observer"),
            timeZone: string(obs, "timeZone", "config.observer"),
        },
        minimumElevationDeg: number(o, "minimumElevationDeg", "config"),
        satellites: array(o, "satellites", "config", (v, p) => {
            if (typeof v !== "number" || !Number.isInteger(v)) {
                throw new ContractError(`${p} is not a catalog number`);
            }
            return v;
        }),
        offline: boolean(o, "offline", "config"),
        clockSimulated: boolean(o, "clockSimulated", "config"),
        clockStart: optionalInstant(o, "clockStartUtc", "config"),
        serverTime: instant(o, "serverTimeUtc", "config"),
    };
}

export function readSatellites(value: unknown): SatelliteSummary[] {
    if (!Array.isArray(value)) {
        throw new ContractError("satellites is not an array");
    }
    return value.map((v: unknown, i) => {
        const path = `satellites[${i}]`;
        const o = object(v, path);
        return {
            id: number(o, "id", path),
            name: string(o, "name", path),
            group: string(o, "group", path),
            epoch: instant(o, "epochUtc", path),
            ageDays: number(o, "ageDays", path),
            featured: boolean(o, "featured", path),
        };
    });
}

function readEvent(value: unknown, path: string): PassEvent {
    const o = object(value, path);
    return { t: instant(o, "timeUtc", path), azimuthDeg: number(o, "azimuthDeg", path), elevationDeg: number(o, "elevationDeg", path) };
}

function readPass(value: unknown, path: string): Pass {
    const o = object(value, path);
    return {
        rise: readEvent(o.rise, `${path}.rise`),
        culmination: readEvent(o.culmination, `${path}.culmination`),
        set: readEvent(o.set, `${path}.set`),
        peakUncertaintyDeg: number(o, "peakUncertaintyDeg", path),
        visible: array(o, "visible", path, (v, p) => {
            const w = object(v, p);
            return {
                start: readEvent(w.start, `${p}.start`),
                startsBecause: string(w, "startsBecause", p),
                highest: readEvent(w.highest, `${p}.highest`),
                end: readEvent(w.end, `${p}.end`),
                endsBecause: string(w, "endsBecause", p),
            };
        }),
        path: array(o, "path", path, (v, p) => {
            const s = object(v, p);
            return { ...readEvent(s, p), sunlit: boolean(s, "sunlit", p) };
        }),
    };
}

export function readNow(value: unknown): Now {
    const path = "now";
    const o = object(value, path);
    const sat = object(o.satellite, `${path}.satellite`);
    const pos = object(o.position, `${path}.position`);
    const look = object(o.look, `${path}.look`);
    const sun = object(o.sun, `${path}.sun`);
    return {
        t: instant(o, "timeUtc", path),
        satellite: {
            id: number(sat, "id", `${path}.satellite`),
            name: string(sat, "name", `${path}.satellite`),
            epoch: instant(sat, "epochUtc", `${path}.satellite`),
            ageDays: number(sat, "ageDays", `${path}.satellite`),
            periodMinutes: number(sat, "periodMinutes", `${path}.satellite`),
        },
        position: {
            latitudeDeg: number(pos, "latitudeDeg", `${path}.position`),
            longitudeDeg: number(pos, "longitudeDeg", `${path}.position`),
            altitudeKm: number(pos, "altitudeKm", `${path}.position`),
            inertialSpeedKmS: number(pos, "inertialSpeedKmS", `${path}.position`),
        },
        look: {
            azimuthDeg: number(look, "azimuthDeg", `${path}.look`),
            elevationDeg: number(look, "elevationDeg", `${path}.look`),
            rangeKm: number(look, "rangeKm", `${path}.look`),
            rangeRateKmS: number(look, "rangeRateKmS", `${path}.look`),
        },
        sunlit: boolean(o, "sunlit", path),
        sun: {
            elevationDeg: number(sun, "elevationDeg", `${path}.sun`),
            subsolarLatitudeDeg: number(sun, "subsolarLatitudeDeg", `${path}.sun`),
            subsolarLongitudeDeg: number(sun, "subsolarLongitudeDeg", `${path}.sun`),
        },
        footprintRadiusDeg: number(o, "footprintRadiusDeg", path),
        visibilityRadiusDeg: number(o, "visibilityRadiusDeg", path),
        currentPass: o.currentPass === null || o.currentPass === undefined ? null : readPass(o.currentPass, `${path}.currentPass`),
        warnings: strings(o, "warnings", path),
    };
}

export function readTrack(value: unknown): Track {
    const path = "track";
    const o = object(value, path);
    return {
        from: instant(o, "fromUtc", path),
        to: instant(o, "toUtc", path),
        stepSeconds: number(o, "stepSeconds", path),
        points: array(o, "points", path, (v, p) => {
            const s = object(v, p);
            return {
                t: instant(s, "timeUtc", p),
                latitudeDeg: number(s, "latitudeDeg", p),
                longitudeDeg: number(s, "longitudeDeg", p),
                sunlit: boolean(s, "sunlit", p),
            };
        }),
        warnings: strings(o, "warnings", path),
    };
}

export function readPasses(value: unknown): Passes {
    const path = "passes";
    const o = object(value, path);
    return {
        from: instant(o, "fromUtc", path),
        to: instant(o, "toUtc", path),
        minimumElevationDeg: number(o, "minimumElevationDeg", path),
        passes: array(o, "passes", path, readPass),
        aboveMinimumAtStartSince: optionalInstant(o, "aboveMinimumAtStartSinceUtc", path),
        aboveMinimumAtEndUntil: optionalInstant(o, "aboveMinimumAtEndUntilUtc", path),
        stoppedBy: optionalString(o, "stoppedBy", path),
        stoppedAt: optionalInstant(o, "stoppedAtUtc", path),
        warnings: strings(o, "warnings", path),
    };
}
