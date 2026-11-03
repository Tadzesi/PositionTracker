#!/usr/bin/env python3
"""Builds the report in Slovak and English: two small pages and shared, content-hashed assets.

usage: build.py <report.json> <outDir> [<big-field-report.json>] [<compare.json>] [<cold.json>]

outDir (site/perf) gets:
  index.sk.html, index.en.html     the text of one language and the translations its script needs; the web server picks one
                                   by the pt_lang cookie or Accept-Language (deploy/home-server/nginx-positiontracker.conf)
  assets/app.<hash>.js             chart library + renderer (both languages)
  assets/core.<hash>.json          the summary, the comparison and every value the text binds to: enough for the first screen
  assets/data.<hash>.json          the long arrays (time series, heatmaps, spectra), fetched in parallel and drawn on scroll
  *.gz next to each file           served as is by nginx (gzip_static)
The build fails when an English translation is missing (a t('…') key of report.js or charts.js, a {{key}} of the template) or
when the two bodies differ in their ids, value bindings or zoom links.
"""
import gzip, hashlib, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = "/tracker/perf/"
LANGS = ("sk", "en")
# strip-chart channels the page draws (report.js, 01); the analysis writes more
STRIP = ["t", "target", "ok", "e503", "p50", "p99", "app", "db", "nginx", "other", "freq", "temp", "temp_nvme", "p_psys", "p_pkg", "p_core",
         "bc_period", "bc_kb", "gc_pause_ms", "psi_cpu", "dns_ms", "jit_p99", "wifi_ms", "net_tx", "spectators"]
# decimals kept per strip channel: what a 92 px tall panel (or its tooltip) can show
DECIMALS = {"t": 0, "target": 1, "ok": 0, "e503": 0, "spectators": 0, "p50": 1, "p99": 1, "temp": 1, "temp_nvme": 1, "freq": 2,
            "bc_period": 2, "bc_kb": 0, "gc_pause_ms": 1, "psi_cpu": 1, "net_tx": 2, "dns_ms": 2, "jit_p99": 3, "wifi_ms": 2}
STEP_DRAWN = {"t", "app", "cpu", "db", "psys", "temp", "s_t", "p50", "p99"}
LONG = 200  # arrays longer than this go to data.json (except the comparison, which the first screen draws)


def read(name):
    with open(os.path.join(HERE, name), encoding="utf-8") as f:
        return f.read()


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def rounded(a, d):
    return [None if v is None else (int(round(v)) if d == 0 else round(v, d)) for v in a]


def assemble(argv):
    data = load(argv[1])
    if len(argv) > 3:  # the 1 500-runner run: only its field sweep is drawn
        data["big"] = {"field": load(argv[3]).get("field")}
    if len(argv) > 4:
        data["cmp"] = load(argv[4])
    if len(argv) > 5:
        data["cmp"]["cold"] = load(argv[5])
    s = data.get("series", {})
    data["series"] = {k: rounded(s[k], DECIMALS.get(k, 3)) for k in STRIP if k in s}
    if "step" in data:
        data["step"] = {k: v for k, v in data["step"].items() if not isinstance(v, list) or k in STEP_DRAWN}
    return data


def split(node, path=""):
    """(core, rest): rest holds the long arrays at the same paths, core everything else; lists of objects are split per item."""
    if path == "cmp":
        return node, None
    if isinstance(node, dict):
        core, rest = {}, {}
        for k, v in node.items():
            c, r = split(v, f"{path}.{k}" if path else k)
            if c is not None:
                core[k] = c
            if r is not None:
                rest[k] = r
        return core, (rest or None)
    if isinstance(node, list):
        if node and all(isinstance(x, dict) for x in node):
            parts = [split(x, path) for x in node]
            rest = [r or {} for _, r in parts]
            return [c for c, _ in parts], (rest if any(rest) else None)
        if leaves(node) > LONG:
            return None, node
    return node, None


def leaves(node):
    return sum(leaves(x) for x in node) if isinstance(node, list) else 1


def asset(out_dir, name, ext, content):
    raw = content.encode("utf-8")
    h = hashlib.sha256(raw).hexdigest()[:10]
    fname = f"{name}.{h}.{ext}"
    write(os.path.join(out_dir, "assets", fname), raw)
    return BASE + "assets/" + fname


def write(path, raw):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(raw)
    with open(path + ".gz", "wb") as f:
        f.write(gzip.compress(raw, 9, mtime=0))


def keys_of_js(src):
    return set(re.findall(r"\b(?:t|tr|trName)\(\s*'((?:[^'\\]|\\.)*)'", src))


def structure(html):
    return sorted(re.findall(r'\b(id|data-v|data-sync|data-zoom|data-d|data-ms)="([^"]*)"', html))


def main(argv):
    out_dir = argv[2]
    data = assemble(argv)
    core, rest = split(data)
    js = read("charts.js") + "\n" + read("report.js")
    page = load(os.path.join(HERE, "i18n", "page.json"))
    en = load(os.path.join(HERE, "i18n", "en.json"))
    bodies = {lang: read(f"body.{lang}.html") for lang in LANGS}

    # ---- checks: every key translated, both bodies with the same anchors and bindings
    problems = []
    missing = sorted(k for k in keys_of_js(js) if k not in en)
    if missing:
        problems.append(f"{len(missing)} report.js/charts.js keys without English: " + " | ".join(missing[:12]))
    # names the analysis writes in Slovak (report.js translates them with trName / trPeak)
    names = {r["name"] for r in data.get("jitter", {}).get("regimes", [])} | {r["name"] for r in data.get("dns", [])}
    for sp in data.get("spectra", {}).values():
        names |= {re.sub(r" \d+×$", "", pk["id"]) for pk in sp.get("peaks", []) if pk.get("id")}
    missing = sorted(n for n in names if n not in en and not re.match(r"^Saturácia \(.+\)$", n))
    if missing:
        problems.append("names from the data without English: " + " | ".join(missing))
    template = read("template.html")
    for k in set(re.findall(r"\{\{(\w+)\}\}", template)) - {"lang"}:
        if not all(page.get(k, {}).get(lang) for lang in LANGS):
            problems.append(f"template key {k} without both languages")
    if structure(bodies["sk"]) != structure(bodies["en"]):
        a, b = set(structure(bodies["sk"])), set(structure(bodies["en"]))
        problems.append(f"body.sk / body.en differ: only sk {sorted(a - b)[:8]}, only en {sorted(b - a)[:8]}")
    for p in problems if os.environ.get("PT_DRAFT") == "1" else []:
        print("warning (PT_DRAFT):", p[:200])
    if problems and os.environ.get("PT_DRAFT") != "1":
        sys.exit("build failed:\n  " + "\n  ".join(problems))

    # ---- assets (shared)
    assets = os.path.join(out_dir, "assets")
    if os.path.isdir(assets):
        for f in os.listdir(assets):
            os.remove(os.path.join(assets, f))
    for f in ("index.html", "index.html.gz"):
        if os.path.exists(os.path.join(out_dir, f)):
            os.remove(os.path.join(out_dir, f))
    dump = lambda o: json.dumps(o, ensure_ascii=False, separators=(",", ":"))
    u_core = asset(out_dir, "core", "json", dump(core))
    u_data = asset(out_dir, "data", "json", dump(rest or {}))
    u_app = asset(out_dir, "app", "js", js)

    # ---- one page per language
    sizes = {}
    for lang in LANGS:
        head = "\n".join([
            f'<link rel="alternate" hreflang="sk" href="https://www.lab463.com{BASE}sk/">',
            f'<link rel="alternate" hreflang="en" href="https://www.lab463.com{BASE}en/">',
            f'<link rel="alternate" hreflang="x-default" href="https://www.lab463.com{BASE}">',
            f'<link rel="preload" href="{u_core}" as="fetch" crossorigin>',
            f'<link rel="preload" href="{u_data}" as="fetch" crossorigin>',
            f'<link rel="preload" href="{u_app}" as="script">',
            f'<meta name="pt-data" content="{u_core} {u_data}">',
        ])
        i18n = dump(en if lang == "en" else {}).replace("</", "<\\/")
        scripts = f'<script id="i18n" type="application/json">{i18n}</script>\n<script src="{u_app}" defer></script>'
        html = template
        for k, v in page.items():
            html = html.replace("{{" + k + "}}", v[lang])
        html = (html.replace("{{lang}}", lang).replace("[[HEAD]]", head).replace("[[SCRIPTS]]", scripts)
                .replace("[[CUR_SK]]", ' aria-current="true"' if lang == "sk" else "")
                .replace("[[CUR_EN]]", ' aria-current="true"' if lang == "en" else "")
                .replace("[[BODY]]", re.sub(r"\[\[(TAKE_\w+|FINDINGS|SOAK)\]\]", "", bodies[lang])))
        raw = html.encode("utf-8")
        write(os.path.join(out_dir, f"index.{lang}.html"), raw)
        sizes[lang] = (len(raw), len(gzip.compress(raw, 9)))

    def gz(u):
        p = os.path.join(out_dir, "assets", os.path.basename(u))
        return os.path.getsize(p) / 1e3, os.path.getsize(p + ".gz") / 1e3
    print("built", out_dir)
    for lang, (a, b) in sizes.items():
        print(f"  index.{lang}.html  {a / 1e3:7.1f} kB  gz {b / 1e3:6.1f} kB")
    for u in (u_app, u_core, u_data):
        a, b = gz(u)
        print(f"  {os.path.basename(u):28} {a:7.1f} kB  gz {b:6.1f} kB")


if __name__ == "__main__":
    main(sys.argv)
