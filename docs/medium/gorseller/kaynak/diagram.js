// Small helpers for the article diagrams: services are white boxes, queues grey, Kasa.Api green.
// Plain scripts, not modules, so a diagram's .html opens straight from disk; the PNGs next to this folder are 2x screenshots of them.
const NS = "http://www.w3.org/2000/svg";
const TONES = {
  ink: { line: "#171717", text: "#171717" },
  green: { line: "#0f5c4c", text: "#0f5c4c" },
  grey: { line: "#a3a3a3", text: "#525252" },
  amber: { line: "#d99a06", text: "#8a5a00" },
  red: { line: "#dc2626", text: "#b42318" },
};
const KINDS = {
  service: { fill: "#ffffff", stroke: "rgba(23,23,23,0.18)" },
  main: { fill: "#eef6f2", stroke: "#0f5c4c" },
  queue: { fill: "#f4f4f5", stroke: "rgba(23,23,23,0.10)" },
  retry: { fill: "#fdf6e3", stroke: "#e8c26a" },
  dead: { fill: "#fdeeec", stroke: "#f0a8a0" },
  container: { fill: "#f7faf9", stroke: "#0f5c4c" },
};

function el(tag, attrs, parent, text) {
  const node = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  if (text != null) node.textContent = text;
  parent.appendChild(node);
  return node;
}

function diagram(width, height) {
  const svg = el("svg", { viewBox: `0 0 ${width} ${height}`, width, height }, document.querySelector(".frame"));
  const defs = el("defs", {}, svg);
  for (const [name, t] of Object.entries(TONES)) {
    const m = el("marker", { id: `head-${name}`, viewBox: "0 0 10 10", refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: "auto-start-reverse" }, defs);
    el("path", { d: "M0,0 L10,5 L0,10 z", fill: t.line }, m);
  }
  return svg;
}

/** A box with a title and up to a few lines under it, centred vertically. Queues get a mono title. */
function box(svg, { x, y, w, h, title, lines = [], kind = "service", mono = kind === "queue" || kind === "retry" || kind === "dead", dashed = false, monoLines = false }) {
  const k = KINDS[kind];
  el("rect", { x, y, width: w, height: h, rx: 10, fill: k.fill, stroke: k.stroke, "stroke-width": kind === "main" ? 1.5 : 1, "stroke-dasharray": dashed ? "5 4" : "none" }, svg);
  const total = 19 + lines.length * 18;
  let ty = y + (h - total) / 2 + 15;
  el("text", { x: x + 16, y: ty, class: mono ? "title mono" : "title" }, svg, title);
  for (const line of lines) {
    ty += 18;
    el("text", { x: x + 16, y: ty, class: monoLines ? "sub mono" : "sub" }, svg, line);
  }
}

/** A line through the given points, with an arrowhead at the end (and at the start when both). */
function arrow(svg, points, tone = "green", { both = false, dashed = false } = {}) {
  el("path", {
    d: points.map(([px, py], i) => `${i ? "L" : "M"}${px},${py}`).join(" "),
    fill: "none", stroke: TONES[tone].line, "stroke-width": tone === "green" ? 2 : 1.5,
    "stroke-dasharray": dashed ? "5 4" : "none", "stroke-linejoin": "round",
    "marker-end": `url(#head-${tone})`, ...(both ? { "marker-start": `url(#head-${tone})` } : {}),
  }, svg);
}

function label(svg, x, y, lines, { tone = "grey", anchor = "start", mono = false, size = 13, weight = 400 } = {}) {
  [].concat(lines).forEach((line, i) =>
    el("text", { x, y: y + i * (size + 5), "text-anchor": anchor, fill: TONES[tone].text, class: mono ? "label mono" : "label", "font-size": size, "font-weight": weight }, svg, line));
}

/** A numbered step, for diagrams that really are a sequence. */
function step(svg, x, y, n) {
  el("circle", { cx: x, cy: y, r: 10, fill: "#0f5c4c" }, svg);
  el("text", { x, y: y + 4.5, "text-anchor": "middle", class: "step" }, svg, String(n));
}
