"""What actually limits terraforming, phase by phase.

    python tools/Balance/cost.py [world ...]

A mole added and a mole removed are not the same job, and this prices them apart.

  - **Adding** gas means mining ice and letting it melt. The limit is how fast ice arrives, so the
    real unit is moles of ice mined, not moles delivered: there is no oxygen tap, and the carbon
    dioxide a cold world needs is normally made by burning volatiles, which is a mole multiplier.
  - **Removing** gas is dilution. A base cannot reach into the planet and pick the acid out; it draws
    outdoor air in, keeps what it does not want and puts the rest back, so every mole drawn carries
    only the unwanted gas's current share (`removal.py`). Gas production has nothing to do with it.
  - **Somewhere to put it.** What comes out has to be stored or sold. That is a build cost, not a
    rate: see `disposal()`.

`path.py` supplies the route: what goes in, what comes out, and the air after every change. A gas
that goes in as a temporary warming blanket and comes back out again counts on both sides; it is not
allowed to net to zero, because the player does both jobs.

Phases are summed when the route forbids them overlapping and taken at the max when it does not, and
`estimate()` says which of the two binds. Every assumption is in docs/ASSUMPTIONS.md.
"""
import math
import sys

from path import _refused, _heat, plan
from planet import DATA, Planet, R
from removal import FLOOR, intake_per_hour
from solve import cheapest

PREFAB = DATA['prefabs']
TICKS_PER_HOUR = 3600.0 / DATA['tickSeconds']
CELL_LITRES = DATA['gridVolumeLitres']
STANDARD = 0.05                 # the mod's default planet size

# ---- 1. sources: what a mole of gas costs in ice mined ---------------------------------------------
# A rocket mines space ice, and the item it brings back carries the deposit's own mixture, 24 moles of
# it (MineableDeposit.SpawnIce; the compositions are in the game's rocketlocations.xml). You take the
# whole item, so a mole of the gas you want costs a little more than a mole of ice. The richest
# shipped deposit for oxygen, nitrogen, volatiles and helium is 22 of its 24 moles; the richest
# carbon dioxide deposit is only 14 of 24, which is why burning volatiles beats mining it (below).
ICE_MOLES_PER_ITEM = 24.0
RICH_SITE = 22.0 / 24.0
CO2_SITE = 14.0 / 24.0
GROUND_ICE = 22.5 / 25.0        # ItemOxite: 22.5 oxygen of 25 moles. Nitrice and volatiles are the same shape

# Combustion.ResultMethaneOxygen, read from the game: 2 volatiles + 1 oxygen -> 6 CO2 + 3 pollutant.
# Three moles in, nine out. That mole multiplier is why a base burns for its carbon dioxide instead of
# mining it, and the price is the pollutant, which has to be caught before it reaches the air.
BURN_FUEL, BURN_OXIDISER = 2.0, 1.0
BURN_OUT = {'CarbonDioxide': 6.0, 'Pollutant': 3.0}

# ---- 2. machine rates, mol per hour (MEASURED prefab values in CODE formulas) -----------------------
def vent_out_per_hour(prefab, kelvin):
    """ActiveVent.PumpGasToWorld: pressurePerTick worth of an 8000 L cell per tick."""
    return PREFAB[prefab]['pressurePerTick'] * CELL_LITRES / (R * kelvin) * TICKS_PER_HOUR


RATES = {
    # Moving gas outdoors. At 280 K. Never the bottleneck: compare with the sources below.
    'active vent, outward': vent_out_per_hour('StructureActiveVent', 280.0),
    'large powered vent, outward': vent_out_per_hour('StructurePoweredVentLarge', 280.0),
    # Rocket ice mining, while the rocket is actually mining: MineableDeposit.OreQuantity and
    # TimeToMine give 10-13 items per 8.4-9.2 s cycle at the richness floor, 24 moles an item, and the
    # ice drill head multiplies the yield.
    'rocket ice miner, while mining': 0.5 * (93900 + 133700) * PREFAB['ItemRocketMiningDrillHeadIce']['IceYieldMultiplier'],
    # Hand mining: 7 items a minable at normal yield (VeinGenerationData), 25 moles an item. 150
    # minables an hour is an ASSUMPTION about a player who is also hauling and crushing.
    'one player hand-mining ice': 150 * 7 * 25.0,
}

# ---- 3. the base: ASSUMPTIONS -----------------------------------------------------------------------
# Two capacities, because the two jobs use different machines and neither queues behind the other.
# `duty` is the share of wall-clock time the source is really producing: rockets travel, players sleep.
# `vents` is inward vents; each needs a partner pushing the filtered air back out, so the structure
# count is double. Gas traders are left out of both: see docs/BALANCE.md.
BASES = {
    'small':  {'hand': 1, 'rockets': 0, 'duty': 0.5, 'vents': 4,  'vent': 'StructureActiveVent',
               'says': 'one player hand-mining ice, four active vents'},
    'medium': {'hand': 0, 'rockets': 1, 'duty': 0.5, 'vents': 8,  'vent': 'StructureActiveVent',
               'says': 'one ice rocket mining half the time, eight active vents'},
    'mega':   {'hand': 0, 'rockets': 4, 'duty': 0.6, 'vents': 20, 'vent': 'StructurePoweredVentLarge',
               'says': 'four ice rockets mining 60 % of the time, twenty large powered vents'},
}
REFERENCE = 'mega'


def ice_rate(base):
    return (base['rockets'] * RATES['rocket ice miner, while mining']
            + base['hand'] * RATES['one player hand-mining ice']) * base['duty']


# ---- 4. the addition phase -------------------------------------------------------------------------
def ice_budget(new):
    """Ice a set of additions costs, in moles of ice mined per outdoor cell.

    `new` is gas that has to be brought from somewhere, in moles per cell. Gas the route took out of
    the planet's own air and put back later is not in it: that comes out of the tanks it went into.
    """
    co2 = new.get('CarbonDioxide', 0.0)
    fuel = co2 * BURN_FUEL / BURN_OUT['CarbonDioxide']
    burn_oxygen = co2 * BURN_OXIDISER / BURN_OUT['CarbonDioxide']
    pollutant_made = co2 * BURN_OUT['Pollutant'] / BURN_OUT['CarbonDioxide']
    wanted = {g: m for g, m in new.items() if m > 0.0 and g not in ('CarbonDioxide',)}
    # The burn makes far more pollutant than any recipe asks for, so the recipe's share is free.
    wanted['Pollutant'] = max(0.0, wanted.get('Pollutant', 0.0) - pollutant_made)
    wanted['Oxygen'] = wanted.get('Oxygen', 0.0) + burn_oxygen
    wanted['Methane'] = wanted.get('Methane', 0.0) + fuel
    ice = {g: m / RICH_SITE for g, m in wanted.items() if m > 0.0}
    return {
        'ice': sum(ice.values()),
        'by gas': ice,
        'pollutant to store': max(0.0, pollutant_made - new.get('Pollutant', 0.0)),
        # The alternative: skip the burner and mine carbon dioxide straight out of a carbon-dioxide
        # deposit. No pollutant to catch, but more ice, because the richest such deposit is 14 of 24.
        'ice, mining the CO2 instead': sum(m / RICH_SITE for g, m in wanted.items()
                                           if m > 0.0 and g not in ('Methane', 'Oxygen'))
                                       + max(0.0, new.get('Oxygen', 0.0)) / RICH_SITE + co2 / CO2_SITE,
    }


# ---- 5. the removal phase --------------------------------------------------------------------------
def _intake(world, mix, size, vents, pressure_per_tick, **kw):
    """Moles an hour the inward vents draw, for this air. Day and night averaged, as removal.py does."""
    p = Planet(world, size=size, **kw)
    p.air = {g: m for g, m in mix.items() if m > 0.0}
    cold, hot = p.extremes()
    return 0.5 * vents * sum(intake_per_hour(pressure_per_tick, p.pressure_at(k), k) for k in (cold, hot) if k > 1.0)


def _stream(world, before, after, cut, cells, size, vents, pressure_per_tick, **kw):
    """Hours and moles of outdoor air to cut one gas by `cut` log-units over one change in the route.

    Integrating dn/dt = -intake x n/N from n to n' gives a stream of N x ln(n/n'). N and the intake
    both move as the air thins, so the change is walked in small log-steps.
    """
    hours = drawn = 0.0
    steps = max(1, int(cut / 0.05) + 1)
    for k in range(steps):
        part = (k + 0.5) / steps
        mix = {g: before.get(g, 0.0) + part * (after.get(g, 0.0) - before.get(g, 0.0))
               for g in set(before) | set(after)}
        total = sum(m for m in mix.values() if m > 0.0)
        rate = _intake(world, mix, size, vents, pressure_per_tick, **kw)
        if rate <= 0.0 or total <= 0.0:
            return float('inf'), drawn
        moles = total * cells * cut / steps
        hours += moles / rate
        drawn += moles
    return hours, drawn


def _overlapping(spans):
    """Gases whose removals overlap in the route come out through one stream of air, so they are
    charged once, for whichever needs the deepest cut. Removals that do not overlap are charged
    separately: one had finished before the other started."""
    groups = []
    for gas, (first, last) in sorted(spans.items(), key=lambda kv: kv[1]):
        if groups and first <= groups[-1][1]:
            groups[-1] = (groups[-1][0], max(groups[-1][1], last), groups[-1][2] + [gas])
        else:
            groups.append((first, last, [gas]))
    return groups


def removal_hours(world, timeline, size, vents, pressure_per_tick, **kw):
    """Hours of dilution for the whole route, and how much outdoor air goes through the filters.

    A base does not pick a gas out of the planet: it draws outdoor air in, keeps what it does not
    want and puts the rest back, so every mole drawn carries only the unwanted gas's current share.
    A gas that is most of the air therefore comes out nearly mole for mole, and the last tenth of it
    costs as much as the first half did. One stream of air can hold filters for several gases at
    once, so removals that run together cost what the deepest of them costs, not the sum.
    """
    cells = Planet(world, size=size, **kw).cells
    cuts = {}
    for i, (before, after) in enumerate(zip(timeline, timeline[1:])):
        for gas, had in before.items():
            if had > FLOOR and after.get(gas, 0.0) < had - 1e-12:
                cuts.setdefault(gas, []).append((i, math.log(had / max(after.get(gas, 0.0), FLOOR))))
    spans = {gas: (steps[0][0], steps[-1][0]) for gas, steps in cuts.items()}
    hours, drawn = 0.0, 0.0
    for _, _, gases in _overlapping(spans):
        deepest = max(gases, key=lambda g: sum(cut for _, cut in cuts[g]))
        for i, cut in cuts[deepest]:
            h, d = _stream(world, timeline[i], timeline[i + 1], cut, cells, size, vents, pressure_per_tick, **kw)
            hours, drawn = hours + h, drawn + d
    return hours, drawn


# ---- 6. somewhere to put it ------------------------------------------------------------------------
PIPE_BURST_KPA = 60794.998                      # Chemistry.Limits.MAXPressureGasPipe
TANK_LITRES = PREFAB['StructureTankBig']['volume']
STORE_KELVIN = 293.15
MOLAR_VOLUME = {                                # Chemistry.MOLAR_VOLUME_*, litres a mole as liquid
    'CarbonDioxide': 0.04, 'Pollutant': 0.04, 'HydrochloricAcid': 0.028, 'Oxygen': 0.03,
    'Nitrogen': 0.0348, 'Methane': 0.04, 'Hydrogen': 0.028, 'NitrousOxide': 0.026, 'Ozone': 0.026,
}
# The game ships buy orders for the gas a world has too much of (tradeables.xml, "World Atmosphere"):
# Venus carbon dioxide, Vulcan volatiles and hydrogen, Europa oxygen. Each unit is 100 moles, the
# order is 50-200 units, and a large trader doubles it (TraderDataInstance.ApplyBulkMultiplier).
TRADER_BUYS = {'Venus': ('CarbonDioxide', 50, 100), 'Vulcan2': ('Methane', 100, 200),
               'Europa3': ('Oxygen', 100, 200), 'Mars2': ('CarbonDioxide', 50, 100)}
TRADER_BULK, TRADER_UNIT_MOLES = 2.0, 100.0
TRADER_VISIT_HOURS = (2400.0 + 90.0 + 500.0) / 3600.0    # LargeTrader LifeTime + DownTime + SecondsToContact


def tank_moles(gas):
    """Moles of one gas a big tank holds: as liquid where the game gives it a molar volume, else as
    gas at the pressure a pipe bursts at."""
    as_gas = PIPE_BURST_KPA * TANK_LITRES / (R * STORE_KELVIN)
    as_liquid = TANK_LITRES / MOLAR_VOLUME[gas] if gas in MOLAR_VOLUME else 0.0
    return max(as_gas, as_liquid)


def disposal(world, removed, cells):
    """Tanks to hold what comes out, and what the world's own trader would take off your hands."""
    tanks = sum(moles * cells / tank_moles(gas) for gas, moles in removed.items() if moles > 0.0)
    gas, low, high = TRADER_BUYS.get(world, (None, 0, 0))
    per_hour = 0.0
    if gas is not None:
        per_hour = 0.5 * (low + high) * TRADER_BULK * TRADER_UNIT_MOLES / TRADER_VISIT_HOURS
    return {'big tanks': tanks, 'trader takes': gas, 'trader mol/h': per_hour,
            'trader hours for the lot': (removed.get(gas, 0.0) * cells / per_hour) if per_hour else float('inf')}


# ---- 7. put it together ----------------------------------------------------------------------------
def staged(world, route, **kw):
    """Why the two phases cannot run side by side, or None if they can.

    They share no machines: rockets and crushers make gas, vents and filters take it away. What can
    still force an order is the game itself. Two things do: a gas the route puts in and takes out
    again, which is one job after another by definition, and the fire rule, which will not have fuel
    outdoors beside an oxidiser, so one has to be gone before the other arrives.
    """
    churn = sorted(g for g in route['added'] if route['added'][g] > 0.5 and route['removed'].get(g, 0.0) > 0.5)
    if churn:
        return '%s goes in and comes out again' % ', '.join(churn)
    mix = dict(Planet(world, **kw).air)
    for gas, moles in route['added'].items():
        mix[gas] = mix.get(gas, 0.0) + moles
    _heat['kelvin'] = 0.0
    why = _refused(world, mix, set(mix), **kw)
    return ('adding before removing is refused: %s' % why) if why else None


def estimate(world, size=STANDARD, base=REFERENCE, level='shirt_sleeves', route=None, **kw):
    """Hours to terraform one world, split by phase, with the constraint that binds.

    `base` is a name in BASES or a base of your own, for asking what another fleet would do."""
    route = route or plan(world, level, **kw)
    if not route or not route['reached']:
        return None
    b = BASES[base] if isinstance(base, str) else base
    cells = Planet(world, size=size, **kw).cells
    banked = route.get('banked', {})
    new = {g: max(0.0, m - banked.get(g, 0.0)) for g, m in route['added'].items()}
    ices = ice_budget(new)
    supply = ice_rate(b)
    add_hours = ices['ice'] * cells / supply if supply > 0.0 else float('inf')
    remove_hours, drawn = removal_hours(world, route['timeline'], size, b['vents'],
                                        PREFAB[b['vent']]['pressurePerTick'], **kw)
    why = staged(world, route, **kw)
    hours = add_hours + remove_hours if why else max(add_hours, remove_hours)
    return {
        'world': world, 'size': size, 'base': base, 'cells': cells,
        'added': route['added'], 'removed': route['removed'], 'new': new,
        'ice per cell': ices['ice'], 'ice budget': ices,
        'addition hours': add_hours, 'removal hours': remove_hours,
        'air through the filters': drawn, 'removed moles': sum(route['removed'].values()) * cells,
        'staged': why, 'hours': hours,
        'bound by': 'removal' if remove_hours > add_hours else 'addition',
        # Removal time is inversely proportional to the number of inward vents, so this is the vent
        # count below which taking gas out becomes the longer job. It is the honest health warning on
        # "addition-bound": that verdict is bought with vents.
        'vents to keep up': b['vents'] * remove_hours / add_hours if add_hours > 0.0 else float('inf'),
        'disposal': disposal(world, route['removed'], cells),
    }


WORLDS = ('Lunar', 'Mars2', 'Venus', 'Vulcan2', 'MimasHerschel', 'Europa3')


def main():
    print('SUPPLY (mol of ice gas an hour, sustained)')
    for name, b in BASES.items():
        print('  %-8s %10s   %s' % (name, format(round(ice_rate(b)), ','), b['says']))
    print('\nWHAT LIMITS EACH WORLD, at %s size and the %s base' % (STANDARD, REFERENCE))
    for world in (sys.argv[1:] or WORLDS):
        r = estimate(world)
        if r is None:
            print('%-14s no route found' % world)
            continue
        print('\n%-14s %.0f h, %s-bound   (adding %.0f h, removing %.0f h, %s)' % (
            world, r['hours'], r['bound by'], r['addition hours'], r['removal hours'],
            ('in that order: ' + r['staged']) if r['staged'] else 'side by side'))
        print('%-14s   in  %s' % ('', ', '.join('%s %.0f' % (g[:4], m) for g, m in sorted(r['added'].items()) if m >= 0.5) or 'nothing'))
        print('%-14s   out %s' % ('', ', '.join('%s %.0f' % (g[:4], m) for g, m in sorted(r['removed'].items()) if m >= 0.5) or 'nothing'))
        print('%-14s   %.0f mol of ice per cell (%.0f mining the CO2 instead), %.0f pollutant per cell to catch' % (
            '', r['ice per cell'], r['ice budget']['ice, mining the CO2 instead'], r['ice budget']['pollutant to store']))
        if r['removed moles'] > 0.0:
            d = r['disposal']
            print('%-14s   %.0f M mol of outdoor air through the filters to take out %.0f M (x%.1f)' % (
                '', r['air through the filters'] / 1e6, r['removed moles'] / 1e6,
                r['air through the filters'] / r['removed moles']))
            print('%-14s   %.0f big tanks to hold it; the %s trader would take %s at %s mol/h, %s h for the lot' % (
                '', d['big tanks'], world, d['trader takes'] or 'nothing',
                format(round(d['trader mol/h']), ','),
                format(round(d['trader hours for the lot']), ',') if d['trader hours for the lot'] != float('inf') else 'never'))


if __name__ == '__main__':
    main()
