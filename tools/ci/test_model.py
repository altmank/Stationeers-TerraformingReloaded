"""Invariants of the temperature rule and the phase rules, on a synthetic fixture (no game needed).

    python tools/ci/make_fixture.py fixture.json
    TR_GAMEDATA=fixture.json python tools/ci/test_model.py

Each test is a property the design promises (docs/TEMPERATURE.md) or a defect the adversarial review
found, pinned so it cannot come back. They run against tools/Balance/planet.py, which is the same
arithmetic as src/Patching/Climate.cs; check_repo.py checks the two share their constants, and
tools/LiveCheck -Observe checks them against the game.
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Balance'))
from planet import ANGLES, DATA, Planet, equilibrium   # noqa: E402

WORLDS = list(DATA['worlds'])
failures = []


def check(name, condition, detail=''):
    print(('ok    ' if condition else 'FAIL  ') + name + ((' : ' + detail) if detail and not condition else ''))
    if not condition:
        failures.append(name)


def with_air(world, air, **kw):
    p = Planet(world, **kw)
    p.air = dict(air)
    return p


# 1. Requirement 4: untouched air reads as shipped, at every angle, anywhere in the orbit, any settings.
for world in WORLDS:
    for kw in ({}, {'ghg_scale': 3.0, 'density_scale': 0.5}, {'albedo': 0.6}):
        p = Planet(world, **kw)
        worst = max(abs(p.adjustment(a, pct)) for a in ANGLES for pct in (0.0, 37.0, 100.0))
        check('untouched %s reads as shipped %s' % (world, kw or ''), worst < 1e-9, 'adjustment %g K' % worst)

# 2. A world that ships its own curves is never adjusted, whatever the air.
p = with_air('Mars2', {'CarbonDioxide': 300.0, 'Nitrogen': 500.0})
check('a world with its own curves is left alone', all(p.adjustment(a) == 0.0 for a in ANGLES))

# 3. Anchoring: strip the greenhouse gases from a hot world and its mean reaches bare-rock equilibrium.
p = with_air('Hot', {})
cold, hot = p.extremes()
target = equilibrium(p.sun_mid, 0.3)
check('a stripped hot world reaches equilibrium', abs(0.5 * (cold + hot) - target) < 12.0, '%.1f vs %.1f' % (0.5 * (cold + hot), target))

# 3b. Tunability without traps: on a fully anchored world the greenhouse strength setting cancels out
# of the warming side altogether (the gain is warming / (g0 * strength)), so no value of it can make a
# hot world impossible. Its shape, from the curves file, is what changes the route there.
ends = []
for scale in (0.1, 0.5, 1.0, 2.0, 5.0):
    c, h = with_air('Hot', {}, ghg_scale=scale).extremes()
    ends.append(0.5 * (c + h))
check('greenhouse strength does not move a stripped hot world', max(ends) - min(ends) < 1.0, str(ends))
half = [0.5 * sum(with_air('Hot', {'CarbonDioxide': 100.0}, ghg_scale=scale).extremes()) for scale in (0.5, 2.0)]
check('nor any point on the way there', abs(half[0] - half[1]) < 1.0, str(half))
cold = [0.5 * sum(with_air('Cold', {'Oxygen': 340.0, 'Methane': 80.0}, ghg_scale=scale).extremes()) for scale in (0.5, 2.0)]
check('and on a cold world more strength means more warming', cold[1] > cold[0] + 5.0, str(cold))

# 3c. Same for the density strength: thick air ends the swing at any strength above zero; zero is off.
for scale in (0.3, 1.0, 3.0):
    c, h = with_air('Swing', {'Helium': 400.0}, density_scale=scale).extremes()
    check('thick air ends the swing at density strength %.1f' % scale, h - c < 1.0, '%.2f K' % (h - c))
start = Planet('Swing')
c0, h0 = start.extremes()
c, h = with_air('Swing', dict(start.air, Helium=100.0), density_scale=0.0).extremes()
check('density strength 0 switches damping off', abs((h - c) - (h0 - c0)) < 1e-6)

# 3d. Storms. A world's weather offsets are sized for the world as shipped; on a cooled world they
# shrink in proportion, and a finished world must survive its own worst storm without freezing out.
swing = Planet('Swing')
check('a storm on untouched air is the world file\'s own', abs(swing.temperature(0.0, 50.0, storm=-275.0) - (swing.temperature(0.0, 50.0) - 275.0)) < 1e-9)
done = with_air('Swing', {'Helium': 300.0, 'Oxygen': 60.0, 'CarbonDioxide': 20.0})
calm = done.temperature(0.0, 50.0)
hit = done.temperature(0.0, 50.0, storm=-275.0)
check('the same storm on a cooled world is milder in proportion', calm - hit < 275.0 * calm / swing.shipped(0.0, 50.0) + 1e-6 and hit > 0.0, '%.1f -> %.1f' % (calm, hit))
check('and a warming storm is never enlarged on a warmed cold world', with_air('Cold', {'Oxygen': 340.0, 'Methane': 80.0}).temperature(0.0, 50.0, storm=50.0) - with_air('Cold', {'Oxygen': 340.0, 'Methane': 80.0}).temperature(0.0, 50.0) <= 50.0 + 1e-9)
check('a world with its own curves gets its storms untouched', abs(Planet('Mars2').temperature(0.0, 50.0, storm=-40.0) - (Planet('Mars2').temperature(0.0, 50.0) - 40.0)) < 1e-9)

# 4. Review finding: thinning the air never makes the swing larger than shipped.
for world in ('Swing', 'Cold', 'Hot'):
    start = Planet(world)
    c0, h0 = start.extremes()
    for keep in (0.9, 0.5, 0.1, 0.0):
        p = with_air(world, {g: m * keep for g, m in start.air.items()})
        c, h = p.extremes()
        mean_shift = 0.5 * (c + h) - 0.5 * (c0 + h0)
        check('%s thinned to %.0f%%: swing not above shipped' % (world, keep * 100), (h - c) <= (h0 - c0) + 1e-6, '%.1f vs %.1f' % (h - c, h0 - c0))
        check('%s thinned to %.0f%%: temperature finite and not negative' % (world, keep * 100), 0.0 <= c <= h < 1e5)

# 5. Review finding: coolants are not amplified by the anchor gain.
p = Planet('Swing')
before = 0.5 * sum(p.extremes())
g_now = p._g(p.ghg_index())
flood = with_air('Swing', dict(p.air, Nitrogen=400.0))
after_mean = flood.mean0 + flood.adjustment(90.0) - (flood.shipped(90.0) - flood.mean0) * 0.0
drop_allowed = flood.gain * max(g_now, 0.0) + 50.0 + 15.0      # all the warming, the curve's whole cooling side, density mean
check('a nitrogen flood cannot cool a hot world past its warming plus the unamplified cooling side',
      flood.mean0 - 0.5 * sum(flood.extremes()) <= drop_allowed, 'dropped %.1f, bound %.1f' % (flood.mean0 - 0.5 * sum(flood.extremes()), drop_allowed))

# 6. Review finding: no cliff at the anchoring threshold, and the gain is bounded.
check('gain is 1 below the band, capped above', Planet('Cold').gain == 1.0 and all(0.0 < Planet(w).gain <= 10.0 for w in WORLDS))
swing = [0.5 * sum(with_air('Swing', {}, ghg_scale=scale).extremes()) for scale in (0.1, 1.0, 5.0)]
check('no greenhouse strength strands the high-gain world either', max(swing) - min(swing) < 1.0, str(swing))
band = Planet('Band')
check('a world inside the band is partly anchored', 1.0 < band.gain < min(band.warming0 / band._g(band._index0), 10.0))

# 7. Thick air removes the swing entirely; the mean is what is left.
p = with_air('Swing', {'Helium': 400.0})
c, h = p.extremes()
check('thick air flattens day and night', h - c < 1.0, '%.2f K' % (h - c))

# 8. The anchor does not move with the airless albedo setting.
a = with_air('Hot', {'Nitrogen': 50.0}, albedo=0.0).extremes()
b = with_air('Hot', {'Nitrogen': 50.0}, albedo=0.6).extremes()
check('the airless albedo setting does not move a hot world', abs(a[0] - b[0]) < 1e-9 and abs(a[1] - b[1]) < 1e-9)

# 9. Phase rules: a gas below its freezing point + 2 K cannot stay; helium always can.
p = with_air('Cold', {'Oxygen': 340.0, 'CarbonDioxide': 20.0, 'Helium': 5.0})
gone = p.unstable()
check('carbon dioxide cannot stay in 124 K air', 'CarbonDioxide' in gone)
check('oxygen and helium can', 'Oxygen' not in gone and 'Helium' not in gone)
p.settle()
check('settling removes it and leaves the rest', p.air.get('CarbonDioxide', 0.0) == 0.0 and p.air['Oxygen'] == 340.0)
check('a habitable verdict requires stable air', not with_air('Cold', {'Oxygen': 340.0, 'CarbonDioxide': 20.0}).report()['stable'])

# 10. The airless base is equilibrium plus the vacuum swing.
p = Planet('Airless')
c, h = p.extremes()
eq = equilibrium(p.sun_mid, 0.3)
check('an airless world starts at bare rock plus the vacuum swing', abs(h - (eq + 50.0)) < 1e-6 and abs(c - (eq - 60.0)) < 1e-6)

# 11. The solver and the path planner run end to end on the fixture.
try:
    from solve import cheapest
    from path import plan
    found = cheapest('Cold', starts=12)
    check('the solver finds habitable air for the cold world', found is not None and found[2]['shirt_sleeves'])
    route = plan('Cold')
    check('the planner needs a temporary gas there (carbon dioxide would freeze) and gets through',
          route is not None and route['reached'] and any('temporary' in s for s in route['stages']), str(route and route.get('stuck')))
except ImportError as e:                                   # scipy missing
    check('solver and planner importable', False, str(e))

# 12. The judge behind `run.ps1 -Model` reads the driver's lines and agrees with the model it is fed.
import io as _io
import compare
sample = Planet('Hot')
sample.air = {'CarbonDioxide': 23.0, 'Oxygen': 48.0}
line = ('tick 40 | angle 50.0 | orbit 47.5%% | T %.2f | readout 0.00 | P 31.474 | gas/cell 71 | liquid/cell 0 | iceClouds/cell 0 '
        '| liquidClouds/cell 0 | caps/cell 0 | latentK 1.5 | weather None | Oxygen=48 CarbonDioxide=23 | extK 0.5' % (sample.temperature(50.0, 47.5) + 2.0))
for offset, expect in ((0.0, 0), (1.0, 1)):
    text = line if not offset else line.replace('| T %.2f' % (sample.temperature(50.0, 47.5) + 2.0), '| T %.2f' % (sample.temperature(50.0, 47.5) + 2.0 + offset))
    sys.argv = ['compare.py', 'Hot', '0.5']
    sys.stdin = _io.StringIO(text + chr(10))
    out, sys.stdout = sys.stdout, _io.StringIO()
    try:
        code = compare.main()
    finally:
        sys.stdout = out
    check('the judge %s a game reading %.0f K off the model' % ('passes' if not expect else 'fails', offset), code == expect)

# 13. Removal is dilution: drawing air through filters costs more than the moles removed, never less.
from removal import hours_to_remove
start = Planet('Hot').air
hours, drawn = hours_to_remove('Hot', start, {'CarbonDioxide': 20.0, 'HydrochloricAcid': 1.0}, 0.05, 20, 40.0)
removed = (start['CarbonDioxide'] - 20.0 + start['HydrochloricAcid'] - 1.0) * Planet('Hot', size=0.05).cells
check('removal draws more air than it removes, and finishes', 0.0 < hours < 1e6 and drawn > removed, '%.0f h, x%.2f' % (hours, drawn / removed))

print()
print('%d failed' % len(failures) if failures else 'all passed')
sys.exit(1 if failures else 0)
