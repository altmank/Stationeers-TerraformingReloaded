"""How long does terraforming take, and what planet size makes that reasonable?

    python tools/Balance/balance.py

Three layers, kept apart so each can be argued with separately:

1. The planet (planet.py): the game's own curves and thresholds. Measured, not assumed.
2. Machine rates (RATES below): computed from prefab values in gamedata.json and formulas cited from
   the game code. Measured, except where marked.
3. Base tiers (TIERS below): what a small, medium and mega base actually runs. ASSUMPTIONS. They are
   the soft part of this model; change them here and everything downstream follows.

Work is counted in moles moved: moles added plus moles removed. A base that can make 100k mol/h of
oxygen is assumed able to strip about as much of something else, because the big vents pull far
faster than any source produces (see RATES).
"""

from path import plan
from planet import DATA, Planet, R, CELL_LITRES
from solve import cheapest, describe

TICKS_PER_HOUR = 3600.0 / DATA['tickSeconds']
PREFAB = DATA['prefabs']


def ice_moles(name):
    return sum(g['moles'] for g in PREFAB[name]['SpawnContents'])


def vent_out_per_hour(prefab, kelvin):
    """ActiveVent.cs:386-432 / PoweredVent.cs:154-183: pressurePerTick worth of an 8000 L cell per tick."""
    return PREFAB[prefab]['pressurePerTick'] * CELL_LITRES / (R * kelvin) * TICKS_PER_HOUR


# ---- 2. machine rates, mol per hour ----------------------------------------------------------------
RATES = {
    # Moving gas outdoors. At 280 K. Not the bottleneck: compare with the sources below.
    'active vent, outward': vent_out_per_hour('StructureActiveVent', 280.0),
    'large powered vent, outward': vent_out_per_hour('StructurePoweredVentLarge', 280.0),

    # Making new gas.
    # Rocket ice mining: MineableDeposit.cs:109-145, site data rocketlocations.xml:550-554.
    # 10-13 items per 8.4-9.2 s cycle at the richness floor, 24 mol per space-ice item, x1.2 ice head.
    'rocket ice miner, while mining': 0.5 * (93900 + 133700) * PREFAB['ItemRocketMiningDrillHeadIce']['IceYieldMultiplier'],
    # Rocket gas collector: MineableDeposit.cs:425-434. 19,500-186,000 by site richness; never depletes.
    'rocket gas collector, while mining (mid site)': 80000.0,
    # Large gas trader bought out: tradeables.xml:1003-1090, 72k-129k mol a visit. One visit an hour is
    # an ASSUMPTION: trader type is a random pick per slot.
    'large gas trader, one buy-out an hour': 100000.0,
    # Hand mining: 7 items a minable at normal yield (VeinGenerationData.cs:100) x 25 mol. 150 minables
    # an hour is an ASSUMPTION about a player who is also hauling and crushing. Veins are finite.
    'one player hand-mining ice': 150 * 7 * ice_moles('ItemOxite'),
    # Burning volatiles triples the moles and turns them into CO2 and pollutant (Combustion.cs:33-89).
}

# ---- 3. base tiers: ASSUMPTIONS --------------------------------------------------------------------
# Duty is the share of wall-clock time the source is really producing: rockets travel, players sleep.
TIERS = {
    'small  (one player, hand-mined ice)': RATES['one player hand-mining ice'] * 0.5,
    'medium (one ice rocket, a trader now and then)': RATES['rocket ice miner, while mining'] * 0.5 + RATES['large gas trader, one buy-out an hour'] * 0.25,
    'mega   (four rockets, traders, combustion)': 4 * RATES['rocket ice miner, while mining'] * 0.6 + RATES['large gas trader, one buy-out an hour'] * 0.5,
}
TARGET_HOURS_MEGA = 100.0       # the original mod's stated aim: a mega base needs at least 100 hours

LEVELS = ('helmet_off', 'shirt_sleeves')

def ice_budget(added):
    """Ices a recipe's additions cost when CO2 is made the way a base makes it: by burning volatiles.

    Combustion.cs (ResultMethaneOxygen): 2 volatiles + 1 oxygen -> 6 CO2 + 3 pollutant. So a mole of
    CO2 costs a third of a mole of volatiles and a sixth of oxygen, and leaves half a mole of pollutant
    that has to be caught before it reaches the air (it is toxic above 0.5 kPa) and stored. The
    hydrogen in volatiles ice burns to steam and is ignored. Ice contents are the game's prefab values.
    """
    oxite = {g['gas']: g['moles'] for g in PREFAB['ItemOxite']['SpawnContents']}
    volatiles = {g['gas']: g['moles'] for g in PREFAB['ItemVolatiles']['SpawnContents']}
    nitrice = {g['gas']: g['moles'] for g in PREFAB['ItemNitrice']['SpawnContents']}
    co2 = added.get('CarbonDioxide', 0.0)
    fuel_ice = (co2 / 3.0) / volatiles.get('Methane', 20.0)
    oxygen = added.get('Oxygen', 0.0) + co2 / 6.0
    oxite_ice = oxygen / oxite.get('Oxygen', 22.5)
    nitrogen = max(0.0, added.get('Nitrogen', 0.0) - oxite_ice * oxite.get('Nitrogen', 0.0))
    nitrice_ice = nitrogen / nitrice.get('Nitrogen', 22.5)
    return {'oxite': oxite_ice, 'volatiles': fuel_ice, 'nitrice': nitrice_ice, 'pollutant to store': co2 / 2.0}


def main():
    print('MACHINE RATES (mol per hour)')
    for name, rate in RATES.items():
        print('  %-48s %12s' % (name, format(round(rate), ',')))
    print('\nBASE TIERS (assumptions, mol per hour sustained)')
    for name, rate in TIERS.items():
        print('  %-48s %12s' % (name, format(round(rate), ',')))

    mega = list(TIERS.values())[-1]
    print('\nWHAT IT TAKES, at the shipped planet size (5,000,000 outdoor cells)')
    print('end state = cheapest habitable air (solve.py); path = reached step by step under phase change (path.py)')
    sizes = {}
    for world in DATA['worlds']:
        if world == 'Vulcan':
            continue                      # deprecated duplicate of Vulcan2
        p = Planet(world)
        r = p.report()
        print('\n%s   start: coldest %.0f K, hottest %.0f K, %.1f kPa, ppO2 %.2f, toxins %.2f kPa' %
              (world, r['T_cold'], r['T_hot'], r['P_kPa'], r['ppO2'], r['toxins_kPa']))
        for level in LEVELS:
            found = cheapest(world, level)
            if found is None:
                print('  %-15s no habitable air found' % level)
                continue
            print('  %-15s end state %s' % (level, describe(world, found)))
            if level == 'shirt_sleeves':
                added = {g: m - p.air.get(g, 0.0) for g, m in found[1].items() if m > p.air.get(g, 0.0)}
                ices = ice_budget(added)
                total_ices = (ices['oxite'] + ices['volatiles'] + ices['nitrice']) * p.cells
                per_hour = RATES['rocket ice miner, while mining'] / 24.0
                print('  %-15s as ices per cell, CO2 made by burning volatiles: oxite %.2f, volatiles %.2f, nitrice %.2f; %.0f pollutant per cell to catch and store' % (
                    '', ices['oxite'], ices['volatiles'], ices['nitrice'], ices['pollutant to store']))
                print('  %-15s = %.0f M ices; one ice rocket mines about %.0f an hour while mining, so %s rocket-hours (additions only)' % (
                    '', total_ices / 1e6, per_hour, format(round(total_ices / per_hour), ',')))
        route = plan(world, 'shirt_sleeves')
        if route and route['reached']:
            total = route['work'] * p.cells
            print('  %-15s path: %.0f mol/cell = %.0f M mol; hours: %s' % ('shirt_sleeves', route['work'], total / 1e6,
                  '   '.join('%s %s' % (n.split('(')[0].strip(), format(round(total / rate), ',')) for n, rate in TIERS.items())))
            for stage in route['stages']:
                print('  %-15s   - %s' % ('', stage[:150]))
            sizes[world] = TARGET_HOURS_MEGA * mega / total
        else:
            print('  %-15s NO PATH FOUND: %s' % ('shirt_sleeves', route and route.get('stuck')))

    print('\nPLANET SIZE for a mega base to need %d hours to reach shirt sleeves' % TARGET_HOURS_MEGA)
    for world, size in sizes.items():
        print('  %-14s x%.3f of shipped  (about 1/%d)' % (world, size, round(1.0 / size)))


if __name__ == '__main__':
    main()
