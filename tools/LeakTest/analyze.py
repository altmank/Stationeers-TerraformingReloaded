"""Turns the leak runs (results/*.json, written by leaktest.py) into one summary for the report.

    python tools/LeakTest/analyze.py > results/summary.json

Per run:
  - released Q, burnt by the planet P (each logged tick's share of the planet's oxidiser, the watch logs every second
    tick), left L (planet plus outdoor cells at the end), burnt locally B = Q - P - L;
  - drain time: the release to the last tick the planet's oxidiser rose;
  - the local fire: ticks with a burning cell within 6 m of R, the farthest ring with a burning cell, hottest cell;
  - damage by station (each object's damage ratio after less before), what broke;
  - every burn at a station other than the two beside R: the cells, the oxidiser they held and burnt, the heat,
    and when (ticks after the release), which is where the planet's hand-out shows;
  - the planet fire: first and last burning tick, its heat (fire heat K, peak), the cause.
"""
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RESULTS = os.path.join(HERE, 'results')
STATION_M = {'s2': 2, 's4': 4, 's8': 8, 's16': 16, 's32': 32, 'far': 100}
RING_M = {'3': 3, '6': 6, '12': 12, '24': 24, '48': 48, '96': 96, '200': 200, 'inf': 1000}


def rate_of(fire):
    m = re.search(r'(heat|spark) ([\d.]+)%', fire)
    return float(m.group(2)) / 100.0 if m else 0.0


def analyse(path, site):
    d = json.load(open(path, encoding='utf-8'))
    r = d['run']
    t0 = d['released_at']
    rows = [x for x in d['rows'] if x['tick'] >= t0 - 2]
    ox = lambda x: x['O2'] + x['N2O']
    q = float(r['mol']) if r['kind'] != 'planet' else 0.0
    burnt_planet = sum(rate_of(x['fire']) * ox(x) * 2.0 for x in rows if x['tick'] >= t0)
    last = rows[-1] if rows else None
    left = (ox(last) + sum(g['O2'] + g['N2O'] for g in last['rings'])) if last else 0.0
    rose = [b['tick'] for a, b in zip(rows, rows[1:]) if ox(b) > ox(a) + 1e-6]
    local = [x for x in rows if any(g['lit'] for g in x['rings'] if RING_M[g['r']] <= 6)]
    lit_rings = [RING_M[g['r']] for x in rows for g in x['rings'] if g['lit']]
    burning = [x for x in rows if x['fire'].startswith(('heat', 'spark'))]
    role_of = {v: (st, role) for st, ids in site['stations'].items() for role, v in ids.items() if v}
    damage = {}
    for rid, before in d['health_before'].items():
        after = d['health_after'].get(rid, {})
        st, role = role_of.get(rid, ('?', '?'))
        dr = (after.get('ratio') or 0.0) - (before.get('ratio') or 0.0)
        if dr > 1e-6 or (after.get('broken') and not before.get('broken')) or after.get('gone'):
            damage.setdefault(st, []).append({'role': role, 'ratio': round(after.get('ratio') or 0.0, 4), 'broken': bool(after.get('broken')), 'gone': bool(after.get('gone'))})
    # Burns at the stations the leak's own cloud never reaches (16 m and beyond: the cloud's burning cells reach
    # 6 m) can only be fed by the planet handing its air out.
    far_burns = [b for b in d['burns'] if b['station'] in ('s16', 's32', 'far') and b['tick'] >= t0 and b['lit'] > 0]
    lead = {}
    for b in far_burns:
        s = lead.setdefault(b['station'], {'ticks': [], 'cells_lit_max': 0, 'held': 0.0, 'held_max_cell': 0.0, 'burnt': 0.0, 'J': 0.0})
        s['ticks'].append(b['tick'] - t0)
        s['cells_lit_max'] = max(s['cells_lit_max'], b['lit'])
        s['held'] += b['held']
        s['held_max_cell'] = max(s['held_max_cell'], b['held_max'])
        s['burnt'] += b['burnt']
        s['J'] += b['J']
    # The hand-out itself, lit or not: the first tick oxidiser shows up in a far station's cells, and everything
    # that burnt there over the run.
    handout = {}
    for st in ('s16', 's32', 'far'):
        rows_st = [b for b in d['burns'] if b['station'] == st and b['tick'] >= t0 and b['held'] > 0]
        if rows_st:
            first = rows_st[0]
            handout[st] = {'tick': first['tick'] - t0, 'lit': first['lit'], 'held': first['held'], 'held_max_cell': first['held_max'],
                           'lit_ticks': len({b['tick'] for b in rows_st if b['lit'] > 0}),
                           'burnt': sum(b['burnt'] for b in rows_st), 'J': sum(b['J'] for b in rows_st)}
    radius = max([STATION_M[s] for s in damage if s in STATION_M] or [0])
    return {
        'name': d['name'], 'run': r, 'sun': d['sun_at_release'], 'ticks': (last['tick'] - t0) if last else 0,
        'released_mol': q, 'burnt_planet_mol': round(burnt_planet, 1), 'left_mol': round(left, 3),
        'burnt_local_mol': round(q - burnt_planet - left, 1) if q else None,
        'burnt_local_share': round((q - burnt_planet - left) / q, 3) if q else None,
        'drain_ticks': (max(rose) - t0) if rose else 0,
        'local_fire_ticks': len(local) * 2, 'fire_reach_m': max(lit_rings or [0]),
        'hottest_cell_k': max([g['T'] for x in rows for g in x['rings']] or [0]),
        'planet_fire': {'first': (burning[0]['tick'] - t0) if burning else None, 'last': (burning[-1]['tick'] - t0) if burning else None,
                        'fire_heat_k_max': max([x['fireK'] for x in rows] or [0]), 'ext_k_max': max([x['extK'] for x in rows] or [0]),
                        'peak_k_max': max([x['peak'] for x in rows if x['peak'] == x['peak']] or [0]),
                        'cause': sorted({x['fire'].split()[0] for x in burning})},
        'damage': damage, 'damage_radius_m': radius, 'station_burns': lead, 'handout': handout,
        'series': [{'t': x['tick'] - t0, 'O2': round(ox(x), 2), 'fireK': x['fireK'], 'extK': x['extK'], 'lit6': sum(g['lit'] for g in x['rings'] if RING_M[g['r']] <= 6),
                    'lit': sum(g['lit'] for g in x['rings']), 'world': x['world'], 'T3': next((g['T'] for g in x['rings'] if g['r'] == '3'), 0)}
                   for x in rows if x['tick'] - t0 <= 1200 and (x['tick'] - t0) % 4 < 2],
    }


def main():
    site = json.load(open(os.path.join(RESULTS, 'site.json'), encoding='utf-8'))
    out = []
    for path in sorted(glob.glob(os.path.join(RESULTS, '*.json'))):
        name = os.path.basename(path)
        if name in ('site.json', 'summary.json') or name.startswith(('live_', 'plan_')):
            continue
        try:
            out.append(analyse(path, site))
        except Exception as e:
            print('skipped %s: %s' % (name, e), file=sys.stderr)
    json.dump(out, sys.stdout, indent=1)


if __name__ == '__main__':
    main()
