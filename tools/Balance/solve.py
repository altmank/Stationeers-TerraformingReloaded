"""Cheapest air that meets a habitability level, by constrained optimisation.

    python tools/Balance/solve.py [world ...]

A coarse grid search came first and could not find mixes that need a precise warming dose against a
lot of cooling bulk gas (Vulcan); this replaced it. Work is moles moved per outdoor cell:
added plus removed. Each gas is split into an amount added and an amount removed so the cost is
linear; the habitability limits are the constraints. SLSQP from many starts, and every answer is
re-checked with the model's own report(), so a solver slip cannot pass as a result.
"""
import sys

import numpy as np
from scipy.optimize import minimize

from planet import Planet, TOXINS

# What a base can realistically add in bulk. Fuels are left out by default: hydrogen or volatiles beside
# oxygen is a fire, not a recipe. ANY is for hunting exploits.
ADDABLE = ('Oxygen', 'Nitrogen', 'CarbonDioxide', 'Pollutant')
ANY = ADDABLE + ('Methane', 'NitrousOxide', 'Hydrogen')
LIMITS = {
    # level: (ppO2 min, toxins max, T min, T max, P min, P max)
    'helmet_off': (16.0, 1.0, 263.15, 323.15, 0.0, 1e9),
    'shirt_sleeves': (16.0, 0.5, 273.15, 323.15, 20.0, 303.97),
}
FUELS = ('Methane', 'Hydrogen')   # beside oxygen these burn at the first spark (GAME-MODEL.md), so habitable air holds none
MARGIN = 0.25       # kelvin and kPa kept in hand so the answer survives report()'s exact test


def _with(model, world, mix, **kw):
    p = model(world, **kw)
    p.air = {g: m for g, m in mix.items() if m > 0.0}
    return p


def cheapest(world, level='shirt_sleeves', model=Planet, starts=40, seed=1, addable=ADDABLE, **kw):
    """Returns (work, mix, report) or None."""
    start = model(world, **kw).air
    gases = sorted(set(start) | set(addable))
    s = np.array([start.get(g, 0.0) for g in gases])
    n = len(gases)
    o2_min, tox_max, t_min, t_max, p_min, p_max = LIMITS[level]

    def planet(x):
        amounts = np.maximum(0.0, s + x[:n] - x[n:])
        return _with(model, world, dict(zip(gases, amounts)), **kw)

    def constraints(x):
        p = planet(x)
        cold, hot = p.extremes()
        toxins = sum(p.partial_at(g, hot) for g in TOXINS)
        return np.array([
            p.partial_at('Oxygen', cold) - o2_min - MARGIN,
            tox_max - toxins - 0.02,
            cold - t_min - MARGIN,
            t_max - hot - MARGIN,
            p.pressure_at(cold) - p_min - MARGIN,
            p_max - p.pressure_at(hot) - MARGIN,
            0.4 - sum(p.air.get(f, 0.0) for f in FUELS),
        ])

    bounds = [(0.0, 2000.0 if g in addable else 0.0) for g in gases] + [(0.0, float(v)) for v in s]
    rng = np.random.default_rng(seed)
    best = None
    for k in range(starts):
        x0 = np.zeros(2 * n)
        x0[:n] = rng.uniform(0.0, 1.0, n) ** 3 * rng.choice((50.0, 300.0, 900.0))
        x0[n:] = s * rng.choice((0.0, 0.5, 1.0), n)
        try:
            r = minimize(lambda x: x.sum(), x0, method='SLSQP', bounds=bounds,
                         constraints=[{'type': 'ineq', 'fun': constraints}],
                         options={'maxiter': 300, 'ftol': 1e-6, 'eps': 1e-3})
        except Exception:
            continue
        p = planet(r.x)
        rep = p.report()
        if not rep[level] or sum(p.air.get(f, 0.0) for f in FUELS) > 0.5:
            continue
        work = sum(abs(p.air.get(g, 0.0) - start.get(g, 0.0)) for g in gases)
        if best is None or work < best[0]:
            best = (work, dict(p.air), rep)
    return best


def describe(world, found, model=Planet, **kw):
    if not found:
        return 'not reachable'
    work, mix, rep = found
    start = model(world, **kw).air
    delta = ', '.join('%s %+.0f' % (g[:4], mix.get(g, 0.0) - start.get(g, 0.0))
                      for g in sorted(set(mix) | set(start)) if abs(mix.get(g, 0.0) - start.get(g, 0.0)) >= 0.5)
    return '%4.0f mol/cell  %3.0f/%3.0f K  %5.1f kPa  ppO2 %4.1f  [%s]' % (
        work, rep['T_hot'], rep['T_cold'], rep['P_kPa'], rep['ppO2'], delta)


if __name__ == '__main__':
    worlds = sys.argv[1:] or ('Mars2', 'Lunar', 'Europa3', 'MimasHerschel', 'Venus', 'Vulcan2')
    for world in worlds:
        print('%-14s %s' % (world, describe(world, cheapest(world))))
