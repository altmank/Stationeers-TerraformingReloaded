"""The report's computed sections; report.py holds the helpers, report_text.html the prose with @@ markers."""
import json
import os
import sys

from report import (COLORS, CSS, HERE, REPO, RES, STATION_M, bar_chart, damage_by_distance, esc, fmt, line_chart,
                    livecheck_cases, load_summary, regression, what_broke)


def item_text(items):
    return ', '.join('%s %s' % (i['role'], 'broken' if i['broken'] or i['gone'] else '%d%%' % round(100 * i['ratio'])) for i in items)


def section_matrix(runs):
    order = {'midday': 0, 'dusk': 1, 'night': 2}
    groups = {}
    for r in runs:
        groups.setdefault((r['run']['time'], r['run']['switch']), []).append(r)
    out = []
    for (t, sw), rs in sorted(groups.items(), key=lambda kv: (order.get(kv[0][0], 9), kv[0][1] != 'on')):
        out.append('<h3>%s, switch %s <span class="m">measured</span></h3>' % (esc(t), esc(sw)))
        out.append('<table><tr><th>Release (mol)</th><th>Gas</th><th>Kind</th><th>Burnt in outdoor cells</th><th>Burnt by the planet (mol)</th>'
                   '<th>Drain (ticks)</th><th>Local fire (ticks)</th><th>Fire reach (m)</th><th>Damage radius (m)</th><th>What was damaged, by distance</th>'
                   '<th>Far station (100 m)</th><th>Planet fire (ticks after release; fire heat)</th></tr>')
        for r in sorted(rs, key=lambda r: (r['released_mol'], r['run']['kind'])):
            pf = r['planet_fire']
            far = r['station_burns'].get('far')
            fartxt = ('%d cell(s) burnt at tick %s: %.2g mol oxidiser, %.0f J' % (
                far['cells_lit_max'], ','.join(map(str, far['ticks'][:3])) + ('...' if len(far['ticks']) > 3 else ''), far['held'], far['J'])) if far else 'no burn'
            if r['damage'].get('far'):
                fartxt += '; ' + item_text(r['damage']['far'])
            out.append('<tr><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>' % (
                fmt(r['released_mol'], 0), esc(r['run']['gas']), esc(r['run']['kind']),
                '-' if r['burnt_local_share'] is None else '%.0f%%' % (100 * r['burnt_local_share']), fmt(r['burnt_planet_mol']),
                r['drain_ticks'], r['local_fire_ticks'], r['fire_reach_m'] if r['fire_reach_m'] < 1000 else 'past 200', r['damage_radius_m'],
                esc(what_broke(r)), esc(fartxt),
                ('%s to %s; %.2f K' % (pf['first'], pf['last'], pf['fire_heat_k_max'])) if pf['first'] is not None else 'none'))
        out.append('</table>')
    return '\n'.join(out)


def main():
    date = sys.argv[1] if len(sys.argv) > 1 else '2026-10-01'
    runs = load_summary()
    leaks = [r for r in runs if r['released_mol'] > 0 and r['run']['kind'] != 'planet']
    body = open(os.path.join(HERE, 'report_text.html'), encoding='utf-8').read()

    names = {1: 'Leak by day (5,000 mol O2, 280 m from the block)', 2: 'Two oxidisers into the planet (1,000 + 1,000 mol)',
             3: 'First flash (50,000 mol O2 held unlit, then the switch on by day)', 4: 'Spark on Mars2 (1,000 mol CH4, a burning cell)',
             5: 'Leak at night (5,000 mol O2)'}
    t = ['<table><tr><th>Case</th><th>Switch</th><th>Verdict</th><th>Measured (the driver\'s own line)</th></tr>']
    for (case, sw), (verdict, text) in sorted(livecheck_cases().items()):
        t.append('<tr><td>%d. %s</td><td>%s</td><td class="%s">%s</td><td>%s</td></tr>' % (case, esc(names[case]), sw, verdict.lower(), verdict, esc(text)))
    t.append('</table>')
    body = body.replace('@@LIVECHECK@@', '\n'.join(t))

    r = ['<table><tr><th>Run</th><th>Verdict</th><th>Its result line</th></tr>']
    for f, name, verdict, line in regression():
        r.append('<tr><td>%s</td><td class="%s">%s</td><td>%s</td></tr>' % (esc(name.strip() or 'default'), verdict.lower(), verdict, esc(line)))
    r.append('</table>')
    body = body.replace('@@REGRESSION@@', '\n'.join(r))

    live = json.load(open(os.path.join(RES, 'live_counts_1.json'), encoding='utf-8'))
    labels = {'none': 'nothing (control)', 'frame': 'a steel frame', 'vent_off': 'an active vent, unpowered', 'vent_in': 'an active vent, powered, inward (+ its battery)',
              'vent_out': 'an active vent, powered, outward (+ its battery)', 'passive': 'a passive vent, no pipe', 'wall': 'an iron wall', 'solar': 'a solar panel on a frame',
              'battery': 'a station battery on a frame'}
    lt = ['<table><tr><th>Source at the station</th><th>Live outdoor cells within 5 m (min to max over 61 readings, 2 ticks apart)</th></tr>']
    for k, v in live['stations'].items():
        lt.append('<tr><td>%s</td><td>%d to %d</td></tr>' % (esc(labels.get(k, k)), v['min'], v['max']))
    lt.append('</table><p>Whole world: %d to %d live outdoor cells, every one of them exchanging with the planet (%d to %d). No player on the server.</p>' % (
        live['world'][0], live['world'][1], live['edge'][0], live['edge'][1]))
    body = body.replace('@@LIVE@@', '\n'.join(lt))
    body = body.replace('@@MATRIX@@', section_matrix(leaks) if leaks else '<p class="muted">no leak runs</p>')

    keyed = {}
    for x in leaks:
        keyed.setdefault((x['run']['time'], x['run']['gas'], x['released_mol'], x['run']['kind']), {})[x['run']['switch']] = x
    charts = []
    for (tm, gas, mol, kind), pair in sorted(keyed.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2], kv[0][3])):
        groups = [('%d m' % m, [(sw, COLORS[sw], damage_by_distance(pair[sw])[m]) for sw in ('off', 'on') if sw in pair]) for m in STATION_M.values()]
        charts.append(bar_chart(groups, 'Worst damage by distance: %s, %s, %s mol, %s' % (tm, gas, fmt(float(mol), 0), kind), 'damage (1 = broken)'))
    body = body.replace('@@DAMAGE@@', '\n'.join(charts) or '<p class="muted">no runs</p>')

    curves = []
    for x in sorted(runs, key=lambda x: (x['run']['kind'] != 'planet', x['released_mol'])):
        keep = x['run']['switch'] == 'on' and (x['run']['kind'] == 'planet' or (x['run']['kind'] == 'gas' and x['released_mol'] in (1000, 5000, 20000)))
        if not keep or not x['series']:
            continue
        curves.append(line_chart([('oxidiser in the planet (mol)', '#c0392b', [(s['t'], s['O2']) for s in x['series']]),
                                  ('burning outdoor cells', '#7f8c8d', [(s['t'], s['lit']) for s in x['series']])],
                                 'Planet oxidiser and burning cells: %s' % x['name'], 'ticks after the release (2 a second)', 'mol / cells'))
        curves.append(line_chart([('fire heat (K)', '#e67e22', [(s['t'], s['fireK']) for s in x['series']]),
                                  ('all added heat (K)', '#2c3e50', [(s['t'], s['extK']) for s in x['series']])],
                                 'Heat: %s' % x['name'], 'ticks after the release', 'K'))
    body = body.replace('@@CURVES@@', '\n'.join(curves) or '<p class="muted">no runs</p>')

    c = ['<table><tr><th>Scenario</th><th>Switch</th><th>Burnt in outdoor cells</th><th>Local fire (ticks)</th><th>Damage radius (m)</th>'
         '<th>Stations with damage</th><th>Far station</th><th>Oxidiser left in the planet and its cells at the end (mol)</th></tr>']
    for (tm, gas, mol, kind), pair in sorted(keyed.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2], kv[0][3])):
        for sw in ('off', 'on'):
            if sw in pair:
                x = pair[sw]
                c.append('<tr><td>%s, %s, %s mol, %s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%d of 6</td><td>%s</td><td>%s</td></tr>' % (
                    esc(tm), esc(gas), fmt(float(mol), 0), esc(kind), sw,
                    '-' if x['burnt_local_share'] is None else '%.0f%%' % (100 * x['burnt_local_share']),
                    x['local_fire_ticks'], x['damage_radius_m'], len(x['damage']),
                    esc(item_text(x['damage']['far'])) if x['damage'].get('far') else 'undamaged', fmt(x['left_mol'])))
    c.append('</table>')
    body = body.replace('@@ONOFF@@', '\n'.join(c))

    lead = ['<table><tr><th>Run</th><th>Switch</th><th>Station</th><th>First oxidiser there (ticks after the release)</th><th>Cells burning then</th>'
            '<th>Oxidiser handed to its cells then (mol)</th><th>Most in one cell (mol)</th><th>Ticks with a cell burning</th><th>Heat over the run (J)</th><th>Damage there at the end</th></tr>']
    for x in sorted(leaks, key=lambda x: (x['run']['switch'] != 'on', x['run']['time'], x['run']['gas'], x['released_mol'], x['run']['kind'])):
        for st in ('s16', 's32', 'far'):
            h = x.get('handout', {}).get(st)
            if h:
                lead.append('<tr><td>%s</td><td>%s</td><td>%s m</td><td>%d</td><td>%d</td><td>%.3g</td><td>%.3g</td><td>%d</td><td>%s</td><td>%s</td></tr>' % (
                    esc(x['name']), x['run']['switch'], STATION_M[st], h['tick'], h['lit'], h['held'], h['held_max_cell'], h['lit_ticks'],
                    fmt(float(h['J']), 0), esc(item_text(x['damage'].get(st, []))) or 'none'))
    lead.append('</table>')
    body = body.replace('@@LEADSITE@@', '\n'.join(lead))

    out = '<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Planet air burns, leak test (%s)</title><style>%s</style></head><body>%s</body></html>' % (date, CSS, body)
    path = os.path.join(REPO, 'docs', 'reports', 'planet-burn-%s.html' % date)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, 'w', encoding='utf-8').write(out)
    print(path, len(out))


if __name__ == '__main__':
    main()
