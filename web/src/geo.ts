// Spherical shapes for the map, built with d3-geo so that the projection, not this code, cuts them
// at the antimeridian and wraps them around the poles. Every shape is a GeoJSON polygon on the
// sphere; none is a circle drawn on the flat map.
//
// These are for display. Rises, sets, and visibility come from the API's ellipsoidal core; the map
// uses a sphere (the API's footprint radii are central angles on the mean-radius sphere).

import { geoCircle } from "d3-geo";
import type { Polygon } from "geojson";
import { normalizeLongitude } from "./format";

/** d3-geo's vertex spacing for circles, in degrees: 360 vertices around a circle. */
const circlePrecision = 1;

/** The Sun's elevation below which the sky counts as dark enough to see a satellite. */
export const twilightSunElevationDeg = -6;

/** [longitude, latitude] of the point opposite the subsolar point: the middle of the night side. */
export function antisolarPoint(subsolarLatDeg: number, subsolarLonDeg: number): [number, number] {
    return [normalizeLongitude(subsolarLonDeg + 180), -subsolarLatDeg];
}

/**
 * Where the Sun is below the horizon: every point more than 90° of arc from the subsolar point,
 * which is the hemisphere within 90° of the antisolar point. A 90° circle is a great circle, so
 * the polygon's edges lie on the terminator exactly.
 */
export function nightPolygon(subsolarLatDeg: number, subsolarLonDeg: number): Polygon {
    return geoCircle().center(antisolarPoint(subsolarLatDeg, subsolarLonDeg)).radius(90).precision(circlePrecision)();
}

/**
 * Where the Sun is more than 6° below the horizon, dark enough to see satellites: more than 96° of
 * arc from the subsolar point, so within 84° of the antisolar point. Geocentric and without
 * refraction; the API decides visibility, this only shades the map.
 */
export function darkPolygon(subsolarLatDeg: number, subsolarLonDeg: number): Polygon {
    return geoCircle()
        .center(antisolarPoint(subsolarLatDeg, subsolarLonDeg))
        .radius(90 + twilightSunElevationDeg)
        .precision(circlePrecision)();
}

/**
 * The ground within `radiusDeg` of arc of the subpoint: from the API's footprintRadiusDeg (where the
 * satellite is above the horizon) or visibilityRadiusDeg (above the minimum elevation).
 */
export function footprintPolygon(latDeg: number, lonDeg: number, radiusDeg: number): Polygon {
    return geoCircle().center([lonDeg, latDeg]).radius(radiusDeg).precision(circlePrecision)();
}
