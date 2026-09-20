"""Writes a synthetic stand-in for tools/Balance/gamedata.json.

The real file is dumped by the game and is game data, so it is not in the repository and CI has no
game to make it with. Nothing here is copied from the game: the curves are straight lines and the
worlds are invented to corner the temperature rule (a hot greenhouse world, a cold one, an airless
one, one with a huge day-night swing, one that ships its own curves, and one in the anchoring blend
band). The tests check the rule's invariants, not the game's numbers.

    python tools/ci/make_fixture.py <out.json>
"""
import json
import sys


def line(points, xs):
    """Piecewise-linear table through `points`, sampled at xs."""
    def at(x):
        if x <= points[0][0]:
            return points[0][1]
        for (x0, y0), (x1, y1) in zip(points, points[1:]):
            if x <= x1:
                return y0 + (y1 - y0) * (x - x0) / (x1 - x0)
        return points[-1][1]
    return {'x': list(xs), 'y': [at(x) for x in xs]}


DENSITY = [i * 0.5 for i in range(0, 601)]
INDEX = list(range(-300, 401))
ANGLE = list(range(0, 181, 5))
PERCENT = list(range(0, 101, 5))
PRESSURE = [i * 1.0 for i in range(0, 401)] + [500.0, 1000.0, 7000.0]


def gas(per_mmol, freezing, min_liquid=6.3, boil_slope=0.2, can_freeze=True, can_condense=True):
    return {
        'state': 'Gas', 'specificHeat': 20.0,
        'freezingK': freezing, 'canFreeze': can_freeze, 'canCondense': can_condense,
        'minLiquidPressureKPa': min_liquid, 'maxLiquidTemperatureK': freezing + 200.0,
        'latentHeatOfVaporization': 10000.0,
        'boilingKByPressureKPa': line([(0.0, freezing), (400.0, freezing + boil_slope * 400.0), (7000.0, freezing + boil_slope * 400.0)], PRESSURE),
        'ghgIndexByMmolPerLitre': line([(0.0, 0.0), (300.0, per_mmol * 300.0)], DENSITY) if per_mmol else None,
    }


def world(air, base=None, solar=None, own=False, sun=(1000.0, 1000.0)):
    cells = 5000000.0
    return {
        'gravity': -3.7, 'volumeLitres': cells * 8000.0,
        'startMolesTotal': {g: m * cells for g, m in air.items()},
        'startGhgIndex': 0.0, 'startDensityMmolPerLitre': 0.0,
        'solarIrradianceMin': sun[0], 'solarIrradianceMax': sun[1], 'solarConstant': 1367.0, 'noonSolarAngle': 45.0,
        'baseTemperatureBySolarAngle': line(base, ANGLE) if base else None,
        'solarOffsetByPercent': {'day': line(solar, PERCENT)} if solar else None,
        'ghgOffsetByIndex': GHG if own else None,
        'densityOffsetByMmolPerLitre': DENSITY_CURVE if own else None,
    }


GHG = {k: line([(-100.0, -50.0), (0.0, 0.0), (100.0, 250.0)], INDEX) for k in ('day', 'night')}
DENSITY_CURVE = {
    'day': line([(0.0, 50.0), (34.0, 0.0), (300.0, -10.0)], DENSITY),
    'night': line([(0.0, -60.0), (34.0, 0.0), (300.0, 10.0)], DENSITY),
}

data = {
    'gameVersion': 'synthetic', 'gridVolumeLitres': 8000.0, 'tickSeconds': 0.5,
    'currentWorld': 'Mars2', 'currentSolarIrradiance': 600.0,
    'gases': {
        'Oxygen': gas(-0.2, 56.0), 'Nitrogen': gas(-0.7, 40.0), 'CarbonDioxide': gas(1.4, 218.0, min_liquid=517.0),
        'Methane': gas(2.9, 81.0), 'Pollutant': gas(3.9, 173.0, min_liquid=1800.0),
        'Helium': gas(0.0, 0.0, can_freeze=False, can_condense=False), 'Hydrogen': gas(-0.9, 15.0),
        'NitrousOxide': gas(-3.3, 251.0, min_liquid=800.0), 'HydrochloricAcid': gas(0.0, 247.0, boil_slope=0.3),
        'Water': {'state': 'Liquid', 'specificHeat': 70.0, 'ghgIndexByMmolPerLitre': None},
    },
    'worlds': {
        # Named Mars2 because planet.py takes the mod's default curves from that world.
        'Mars2': world({'CarbonDioxide': 8.66}, base=[(0.0, 266.0), (180.0, 248.0)], own=True, sun=(500.0, 700.0)),
        'Hot': world({'CarbonDioxide': 200.0, 'HydrochloricAcid': 90.0}, base=[(0.0, 737.0), (180.0, 737.0)], sun=(2600.0, 2700.0)),
        'Swing': world({'CarbonDioxide': 12.0, 'Methane': 27.0, 'Pollutant': 15.0}, base=[(0.0, 975.0), (180.0, 400.0)],
                       solar=[(0.0, 0.0), (100.0, 750.0)], sun=(1000.0, 3400.0)),
        'Cold': world({'Oxygen': 340.0}, base=[(0.0, 134.0), (180.0, 124.0)], sun=(45.0, 56.0)),
        'Airless': world({}, sun=(1300.0, 1400.0)),
        'Band': world({'CarbonDioxide': 40.0}, base=[(0.0, 500.0), (180.0, 480.0)], sun=(1000.0, 1000.0)),   # index 7: half anchored
    },
    'prefabs': {},
}

if __name__ == '__main__':
    with open(sys.argv[1], 'w', encoding='utf-8') as f:
        json.dump(data, f)
    print('wrote', sys.argv[1])
