"""How long does terraforming take, and what planet size makes that reasonable?

    python tools/Balance/balance.py

Four layers, kept apart so each can be argued with separately:

1. The planet (planet.py): the game's own curves and thresholds. Measured, not assumed.
2. The end state (solve.py) and the route to it (path.py): the cheapest habitable air, and whether it
   can be reached step by step under the game's phase change and fire rules.
3. What each phase of that route costs (cost.py): ice mined to put gas in, dilution through vents to
   take gas out, tanks to hold what comes out. Prefab values in formulas read from the game.
4. The base (cost.BASES): what a small, medium and mega base actually runs. ASSUMPTIONS. They are the
   soft part; change them there and everything downstream follows.

Adding a mole and removing a mole are different jobs with different limits, and they are not priced
the same here. Layer 3 says which of the two binds on each world, because one blended number hides
more than it tells.
"""

from cost import BASES, RATES, STANDARD, estimate, ice_rate
from path import plan
from planet import Planet
from solve import cheapest, describe

TARGET_HOURS_MEGA = 100.0       # the original mod's stated aim: a mega base needs at least 100 hours
LEVELS = ('helmet_off', 'shirt_sleeves')
SIZES = (('Short', 0.01), ('Standard', 0.05), ('Long', 0.25), ('Unmodded baseline', 1.0))
WORLDS = ('Lunar', 'Mars2', 'Venus', 'Vulcan2', 'MimasHerschel', 'Europa3')


def main():
    print('MACHINE RATES (mol per hour)')
    for name, rate in RATES.items():
        print('  %-48s %12s' % (name, format(round(rate), ',')))
    print('\nWHAT EACH BASE RUNS (assumptions)')
    for name, b in BASES.items():
        print('  %-8s %10s mol/h of ice gas, %2d inward vents   %s' % (
            name, format(round(ice_rate(b)), ','), b['vents'], b['says']))

    print('\nWHAT IT TAKES, per outdoor cell')
    print('end state = cheapest habitable air (solve.py); route = reached step by step (path.py)')
    found = {}
    for world in WORLDS:
        p = Planet(world)
        r = p.report()
        print('\n%s   start: coldest %.0f K, hottest %.0f K, %.1f kPa, ppO2 %.2f, toxins %.2f kPa' %
              (world, r['T_cold'], r['T_hot'], r['P_kPa'], r['ppO2'], r['toxins_kPa']))
        for level in LEVELS:
            best = cheapest(world, level)
            print('  %-15s end state %s' % (level, describe(world, best) if best else 'no habitable air found'))
        route = plan(world, 'shirt_sleeves')
        if not (route and route['reached']):
            print('  %-15s NO ROUTE FOUND: %s' % ('shirt_sleeves', route and route.get('stuck')))
            continue
        e = estimate(world, STANDARD, route=route)
        found[world] = e
        print('  %-15s route: %s in, %s out, %.0f mol/cell moved' % (
            'shirt_sleeves',
            ', '.join('%s %.0f' % (g[:4], m) for g, m in sorted(e['added'].items()) if m >= 0.5) or 'nothing',
            ', '.join('%s %.0f' % (g[:4], m) for g, m in sorted(e['removed'].items()) if m >= 0.5) or 'nothing',
            route['work']))
        print('  %-15s %.0f mol of ice mined per cell (%.0f if the CO2 were mined rather than burnt), '
              '%.0f pollutant per cell to catch and store' % (
                  '', e['ice per cell'], e['ice budget']['ice, mining the CO2 instead'],
                  e['ice budget']['pollutant to store']))
        if e['removed moles'] > 0.0:
            d = e['disposal']
            print('  %-15s %.0f M mol of outdoor air through the filters to take out %.0f M (x%.1f); '
                  '%.0f big tanks to hold it' % (
                      '', e['air through the filters'] / 1e6, e['removed moles'] / 1e6,
                      e['air through the filters'] / e['removed moles'], d['big tanks']))

    print('\nWHICH CONSTRAINT BINDS, at Standard size and the mega base')
    print('  %-14s %7s %9s %9s %7s  %s' % ('world', 'hours', 'adding', 'removing', 'vents', 'why'))
    for world, e in found.items():
        print('  %-14s %7.0f %9.0f %9.0f %7.0f  %s-bound, %s' % (
            world, e['hours'], e['addition hours'], e['removal hours'], e['vents to keep up'], e['bound by'],
            ('one after the other: ' + e['staged']) if e['staged'] else 'the two run side by side'))
    print('  "vents" is the inward vent count below which removal becomes the longer job.')

    # Hours are exactly proportional to planet size: the ice bill and the air the filters have to
    # pass both scale with the number of outdoor cells, and nothing else in the model does.
    print('\nHOURS BY PLANET SIZE, mega base')
    print('  %-14s%s' % ('world', ''.join('%20s' % ('%s (%g)' % (n, s)) for n, s in SIZES)))
    for world, e in found.items():
        print('  %-14s%s' % (world, ''.join('%20s' % format(round(e['hours'] * s / STANDARD), ',') for _, s in SIZES)))

    print('\nPLANET SIZE for a mega base to need %d hours to reach shirt sleeves' % TARGET_HOURS_MEGA)
    for world, e in found.items():
        share = STANDARD * TARGET_HOURS_MEGA / e['hours']
        print('  %-14s x%.3f of shipped  (about 1/%d)' % (world, share, round(1.0 / share)))


if __name__ == '__main__':
    main()
