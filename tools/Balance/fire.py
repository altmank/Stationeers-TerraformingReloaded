"""The planet's air burns: what the rule does to each world's terraforming route (docs/BALANCE.md,
"The planet's air burns: what it does to each route").

    python tools/Balance/fire.py            the scenario table and a paragraph per world that is affected
    python tools/Balance/fire.py --json     the same, as data

The rule (src/Patching/FireRule.cs) burns the planet's air as one big cell: lit when one cell's worth
self-ignites at the day's hottest hour, or while a burning outdoor cell touches it; burnt at the game's
share a tick, so a planet-wide fire is over in minutes, which is instantaneous on the scale of a route
(hours to hundreds of hours); products from the game's table; heat booked as a burnt cell books it.
This module is the same chemistry on the model's air, which is held per outdoor cell, so one cell's
worth is the model's air itself.

Constants are the game's (CODE, build 0.2.6428): the combustion table (Combustion.cs), the enthalpies and
their multipliers (Mole.cs), the ignition rule (GasMixture.IsAutoIgnition, Chemistry.cs) and the smallest
amounts (Chemistry.MINIMUM_QUANTITY_MOLES, GasMixture.MinCombustionMoles). Specific heats come from
gamedata.json, which the game wrote.

How a route is judged, and why (every assumption is also in docs/ASSUMPTIONS.md):
  - path.py's routes never hold more than half a mole per cell of a fuel beside more than half a mole of an
    oxidiser: its fire rule refuses that state. So no state of any route is a fire in the model's terms.
  - But removal is dilution and stops at removal.FLOOR (half a mole per cell): the model's timeline takes a
    removed gas to zero, the game never does. A route that removes one side and later adds the other
    therefore holds, in the game, the FLOOR's residue of the first beside the second. Those are the states
    the rule can act on, and the only ones: they are judged here with the residue put back in.
  - Lit by heat needs a fuel above one mole per cell (IsAutoIgnition), so a residue at the FLOOR is never
    lit by heat, whatever the temperature: it burns only while something outdoors burns beside open
    ground (a spark). How much burns per spark depends on how long the spark lasts, so the effect is
    priced at its bound: the whole residue burnt, once.
  - The heat of a burn fades with the added heat's half-life (60 minutes); the route's steps take hours,
    so it is reported as a transient and does not carry into the next step.
"""
import json
import sys

from planet import DATA, Planet

# ---- the game's chemistry (CODE) -------------------------------------------------------------------------
FUELS = ('Methane', 'Hydrogen')
OXIDISERS = ('Oxygen', 'NitrousOxide', 'Ozone')
HYPERGOLIC = 'Hydrazine'
ENTHALPY = {'Methane': 286000.0, 'Hydrogen': 306000.0, 'Hydrazine': 306000.0}       # Mole.Enthalpy, J/mol
MULTIPLIER = {'Oxygen': 1.0, 'NitrousOxide': 2.0, 'Ozone': 2.0, 'Hydrazine': 1.0}   # Mole.EnthalpyMultiplier
# Combustion.cs: (fuel count, oxidiser count, {product: count}) per fuel and oxidiser.
TABLE = {
    ('Methane', 'Oxygen'): (2.0, 1.0, {'Pollutant': 3.0, 'CarbonDioxide': 6.0}),
    ('Methane', 'NitrousOxide'): (1.0, 1.0, {'CarbonDioxide': 2.0, 'Nitrogen': 2.0}),
    ('Methane', 'Ozone'): (3.0, 2.0, {'Pollutant': 3.0, 'CarbonDioxide': 6.0, 'Steam': 1.0}),
    ('Hydrogen', 'Oxygen'): (2.0, 1.0, {'Steam': 3.0}),
    ('Hydrogen', 'NitrousOxide'): (1.0, 1.0, {'Steam': 1.0, 'Nitrogen': 1.0}),
    ('Hydrogen', 'Ozone'): (3.0, 1.0, {'Steam': 4.0}),
    ('Hydrazine', 'Hydrazine'): (1.0, 1.0, {'Pollutant': 8.0}),
}
IGNITION_K = {'Methane': 573.15, 'Hydrogen': 573.15}       # Chemistry.AutoIgnitionMethane / Hydrogen
HYDRAZINE_IGNITION_K = 520.808                             # Chemistry.AutoIgnitionHydrazine, printed by PatchCheck
OFFSET_K = {'NitrousOxide': -250.0, 'Ozone': -150.0}       # Chemistry.AutoIgnitionOffset*
MIN_COMBUSTION_MOLES = 1.0                                 # GasMixture.MinCombustionMoles
MINIMUM_QUANTITY = 1e-5                                    # Chemistry.MINIMUM_QUANTITY_MOLES

FLOOR = 0.5                                                # removal.FLOOR: where a dilution removal stops

# The most a world's own storms raise its temperature (Data/weather.xml, the events each world file lists):
# Mars2 MarsDustStorm night +30; Europa3 EuropaSnowStorm cools by day and night; Vulcan2 VulcanSolarStorm
# day +500 (VulcanAshStorm cools by day, +150 at night); Venus VenusStorm cools; Lunar SolarStorm +50;
# MimasHerschel ships none. A storm can only light a planet the hottest hour has not, so only a rise counts.
STORM_RISE_K = {'Mars2': 30.0, 'Europa3': 0.0, 'Vulcan2': 500.0, 'Venus': 0.0, 'Lunar': 50.0, 'MimasHerschel': 0.0}


def specific_heat(gas):
    return DATA['gases'][gas]['specificHeat']


def heat_capacity(air):
    return sum(m * specific_heat(g) for g, m in air.items() if m > 0.0)


def enough_to_burn(air):
    """Atmosphere.TryCombust's quantity test: a hypergolic, or a fuel and an oxidiser."""
    fuel = sum(air.get(g, 0.0) for g in FUELS)
    oxidiser = sum(air.get(g, 0.0) for g in OXIDISERS)
    return air.get(HYPERGOLIC, 0.0) >= MINIMUM_QUANTITY or (fuel >= MINIMUM_QUANTITY and oxidiser >= MINIMUM_QUANTITY)


def ignition_kelvin(air):
    """The temperature above which this air lights itself (GasMixture.IsAutoIgnition), or None for never."""
    offset = min([OFFSET_K[g] for g in OFFSET_K if air.get(g, 0.0) > MIN_COMBUSTION_MOLES] + [0.0])
    points = [IGNITION_K[g] + offset for g in FUELS if air.get(g, 0.0) > MIN_COMBUSTION_MOLES]
    if air.get(HYPERGOLIC, 0.0) > MIN_COMBUSTION_MOLES:
        points.append(HYDRAZINE_IGNITION_K)
    return min(points) if points else None


def burn(air, rate=1.0):
    """GasMixture.Combust on gases: each oxidiser shared between the fuels by what each needs, each fuel
    shared between the oxidisers the same way, every pair burnt at `rate` of its limiting side. Returns the
    air after and the heat of combustion in joules (before the products' heat capacity is netted)."""
    after = dict(air)
    energy = 0.0
    hydrazine = air.get(HYPERGOLIC, 0.0)
    if hydrazine > 0.0:
        units = hydrazine / 2.0 * rate
        after[HYPERGOLIC] = hydrazine - 2.0 * units
        after['Pollutant'] = after.get('Pollutant', 0.0) + 8.0 * units
        energy += units * ENTHALPY[HYPERGOLIC] * 2.0
    fuels = {f: air.get(f, 0.0) for f in FUELS}
    oxidisers = {o: air.get(o, 0.0) for o in OXIDISERS}
    if sum(fuels.values()) <= 0.0 or sum(oxidisers.values()) <= 0.0:
        return after, energy
    # Oxidiser each fuel would take of each oxidiser, capped by what there is of that oxidiser.
    ox_share = {}
    for o, have in oxidisers.items():
        need = {f: fuels[f] * TABLE[(f, o)][1] / TABLE[(f, o)][0] for f in FUELS}
        cap = min(have / sum(need.values()), 1.0) if sum(need.values()) > 0.0 else 1.0
        for f in FUELS:
            ox_share[(f, o)] = need[f] * cap
    # Fuel each pair would take, capped by what there is of that fuel.
    fuel_share = {}
    for f, have in fuels.items():
        need = {o: ox_share[(f, o)] * TABLE[(f, o)][0] / TABLE[(f, o)][1] for o in OXIDISERS}
        cap = min(have / sum(need.values()), 1.0) if sum(need.values()) > 0.0 else 1.0
        for o in OXIDISERS:
            fuel_share[(f, o)] = need[o] * cap
    for (f, o), (fc, oc, products) in ((k, v) for k, v in TABLE.items() if k[0] in FUELS):
        if fuels[f] <= 0.0 or oxidisers[o] <= 0.0:
            continue
        units = min(fuel_share[(f, o)] / fc, ox_share[(f, o)] / oc) * rate
        if units <= 0.0:
            continue
        after[f] = after.get(f, 0.0) - units * fc
        after[o] = after.get(o, 0.0) - units * oc
        for p, n in products.items():
            after[p] = after.get(p, 0.0) + units * n
        energy += units * fc * ENTHALPY[f] * MULTIPLIER[o]
    return {g: max(0.0, m) for g, m in after.items()}, energy


def burn_out(air, kelvin):
    """The whole scarce side burnt, as the planet's fire leaves it, and the rise in kelvin the game's way:
    T after = (C before x T + E) / C after, so rise = (E - (C after - C before) x T) / C after."""
    after, energy = burn(air, 1.0)
    c0, c1 = heat_capacity(air), heat_capacity(after)
    rise = (energy - (c1 - c0) * kelvin) / c1 if c1 > 0.0 else 0.0
    return after, energy, rise


# ---- judging a state -------------------------------------------------------------------------------------

def hottest_mid_orbit(p):
    """The day's hottest hour half way between the orbit's ends: the temperature a burn is booked at."""
    from planet import ANGLES
    return max(p.temperature(a, 50.0) for a in ANGLES)


def judge(world, air, **kw):
    """What the rule does to this air, per outdoor cell: none, a spark-only fire, or a fire lit by heat.
    Lit by heat is asked at the hottest hour of the hottest season and in the world's hottest storm, so a
    fire the route could meet at any time of year or in any weather is found; the rise is booked half way
    round the orbit."""
    p = Planet(world, **kw)
    p.air = {g: m for g, m in air.items() if m > 0.0}
    cold, hot = p.extremes()
    if not enough_to_burn(air):
        return {'state': 'none', 'cold': cold, 'hot': hot}
    ignition = ignition_kelvin(air)
    storm = STORM_RISE_K.get(world, 0.0)
    after, energy, rise = burn_out(air, hottest_mid_orbit(p))
    lit = ignition is not None and hot > ignition
    in_storm = not lit and ignition is not None and hot + storm > ignition
    # A spark-lit fire keeps going once its own heat lifts the hottest hour past ignition, as a cell would.
    sustains = lit or in_storm or (ignition is not None and hot + rise > ignition)
    burnt = {g: air.get(g, 0.0) - after.get(g, 0.0) for g in set(air) | set(after)}
    state = 'lit by heat' if lit else 'lit in a storm' if in_storm else 'spark, keeps itself going' if sustains else 'spark only, fizzles'
    return {'state': state, 'cold': cold, 'hot': hot, 'storm': storm, 'ignition': ignition, 'energy': energy, 'rise': rise,
            'burnt': burnt, 'after': after}


def windows(route):
    """The states of a route where a fuel and an oxidiser are in the air together in the game: the model's
    own states, and the ones where a gas the route removes still holds its FLOOR residue while the other
    side goes in. Each is (step index, air per cell, what it is)."""
    found = []
    timeline = route['timeline']
    start = timeline[0]
    residue = {}
    previous = dict(start)
    for i, air in enumerate(timeline):
        for g in FUELS + OXIDISERS:
            if previous.get(g, 0.0) > FLOOR and air.get(g, 0.0) <= 0.0:
                residue[g] = FLOOR                         # removed: in the game, the FLOOR is left
            if air.get(g, 0.0) > residue.get(g, 0.0):
                residue.pop(g, None)                       # put back above the residue
        real = dict(air)
        for g, m in residue.items():
            real[g] = max(real.get(g, 0.0), m)
        if enough_to_burn(real):
            kind = 'model' if enough_to_burn(air) else 'residue ' + '+'.join(sorted(residue))
            found.append((i, real, kind))
        previous = air
    return found


def scenario(world, route=None, **kw):
    """The fire report for one world: its route, every fire window, and the verdict."""
    from path import plan
    route = route or plan(world, **kw)
    if route is None or not route.get('reached'):
        return {'world': world, 'route': 'no route found', 'windows': [], 'verdict': 'unaffected',
                'why': 'no route to judge'}
    seen = []
    for i, air, kind in windows(route):
        j = judge(world, air, **kw)
        if j['state'] == 'none':
            continue
        side = {g: round(air.get(g, 0.0), 3) for g in FUELS + OXIDISERS + (HYPERGOLIC,) if air.get(g, 0.0) > 0.0}
        seen.append({'step': i, 'kind': kind, 'air': side, **{k: j[k] for k in ('state', 'hot', 'ignition', 'energy', 'rise', 'burnt')}})
    # One entry per distinct window: consecutive steps of the same kind and state are one window.
    merged = []
    for w in seen:
        if merged and merged[-1]['kind'] == w['kind'] and merged[-1]['state'] == w['state'] and w['step'] - merged[-1]['last'] <= 1:
            merged[-1]['last'] = w['step']
            merged[-1]['worst_rise'] = max(merged[-1]['worst_rise'], w['rise'])
            continue
        merged.append(dict(w, first=w['step'], last=w['step'], worst_rise=w['rise']))
    return {'world': world, 'route': summary(route), 'windows': merged, **verdict(world, route, merged, **kw)}


def summary(route):
    added = ', '.join('%s %.0f' % (g, m) for g, m in sorted(route['added'].items()) if m >= 0.5) or 'nothing'
    removed = ', '.join('%s %.0f' % (g, m) for g, m in sorted(route['removed'].items()) if m >= 0.5) or 'nothing'
    return 'add %s; remove %s' % (added, removed)


def verdict(world, route, merged, **kw):
    """Unaffected, slower, faster, blocked or dangerous, with the reason and the way round."""
    if not merged:
        return {'verdict': 'unaffected', 'why': 'no fuel and oxidiser are ever in the air together, residues included'}
    if any(w['state'] != 'spark only, fizzles' for w in merged):
        return {'verdict': 'dangerous', 'why': 'a state of the route burns by itself or keeps a spark going'}
    # Spark-only residue fires: what burning the whole residue once leaves in the finished air.
    final = dict(route['mix'])
    # Each residue burns once: windows that burn the same residue away (the same gases used up) are one
    # fire, whose products are the largest any of them makes; different residues add up.
    by_kind = {}
    for w in merged:
        used_up = tuple(sorted(g for g in FUELS + OXIDISERS + (HYPERGOLIC,)
                               if w['burnt'].get(g, 0.0) > 0.0 and abs(w['burnt'][g] - w['air'].get(g, 0.0)) <= 1e-6))
        made = by_kind.setdefault(used_up, {})
        for g, d in w['burnt'].items():
            if d < 0.0:
                made[g] = max(made.get(g, 0.0), -d)
    products = {}
    for made in by_kind.values():
        for g, m in made.items():
            products[g] = products.get(g, 0.0) + m
    # A product the route still has to put in after the fire is made for it instead: it costs nothing and
    # saves that much. Only what is left over has to come back out.
    first = min(w['first'] for w in merged)
    timeline = route['timeline']
    later = {}
    for before, after in zip(timeline[first:], timeline[first + 1:]):
        for g, m in after.items():
            later[g] = later.get(g, 0.0) + max(0.0, m - before.get(g, 0.0))
    absorbed = {g: min(m, later.get(g, 0.0)) for g, m in products.items() if g not in FUELS + OXIDISERS}
    extra = {g: m - absorbed.get(g, 0.0) for g, m in products.items()
             if g not in FUELS + OXIDISERS and m - absorbed.get(g, 0.0) > 1e-9}
    finished = dict(route['mix'])
    for g, m in extra.items():
        finished[g] = finished.get(g, 0.0) + m
    p = Planet(world, **kw)
    p.air = {g: m for g, m in finished.items() if m > 0.0}
    still = p.report()['shirt_sleeves']

    def text(d):
        return ', '.join('%s %.2f' % (g, m) for g, m in sorted(d.items()) if m > 1e-9) or 'nothing'

    hours = extra_removal_hours(world, finished, route['mix'], **kw) if extra else 0.0
    if still:
        return {'verdict': 'unaffected', 'extra': extra, 'absorbed': absorbed, 'hours': hours,
                'why': 'a spark can only burn the half-mole residues a removal leaves, once each. That makes %s per cell, of which '
                       '%s is gas the route puts in later anyway, and the finished air still meets the target with the rest (%s) in it' % (
                           text(products), text(absorbed), text(extra))}
    return {'verdict': 'slower', 'extra': extra, 'absorbed': absorbed, 'hours': hours,
            'why': 'burning the residues makes %s per cell. %s of it is gas the route puts in later anyway (%.2f mol a cell less to '
                   'add), but %s more has to come out again for the finished air to meet the target, about %.1f more hours of '
                   'removal for the reference base at Standard size. Way round: take the removed gas below the half-mole residue '
                   'before the other side goes in, or set the residue off while the route still takes that gas out' % (
                       text(products), text(absorbed), sum(absorbed.values()), text(extra), hours)}


def extra_removal_hours(world, finished, final, **kw):
    """Hours of dilution the reference base needs, at Standard size, to take the fire's leftovers back out."""
    from cost import BASES, PREFAB, REFERENCE, STANDARD
    from removal import hours_to_remove
    base = BASES[REFERENCE]
    hours, _ = hours_to_remove(world, finished, final, STANDARD, base['vents'], PREFAB[base['vent']]['pressurePerTick'])
    return hours


# ---- the deliberate oxygen dump on Vulcan ---------------------------------------------------------------

def oxygen_dump(world='Vulcan2', **kw):
    """Strip Vulcan's fuel by dumping just enough oxygen into its air for the planet to burn all of it,
    then terraform from what is left. Priced against the route that takes the fuel out by dilution."""
    from cost import estimate
    from path import plan
    p = Planet(world, **kw)
    start = dict(p.air)
    need = sum(start.get(f, 0.0) * TABLE[(f, 'Oxygen')][1] / TABLE[(f, 'Oxygen')][0] for f in FUELS)
    dumped = dict(start, Oxygen=start.get('Oxygen', 0.0) + need)
    cold, hot = p.extremes()
    after, energy, rise = burn_out(dumped, hottest_mid_orbit(p))
    after = {g: m for g, m in after.items() if m > 1e-9}
    lit = ignition_kelvin(dumped) is not None and hot > ignition_kelvin(dumped)
    base = estimate(world, **kw)
    route = plan(world, start=after, **kw)
    dump = None
    if route and route.get('reached'):
        route['added'] = dict(route['added'])
        route['added']['Oxygen'] = route['added'].get('Oxygen', 0.0) + need
        dump = estimate(world, route=route, **kw)
    return {'world': world, 'oxygen per cell': need, 'lit by heat': lit, 'hot': hot, 'heat per cell J': energy,
            'rise at the hottest hour K': rise, 'air after': after, 'baseline': base, 'dump': dump, 'dump route': route}


# ---- the report -----------------------------------------------------------------------------------------

WORLDS = ('Mars2', 'Lunar', 'Europa3', 'MimasHerschel', 'Venus', 'Vulcan2')


def cost_change(r):
    saved = sum((r.get('absorbed') or {}).values())
    parts = []
    if saved > 1e-9:
        parts.append('%.2f mol a cell less to add' % saved)
    if r.get('extra'):
        parts.append('+%.1f h removal' % r.get('hours', 0.0))
    return ', '.join(parts) or 'none'


def table(results, dump):
    rows = ['| World | Route | Fuel beside an oxidiser | Ignition crossing | Fire size (per cell) | Heat | Cost change | Verdict |',
            '| --- | --- | --- | --- | --- | --- | --- | --- |']
    for r in results:
        if not r['windows']:
            rows.append('| %s | %s | never | none | none | none | none | %s |' % (r['world'], r['route'], r['verdict']))
            continue
        for w in r['windows']:
            burnt = ', '.join('%s %.2f' % (g, d) for g, d in sorted(w['burnt'].items()) if d > 1e-6)
            rows.append('| %s | %s | %s, steps %d to %d: %s | %s (hottest hour %.0f K, storms +%.0f K, ignition %s) | %s | %.1f GJ per M cells, +%.0f K at most, fading | %s | %s |' % (
                r['world'], r['route'], w['kind'], w['first'], w['last'],
                ', '.join('%s %g' % kv for kv in w['air'].items()), w['state'], w['hot'], STORM_RISE_K.get(r['world'], 0.0),
                ('%.0f K' % w['ignition']) if w['ignition'] else 'never (no fuel above 1 mol a cell)',
                burnt, w['energy'] / 1e3, w['worst_rise'], cost_change(r), r['verdict']))
    if dump:
        b, d = dump['baseline'], dump['dump']
        change = ('%.0f h against %.0f h' % (d['hours'], b['hours'])) if d and b else 'no route from the burnt air'
        rows.append('| Vulcan2 | oxygen dump: %.1f O2 per cell, then the route from the burnt air | deliberate, at once | lit by heat (hottest hour %.0f K) | all fuel: %s | %.1f GJ per M cells, +%.0f K, fading by half every hour | %s | %s |' % (
            dump['oxygen per cell'], dump['hot'], ', '.join('%s %.1f' % (g, m) for g, m in sorted(dump['air after'].items())),
            dump['heat per cell J'] / 1e3, dump['rise at the hottest hour K'], change,
            'faster' if d and b and d['hours'] < b['hours'] else ('slower' if d and b else 'blocked')))
    return rows


def main():
    results = [scenario(w) for w in WORLDS]
    dump = oxygen_dump()
    if '--json' in sys.argv:
        print(json.dumps({'worlds': results, 'dump': {k: v for k, v in dump.items() if k not in ('dump route', 'baseline', 'dump')},
                          'hours': {'baseline': dump['baseline'] and dump['baseline']['hours'], 'dump': dump['dump'] and dump['dump']['hours']}},
                         indent=1, default=str))
        return 0
    print('\n'.join(table(results, dump)))
    for r in results:
        print('\n%s: %s. %s.' % (r['world'], r['verdict'], r['why']))
    b, d = dump['baseline'], dump['dump']
    if b and d:
        print('\nVulcan2 oxygen dump: %.1f O2 per cell burns %s; %.0f h (adding %.0f, removing %.0f) against %.0f h (adding %.0f, removing %.0f) by dilution.' % (
            dump['oxygen per cell'], ', '.join('%s %.1f' % kv for kv in sorted(dump['air after'].items())),
            d['hours'], d['addition hours'], d['removal hours'], b['hours'], b['addition hours'], b['removal hours']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
