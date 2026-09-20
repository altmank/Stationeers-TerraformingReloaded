"""Interaction census: every place the game's code touches the planet or outdoor air.

    python tools/census/census.py <decompile dir>            # list every site
    python tools/census/census.py <decompile dir> --check    # fail if a site has no verdict in ledger.csv

Reviews by reading are hunts: they find what the reader thought to look for. This is the opposite.
The symbols through which anything can reach the planet's gas, heat, temperature or outdoor air are
a short closed list (SYMBOLS). The script finds every reference to them in a decompile of the game,
names each by the method it sits in, and ledger.csv records a verdict for every one. Coverage is then
a fact the script checks, not a hope: a site with no verdict fails the check, and after a game update
new or moved sites show up as unclassified.

The decompile is not in this repository and must not be. The ledger holds only names of game
methods and our verdicts about them.

Verdict kinds (ledger column `kind`):
  read      only reads planet state; cannot change gas or heat
  gated     goes through one of the eight switched call sites, so it debits or credits the planet
  bypass    changes gas or heat on the read-only outdoor air or around the switch: gas from nowhere,
            or gas destroyed. Every one of these needs a fix or a written acceptance
  internal  the planet's own bookkeeping (tick, phase change, save, load, create)
  display   UI, debug windows, menus, sky
  none      matched the pattern but does not touch the planet (a name collision)
"""
import csv
import os
import re
import sys

SYMBOLS = [
    r'PlanetaryAtmosphereSimulation\.',      # any use of the planet from outside its own file
    r'\bGlobalGasMix\b',                     # the tank type
    r'SampleGlobalAtmosphere\s*\(',          # outdoor air at a grid: a real cell, or the read-only copy
    r'ReadOnlyGlobal\b',
    r'CloneGlobal\w*\s*\(',                  # builds a real outdoor cell
    r'IsGlobalAtmosphere\b',
    r'AtmosphereMode\.Global\b',
    r'\bWorldAtmosphere\b',                  # a thing's cached outdoor air: devices add to and remove from it
    r'GlobalAtmosphereLiquid\b|IsUnderGlobalLiquid\b',   # the global sea
    r'\bTerraForming\.',
    r'GlobalAtmosphereData\b',
    r'WeatherEvent\b.*TemperatureOffset|TemperatureOffset\.GetOffset',
    r'GlobalTemperature\w*Offset\b',
    r'ExternalInputEnergyOffset\b|LatentEnergyOffset\b',
    r'IsInSpaceAtmosphere\s*\(',
    # Wrappers the first pass found: members that hand a sampled atmosphere on to other code. Added until a
    # pass finds no new wrapper (closure).
    r'ReadonlyGlobalAtmosphere\b',
    r'SetWorldAtmosphere\s*\(',
    r'\bBreathingAtmosphere\b|\bSoilingAtmosphere\b',
    r'GetBurningAtmosphere\s*\(',
    r'\bScannedAtmosphere\b|GetScannedAtmosphere\s*\(',
    r'\bFindAtmosphere\s*\(',
    r'GetInputAtmos\s*\(',
    r'\b_worldAtmosphere\b',
    r'\b_mixingAtmos\b',
    r'AtmosphericsController\b.*\bHasAtmosphere\s*\(',
    r'\bSmelt\s*\(',
]
PATTERN = re.compile('|'.join('(?:%s)' % s for s in SYMBOLS))
METHOD = re.compile(r'^\t(?:\t)?(?:public|private|protected|internal|static|override|virtual|async|sealed|abstract|new|extern|unsafe|readonly|\s)+[\w<>\[\],.? ]+?\s+(\w+)\s*(?:<[^>]*>)?\s*\(')
PROPERTY = re.compile(r'^\t(?:\t)?(?:public|private|protected|internal|static|override|virtual|sealed|abstract|new|\s)+[\w<>\[\],.? ]+?\s+(\w+)\s*$')
TYPE = re.compile(r'^\t?(?:public|internal|private|protected|static|sealed|abstract|partial|\s)*(?:class|struct|interface|enum)\s+(\w+)')


def sites(root):
    found = {}
    for folder, _, files in os.walk(root):
        for name in sorted(files):
            if not name.endswith('.cs'):
                continue
            path = os.path.join(folder, name)
            rel = os.path.relpath(path, root).replace('\\', '/')
            try:
                lines = open(path, encoding='utf-8-sig', errors='replace').read().split('\n')
            except OSError:
                continue
            kind, member = os.path.splitext(name)[0], '(type)'
            for number, line in enumerate(lines, 1):
                t = TYPE.match(line)
                if t and line.startswith(('public', 'internal', '\tpublic', '\tinternal', '\tprivate', 'static', 'sealed', 'abstract')):
                    if not line.startswith('\t\t'):
                        kind = t.group(1) if not line.startswith('\t') else kind + '+' + t.group(1)
                m = METHOD.match(line) or PROPERTY.match(line)
                if m and not line.strip().startswith(('return', 'if', 'else', 'new ', 'throw', 'await')) and m.group(1) not in ('get', 'set', 'add', 'remove'):
                    member = m.group(1)
                if PATTERN.search(line) and not line.strip().startswith(('using ', '//', '[')):
                    key = '%s::%s' % (rel, member)
                    entry = found.setdefault(key, {'file': rel, 'member': member, 'lines': [], 'symbols': set()})
                    entry['lines'].append(number)
                    for s in SYMBOLS:
                        hit = re.search(s, line)
                        if hit:
                            entry['symbols'].add(hit.group(0).strip('( .'))
    return found


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    found = sites(sys.argv[1])
    here = os.path.dirname(os.path.abspath(__file__))
    ledger_path = os.path.join(here, 'ledger.csv')
    ledger = {}
    if os.path.exists(ledger_path):
        with open(ledger_path, newline='', encoding='utf-8') as f:
            for row in csv.DictReader(f):
                ledger[row['site']] = row
    if '--check' in sys.argv:
        missing = sorted(k for k in found if k not in ledger or not ledger[k].get('kind'))
        gone = sorted(k for k in ledger if k not in found)
        open_bypass = sorted(k for k, r in ledger.items() if r.get('kind') == 'bypass' and not r.get('handled'))
        print('%d sites, %d with a verdict, %d without, %d in the ledger no longer in the game, %d bypasses not yet handled' % (
            len(found), len(found) - len(missing), len(missing), len(gone), len(open_bypass)))
        for k in missing[:40]:
            print('  no verdict:', k)
        for k in open_bypass:
            print('  bypass not handled:', k, '-', ledger[k].get('note', ''))
        return 1 if missing or open_bypass else 0
    out = csv.writer(sys.stdout, lineterminator='\n')
    out.writerow(['site', 'lines', 'symbols'])
    for key in sorted(found):
        e = found[key]
        out.writerow([key, ' '.join(map(str, e['lines'][:12])) + (' ...' if len(e['lines']) > 12 else ''), ' '.join(sorted(e['symbols']))])
    print('%d sites in %d files' % (len(found), len({e['file'] for e in found.values()})), file=sys.stderr)
    return 0


if __name__ == '__main__':
    sys.exit(main())
