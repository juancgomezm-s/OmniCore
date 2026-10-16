#!/usr/bin/env python3
"""Render one bounded report as native-terminal Markdown or offline HTML. Python 3 stdlib only."""
import argparse
import html
import json
from pathlib import Path
import re
import sys
import unicodedata

STATES = {"info": "i", "passed": "OK", "failed": "!", "pending": "...", "unknown": "?"}
COPY = {"es": ("Fuentes", "Imprimir", "Secciones"), "en": ("Sources", "Print", "Sections"),
        "fr": ("Sources", "Imprimer", "Sections"), "it": ("Fonti", "Stampa", "Sezioni")}


def text(value, maximum=8000):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValueError("Expected nonempty bounded text")
    if any(unicodedata.category(c) == "Cc" and c not in "\n\t" for c in value):
        raise ValueError("Terminal control characters are not report content")
    return value


def keys(value, allowed):
    if not isinstance(value, dict) or set(value) - set(allowed):
        raise ValueError("Unexpected report fields")


def validate(report):
    keys(report, ("title", "summary", "sections", "sources"))
    text(report.get("title"), 200)
    text(report.get("summary"))
    sections = report.get("sections")
    if not isinstance(sections, list) or not 1 <= len(sections) <= 20:
        raise ValueError("Expected 1..20 sections")
    for section in sections:
        keys(section, ("title", "text", "flow", "rows"))
        text(section.get("title"), 200)
        if not any(section.get(k) for k in ("text", "flow", "rows")):
            raise ValueError("A section needs text, flow or rows")
        if "text" in section:
            text(section["text"])
        if "flow" in section:
            if not isinstance(section["flow"], list) or not 1 <= len(section["flow"]) <= 16:
                raise ValueError("Expected 1..16 flow steps")
            for step in section["flow"]:
                text(step, 800)
        if "rows" in section:
            if not isinstance(section["rows"], list) or not 1 <= len(section["rows"]) <= 100:
                raise ValueError("Expected 1..100 rows")
            for row in section["rows"]:
                keys(row, ("label", "status", "detail"))
                text(row.get("label"), 800)
                if not isinstance(row.get("status"), str) or row["status"] not in STATES:
                    raise ValueError("Unknown row status")
                if "detail" in row:
                    text(row["detail"])
    sources = report.get("sources", [])
    if not isinstance(sources, list) or len(sources) > 100:
        raise ValueError("Expected at most 100 sources")
    for source in sources:
        text(source, 2000)
    return report


def cells(value):
    return sum(0 if unicodedata.combining(c) else 2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in value)


def wrap(value, width):
    lines, line = [], ""
    for word in value.split():
        if line and cells(line + " " + word) > width:
            lines.append(line)
            line = ""
        for char in ((" " if line else "") + word):
            if cells(line + char) > width:
                lines.append(line)
                line = ""
            line += char
    if line:
        lines.append(line)
    return lines or [""]


def md(value):
    return re.sub(r"([\\`*_{}\[\]()#+.!<>|\-])", r"\\\1", value)


def tui(report, width=64, lang="es"):
    if not 24 <= width <= 120:
        raise ValueError("Terminal width must be 24..120")
    out = ["# " + md(report["title"]), "", md(report["summary"]), ""]
    for section in report["sections"]:
        out += ["## " + md(section["title"]), ""]
        if "text" in section:
            out += [md(section["text"]), ""]
        if "flow" in section:
            steps = section["flow"]
            box_width = min(width, max(12, max(cells(step) for step in steps) + 4))
            graph = []
            for index, step in enumerate(steps):
                if index:
                    graph += [" " * (box_width // 2) + "|", " " * (box_width // 2) + "v"]
                border = "+" + "-" * (box_width - 2) + "+"
                graph.append(border)
                graph += ["| " + line + " " * (box_width - 4 - cells(line)) + " |" for line in wrap(step, box_width - 4)]
                graph.append(border)
            fence = "`" * max(3, max((len(run) + 1 for step in steps for run in re.findall(r"`+", step)), default=0))
            out += [fence + "text", *graph, fence, ""]
        for row in section.get("rows", []):
            out += ["- [" + STATES[row["status"]] + "] **" + md(row["label"]) + "**"
                    + (": " + md(row["detail"]) if "detail" in row else "")]
        out.append("")
    if report.get("sources"):
        out += ["## " + COPY[lang][0], "", *("- " + md(source) for source in report["sources"])]
    return "\n".join(out).rstrip() + "\n"


CSS = """
:root{color-scheme:dark;--bg:#061822;--panel:#13222e;--text:#f4f7fb;--muted:#b9cbdf;--accent:#67d4d0;--line:#405666}
@media(prefers-color-scheme:light){:root{color-scheme:light;--bg:#f7f8fb;--panel:#fff;--text:#1a1a22;--muted:#3c3c48;--accent:#087f9e;--line:#c6d0da}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:17px/1.65 system-ui,sans-serif}
main{max-width:1100px;margin:auto;padding:40px 24px}h1{font-size:clamp(2rem,5vw,3.5rem);line-height:1.15;max-width:850px}
header{border-bottom:1px solid var(--line);padding-bottom:28px}header p{font-size:1.2rem;max-width:75ch;color:var(--muted)}
a{color:var(--accent)}nav{display:flex;flex-wrap:wrap;gap:8px 24px;margin:24px 0}section{margin:38px 0;scroll-margin-top:20px}
h2{border-left:4px solid var(--accent);padding-left:14px}p,.detail{white-space:pre-wrap;overflow-wrap:anywhere}
.flow{list-style:none;max-width:600px;padding:0;margin:24px auto}.flow li{position:relative;background:var(--panel);border:1px solid var(--line);padding:16px 24px;margin-bottom:36px;overflow-wrap:anywhere;text-align:center}
.flow li:not(:last-child)::after{content:'↓';position:absolute;top:100%;left:48%;color:var(--accent);font-size:24px}
.rows{padding:0;list-style:none}.rows li{display:grid;grid-template-columns:55px minmax(0,1fr);gap:12px;margin:18px 0}
.badge{font:14px/1.6 ui-monospace,monospace;color:var(--accent)}.detail{display:block;color:var(--muted)}
button{border:1px solid var(--line);color:var(--text);background:var(--panel);padding:8px 16px;cursor:pointer}
@media print{body{--bg:#fff;--panel:#fff;--text:#111;--muted:#333;--accent:#17485c;--line:#aaa}nav,button{display:none}main{max-width:none;padding:0}section{break-inside:avoid}}
"""


def webpage(report, lang="es"):
    esc = html.escape
    nav, sections = [], []
    for index, section in enumerate(report["sections"]):
        anchor = "section-" + str(index + 1)
        nav.append(f'<a href="#{anchor}">{esc(section["title"])}</a>')
        parts = [f'<section id="{anchor}"><h2>{esc(section["title"])}</h2>']
        if "text" in section:
            parts.append("<p>" + esc(section["text"]) + "</p>")
        if "flow" in section:
            parts.append('<ol class="flow">' + "".join("<li>" + esc(step) + "</li>" for step in section["flow"]) + "</ol>")
        parts.append('<ul class="rows">')
        for row in section.get("rows", []):
            parts.append('<li><span class="badge">[' + STATES[row["status"]] + ']</span><div><strong>' + esc(row["label"])
                         + '</strong><span class="detail">' + esc(row.get("detail", "")) + '</span></div></li>')
        sections.append("".join(parts) + "</ul></section>")
    sources = ""
    if report.get("sources"):
        sources = "<section><h2>" + COPY[lang][0] + "</h2><ul>" + "".join("<li>" + esc(source) + "</li>" for source in report["sources"]) + "</ul></section>"
    return ('<!doctype html><html lang="' + lang + '"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
            + '<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src \'unsafe-inline\'; script-src \'unsafe-inline\'; base-uri \'none\'; form-action \'none\'">'
            + '<title>' + esc(report["title"]) + '</title><style>' + CSS + '</style></head><body><main><header><h1>' + esc(report["title"])
            + '</h1><p>' + esc(report["summary"]) + '</p><button id="print" type="button">' + COPY[lang][1] + '</button></header><nav aria-label="'
            + COPY[lang][2] + '">' + "".join(nav) + '</nav>' + "".join(sections) + sources
            + '</main><script>document.getElementById("print").addEventListener("click",()=>window.print());</script></body></html>\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--format", choices=("tui", "html"), default="tui")
    parser.add_argument("--width", type=int, default=64)
    parser.add_argument("--lang", choices=tuple(COPY), default="es")
    parser.add_argument("--out", type=Path)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()
    try:
        with args.input.open("r", encoding="utf-8-sig") as source:
            raw = source.read(1_048_577)
        if len(raw) > 1_048_576:
            raise ValueError("Report input exceeds 1 MiB")
        report = validate(json.loads(raw))
        if args.format == "tui":
            if args.out is not None:
                raise ValueError("TUI output is returned on stdout; --out is for HTML")
            print(tui(report, args.width, args.lang), end="")
        else:
            if args.out is None:
                raise ValueError("HTML requires --out reports/name.html")
            destination = args.out.resolve()
            if destination.suffix.lower() != ".html" or not destination.is_relative_to(Path.cwd().resolve()):
                raise ValueError("HTML destination must be inside the current workspace")
            destination.parent.mkdir(parents=True, exist_ok=True)
            with destination.open("w" if args.force else "x", encoding="utf-8") as output:
                output.write(webpage(report, args.lang))
            print("[" + md(report["title"]).replace("\n", " ") + "](" + destination.as_uri() + ")")
        return 0
    except (ValueError, OSError, TypeError) as error:
        print("visual-explainer: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
