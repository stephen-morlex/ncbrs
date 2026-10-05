// Regenerates Icons.cs from the web client's lucide-react package:
//
//   node client/NCBRS.Client.App/Pages/Icons.generate.mjs
//
// It reads web/node_modules (or the icons folder named by LUCIDE_ICONS, for a
// checkout without one). To add an icon, add it to `wanted`.
//
// Every icon is rewritten as one path of absolute moves, lines and cubic
// curves only. MAUI's path drawing on Android drew nothing at all for some
// valid SVG paths: Lucide's shield, whose small corner arcs it could not
// draw, and paths written with numbers like "-.67". Circles, rects, lines,
// arcs and every relative or shorthand command become M, L, C and Z, which
// it draws reliably. Check new icons on the emulator before relying on them.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const icons = process.env.LUCIDE_ICONS ?? join(here, "../../../web/node_modules/lucide-react/dist/esm/icons/");
const target = join(here, "Icons.cs");

const wanted = {
  Home: "house", Add: "plus", Records: "list", More: "menu", Sync: "refresh-cw",
  ScanLine: "scan-line", Print: "printer", Lock: "lock", Export: "download", Usb: "usb",
  ChevronForward: "chevron-right", ChevronBack: "chevron-left", ChevronDown: "chevron-down",
  Tick: "check", Close: "x", Globe: "globe", MapPin: "map-pin",
  CloudCheck: "cloud-check", CloudOff: "cloud-off", CloudUpload: "cloud-upload",
  Warning: "triangle-alert", Alert: "circle-alert", Error: "circle-x", Success: "circle-check",
  Person: "user", PersonAdd: "user-round-plus", Backspace: "delete", Shield: "shield-plus",
  Search: "search", Calendar: "calendar", Edit: "pencil", Document: "file-text",
  Numbers: "hash", Clock: "clock", Key: "key-round", Language: "languages",
};

// --- the elements, as path data ---------------------------------------------------------------

const n = Number;

function ellipse(cx, cy, rx, ry) {
  return `M${cx - rx} ${cy}A${rx} ${ry} 0 1 0 ${cx + rx} ${cy}A${rx} ${ry} 0 1 0 ${cx - rx} ${cy}Z`;
}

function rect({ x = 0, y = 0, width, height, rx, ry }) {
  [x, y, width, height] = [x, y, width, height].map(n);
  const r = Math.min(n(rx ?? ry ?? 0), width / 2);
  const s = Math.min(n(ry ?? rx ?? 0), height / 2);
  if (!r) return `M${x} ${y}H${x + width}V${y + height}H${x}Z`;
  return `M${x + r} ${y}H${x + width - r}A${r} ${s} 0 0 1 ${x + width} ${y + s}V${y + height - s}`
    + `A${r} ${s} 0 0 1 ${x + width - r} ${y + height}H${x + r}A${r} ${s} 0 0 1 ${x} ${y + height - s}`
    + `V${y + s}A${r} ${s} 0 0 1 ${x + r} ${y}Z`;
}

function points(list, close) {
  const xs = list.trim().split(/[\s,]+/).map(n);
  let d = `M${xs[0]} ${xs[1]}`;
  for (let i = 2; i < xs.length; i += 2) d += `L${xs[i]} ${xs[i + 1]}`;
  return close ? d + "Z" : d;
}

function element([tag, a]) {
  switch (tag) {
    case "path": return a.d;
    case "circle": return ellipse(n(a.cx), n(a.cy), n(a.r), n(a.r));
    case "ellipse": return ellipse(n(a.cx), n(a.cy), n(a.rx), n(a.ry));
    case "rect": return rect(a);
    case "line": return `M${a.x1} ${a.y1}L${a.x2} ${a.y2}`;
    case "polyline": return points(a.points, false);
    case "polygon": return points(a.points, true);
    default: throw new Error("unknown element " + tag);
  }
}

// --- any path, as absolute M, L, C and Z --------------------------------------------------------

const arity = { m: 2, l: 2, h: 1, v: 1, c: 6, s: 4, q: 4, t: 2, a: 7, z: 0 };

function* commands(d) {
  const re = /([a-zA-Z])|([-+]?(?:\d+\.?\d*|\.\d+)(?:e[-+]?\d+)?)/g;
  let command = null, args = [], m;
  const tokens = [];
  while ((m = re.exec(d))) tokens.push(m[1] ?? Number(m[2]));
  for (let i = 0; i < tokens.length;) {
    if (typeof tokens[i] === "string") command = tokens[i++];
    const lower = command.toLowerCase();
    const count = arity[lower];
    if (count === 0) { yield [command, []]; continue; }
    args = tokens.slice(i, i + count);
    if (args.length < count || args.some((value) => typeof value !== "number")) throw new Error("bad path: " + d);
    i += count;
    yield [command, args];
    // Further pairs after a move are lines.
    if (lower === "m") command = command === "m" ? "l" : "L";
  }
}

// An SVG arc as cubic curves, at most a quarter turn each (SVG 1.1 F.6.5).
function arc(x1, y1, rx, ry, angle, large, sweep, x2, y2) {
  if (rx === 0 || ry === 0 || (x1 === x2 && y1 === y2)) return [[x1, y1, x2, y2, x2, y2]];
  rx = Math.abs(rx); ry = Math.abs(ry);
  const phi = (angle * Math.PI) / 180, cos = Math.cos(phi), sin = Math.sin(phi);
  const dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
  const xp = cos * dx + sin * dy, yp = -sin * dx + cos * dy;
  const lambda = (xp * xp) / (rx * rx) + (yp * yp) / (ry * ry);
  if (lambda > 1) { rx *= Math.sqrt(lambda); ry *= Math.sqrt(lambda); }
  const sign = large === sweep ? -1 : 1;
  const num = rx * rx * ry * ry - rx * rx * yp * yp - ry * ry * xp * xp;
  const coef = sign * Math.sqrt(Math.max(0, num / (rx * rx * yp * yp + ry * ry * xp * xp)));
  const cxp = (coef * rx * yp) / ry, cyp = (-coef * ry * xp) / rx;
  const cx = cos * cxp - sin * cyp + (x1 + x2) / 2, cy = sin * cxp + cos * cyp + (y1 + y2) / 2;
  const angleOf = (ux, uy, vx, vy) => Math.atan2(ux * vy - uy * vx, ux * vx + uy * vy);
  const theta = angleOf(1, 0, (xp - cxp) / rx, (yp - cyp) / ry);
  let delta = angleOf((xp - cxp) / rx, (yp - cyp) / ry, (-xp - cxp) / rx, (-yp - cyp) / ry);
  if (!sweep && delta > 0) delta -= 2 * Math.PI;
  if (sweep && delta < 0) delta += 2 * Math.PI;

  const segments = Math.ceil(Math.abs(delta) / (Math.PI / 2) - 1e-9);
  const step = delta / segments, k = (4 / 3) * Math.tan(step / 4);
  const at = (t) => [cx + rx * Math.cos(t) * cos - ry * Math.sin(t) * sin, cy + rx * Math.cos(t) * sin + ry * Math.sin(t) * cos];
  const tangent = (t) => [-rx * Math.sin(t) * cos - ry * Math.cos(t) * sin, -rx * Math.sin(t) * sin + ry * Math.cos(t) * cos];
  const curves = [];
  for (let i = 0; i < segments; i++) {
    const t1 = theta + i * step, t2 = t1 + step;
    const [px, py] = at(t1), [qx, qy] = at(t2), [ax, ay] = tangent(t1), [bx, by] = tangent(t2);
    curves.push([px + k * ax, py + k * ay, qx - k * bx, qy - k * by, i === segments - 1 ? x2 : qx, i === segments - 1 ? y2 : qy]);
  }
  return curves;
}

function absolute(d) {
  const out = [];
  let x = 0, y = 0, startX = 0, startY = 0, lastControl = null, lastQuad = null;
  for (const [command, args] of commands(d)) {
    const relative = command === command.toLowerCase();
    const c = command.toLowerCase();
    const X = (value) => (relative ? x + value : value);
    const Y = (value) => (relative ? y + value : value);
    let control = null, quad = null;
    switch (c) {
      case "m": x = X(args[0]); y = Y(args[1]); startX = x; startY = y; out.push(["M", x, y]); break;
      case "l": x = X(args[0]); y = Y(args[1]); out.push(["L", x, y]); break;
      case "h": x = X(args[0]); out.push(["L", x, y]); break;
      case "v": y = Y(args[0]); out.push(["L", x, y]); break;
      case "c": {
        const p = [X(args[0]), Y(args[1]), X(args[2]), Y(args[3]), X(args[4]), Y(args[5])];
        out.push(["C", ...p]); control = [p[2], p[3]]; [x, y] = [p[4], p[5]]; break;
      }
      case "s": {
        const [rx, ry] = lastControl ? [2 * x - lastControl[0], 2 * y - lastControl[1]] : [x, y];
        const p = [rx, ry, X(args[0]), Y(args[1]), X(args[2]), Y(args[3])];
        out.push(["C", ...p]); control = [p[2], p[3]]; [x, y] = [p[4], p[5]]; break;
      }
      case "q": case "t": {
        const [qx, qy] = c === "q" ? [X(args[0]), Y(args[1])]
          : lastQuad ? [2 * x - lastQuad[0], 2 * y - lastQuad[1]] : [x, y];
        const [ex, ey] = c === "q" ? [X(args[2]), Y(args[3])] : [X(args[0]), Y(args[1])];
        out.push(["C", x + (2 / 3) * (qx - x), y + (2 / 3) * (qy - y), ex + (2 / 3) * (qx - ex), ey + (2 / 3) * (qy - ey), ex, ey]);
        quad = [qx, qy]; [x, y] = [ex, ey]; break;
      }
      case "a": {
        const [ex, ey] = [X(args[5]), Y(args[6])];
        for (const curve of arc(x, y, args[0], args[1], args[2], args[3], args[4], ex, ey)) out.push(["C", ...curve]);
        [x, y] = [ex, ey]; break;
      }
      case "z": out.push(["Z"]); [x, y] = [startX, startY]; break;
    }
    lastControl = control; lastQuad = quad;
  }
  const number = (value) => String(+value.toFixed(3));
  return out.map(([command, ...values]) => [command, ...values.map(number)].join(" ")).join(" ");
}

// --- Icons.cs ------------------------------------------------------------------------------------

let body = "";
for (const [name, file] of Object.entries(wanted)) {
  const text = readFileSync(icons + file + ".mjs", "utf8");
  const start = text.indexOf("node:");
  const end = text.indexOf("\n};", start);
  const node = Function("return {" + text.slice(start, end) + "}")().node;
  // Each element on its own, from the origin: joined first, a later element
  // opening with a relative "m" would start from where the last one ended.
  const d = node.map((part) => absolute(element(part))).join(" ");
  body += `    /// <summary>Lucide "${file}".</summary>\n    public const string ${name} = "${d}";\n\n`;
}

writeFileSync(target, `namespace NCBRS.Client.App.Pages;

/// <summary>
/// The tablet's icons: Lucide (ISC licence), as 24-unit outline path data
/// drawn with a 2-unit stroke by <see cref="Ui.Icon"/>. Generated by
/// Icons.generate.mjs from the web client's lucide-react package, so the
/// tablet and the web site draw the same symbols; edit that script, not this
/// file. Every path is absolute moves, lines and cubic curves only, because
/// MAUI on Android drew nothing at all for some valid SVG paths.
/// </summary>
public static class Icons
{
${body.trimEnd()}
}
`);
console.log(Object.keys(wanted).length + " icons written to Icons.cs");
