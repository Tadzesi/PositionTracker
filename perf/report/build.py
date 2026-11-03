#!/usr/bin/env python3
"""Builds the single-file report page: template + body + charts.js + report.js + the analysis JSON.

usage: build.py <report.json> <out.html> [<big-field-report.json>]
The page is fully static and self-contained (no CDN); only the Archivo font is shared with the site (../fonts/).
"""
import json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
data_path, out_path = sys.argv[1], sys.argv[2]


def read(name):
    with open(os.path.join(HERE, name), encoding="utf-8") as f:
        return f.read()


data = json.load(open(data_path, encoding="utf-8"))
if len(sys.argv) > 3:  # the 1 500-runner run (field sweep until it saturated) goes in as "big"
    big = json.load(open(sys.argv[3], encoding="utf-8"))
    data["big"] = {"field": big.get("field"), "phase_stats": big.get("phase_stats"), "series": big.get("series"),
                   "phases": big.get("phases"), "kpi": big.get("kpi")}
data = json.dumps(data, ensure_ascii=False, separators=(",", ":")).replace("</", "<\\/")
body = re.sub(r"\[\[(TAKE_\w+|FINDINGS|SOAK)\]\]", "", read("body.html"))  # sections without a written conclusion yet
page = (read("template.html")
        .replace("[[BODY]]", body)
        .replace("[[CHARTS]]", read("charts.js"))
        .replace("[[REPORT]]", read("report.js"))
        .replace("[[DATA]]", data))
os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
with open(out_path, "w", encoding="utf-8") as f:
    f.write(page)
print("built", out_path, f"{len(page) / 1e6:.2f} MB")
