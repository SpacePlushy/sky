// The ground track, cut into runs that share a style: past or future, sunlit or in shadow.
//
// Each run is a GeoJSON LineString on the sphere; d3.geoPath cuts it at the antimeridian, so this
// code never splits by longitude. A style belongs to the stretch between two samples and is the
// style of the stretch's first sample: sample and hold, since the flags are known only at the
// samples. Adjacent runs share their boundary point, so the drawn line has no gaps.

import type { Feature, LineString } from "geojson";

export interface TrackSample {
    readonly t: number;
    readonly latitudeDeg: number;
    readonly longitudeDeg: number;
    readonly sunlit: boolean;
}

export interface TrackSegment {
    readonly past: boolean;
    readonly sunlit: boolean;
    readonly points: readonly TrackSample[];
}

/**
 * Splits a track at `nowMs` and wherever `sunlit` changes. A sample before `nowMs` is past; one at
 * or after it is future. When `nowPoint` is given (the satellite's position at `nowMs`, from
 * /now) and falls inside the track, it is added as a sample, so the past and future runs meet
 * exactly at the satellite. Samples are sorted by time, and a repeated time keeps its first sample.
 */
export function segmentTrack(points: readonly TrackSample[], nowMs: number, nowPoint?: TrackSample): TrackSegment[] {
    const samples = [...points].sort((a, b) => a.t - b.t);
    const first = samples[0];
    const last = samples[samples.length - 1];
    if (nowPoint !== undefined && first !== undefined && last !== undefined && nowPoint.t >= first.t && nowPoint.t <= last.t) {
        samples.push(nowPoint);
        samples.sort((a, b) => a.t - b.t);
    }
    const unique = samples.filter((s, i) => i === 0 || s.t !== samples[i - 1]?.t);

    if (unique.length === 0) {
        return [];
    }
    if (unique.length === 1) {
        const only = unique[0];
        return only === undefined ? [] : [{ past: only.t < nowMs, sunlit: only.sunlit, points: [only] }];
    }

    const segments: { past: boolean; sunlit: boolean; points: TrackSample[] }[] = [];
    for (let i = 0; i + 1 < unique.length; i++) {
        const start = unique[i];
        const end = unique[i + 1];
        if (start === undefined || end === undefined) {
            continue;
        }
        const past = start.t < nowMs;
        const current = segments[segments.length - 1];
        if (current?.past === past && current.sunlit === start.sunlit) {
            current.points.push(end);
        } else {
            segments.push({ past, sunlit: start.sunlit, points: [start, end] });
        }
    }
    return segments;
}

/** A run as a GeoJSON LineString, [longitude, latitude] per point, for d3.geoPath. */
export function segmentFeature(segment: TrackSegment): Feature<LineString, { past: boolean; sunlit: boolean }> {
    return {
        type: "Feature",
        properties: { past: segment.past, sunlit: segment.sunlit },
        geometry: { type: "LineString", coordinates: segment.points.map((p) => [p.longitudeDeg, p.latitudeDeg]) },
    };
}
