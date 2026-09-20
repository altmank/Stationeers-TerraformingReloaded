"""Builds ledger.csv from the classification batches in parts/ and the decisions below.

    python tools/census/merge.py

The batches are the raw verdicts (every site read, with the guard quoted). What is decided here is
what happens to each bypass: fixed in the mod, or accepted with its size written down. The rule for
accepting: it cannot move an amount that matters to a planet (a Standard-size Mars holds 2.3 million
mol; the smallest base tier moves 13,000 mol an hour) and it is not a path ordinary venting goes through.
"""
import csv
import glob
import os

HERE = os.path.dirname(os.path.abspath(__file__))

HANDLED = {
    'WallVent.cs::OnAtmosphericTick': 'FIXED: Guards.WallVentPrefix builds a real outdoor cell for the side that has none. The ordinary way to open a base to outdoors; averaged a whole room with a bottomless copy every tick',
    'RoboticArmDockAtmos.cs::HandleVentDirection': 'accepted: only when no pipe is docked, and then the gas can only fill or empty the dock\'s own small internal volume once; per tick it is the smaller of the 1 atm formula cap and that volume. A few moles, one-off. (A first reading took the formula cap, 330 mol a tick, for a flow: it is not)',
    'PortableAtmospherics.cs::VentAllAtmosphereToWorld': 'accepted: on destroying a portable outdoors where no cell exists its contents are lost instead of reaching the planet. One-off, at most a 790 L tank at 10 MPa, about 3,300 mol',
    'DynamicGenerator.cs::VentAllAtmosphereToWorld': 'accepted: same, on destroying a portable generator; its small internal exhaust volume, one-off',
    'DynamicGenerator.cs::OnAtmosphericTick': 'accepted: a portable generator\'s exhaust is lost where no outdoor cell exists. Bounded by the fuel a player feeds it: one full canister of fuel is about 10,000 mol of exhaust, 0.4 % of a Standard Mars. Industrial burners and furnaces, the way to make CO2 in bulk, go through the planet',
    'DynamicScrubber.cs::ReleaseToAtmos': 'accepted: an opened portable scrubber\'s contents are lost outdoors where no cell exists; one portable\'s volume',
    'DynamicScrubber.cs::GetFromCell': 'accepted: a portable scrubber pulls from neighbouring grids without debiting the planet where they hold no cell; one portable device\'s intake',
    'DynamicAirConditioner.cs::VentWaste': 'accepted: a portable air conditioner\'s waste gas is lost outdoors where no cell exists; its small internal volume per tick while open',
    'GasMask.cs::OnAtmosphericTick': 'accepted: a mask worn without a suit mixes with the copy; one mask volume per wearer per tick',
    'GasMask.cs::FlushMask': 'accepted: one lungful per flush',
    'FilterMask.cs::OnAtmosphericTick': 'accepted: one mask volume per wearer per tick',
    'SuitModuleHelper.cs::HandleFilters': 'accepted: filtered suit waste is lost outdoors, only for suits without a waste tank slot; filter rate, a breath\'s worth per tick',
    'SuitModuleHelper.cs::RegulatePressure': 'accepted: suit over-pressure relief is lost outdoors, only for suits without a waste tank slot; grams',
    'Jetpack.cs::OnAtmosphericTick': 'accepted: jetpack propellant is lost instead of reaching the planet; the player\'s own propellant, a canister at most',
    'Entity.cs::LifeBreathe': 'accepted: animals and NPCs outdoors breathe the copy; at most 0.005 mol of oxygen a breath. Humans breathe through a cloned cell',
    'FridgePowered.cs::OnAtmosphericTick': 'accepted: heat only, no gas. Up to 1000 J a tick (2 kW) shed outdoors never reaches the planet\'s heat counter',
    'VendingMachineRefrigerated.cs::OnAtmosphericTick': 'accepted: heat only, as the powered fridge',
}

# Sites the closure pass added (wrapper symbols), read directly.
CLOSURE = [
    ('Assets.Scripts.Objects.Items/Egg.cs::CentrifugeProcessUnit', 'none', 'no', '', 'reagents only; shares a name with a wrapper, touches no atmosphere'),
    ('Assets.Scripts.Objects.Items/SpaceOre.cs::CentrifugeProcessUnit', 'none', 'no', '', 'reagents only'),
    ('OrganicMaterial.cs::CentrifugeProcessUnit', 'none', 'no', '', 'reagents only'),
    ('Assets.Scripts.Objects.Items/ICentrifugable.cs::(type)', 'none', 'no', '', 'interface declaration'),
    ('Assets.Scripts.Objects.Pipes/Centrifuge.cs::CollectResource', 'none', 'no', '', 'collects reagents'),
    ('Assets.Scripts.Objects.Pipes/CombustionCentrifuge.cs::CollectResource', 'none', 'no', '', 'collects reagents'),
    ('Assets.Scripts.Objects.Items/RoadFlare.cs::OnAtmosphericTick', 'gated', 'yes', 'GetBurningAtmosphere: WorldAtmosphere null or Mode == Global -> CloneGlobalAtmosphere (line 161)', 'sparks and heats a real cell; 1000 J a tick while lit'),
    ('Objects.RoboticArm/RoboticArmDockAtmos.cs::GetLogicValue', 'read', 'no', '', 'logic readout of the input atmosphere'),
]


def main():
    rows, extras = [], []
    for path in sorted(glob.glob(os.path.join(HERE, 'parts', 'verdicts_*.csv'))):
        with open(path, newline='', encoding='utf-8') as f:
            for r in csv.DictReader(f):
                (extras if r['site'].startswith(('WRAPPER::', 'CONSTANT::')) else rows).append(r)
    known = {r['site'] for r in rows}
    for site, kind, mutates, guard, note in CLOSURE:
        full = next((k for k in known if k.endswith(site)), None)
        if full is None:
            rows.append({'site': site, 'kind': kind, 'mutates': mutates, 'guard': guard, 'note': note})
    unhandled = []
    for r in rows:
        r['handled'] = ''
        if r['kind'] == 'bypass':
            key = next((k for k in HANDLED if r['site'].endswith(k)), None)
            if key:
                r['handled'] = HANDLED[key]
            else:
                unhandled.append(r['site'])
    rows.sort(key=lambda r: r['site'])
    with open(os.path.join(HERE, 'ledger.csv'), 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=['site', 'kind', 'mutates', 'guard', 'handled', 'note'], lineterminator='\n')
        w.writeheader()
        w.writerows(rows)
    with open(os.path.join(HERE, 'wrappers_and_constants.csv'), 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=['site', 'kind', 'mutates', 'guard', 'note'], lineterminator='\n')
        w.writeheader()
        seen = set()
        for r in sorted(extras, key=lambda r: r['site']):
            if r['site'] not in seen:
                seen.add(r['site'])
                w.writerow({k: r.get(k, '') for k in w.fieldnames})
    print('%d sites in the ledger, %d wrapper and constant notes, %d bypasses without a decision %s' % (
        len(rows), len(seen), len(unhandled), unhandled or ''))


if __name__ == '__main__':
    main()
