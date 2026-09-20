"""Checks that need neither the game nor a build. Run from the repository root.

    python tools/ci/check_repo.py

  - the version is the same in the project file, the plugin and About.xml, and the changelog has it;
  - About.xml and the curves embedded in Climate.cs are well-formed XML with the blocks the mod needs;
  - the constants of the temperature rule are the same in Climate.cs and tools/Balance/planet.py,
    so the simulator that the design is tested in cannot drift from the mod;
  - every relative link in the docs resolves, and every file build.ps1 ships exists;
  - nothing that must stay out of a public repository is tracked: game data, game or mod binaries,
    decompiled code, anything locally ignored or excluded, an email address; and every commit carries a
    noreply identity.
"""
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

problems = []


def read(path):
    with open(path, encoding='utf-8-sig') as f:
        return f.read()


def need(condition, message):
    print(('ok    ' if condition else 'FAIL  ') + message)
    if not condition:
        problems.append(message)


# ---- versions -------------------------------------------------------------------------------------
found = {
    'csproj': re.search(r'<Version>([^<]+)</Version>', read('src/TerraformingReloaded.csproj')),
    'plugin': re.search(r'PluginVersion\s*=\s*"([^"]+)"', read('src/Plugin.cs')),
    'About.xml': re.search(r'<Version>([^<]+)</Version>', read('About/About.xml')),
}
versions = {k: (m.group(1) if m else None) for k, m in found.items()}
need(len(set(versions.values())) == 1 and None not in versions.values(), 'one version everywhere: %s' % versions)
version = versions['plugin']
need(re.search(r'^## ' + re.escape(version or '?') + r'\b', read('CHANGELOG.md'), re.M) is not None, 'CHANGELOG.md has a section for %s' % version)

# ---- XML ------------------------------------------------------------------------------------------
try:
    about = ET.fromstring(read('About/About.xml'))
    need(all(about.find(tag) is not None and (about.find(tag).text or '').strip() for tag in ('Name', 'Author', 'Version', 'Description')),
         'About.xml has a name, author, version and description')
except ET.ParseError as e:
    need(False, 'About.xml parses: %s' % e)

climate = read('src/Patching/Climate.cs')
embedded = re.search(r'DefaultCurvesXml = @"(.*?)";', climate, re.S)
need(embedded is not None, 'Climate.cs embeds default curves')
if embedded:
    try:
        curves = ET.fromstring(embedded.group(1).replace('""', '"').split('?>', 1)[1])
        for block in ('GHGTemperatureOffset', 'DensityOffset'):
            for part in ('Day', 'Night'):
                keys = curves.findall('%s/%s/Key' % (block, part))
                need(len(keys) >= 1 and all(k.get('Time') is not None and k.get('Value') is not None for k in keys),
                     'default curves: %s %s has keys with Time and Value' % (block, part))
    except (ET.ParseError, IndexError) as e:
        need(False, 'embedded default curves parse: %s' % e)

# ---- the mod and the simulator share the rule's constants -----------------------------------------------
planet = read('tools/Balance/planet.py')
pairs = {
    'AnchorAlbedo': r'ANCHOR_ALBEDO\s*=\s*([0-9.]+)',
    'AnchorIndexFrom': r'ANCHOR_INDEX_FROM, ANCHOR_INDEX_FULL\s*=\s*([0-9.]+)',
    'AnchorIndexFull': r'ANCHOR_INDEX_FROM, ANCHOR_INDEX_FULL\s*=\s*[0-9.]+,\s*([0-9.]+)',
    'AnchorMaxGain': r'ANCHOR_MAX_GAIN\s*=\s*([0-9.]+)',
}
for name, pattern in pairs.items():
    cs = re.search(r'const double ' + name + r'\s*=\s*([0-9.]+)', climate)
    py = re.search(pattern, planet)
    need(cs is not None and py is not None and float(cs.group(1)) == float(py.group(1)),
         '%s is the same in Climate.cs and planet.py (%s, %s)' % (name, cs and cs.group(1), py and py.group(1)))
need('278.6' in climate and '278.6' in planet, 'both use the same equilibrium constant')

tracked = subprocess.run(['git', 'ls-files'], capture_output=True, text=True, check=True).stdout.split('\n')
tracked_set = set(tracked)

# ---- links and shipped files ----------------------------------------------------------------------------
for folder, _, files in os.walk('.'):
    if any(part in folder for part in ('.git', 'package', 'reference', 'bin', 'obj')):
        continue
    for name in files:
        path = os.path.join(folder, name)
        if name.endswith('.md') and os.path.normpath(path).replace('\\', '/').lstrip('./') in tracked_set:
            for target in re.findall(r'\]\((?!https?://|#)([^)#]+)', read(path)):
                need(os.path.exists(os.path.normpath(os.path.join(folder, target))), '%s links to %s' % (path, target))
for shipped in re.findall(r"Copy-Item \(Join-Path \$root '([^']+)'\)", read('build.ps1')):
    need(os.path.exists(shipped.replace('\\', '/').rstrip('*').rstrip('/')), 'build.ps1 ships %s, which exists' % shipped)

# ---- hygiene ------------------------------------------------------------------------------------------
bad = [f for f in tracked if re.search(r'(gamedata\.json|\.dll|\.pdb|\.exe|Assembly-CSharp)$|/decompile/|^reference/', f)]
need(not bad, 'no game data, binaries or decompiled code are tracked %s' % (bad or ''))
# Anything git is told to ignore or exclude locally must not have been added by force.
ignored = subprocess.run(['git', 'ls-files', '-i', '-c', '--exclude-standard'], capture_output=True, text=True).stdout.split()
need(not ignored, 'nothing that is ignored or locally excluded is tracked %s' % (ignored or ''))
authors = set(subprocess.run(['git', 'log', '--all', '--format=%ae%n%ce'], capture_output=True, text=True).stdout.split())
strangers = sorted(a for a in authors if not a.endswith('@users.noreply.github.com') and a != 'noreply@github.com')
need(not strangers, 'every commit is authored and committed with a noreply address %s' % (strangers or ''))
leaks = []
for f in tracked:
    if f and os.path.isfile(f) and not f.lower().endswith(('.png', '.jpg', '.ico')):
        try:
            text = read(f)
        except (UnicodeDecodeError, OSError):
            continue
        found = re.findall(r'([A-Za-z0-9._%+-]+)@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+)', text)
        if any(user != 'noreply' and not domain.endswith(('users.noreply.github.com', 'example.com')) for user, domain in found):
            leaks.append(f)
need(not leaks, 'no personal email address in tracked files %s' % (leaks or ''))

# ---- the census can only be trusted if its patterns really match ----------------------------------------
# A shell once turned every \b in a pattern into a backspace character; the patterns then matched
# nothing and a closure pass reported "no new sites" that was simply false.
sys.path.insert(0, os.path.join('tools', 'census'))
import census                                                     # noqa: E402
need(not any(ord(c) < 32 for pattern in census.SYMBOLS for c in pattern), 'census patterns hold no control characters')
SAMPLES = (
    'PlanetaryAtmosphereSimulation.GiveToGlobal(mix);', 'GlobalGasMix tank', 'x.SampleGlobalAtmosphere(grid)',
    'PAS.ReadOnlyGlobal(grid)', 'c.CloneGlobalAtmosphere(grid, 0L)', 'if (a.IsGlobalAtmosphere)',
    'Mode == AtmosphereHelper.AtmosphereMode.Global', 'base.WorldAtmosphere.Add(m)', 'GlobalAtmosphereLiquid.IsRendered',
    'TerraForming.GetGhgIndex(t)', 'GlobalAtmosphereData data', 'e.TemperatureOffset.GetOffset(angle)',
    'GlobalTemperatureCurveOffset o', 'ExternalInputEnergyOffset += e', 'LatentEnergyOffset += e',
    'IsInSpaceAtmosphere(grid)', 'AtmosphericsController.ReadonlyGlobalAtmosphere(g)', 'SetWorldAtmosphere();',
    'if (BreathingAtmosphere == null)', 'SoilingAtmosphere.Add(m)', 'GetBurningAtmosphere()', 'ScannedAtmosphere.Pressure',
    'FindAtmosphere(grid)', 'GetInputAtmos()', '_worldAtmosphere.GasMixture', '_mixingAtmos[i]', 'thing.Smelt(atmosphere)',
    'base.GridController.AtmosphericsController.HasAtmosphere(grid)',
)
missed = [text for text in SAMPLES if not census.PATTERN.search(text)]
need(not missed, 'every census symbol matches its sample %s' % (missed or ''))
unused = [p_ for p_ in census.SYMBOLS if not any(re.search(p_, text) for text in SAMPLES)]
need(not unused, 'every census pattern is exercised by a sample %s' % (unused or ''))

print()
print('%d problem(s)' % len(problems) if problems else 'all checks passed')
sys.exit(1 if problems else 0)
