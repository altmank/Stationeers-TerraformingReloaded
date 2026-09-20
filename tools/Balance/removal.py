"""How long removal takes. Removing a gas from a planet is dilution, not subtraction.

    python tools/Balance/removal.py

A base cannot reach into the planet and take out the acid. It draws outdoor air in through vents,
filters the unwanted gas out, and returns the rest, so every mole drawn in carries only the unwanted
gas's current share: dn/dt = -(intake) * n / N. Getting Venus's acid from 90 mol per cell down to 1
takes ln(90) = 4.5 "acid-volumes" of intake, and the last mole is as slow as the first was fast.

Intake per vent is the game's own rule (ActiveVent.PumpGasToPipe): each tick a vent moves the moles
that `pressurePerTick` kPa of the outdoor cell's 8000 L holds at the outdoor temperature, or what the
cell has if that is less. `pressurePerTick` is 10 kPa for the active vent, 20 and 40 for the powered
vents (read from the prefabs by tools/LiveCheck -Dump). Colder, denser air therefore comes in faster,
and thin air (Mars) is limited by the cell, not the vent.

Assumed, and listed in docs/ASSUMPTIONS.md: filtration and storage keep up with the vents; the gas
returned goes straight back out; intake is averaged over the coldest and hottest hour of the model's settled state for
the air at that moment. Rain-out and freeze-out, where the planet offers them, are free and not counted.
"""
import sys

from planet import CELL_LITRES, Planet, R
from solve import cheapest

TICKS_PER_HOUR = 7200.0
VENTS = (('active vent', 10.0), ('large powered vent', 40.0))
SIZES = (('Standard', 0.05),)


def intake_per_hour(pressure_per_tick, outdoor_kpa, kelvin):
    """Moles an inward vent moves in an hour (PumpGasToPipe, pipe side unconstrained)."""
    return min(pressure_per_tick, outdoor_kpa) * CELL_LITRES / (R * kelvin) * TICKS_PER_HOUR


def hours_to_remove(world, start, final, size, vents, pressure_per_tick, steps=400):
    """Integrate dn/dt = -intake * share for every gas the recipe wants less of, all at once:
    one stream of outdoor air through the filters removes each unwanted gas in proportion."""
    cells = Planet(world, size=size).cells
    air = dict(start)
    unwanted = [g for g in start if final.get(g, 0.0) < start[g] - 1e-9]
    if not unwanted:
        return 0.0, 0.0
    hours, drawn = 0.0, 0.0
    # Step in equal log-decrements of the slowest gas, so the long tail is resolved.
    while any(air[g] > max(final.get(g, 0.0), 1e-3) * 1.001 for g in unwanted):
        p = Planet(world, size=size)
        p.air = dict(air)
        cold, hot = p.extremes()
        total = p.gas_moles_per_cell()
        # Vents run day and night: average the two ends. (A stripped Vulcan's model night reaches 0 K,
        # where nothing is left in the air to draw; its days still are.)
        rate = 0.5 * vents * sum(intake_per_hour(pressure_per_tick, p.pressure_at(k), k) for k in (cold, hot) if k > 1.0)
        if rate <= 0.0 or total <= 0.0:
            return float('inf'), drawn
        dt = 0.01 * total * cells / rate                       # 1 % of the planet's air through the filters
        for g in unwanted:
            floor = final.get(g, 0.0)
            if air[g] > floor:
                air[g] = max(floor, air[g] * (1.0 - 0.01))
        hours += dt
        drawn += rate * dt
        if hours > 1e7:
            return float('inf'), drawn
    return hours, drawn


if __name__ == '__main__':
    worlds = sys.argv[1:] or ('Venus', 'Vulcan2')
    for world in worlds:
        found = cheapest(world)
        if not found:
            print(world, 'no habitable air found')
            continue
        _, final, _ = found
        start = Planet(world).air
        removed = {g: start[g] - final.get(g, 0.0) for g in start if final.get(g, 0.0) < start[g] - 0.5}
        print('%s: the recipe removes %s (mol per cell)' % (world, ', '.join('%s %.0f' % kv for kv in removed.items())))
        for size_name, size in SIZES:
            cells = Planet(world, size=size).cells
            moved = sum(removed.values()) * cells
            for vent_name, ppt in VENTS:
                for vents in (4, 20, 100):
                    hours, drawn = hours_to_remove(world, start, final, size, vents, ppt)
                    print('  %-9s %3d x %-18s %9s h   (%.0f M mol of outdoor air through the filters to take out %.0f M mol: x%.1f)' % (
                        size_name, vents, vent_name, (format(round(hours), ',') if hours != float('inf') else 'never'), drawn / 1e6, moved / 1e6, drawn / moved if moved else 0))
