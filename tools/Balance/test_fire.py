"""Guards the planet fire's scenario report (docs/BALANCE.md, "The planet's air burns").

    python tools/Balance/test_fire.py

Needs the real tools/Balance/gamedata.json (the routes are the shipped worlds'), so it runs on a machine
with the game, not in CI; CI runs the chemistry part through tools/ci/test_model.py. It recomputes every
verdict with fire.py and fails when one differs from the table in docs/BALANCE.md, so a change to the
planner, the cost model or the rule that changes a world's verdict cannot land without the table being
updated with it. About three minutes: it plans every world twice.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from fire import WORLDS, oxygen_dump, scenario       # noqa: E402
from planet import DATA                               # noqa: E402

failures = []


def check(name, condition, detail=''):
    print(('ok    ' if condition else 'FAIL  ') + name + ((' : ' + detail) if detail and not condition else ''))
    if not condition:
        failures.append(name)


def documented():
    """{(world, 'route' or 'oxygen dump'): verdict} from the scenario table in docs/BALANCE.md."""
    with open(os.path.join(HERE, '..', '..', 'docs', 'BALANCE.md'), encoding='utf-8') as f:
        text = f.read()
    section = text.split("## The planet's air burns", 1)
    if len(section) < 2:
        return {}
    table = section[1].split('\n## ', 1)[0]
    found = {}
    for line in table.splitlines():
        cells = [c.strip() for c in line.strip().strip('|').split('|')]
        if len(cells) < 8 or cells[0] in ('World', '---'):
            continue
        key = (cells[0], 'oxygen dump' if cells[1].startswith('oxygen dump') else 'route')
        found.setdefault(key, set()).add(cells[-1])
    return found


def main():
    if 'Vulcan2' not in DATA['worlds']:
        print('skipped: this is not the game\'s gamedata.json (the shipped worlds are not in it)')
        return 0
    table = documented()
    check('docs/BALANCE.md has the scenario table', bool(table))
    for world in WORLDS:
        verdict = scenario(world)['verdict']
        written = table.get((world, 'route'), set())
        check('%s: the table says %s and the model says %s' % (world, ', '.join(sorted(written)) or 'nothing', verdict),
              written == {verdict})
    dump = oxygen_dump()
    b, d = dump['baseline'], dump['dump']
    verdict = 'faster' if d and b and d['hours'] < b['hours'] else ('slower' if d and b else 'blocked')
    written = table.get(('Vulcan2', 'oxygen dump'), set())
    check('Vulcan2 oxygen dump: the table says %s and the model says %s' % (', '.join(sorted(written)) or 'nothing', verdict),
          written == {verdict})
    print()
    print('%d failed' % len(failures) if failures else 'all passed')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
