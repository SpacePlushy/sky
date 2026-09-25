// The world map: an equirectangular projection fitted to the panel, drawn as SVG. Land is Natural
// Earth's 1:110m outline (public domain) from the world-atlas package, bundled with the page, so
// the map needs no tiles, keys, or network. Everything spherical (night, footprints, track) is a
// GeoJSON shape that d3.geoPath projects and cuts at the antimeridian.

import { geoEquirectangular, geoGraticule, geoPath, type GeoPath, type GeoPermissibleObjects } from "d3-geo";
import type { FeatureCollection } from "geojson";
import { feature } from "topojson-client";
import type { GeometryCollection, Topology } from "topojson-specification";
import landTopology from "world-atlas/land-110m.json";
import { clear, svg } from "./dom";
import { latitude, longitude, fixed } from "./format";
import { darkPolygon, footprintPolygon, nightPolygon } from "./geo";
import type { Now, TrackPoint } from "./model";
import { segmentFeature, segmentTrack } from "./track";

type LandTopology = Topology<{ land: GeometryCollection }>;

function isLandTopology(value: unknown): value is LandTopology {
    if (typeof value !== "object" || value === null || !("type" in value) || value.type !== "Topology" || !("objects" in value)) {
        return false;
    }
    const objects = value.objects;
    return typeof objects === "object" && objects !== null && "land" in objects &&
        typeof objects.land === "object" && objects.land !== null && "type" in objects.land && objects.land.type === "GeometryCollection";
}

let land: FeatureCollection | undefined;

function landFeature(): FeatureCollection {
    if (land === undefined) {
        const topology: unknown = landTopology;
        if (!isLandTopology(topology)) {
            throw new Error("world-atlas/land-110m.json is not the expected TopoJSON");
        }
        land = feature(topology, topology.objects.land);
    }
    return land;
}

export interface MapView {
    readonly observer: { readonly latitudeDeg: number; readonly longitudeDeg: number } | null;
    readonly now: Now | null;
    readonly track: readonly TrackPoint[] | null;
}

const sphere: GeoPermissibleObjects = { type: "Sphere" };

export class WorldMap {
    private width = 960;
    private height = 480;
    private readonly projection = geoEquirectangular();
    private readonly path: GeoPath = geoPath(this.projection).digits(1);
    private view: MapView = { observer: null, now: null, track: null };

    private readonly root: SVGSVGElement;
    private readonly desc: SVGDescElement;
    private readonly ocean = svg("path", { class: "map-ocean" });
    private readonly graticule = svg("path", { class: "map-graticule" });
    private readonly land = svg("path", { class: "map-land" });
    private readonly night = svg("path", { class: "map-night" });
    private readonly dark = svg("path", { class: "map-dark" });
    private readonly coast = svg("path", { class: "map-coast" });
    private readonly horizon = svg("path", { class: "map-footprint-horizon" });
    private readonly footprint = svg("path", { class: "map-footprint" });
    private readonly track = svg("g", { class: "map-track" });
    private readonly observer = svg("g", { class: "map-observer" });
    private readonly satellite = svg("g", { class: "map-satellite" });
    private readonly outline = svg("path", { class: "map-outline" });

    constructor(container: HTMLElement) {
        this.root = svg("svg", { role: "img", class: "map", "aria-labelledby": "map-title map-desc", preserveAspectRatio: "xMidYMid meet" });
        const title = svg("title", { id: "map-title" });
        title.textContent = "World map with the satellite's ground track";
        this.desc = svg("desc", { id: "map-desc" });
        this.root.append(
            title, this.desc,
            this.ocean, this.graticule, this.land, this.night, this.dark, this.coast,
            this.horizon, this.footprint, this.track, this.observer, this.satellite, this.outline,
        );
        container.append(this.root);

        this.resize(container.clientWidth > 0 ? container.clientWidth : 960);
        if (typeof ResizeObserver !== "undefined") {
            let pending = 0;
            new ResizeObserver((entries) => {
                const width = entries[0]?.contentRect.width;
                if (width === undefined || width <= 0) {
                    return;
                }
                cancelAnimationFrame(pending);
                pending = requestAnimationFrame(() => {
                    this.resize(width);
                });
            }).observe(container);
        }
    }

    /** Fits the projection to the panel's width in CSS pixels, so strokes and markers keep their size. */
    private resize(width: number): void {
        const w = Math.max(240, Math.round(width));
        const h = Math.round(w / 2);
        if (w === this.width && h === this.height && this.land.hasAttribute("d")) {
            return;
        }
        this.width = w;
        this.height = h;
        this.root.setAttribute("viewBox", `0 0 ${w} ${h}`);
        this.projection.fitExtent([[1, 1], [w - 1, h - 1]], sphere);
        this.ocean.setAttribute("d", this.path(sphere) ?? "");
        this.outline.setAttribute("d", this.path(sphere) ?? "");
        this.graticule.setAttribute("d", this.path(geoGraticule().step([30, 30])()) ?? "");
        const landPath = this.path(landFeature()) ?? "";
        this.land.setAttribute("d", landPath);
        this.coast.setAttribute("d", landPath);
        this.draw();
    }

    update(view: MapView): void {
        this.view = view;
        this.draw();
    }

    private point(latitudeDeg: number, longitudeDeg: number): [number, number] | null {
        return this.projection([longitudeDeg, latitudeDeg]);
    }

    private draw(): void {
        const { now, track, observer } = this.view;

        if (now === null) {
            for (const layer of [this.night, this.dark, this.horizon, this.footprint]) {
                layer.removeAttribute("d");
            }
            clear(this.satellite);
        } else {
            const { subsolarLatitudeDeg: sunLat, subsolarLongitudeDeg: sunLon } = now.sun;
            const { latitudeDeg: lat, longitudeDeg: lon } = now.position;
            this.night.setAttribute("d", this.path(nightPolygon(sunLat, sunLon)) ?? "");
            this.dark.setAttribute("d", this.path(darkPolygon(sunLat, sunLon)) ?? "");
            this.horizon.setAttribute("d", this.path(footprintPolygon(lat, lon, now.footprintRadiusDeg)) ?? "");
            this.footprint.setAttribute("d", this.path(footprintPolygon(lat, lon, now.visibilityRadiusDeg)) ?? "");

            clear(this.satellite);
            const p = this.point(lat, lon);
            if (p !== null) {
                this.satellite.append(
                    svg("circle", { cx: p[0], cy: p[1], r: 11, class: "map-satellite-halo" }),
                    svg("circle", { cx: p[0], cy: p[1], r: 5.5, class: "map-satellite-dot" }),
                );
            }
        }

        clear(this.track);
        if (track !== null && now !== null) {
            const nowPoint = { t: now.t, latitudeDeg: now.position.latitudeDeg, longitudeDeg: now.position.longitudeDeg, sunlit: now.sunlit };
            // Past first, so the future line is drawn on top where an orbit crosses itself.
            const segments = segmentTrack(track, now.t, nowPoint).sort((a, b) => Number(b.past) - Number(a.past));
            for (const segment of segments) {
                const d = this.path(segmentFeature(segment));
                if (d !== null) {
                    const classes = ["map-track-line", segment.past ? "is-past" : "is-future", segment.sunlit ? "is-sunlit" : "is-shadow"];
                    this.track.append(svg("path", { d, class: classes.join(" ") }));
                }
            }
        }

        clear(this.observer);
        if (observer !== null) {
            const p = this.point(observer.latitudeDeg, observer.longitudeDeg);
            if (p !== null) {
                const [x, y] = p;
                this.observer.append(svg("path", { d: `M${x},${y - 7}L${x + 7},${y}L${x},${y + 7}L${x - 7},${y}Z`, class: "map-observer-mark" }));
            }
        }

        this.desc.textContent = now === null
            ? "Waiting for the satellite's position."
            : `${now.satellite.name} is over ${latitude(now.position.latitudeDeg)}, ${longitude(now.position.longitudeDeg)} at ` +
              `${fixed(now.position.altitudeKm, 0)} km, ${now.sunlit ? "in sunlight" : "in the Earth's shadow"}. ` +
              "The track runs one orbit back and one ahead; the shaded region is night.";
    }
}
