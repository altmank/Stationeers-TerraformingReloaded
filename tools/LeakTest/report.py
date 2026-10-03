"""Writes the leak test HTML report (docs/reports/planet-burn-<date>.html) from the measured runs.

    python tools/LeakTest/report.py [date]

Inputs, all written by measured runs on the dedicated test server:
  results/summary.json            the leak matrix (analyze.py)
  results/live_counts_1.json      the cells each source keeps live
  results/livecheck/*.txt|*.log   the LiveCheck -PlanetBurn cases and the standing regression
Modelled figures (docs/BALANCE.md, PatchCheck) are labelled as such where they appear.
Self-contained: inline CSS and SVG, no external assets.
"""
import glob
import html
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
RES = os.path.join(HERE, 'results')
STATION_M = {'s2': 2, 's4': 4, 's8': 8, 's16': 16, 's32': 32, 'far': 100}
COLORS = {'on': '#c0392b', 'off': '#2c3e50'}


def esc(x):
    return html.escape(str(x))


def fmt(v, nd=1):
    if v is None:
        return '-'
    if isinstance(v, float):
        if abs(v) >= 1000:
            return '{:,.0f}'.format(v)
        return ('{:.%df}' % nd).format(v)
    return esc(v)


# ---- SVG charts ---------------------------------------------------------------------------------------------

def line_chart(series, title, xlabel, ylabel, w=640, h=240, ylog=False):
    """series: [(label, colour, [(x, y), ...])]"""
    import math
    pts = [(x, y) for _, _, s in series for x, y in s if y is not None]
    if not pts:
        return '<p class="muted">no data</p>'
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    x0, x1 = min(xs), max(xs) or 1
    tr = (lambda v: math.log10(max(v, 1e-9))) if ylog else (lambda v: v)
    y0, y1 = min(tr(y) for y in ys), max(tr(y) for y in ys)
    if y1 == y0:
        y1 = y0 + 1
    L, B = 56, 34
    sx = lambda x: L + (x - x0) / ((x1 - x0) or 1) * (w - L - 10)
    sy = lambda y: h - B - (tr(y) - y0) / (y1 - y0) * (h - B - 24)
    out = ['<svg viewBox="0 0 %d %d" class="chart"><text x="%d" y="16" class="ct">%s</text>' % (w, h, L, esc(title))]
    out.append('<line x1="%d" y1="%d" x2="%d" y2="%d" class="ax"/><line x1="%d" y1="24" x2="%d" y2="%d" class="ax"/>' % (L, h - B, w - 10, h - B, L, L, h - B))
    for i in range(5):
        v = y0 + (y1 - y0) * i / 4
        lab = ('1e%.1f' % v) if ylog else ('%.3g' % v)
        yy = h - B - i / 4 * (h - B - 24)
        out.append('<text x="%d" y="%.1f" class="tk" text-anchor="end">%s</text><line x1="%d" x2="%d" y1="%.1f" y2="%.1f" class="gr"/>' % (L - 4, yy + 3, lab, L, w - 10, yy, yy))
    for i in range(5):
        v = x0 + (x1 - x0) * i / 4
        out.append('<text x="%.1f" y="%d" class="tk" text-anchor="middle">%.4g</text>' % (sx(v), h - B + 14, v))
    out.append('<text x="%d" y="%d" class="tk" text-anchor="middle">%s</text>' % ((w + L) // 2, h - 4, esc(xlabel)))
    out.append('<text x="12" y="%d" class="tk" transform="rotate(-90 12 %d)" text-anchor="middle">%s</text>' % (h // 2, h // 2, esc(ylabel)))
    ly = 30
    for label, colour, s in series:
        s = [(x, y) for x, y in s if y is not None]
        if s:
            out.append('<polyline fill="none" stroke="%s" stroke-width="1.6" points="%s"/>' % (colour, ' '.join('%.1f,%.1f' % (sx(x), sy(y)) for x, y in s)))
        out.append('<rect x="%d" y="%d" width="10" height="3" fill="%s"/><text x="%d" y="%d" class="tk">%s</text>' % (w - 190, ly, colour, w - 176, ly + 4, esc(label)))
        ly += 13
    out.append('</svg>')
    return ''.join(out)


def bar_chart(groups, title, ylabel, w=640, h=240):
    """groups: [(xlabel, [(label, colour, value)])]"""
    vals = [v for _, bars in groups for _, _, v in bars]
    top = max(vals + [0.01])
    L, B = 56, 40
    n = len(groups)
    gw = (w - L - 10) / max(n, 1)
    out = ['<svg viewBox="0 0 %d %d" class="chart"><text x="%d" y="16" class="ct">%s</text>' % (w, h, L, esc(title))]
    out.append('<line x1="%d" y1="%d" x2="%d" y2="%d" class="ax"/>' % (L, h - B, w - 10, h - B))
    for i in range(5):
        v = top * i / 4
        yy = h - B - i / 4 * (h - B - 24)
        out.append('<text x="%d" y="%.1f" class="tk" text-anchor="end">%.2g</text><line x1="%d" x2="%d" y1="%.1f" y2="%.1f" class="gr"/>' % (L - 4, yy + 3, v, L, w - 10, yy, yy))
    legend = {}
    for gi, (xl, bars) in enumerate(groups):
        bw = gw * 0.8 / max(len(bars), 1)
        for bi, (label, colour, v) in enumerate(bars):
            x = L + gi * gw + gw * 0.1 + bi * bw
            hh = (v / top) * (h - B - 24) if top else 0
            out.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" fill="%s"><title>%s %s: %.4g</title></rect>' % (x, h - B - hh, bw - 1, hh, colour, esc(xl), esc(label), v))
            legend[label] = colour
        out.append('<text x="%.1f" y="%d" class="tk" text-anchor="middle">%s</text>' % (L + gi * gw + gw / 2, h - B + 14, esc(xl)))
    ly = 30
    for label, colour in legend.items():
        out.append('<rect x="%d" y="%d" width="10" height="8" fill="%s"/><text x="%d" y="%d" class="tk">%s</text>' % (w - 190, ly, colour, w - 176, ly + 8, esc(label)))
        ly += 13
    out.append('<text x="12" y="%d" class="tk" transform="rotate(-90 12 %d)" text-anchor="middle">%s</text></svg>' % (h // 2, h // 2, esc(ylabel)))
    return ''.join(out)


# ---- inputs -------------------------------------------------------------------------------------------------

def livecheck_cases():
    out = {}
    for f in sorted(glob.glob(os.path.join(RES, 'livecheck', 'suite*.txt'))):
        for line in open(f, encoding='utf-8', errors='replace'):
            m = re.match(r'burn case (\d) (on|off) (PASS|FAIL): (.*)', line.strip())
            if m:
                out[(int(m.group(1)), m.group(2))] = (m.group(3), m.group(4))
    return out


def regression():
    out = []
    for f in sorted(glob.glob(os.path.join(RES, 'livecheck', 'regression*.txt')) + glob.glob(os.path.join(RES, 'livecheck', 'reset.txt'))):
        text = open(f, encoding='utf-8', errors='replace').read()
        for block in re.split(r'=== RUN', text):
            ok = re.findall(r'LiveCheck OK[^\n]*', block)
            fail = re.findall(r'LiveCheck FAILED[^\n]*|throw [^\n]*', block)
            name = block.strip().split('\n')[0][:30] if block.strip() else ''
            if os.path.basename(f) == 'reset.txt':
                name = '-Reset'
            elif name.startswith('Build'):
                name = '(default)'
            if ok or fail:
                out.append((os.path.basename(f), name or '(default)', 'PASS' if ok and not fail else 'FAIL', (ok or fail)[0]))
    # A run repeated after a fix keeps its last result; the earlier failure is reported in the text.
    last = {}
    for row in out:
        last[row[1].strip()] = row
    return list(last.values())


def load_summary():
    try:
        return json.load(open(os.path.join(RES, 'summary.json'), encoding='utf-8'))
    except OSError:
        return []


def damage_by_distance(run):
    """{distance m: worst damage ratio of anything there, 1 for broken}"""
    out = {m: 0.0 for m in STATION_M.values()}
    for st, items in run['damage'].items():
        if st in STATION_M:
            out[STATION_M[st]] = max([1.0 if i['broken'] or i['gone'] else i['ratio'] for i in items] + [0.0])
    return out


def what_broke(run):
    parts = []
    for st in ('s2', 's4', 's8', 's16', 's32', 'far'):
        items = run['damage'].get(st)
        if items:
            parts.append('%s m: %s' % (STATION_M[st], ', '.join('%s %s' % (i['role'], 'broken' if i['broken'] or i['gone'] else '%d%%' % round(100 * i['ratio'])) for i in items)))
    return '; '.join(parts) or 'nothing'


CSS = """
body{font:14px/1.5 system-ui,Segoe UI,sans-serif;max-width:1100px;margin:24px auto;padding:0 16px;color:#222}
h1{font-size:26px}h2{border-bottom:2px solid #ddd;padding-bottom:4px;margin-top:36px}h3{margin-top:22px}
table{border-collapse:collapse;margin:10px 0;font-size:12.5px;width:100%}th,td{border:1px solid #ccc;padding:3px 6px;vertical-align:top}
th{background:#f3f3f3;text-align:left}tr:nth-child(even) td{background:#fafafa}
.m{display:inline-block;font-size:11px;padding:0 5px;border-radius:3px;background:#2e7d32;color:#fff}
.mod{display:inline-block;font-size:11px;padding:0 5px;border-radius:3px;background:#8e6c00;color:#fff}
.pass{color:#2e7d32;font-weight:600}.fail{color:#c0392b;font-weight:600}.muted{color:#777}
.chart{width:100%;max-width:660px;background:#fff;border:1px solid #e3e3e3;margin:8px 0}.ct{font-size:13px;font-weight:600}
.tk{font-size:10px;fill:#555}.ax{stroke:#333}.gr{stroke:#eee}
.box{background:#fff8e1;border-left:4px solid #f0b400;padding:8px 12px;margin:10px 0}
.key{background:#e8f5e9;border-left:4px solid #2e7d32;padding:8px 12px;margin:10px 0}
code{background:#f4f4f4;padding:0 3px}
"""

