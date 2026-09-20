"""Can a recipe actually be reached, step by step?

    python tools/Balance/path.py [world ...]

solve.py finds the cheapest habitable air. That is an end state. The planet tick freezes a whole gas
off the planet in one tick below its freezing point + 2 K and rains it out below its boiling point,
so on a cold world the warming gas a recipe calls for cannot simply be added: it would not stay in
the air. This walks from the world's starting air to the recipe in small steps, only ever making a
change that holds, and when nothing in the recipe can be added yet it looks for the cheapest
temporary gas that gets it unstuck (methane stays a gas down to 84 K, for instance) and counts adding
and later removing it as work.

Rules applied at every step:
  - a gas may only be added if, once added, it stays in the air at the coldest point of the day;
  - nothing already placed may be lost to a step (a removal that lets the planet cool below the
    freezing point of a gas it still relies on is refused);
  - a temporary gas is ramped in, and every part of the ramp has to hold: the planet tick freezes a
    gas whole, so an amount that would only be stable once it is all there can never be built up;
  - phase change is a tool as well as an obstacle. A gas the recipe wants less of that freezes or
    rains out on the way is let go for free and noted. At the end each such gas is tested against the
    finished planet: if the hottest hour would melt and boil it back into the air, removing it is
    counted as the player's work after all (it has to be caught as it returns);
  - when no gas helps, outside heat is tried: the game banks the heat of everything vented outdoors
    and the mod lets it fade (half-life setting) and caps it (50 K by default). Gas vented warmer
    than the planet therefore holds the planet above its settled temperature for as long as the
    venting goes on. The planner holds the smallest kick that unsticks it, and at the end the recipe
    has to stand with the kick gone;
  - fuel (volatiles, hydrogen) and an oxidiser (oxygen, nitrous oxide, ozone) are never outdoors
    together. `Atmosphere.TryCombust` burns any fuel beside any oxidiser once a cell is sparked, at any
    temperature and with no minimum ratio, a burning cell sparks its neighbours, and above 573 K no
    spark is needed. The planet itself never burns, only outdoor cells, so such air is survivable for
    a careful player; but a route that depends on nobody ever striking a spark is not one to recommend.
    SAFE = False shows what the fire rule costs;
  - when adding nothing helps, taking a gas out for a while is tried too (Europa's oxygen, so that
    volatiles can warm the planet safely), and counted both ways.

A failure here is "this planner found no path", not proof that none exists. Every path it does find
should be replayed in the game at its waypoints with tools/LiveCheck/run.ps1 -Observe -SetAir.
"""
import sys

from planet import DATA, GAS_ORDER, Planet
from solve import cheapest

FUELS = ('Methane', 'Hydrogen')
OXIDISERS = ('Oxygen', 'NitrousOxide', 'Ozone')
IGNITION_K = 573.0              # GAME-MODEL.md
SAFE = True                     # never fuel beside an oxidiser outdoors; False allows it below auto-ignition
STEPS = 40
HELPERS = ('Methane', 'Pollutant', 'CarbonDioxide', 'Helium', 'Hydrogen', 'Nitrogen', 'Oxygen')


HEAT_CAP_K = 50.0               # Settings.MaxExternalOffsetKelvin default
_heat = {'kelvin': 0.0}


def _planet(world, mix, **kw):
    p = Planet(world, **kw)
    p.external = _heat['kelvin']
    p.air = {g: m for g, m in mix.items() if m > 1e-9}
    return p


def _refused(world, mix, wanted, **kw):
    """Why this air cannot be held, or None. `wanted` are gases whose loss would be a failure."""
    p = _planet(world, mix, **kw)
    unstable = p.unstable()
    lost = [g for g in unstable if g in wanted and mix.get(g, 0.0) > 1e-9]
    if lost:
        return '%s %s' % (lost[0], unstable[lost[0]])
    _, hot = p.extremes()
    if any(mix.get(f, 0.0) > 0.5 for f in FUELS) and any(mix.get(o, 0.0) > 0.5 for o in OXIDISERS):
        if SAFE:
            return 'fuel beside an oxidiser outdoors burns at the first spark'
        if hot >= IGNITION_K:
            return 'fuel beside an oxidiser at %.0f K ignites by itself' % hot
    return None


def _comes_back(world, mix, gas, **kw):
    """Would this gas, lying frozen or liquid on the finished planet, return to the air at its hottest?"""
    p = _planet(world, mix, **kw)
    _, hot = p.extremes()
    info = DATA['gases'][gas]
    if info['canFreeze'] and hot < info['freezingK'] + 2.0:
        return False                                  # stays in the ice caps
    pressure = p.pressure_at(hot)
    if info['canCondense'] and pressure >= info['minLiquidPressureKPa']:
        from planet import interp
        if hot < interp(info['boilingKByPressureKPa'], pressure) + 2.0:
            return False                              # stays liquid
    return True


def plan(world, level='shirt_sleeves', **kw):
    """The cheapest route found over the orders the greedy walk can take its steps in. Removing first
    suits a world whose air is in the way; adding first suits one that needs bulk gas to steady it
    before anything is taken out (Vulcan). Still a heuristic: a found route is an upper bound."""
    found = cheapest(world, level, **kw)
    if not found:
        return None
    routes = [_walk(world, level, found, order, **kw) for order in ('removals first', 'additions first')]
    reached = [r for r in routes if r['reached']]
    best = min(reached, key=lambda r: r['work']) if reached else routes[0]
    best['order'] = ('removals first', 'additions first')[routes.index(best)]
    return best


def _walk(world, level, found, order, **kw):
    _heat['kelvin'] = 0.0
    _, final, _ = found
    start = Planet(world, **kw).air
    gases = sorted(set(start) | set(final))
    mix = dict(start)
    helpers = {}                                    # temporary gas -> amount in the air now
    let_go = {}                                     # gas -> moles that froze or rained out on the way
    set_aside = {}                                  # gas -> moles taken out for a while, to go back in at the end
    stages, work = [], 0.0

    def remaining():
        # A gas set aside goes back in at the very end, once the helpers it made room for are out.
        return {g: (0.0 if g in set_aside else final.get(g, 0.0) - mix.get(g, 0.0) + helpers.get(g, 0.0)) for g in gases}

    def wanted_now():
        return {g for g in set(mix) | set(helpers) if mix.get(g, 0.0) > start.get(g, 0.0) + 1e-9 or g in helpers}

    for _ in range(400):
        # Anything the recipe wants less of that the planet is removing by itself: let it.
        current = _planet(world, mix, **kw)
        for gas, why in current.unstable().items():
            surplus = mix.get(gas, 0.0) - final.get(gas, 0.0) - helpers.get(gas, 0.0)
            if surplus > 1e-6 and gas not in wanted_now():
                let_go[gas] = let_go.get(gas, 0.0) + mix[gas]
                stages.append('%.0f %s leaves the air by itself: %s' % (mix[gas], gas, why))
                mix[gas] = 0.0
                final = dict(final)
                final[gas] = 0.0
        rest = {g: d for g, d in remaining().items() if abs(d) > 1e-6}
        if not rest:
            break
        progressed, blocked = False, {}
        for gas in sorted(rest, key=lambda g: rest[g], reverse=(order == 'additions first')):
            total = final.get(gas, 0.0) - start.get(gas, 0.0)
            step = rest[gas] if abs(rest[gas]) <= abs(total) / STEPS + 1e-9 else (total / STEPS)
            trial = dict(mix)
            trial[gas] = max(0.0, trial.get(gas, 0.0) + step)
            why = _refused(world, trial, wanted_now() | ({gas} if step > 0 else set()), **kw)
            if not why and step > 0:
                puff = dict(mix)                     # the first puff has to hold too, not only the whole step
                puff[gas] = puff.get(gas, 0.0) + min(step, 0.01)
                why = _refused(world, puff, wanted_now() | {gas}, **kw)
            if why:
                blocked[gas] = why
                continue
            mix = trial
            work += abs(step)
            progressed = True
        if progressed:
            continue
        # Stuck: find the cheapest temporary gas that unblocks something.
        best = None
        for helper in HELPERS:
            if helper in blocked:
                continue                              # adding a gas in order to be allowed to remove it is no help
            for amount in (2, 5, 10, 20, 40, 80, 160, 320):
                trial = dict(mix)
                trial[helper] = trial.get(helper, 0.0) + amount
                ramp_holds = True
                # From the first puff: a gas that only holds once a lot of it is there can never start.
                for fraction in [2.0 ** -k for k in range(12, 4, -1)] + [part / 16.0 for part in range(1, 17)]:
                    ramp = dict(mix)
                    ramp[helper] = ramp.get(helper, 0.0) + amount * fraction
                    if _refused(world, ramp, wanted_now() | {helper}, **kw):
                        ramp_holds = False
                        break
                if not ramp_holds:
                    continue
                opened = False
                for gas, why in blocked.items():
                    total = final.get(gas, 0.0) - start.get(gas, 0.0)
                    probe = dict(trial)
                    probe[gas] = max(0.0, probe.get(gas, 0.0) + total / STEPS)
                    if not _refused(world, probe, wanted_now() | {helper, gas}, **kw):
                        opened = True
                        break
                if opened:
                    if best is None or amount < best[1]:
                        best = (helper, amount)
                    break
        if not best:
            # Nothing to add helps. Try taking something out of the way for a while.
            # Only an oxidiser is worth setting aside, and only to let a fuel in as the warming gas.
            for gas in sorted(mix, key=lambda g: mix[g]):
                if mix.get(gas, 0.0) <= 0.5 or gas in set_aside or gas not in OXIDISERS:
                    continue
                trial = dict(mix)
                trial[gas] = 0.0
                if _refused(world, trial, wanted_now() - {gas}, **kw):
                    continue
                opened = False
                for blocked_gas in FUELS:
                    probe = dict(trial)
                    probe[blocked_gas] = probe.get(blocked_gas, 0.0) + 0.01
                    if blocked_gas != gas and not _refused(world, probe, (wanted_now() - {gas}) | {blocked_gas}, **kw):
                        opened = True
                        break
                if opened:
                    set_aside[gas] = mix[gas]
                    work += mix[gas]
                    stages.append('take the %.0f %s out for now (was stuck on: %s)' % (mix[gas], gas, '; '.join('%s: %s' % kv for kv in blocked.items())))
                    mix = trial
                    break
            else:
                gas = None
            if gas is not None and gas in set_aside and mix.get(gas, 0.0) == 0.0:
                continue
        if not best and _heat['kelvin'] < HEAT_CAP_K:
            _heat['kelvin'] = min(HEAT_CAP_K, _heat['kelvin'] + 5.0)
            p = _planet(world, mix, **kw)
            cold, hot = p.extremes()
            stages.append('hold the planet %.0f K warm with vented heat (was stuck on: %s) -> %.0f..%.0f K' % (
                _heat['kelvin'], '; '.join('%s: %s' % kv for kv in blocked.items()), cold, hot))
            continue
        if not best:
            p = _planet(world, mix, **kw)
            cold, hot = p.extremes()
            return {'world': world, 'reached': False, 'final': final, 'stages': stages, 'work': work,
                    'stuck': blocked, 'cold': cold, 'hot': hot, 'mix': mix}
        helper, amount = best
        mix[helper] = mix.get(helper, 0.0) + amount
        helpers[helper] = helpers.get(helper, 0.0) + amount
        work += amount
        p = _planet(world, mix, **kw)
        cold, hot = p.extremes()
        stages.append('add %g %s as a temporary gas (was stuck on: %s) -> %.0f..%.0f K' % (
            amount, helper, '; '.join('%s: %s' % kv for kv in blocked.items()), cold, hot))

    # Let the heat go first, a step at a time; everything placed has to survive without it.
    while _heat['kelvin'] > 0.0:
        _heat['kelvin'] = max(0.0, _heat['kelvin'] - 5.0)
        why = _refused(world, mix, set(final) | set(helpers), **kw)
        if why:
            return {'world': world, 'reached': False, 'final': final, 'stages': stages, 'work': work,
                    'stuck': {'heat': 'the planet cannot yet stand without the vented heat: ' + why}, 'mix': mix}
    if any('vented heat' in stage for stage in stages):
        stages.append('let the vented heat fade; the air now holds the temperature by itself')
    # The recipe is in place with helpers still in the air; take them out again in steps.
    for helper in list(helpers):
        amount = helpers[helper]
        for _ in range(STEPS):
            trial = dict(mix)
            trial[helper] = max(final.get(helper, 0.0), trial[helper] - amount / STEPS)
            why = _refused(world, trial, set(final), **kw)
            if why:
                return {'world': world, 'reached': False, 'final': final, 'stages': stages, 'work': work,
                        'stuck': {helper: 'cannot be taken back out: ' + why}, 'mix': mix}
            work += mix[helper] - trial[helper]
            mix = trial
        stages.append('take the %g %s back out' % (amount, helper))
    # What was set aside goes back in, to the recipe's amount.
    for gas in list(set_aside):
        target = final.get(gas, 0.0)
        for _ in range(STEPS):
            trial = dict(mix)
            trial[gas] = min(target, trial.get(gas, 0.0) + target / STEPS)
            why = _refused(world, trial, set(final), **kw)
            if why:
                return {'world': world, 'reached': False, 'final': final, 'stages': stages, 'work': work,
                        'stuck': {gas: 'cannot be put back: ' + why}, 'mix': mix}
            work += trial[gas] - mix.get(gas, 0.0)
            mix = trial
        stages.append('put %.0f %s back' % (target, gas))
    for gas, moles in let_go.items():
        if _comes_back(world, mix, gas, **kw):
            work += moles
            stages.append('the %.0f %s that left would return at the hottest hour, so removing it is counted as work' % (moles, gas))
        else:
            stages.append('the %.0f %s that left stays out of the air on the finished planet: free removal' % (moles, gas))
    p = _planet(world, mix, **kw)
    direct = sum(abs(final.get(g, 0.0) - start.get(g, 0.0)) for g in gases)
    return {'world': world, 'reached': bool(p.report()[level]), 'final': final, 'stages': stages,
            'work': work, 'direct': direct, 'mix': mix, 'report': p.report()}


if __name__ == '__main__':
    for world in (sys.argv[1:] or ('Mars2', 'Lunar', 'Europa3', 'MimasHerschel', 'Venus', 'Vulcan2')):
        result = plan(world)
        if result is None:
            print('%-14s no habitable end state found' % world)
            continue
        recipe = ', '.join('%s %.0f' % (g[:4], m) for g, m in sorted(result['final'].items()) if m >= 0.5)
        if result['reached']:
            print('%-14s reachable, %s: %.0f mol/cell moved (%.0f if the recipe could be added directly). End air: %s' % (
                world, result['order'], result['work'], result['direct'], recipe))
        else:
            print('%-14s NO PATH FOUND to: %s. Stuck at %.0f..%.0f K on: %s' % (
                world, recipe, result.get('cold', 0), result.get('hot', 0), result.get('stuck') or ('ran out of steps or ended off the recipe: %s' % {k: round(v, 1) for k, v in result['mix'].items() if v > 0.05})))
        for stage in result['stages']:
            print('                 - ' + stage)
