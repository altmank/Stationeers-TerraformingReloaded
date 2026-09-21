"""Holds the simulator to the game.

    tools/LiveCheck/run.ps1 -Model -World Venus -SetAir "CarbonDioxide=23;Oxygen=48"

run.ps1 pipes the driver's observation lines here. For every line the planet is rebuilt in the model
from the air the game reported, at the sun angle and point in the orbit the game reported, and the
model's temperature is compared with the temperature the game hands outdoor cells. Latent and external
heat are transient and the model does not predict them, so the game's own figures for them are added
to the model's side; the same goes for a running weather event's own offset, which the model then has
to scale the way the mod does. What is compared is everything else: base, sun distance, the mod's
greenhouse, density and storm rules, and whatever the world ships itself.

Also reported, not judged: what the model says should be freezing or raining out, beside what the
game's clouds and ice caps did over the run.

Exit code 1 when the largest gap exceeds the tolerance (kelvin, first argument after the world).
"""
import re
import sys

from planet import DATA, Planet

LINE = re.compile(
    r'tick (?P<tick>\d+) \| angle (?P<angle>[-\d.]+) \| orbit (?P<orbit>[-\d.]+)% \| T (?P<t>[-\d.]+) \| readout [-\d.]+ \| P (?P<p>[-\d.]+)'
    r' \| gas/cell (?P<gas>[-\d.E+]+) \| liquid/cell (?P<liquid>[-\d.E+]+) \| iceClouds/cell (?P<ice>[-\d.E+]+)'
    r' \| liquidClouds/cell (?P<lclouds>[-\d.E+]+) \| caps/cell (?P<caps>[-\d.E+]+) \| latentK (?P<latent>[-\d.E+]+)'
    r' \| weather (?P<weather>\w+) \|(?P<air>[^|]*)(?:\| extK (?P<ext>[-\d.E+]+))?'
    r'(?:\s*\| stormK (?P<storm>[-\d.E+]+))?')     # both optional, so older logs still parse


def main():
    world = sys.argv[1]
    tolerance = float(sys.argv[2]) if len(sys.argv) > 2 else 0.5
    skip_until = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    second_air = int(sys.argv[4]) if len(sys.argv) > 4 else 0       # the tick a second air was set, 0 for none
    rows = []
    for raw in sys.stdin:
        m = LINE.search(raw)
        if not m or int(m.group('tick')) < skip_until:
            continue
        # The sample taken in the ticks the air is being swapped reads half of each.
        if second_air and second_air - 5 <= int(m.group('tick')) < second_air + 10:
            continue
        rows.append(m)
    if not rows:
        print('compare: no observation lines')
        return 1
    worst, shown = 0.0, 0
    print('%6s %7s %7s %8s %9s %9s %7s  %s' % ('tick', 'angle', 'orbit%', 'storm K', 'game K', 'model K', 'gap', 'model says would leave the air'))
    for i, m in enumerate(rows):
        p = Planet(world)
        p.air = {}
        for part in m.group('air').split():
            gas, moles = part.split('=')
            if gas in DATA['gases']:
                p.air[gas] = float(moles)
        p.external = float(m.group('latent')) + float(m.group('ext') or 0.0)   # so the phase verdict sees the heat too
        storm = float(m.group('storm') or 0.0)
        model = p.temperature(float(m.group('angle')), float(m.group('orbit')), storm=storm)
        game = float(m.group('t'))
        gap = game - model
        worst = max(worst, abs(gap))
        if i % max(1, len(rows) // 12) == 0 or abs(gap) > tolerance:
            shown += 1
            leaving = '; '.join('%s %s' % kv for kv in p.unstable().items()) or '-'
            print('%6s %7.1f %7.1f %8.1f %9.2f %9.2f %+7.2f  %s' % (m.group('tick'), float(m.group('angle')), float(m.group('orbit')), storm, game, model, gap, leaving[:90]))
    first, last = rows[0], rows[-1]
    print('game over the run: gas %s -> %s, tank liquid %s -> %s, clouds %.4g -> %.4g, ice caps %s -> %s mol per cell' % (
        first.group('gas'), last.group('gas'), first.group('liquid'), last.group('liquid'),
        float(first.group('ice')) + float(first.group('lclouds')), float(last.group('ice')) + float(last.group('lclouds')),
        first.group('caps'), last.group('caps')))
    print('weather over the run: %s' % ', '.join(sorted({m.group('weather') for m in rows})))
    print('largest gap %.3f K over %d samples (tolerance %.2f)' % (worst, len(rows), tolerance))
    return 1 if worst > tolerance else 0


if __name__ == '__main__':
    sys.exit(main())
