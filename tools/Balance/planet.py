"""The planet as the game computes it, from tools/Balance/gamedata.json.

gamedata.json is written by the game itself (tools/LiveCheck/run.ps1 -Dump): every curve in it was
evaluated by Unity, and every phase-change constant came out of the game's own functions, so nothing
here re-implements them. This module interpolates those tables and adds them up the way
GlobalGasMix.GetGlobalGasMixTemperature does,

    T = base(angle) + sun distance(angle, % of orbit range) + greenhouse(index) + density(mmol/L)

plus the mod's rule for worlds that ship without a greenhouse or density curve (docs/TEMPERATURE.md;
src/Patching/Climate.cs is the same arithmetic and tools/LiveCheck -Observe checks the two agree).

What is and is not here, so nothing is left out silently (docs/ASSUMPTIONS.md has the register):
  - Weather offset, latent heat and external heat are transient terms and are zero in a settled state;
    a mix is judged settled. `external` can be set to study a planet being held warm by vented heat. Paths through unsettled states are checked in the game with -Observe.
  - Phase change is applied as the game's rule for a settled planet: a gas whose freezing point + 2 K
    is above the coldest temperature is not in the air (the planet tick freezes all of it in one
    tick), and a gas below its boiling point + 2 K at the planet's total pressure rains out. unstable()
    says what would leave; settle() removes it, in the game's order.
  - Day and night are taken as the hottest and coldest the formula gives over every sun angle and
    both ends of the orbit, so a mix judged habitable is habitable at any latitude and season.
"""
import bisect
import json
import math
import os

R = 8.3144                      # Chemistry.cs:147
DAY, NIGHT = 0.0, 180.0         # solar angle, degrees from overhead
ANGLES = tuple(range(0, 181, 5))
PHASE_MARGIN = 2.0              # GlobalGasMix.GlobalTemperatureStateChangeOffset

# The mod's rule (Climate.cs). Not world data: one set of numbers for every world.
ANCHOR_ALBEDO = 0.3             # fixed, so the AirlessAlbedo setting cannot move a hot world mid-save
ANCHOR_INDEX_FROM, ANCHOR_INDEX_FULL = 5.0, 10.0
ANCHOR_MAX_GAIN = 10.0

TOXINS = ('Pollutant', 'Methane', 'Hydrazine', 'Silanol', 'HydrochloricAcid')   # Atmosphere.cs:812-823


def _load():
    here = os.path.dirname(os.path.abspath(__file__))
    # TR_GAMEDATA points the tests at a synthetic fixture (tools/ci); the real file is dumped by the game.
    with open(os.environ.get('TR_GAMEDATA') or os.path.join(here, 'gamedata.json'), encoding='utf-8') as f:
        return json.load(f)


DATA = _load()
CELL_LITRES = DATA['gridVolumeLitres']
GAS_ORDER = [g for g in DATA['gases'] if DATA['gases'][g]['state'] == 'Gas']     # the game's enum order


def interp(table, x):
    """Linear interpolation in a dumped {x, y} table; holds the end values, as Unity's curves do."""
    xs, ys = table['x'], table['y']
    if x <= xs[0]:
        return ys[0]
    if x >= xs[-1]:
        return ys[-1]
    i = bisect.bisect_right(xs, x)
    t = (x - xs[i - 1]) / (xs[i] - xs[i - 1])
    return ys[i - 1] + t * (ys[i] - ys[i - 1])


def day_night(tables, angle, x):
    """GlobalTemperatureCurveOffset.GetOffset: day within 70 degrees, night past 110, eased between."""
    if not tables:
        return 0.0
    day = interp(tables['day'], x) if tables.get('day') else 0.0
    night = interp(tables['night'], x) if tables.get('night') else 0.0
    if angle < 70.0:
        return day
    if angle > 110.0:
        return night
    t = (angle - 70.0) / 40.0
    t = math.sin(math.pi * t / 2.0) if t >= 0.5 else 1.0 - math.cos(math.pi * t / 2.0)
    return day + (night - day) * t


def equilibrium(irradiance, albedo):
    """Radiative equilibrium of a rotating sphere; 278.6 K at 1367 W/m2 with no reflection."""
    if irradiance <= 0.0:
        return 0.0
    return 278.6 * (irradiance / 1367.0 * max(0.0, min(1.0, 1.0 - albedo))) ** 0.25


class Planet:
    """One world's air, held as moles per 8000 L outdoor cell, which is how players think about it."""

    def __init__(self, world_id, size=1.0, albedo=0.3, ghg_scale=1.0, density_scale=1.0, curves=None):
        self.id = world_id
        self.w = DATA['worlds'][world_id]
        self.size = size
        self.cells = self.w['volumeLitres'] * size / CELL_LITRES
        shipped_cells = self.w['volumeLitres'] / CELL_LITRES
        self.air = {g: m / shipped_cells for g, m in self.w['startMolesTotal'].items()}
        self.albedo, self.ghg_scale, self.density_scale = albedo, ghg_scale, density_scale
        default = curves or DATA['worlds']['Mars2']        # the mod's default response equals Mars's
        self._fill_ghg = not self.w['ghgOffsetByIndex']
        self._fill_density = not self.w['densityOffsetByMmolPerLitre']
        self._ghg = self.w['ghgOffsetByIndex'] or default['ghgOffsetByIndex']
        self._density = self.w['densityOffsetByMmolPerLitre'] or default['densityOffsetByMmolPerLitre']
        self._fill_base = not self.w['baseTemperatureBySolarAngle'] and not self.air
        self.sun_mid = 0.5 * (self.w['solarIrradianceMin'] + self.w['solarIrradianceMax'])
        self.external = 0.0                                # kelvin of banked outside heat (hot gas vented, heaters)
        self._index0 = self.ghg_index()
        self._density0 = self.density()
        self._anchor()

    # ---- composition -------------------------------------------------------------------------
    def add(self, gas, total_moles):
        """Add (or with a negative number remove) moles planet-wide. Returns what actually moved."""
        per_cell = total_moles / self.cells
        have = self.air.get(gas, 0.0)
        moved = max(per_cell, -have)
        self.air[gas] = have + moved
        return moved * self.cells

    def gas_moles_per_cell(self):
        return sum(m for g, m in self.air.items() if DATA['gases'][g]['state'] == 'Gas')

    def density(self):
        """mmol of gas per litre."""
        return self.gas_moles_per_cell() / CELL_LITRES * 1000.0

    def ghg_index(self):
        """TerraForming.GetGhgIndex: each gas's own curve at its own density, summed."""
        total = 0.0
        for gas, moles in self.air.items():
            table = DATA['gases'][gas]['ghgIndexByMmolPerLitre']
            if table:
                total += interp(table, moles / CELL_LITRES * 1000.0)
        return total

    # ---- the game's parts ----------------------------------------------------------------------
    def shipped(self, angle, percent=50.0):
        """What the world reads untouched: base plus sun distance (an airless world with no base
        curve gets bare-rock equilibrium plus the vacuum end of the density curve from the mod)."""
        if self._fill_base:
            base = equilibrium(self.sun_mid, self.albedo) + day_night(self._density, angle, 0.0)
        else:
            table = self.w['baseTemperatureBySolarAngle']
            base = interp(table, angle) if table else 0.0
        return base + day_night(self.w['solarOffsetByPercent'], angle, percent)

    # ---- the mod's rule --------------------------------------------------------------------------
    def _g(self, index):
        return 0.5 * (day_night(self._ghg, DAY, index) + day_night(self._ghg, NIGHT, index))

    def _swing(self, density):
        return max(0.0, day_night(self._density, DAY, density) - day_night(self._density, NIGHT, density))

    def _density_mean(self, density):
        return 0.5 * (day_night(self._density, DAY, density) + day_night(self._density, NIGHT, density))

    def _anchor(self):
        self.mean0 = 0.5 * (self.shipped(DAY) + self.shipped(NIGHT))
        self.warming0 = self.mean0 - equilibrium(self.sun_mid, ANCHOR_ALBEDO)
        g0 = self._g(self._index0)
        weight = (self._index0 - ANCHOR_INDEX_FROM) / (ANCHOR_INDEX_FULL - ANCHOR_INDEX_FROM)
        weight = max(0.0, min(1.0, weight))
        # The strength setting is divided out of the gain, so on an anchored world it changes the
        # route and never the end point: stripped of its greenhouse air the world still lands on
        # equilibrium. Otherwise a player lowering the strength would make Venus impossible.
        gain = 1.0
        if self._fill_ghg and self.warming0 > 0.0 and g0 > 0.0 and weight > 0.0 and self.ghg_scale > 0.0:
            gain = 1.0 + weight * (min(self.warming0 / g0, ANCHOR_MAX_GAIN) / self.ghg_scale - 1.0)
        self.gain = gain
        self.swing0 = self._swing(self._density0)

    def adjustment(self, angle, percent=50.0):
        """Kelvin the mod adds to the game's own answer. Zero on untouched air, by construction."""
        total = 0.0
        if self._fill_ghg:
            g, g0 = self._g(self.ghg_index()), self._g(self._index0)
            total += (self.gain * (max(g, 0.0) - max(g0, 0.0)) + (min(g, 0.0) - min(g0, 0.0))) * self.ghg_scale
        if self._fill_density:
            density = self.density()
            damp = max(0.0, min(1.0, self._swing(density) / self.swing0)) if self.swing0 > 0.0 else 1.0
            # Strength as an exponent: 0 switches damping off, more makes it bite sooner, and air thick
            # enough to end the swing ends it at any strength, so no setting strands a world like Vulcan.
            damp = damp ** max(0.0, self.density_scale)
            total += (self.shipped(angle, percent) - self.mean0) * (damp - 1.0)
            total += (self._density_mean(density) - self._density_mean(self._density0)) * self.density_scale
        return total

    def temperature(self, angle, percent=50.0):
        t = self.shipped(angle, percent)
        index, density = self.ghg_index(), self.density()
        if not self._fill_ghg:
            t += day_night(self._ghg, angle, index)
        if not self._fill_density:
            t += day_night(self._density, angle, density)
        return max(0.0, t + self.adjustment(angle, percent) + self.external)

    def extremes(self):
        """(coldest, hottest) over every sun angle and both ends of the orbit."""
        values = [self.temperature(a, p) for a in ANGLES for p in (0.0, 100.0)]
        return min(values), max(values)

    def pressure_at(self, kelvin):
        return self.gas_moles_per_cell() * R * kelvin / CELL_LITRES

    def partial_at(self, gas, kelvin):
        return self.air.get(gas, 0.0) * R * kelvin / CELL_LITRES

    # ---- phase change, as the planet tick applies it to a settled planet ---------------------------
    def leaves(self, gas, cold=None):
        """Why this gas cannot stay in the air at the coldest point of the day, or None."""
        info = DATA['gases'][gas]
        if cold is None:
            cold = self.extremes()[0]
        if info['canFreeze'] and cold < info['freezingK'] + PHASE_MARGIN:
            return 'freezes below %.0f K' % (info['freezingK'] + PHASE_MARGIN)
        pressure = self.pressure_at(cold)
        if info['canCondense'] and pressure >= info['minLiquidPressureKPa']:
            boils = interp(info['boilingKByPressureKPa'], pressure)
            if cold < boils + PHASE_MARGIN:
                return 'condenses below %.0f K at %.0f kPa' % (boils + PHASE_MARGIN, pressure)
        return None

    def unstable(self):
        cold = self.extremes()[0]
        found = {}
        for gas, moles in self.air.items():
            if moles > 0.0 and DATA['gases'][gas]['state'] == 'Gas':
                why = self.leaves(gas, cold)
                if why:
                    found[gas] = why
        return found

    def settle(self, limit=30):
        """Remove what freezes or rains out until nothing more does. Returns {gas: (moles, why)}."""
        gone = {}
        for _ in range(limit):
            changed = False
            for gas in GAS_ORDER:
                if self.air.get(gas, 0.0) > 0.0:
                    why = self.leaves(gas)
                    if why:
                        gone[gas] = (gone.get(gas, (0.0, ''))[0] + self.air[gas], why)
                        self.air[gas] = 0.0
                        changed = True
            if not changed:
                break
        return gone

    # ---- the game's own idea of habitable ----------------------------------------------------
    def report(self):
        cold, hot = self.extremes()
        p_cold, p_hot = self.pressure_at(cold), self.pressure_at(hot)
        o2 = self.partial_at('Oxygen', cold)
        toxins = sum(self.partial_at(g, hot) for g in TOXINS)
        n = self.gas_moles_per_cell()
        co2_fraction = self.air.get('CarbonDioxide', 0.0) / n if n else 0.0
        stable = not self.unstable()
        return {
            'T_cold': cold, 'T_hot': hot, 'P_kPa': p_cold, 'ppO2': o2, 'toxins_kPa': toxins, 'CO2_frac': co2_fraction,
            'stable': stable,
            # Lungs.cs:28-36, Entity.cs:91-93: breathable, non-toxic, lungs not burning or freezing.
            'helmet_off': stable and o2 >= 16.0 and toxins <= 1.0 and cold >= 263.15 and hot <= 323.15,
            # StatusUpdates.cs: no pressure or temperature warnings either.
            'shirt_sleeves': stable and o2 >= 16.0 and toxins <= 0.5 and cold >= 273.15 and hot <= 323.15 and p_cold >= 20.0 and p_hot <= 303.97,
            # plantliferequirements.xml defaults: CO2 >= 1 %, 25-200 kPa, 0-50 C, low toxins.
            'crops_outdoors': stable and co2_fraction >= 0.01 and p_cold >= 25.0 and p_hot <= 200.0 and cold >= 273.15 and hot <= 323.15 and toxins <= 1.0,
        }


if __name__ == '__main__':
    print('start conditions as this model computes them (check against the game with -Observe):')
    for world in DATA['worlds']:
        p = Planet(world)
        r = p.report()
        print('  %-14s cold %6.1f K  hot %6.1f K  %8.2f kPa  gain %5.2f  swing0 %5.1f  unstable at start: %s' %
              (world, r['T_cold'], r['T_hot'], r['P_kPa'], p.gain, p.swing0, p.unstable() or 'nothing'))
