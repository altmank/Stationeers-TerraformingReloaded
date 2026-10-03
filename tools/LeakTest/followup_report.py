"""What each follow-up run (followup.py) measured, as figures for the report.

    python tools/LeakTest/followup_report.py            prints them
    python tools/LeakTest/followup_report.py --json     writes results/followup_summary.json too
"""
import json
import os
import sys

from leaktest import RESULTS

FAR = ('s16', 's32', 'far')


def load(name):
    path = os.path.join(RESULTS, 'followup_%s.json' % name)
    return json.load(open(path, encoding='utf-8')) if os.path.exists(path) else None


def damage(d):
    out = {}
    before, after = d.get('health_before') or {}, d.get('health_after') or {}
    site = json.load(open(os.path.join(RESULTS, 'site.json'), encoding='utf-8'))
    role_of = {v: (st, role) for st, ids in site['stations'].items() for role, v in ids.items() if v}
    for rid, b in before.items():
        a = after.get(rid, {})
        dr = (a.get('ratio') or 0.0) - (b.get('ratio') or 0.0)
        if dr > 1e-6 or (a.get('broken') and not b.get('broken')):
            st, role = role_of.get(rid, ('?', '?'))
            out.setdefault(st, []).append('%s %s' % (role, 'broken' if a.get('broken') else '%.1f%%' % (100 * (a.get('ratio') or 0.0))))
    return out


def leak(d):
    t0 = d['released_at']
    rows = [r for r in d['rows'] if r['tick'] >= t0]
    burns = [b for b in d['burns'] if b['tick'] >= t0]
    far = {}
    for st in FAR:
        bs = [b for b in burns if b['station'] == st]
        far[st] = {'held_total': sum(b['held'] for b in bs), 'held_max_cell': max([b['held_max'] for b in bs] or [0.0]),
                   'lit_ticks': len({b['tick'] for b in bs if b['lit'] > 0}), 'J': sum(b['J'] for b in bs),
                   'first_tick': (bs[0]['tick'] - t0) if bs else None}
    fire = [r for r in rows if r['fire'].startswith(('heat', 'spark'))]
    out_at = fire[-1]['tick'] if fire else t0
    after = [r for r in rows if r['tick'] > out_at]
    planet_after = [r['O2'] for r in after]
    cells_after = [sum(g['O2'] for g in r['rings']) for r in after]
    return {'far': far, 'damage': damage(d),
            'planet_fire': [(fire[0]['tick'] - t0, fire[-1]['tick'] - t0)] if fire else None,
            'planet_o2_after_fire': [planet_after[0], planet_after[-1]] if planet_after else None,
            'cells_o2_after_fire': [min(cells_after), max(cells_after)] if cells_after else None,
            'ticks_after_fire': (after[-1]['tick'] - out_at) if after else 0}


def trace(d):
    ev = d['events']
    marks = [e['tick'] for e in ev]
    rows = d['rows']

    def seg(a, b):
        rs = [r for r in rows if a <= r['tick'] < b]
        if not rs:
            return None
        return {'from': rs[0]['tick'], 'to': rs[-1]['tick'], 'planet_o2': [rs[0]['O2'], rs[-1]['O2']],
                'cells_o2_max': max(sum(g['O2'] for g in r['rings']) for r in rs),
                'edge_cells': [min(r['edge'] for r in rs), max(r['edge'] for r in rs)], 'lerp': rs[-1]['lerp']}
    # events: settings, release, trace off, trace on again, fire+armed on, armed off
    release = d['released_at']
    segs = {'trace on': seg(release + 2, marks[2]), 'trace off': seg(marks[2] + 2, marks[3]), 'trace on again': seg(marks[3] + 2, marks[4]),
            'fire + armed on': seg(marks[4] + 2, marks[5]), 'armed off': seg(marks[5] + 2, 10 ** 9)}
    status = {}
    for e_i, name in ((1, 'trace on'), (2, 'trace off'), (3, 'trace on again'), (4, 'fire + armed on'), (5, 'armed off')):
        lo = marks[e_i] + 1
        hi = marks[e_i + 1] if e_i + 1 < len(marks) else 10 ** 9
        lines = [s for s in d['status'] if lo < s['tick'] < hi and ('holds back' in s['text'] or 'settings' in s['text'])]
        status[name] = sorted({s['text'] for s in lines})
    return {'segments': segs, 'status': status}


def main():
    out = {}
    for name in ('armed_on', 'armed_off', 'leftover_on', 'leftover_off'):
        d = load(name)
        if d:
            out[name] = leak(d)
    d = load('trace')
    if d:
        out['trace'] = trace(d)
    print(json.dumps(out, indent=1))
    if '--json' in sys.argv:
        json.dump(out, open(os.path.join(RESULTS, 'followup_summary.json'), 'w', encoding='utf-8'), indent=1)


if __name__ == '__main__':
    main()
