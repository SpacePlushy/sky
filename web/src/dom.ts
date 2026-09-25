// Small DOM helpers. Text always goes in through textContent, never as HTML, so nothing the API
// sends (satellite names, warnings, error details) can inject markup.

const svgNamespace = "http://www.w3.org/2000/svg";

type Attributes = Readonly<Record<string, string | number>>;

/** An SVG element with attributes. */
export function svg<K extends keyof SVGElementTagNameMap>(tag: K, attributes: Attributes = {}): SVGElementTagNameMap[K] {
    const element = document.createElementNS(svgNamespace, tag);
    for (const [name, value] of Object.entries(attributes)) {
        element.setAttribute(name, String(value));
    }
    return element;
}

/** An HTML element with attributes and children (strings become text). */
export function h<K extends keyof HTMLElementTagNameMap>(tag: K, attributes: Attributes = {}, ...children: (Node | string)[]): HTMLElementTagNameMap[K] {
    const element = document.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) {
        element.setAttribute(name, String(value));
    }
    element.append(...children);
    return element;
}

export function clear(node: Element): void {
    node.replaceChildren();
}

/** Sets text only when it changed, so screen readers and layout are not disturbed every second. */
export function setText(node: Element, text: string): void {
    if (node.textContent !== text) {
        node.textContent = text;
    }
}

/** The element with an id, which the page's HTML must contain. */
export function byId<T extends HTMLElement>(id: string, type: new () => T): T {
    const element = document.getElementById(id);
    if (!(element instanceof type)) {
        throw new Error(`index.html has no ${type.name} #${id}`);
    }
    return element;
}
