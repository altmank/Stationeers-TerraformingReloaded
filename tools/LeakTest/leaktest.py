"""The leak test: what a leak does with the planet's fire on and off (docs/VERIFICATION.md, "The leak test").

Runs against a dedicated TEST server only, the folder named by the TR_TEST_SERVER environment variable (it
holds the server, setup.ps1 and the server's StationGod pipe client, tsclient.py), and that client refuses any
pipe but the test one. Never against a game someone is playing: it stops the server the moment a game client
starts.

    python tools/LeakTest/leaktest.py site --world Vulcan2 --at X Y Z --save leak-vulcan
    python tools/LeakTest/leaktest.py run --save leak-vulcan --gas Oxygen --mol 1000 --kind gas --time midday --switch on
    python tools/LeakTest/leaktest.py matrix --save leak-vulcan [--only text]
    python tools/LeakTest/leaktest.py live --save leak-vulcan

How it measures. The test driver (tools/LiveCheck, its Watch) runs inside the server beside the mod and logs,
from inside the mod's fire upkeep where the cells are at rest, every outdoor cell summed in rings around the
release point and around each station (cells, burning cells, oxygen, nitrous oxide, methane, hottest), the
planet's fire state and heat, and every burn of a cell near a station caught at the burn (what the cell held,
burnt, and the heat). StationGod builds the site, releases the gas and reads damage (thing_health) before and
after. Every StationGod call and reply goes to results/calls.jsonl; each run's readings to results/<run>.json.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
TS_ROOT = os.environ.get('TR_TEST_SERVER', '')   # the dedicated test server's folder; Rig refuses to start without it
LOG = os.path.join(TS_ROOT, 'server', 'BepInEx', 'LogOutput.log')
RESULTS = os.path.join(HERE, 'results')

STATIONS = (('s2', 2), ('s4', 4), ('s8', 8), ('s16', 16), ('s32', 32), ('far', -100))
# role, prefab, offset across the bearing (m), offset up (m)
KIT = (
    ('vent', 'StructureActiveVent', 0, 0),
    ('tank_small', 'StructureTankSmallInsulated', 2, 0),
    ('tank_big', 'StructureTankBigInsulated', -2, 0),
    ('cable', 'StructureCableStraight', 4, 0),
    ('cable_heavy', 'StructureCableStraightH', -4, 0),
    ('pipe', 'StructurePipeStraight', 6, 0),
    ('pipe_insulated', 'StructureInsulatedPipeStraight', -6, 0),
    ('solar', 'StructureSolarPanel', 8, 0),
    ('wall', 'StructureWallIron', -8, 0),
    ('frame', 'StructureFrame', 10, 0),
)
SECONDARY_MOL = 467.0


class Stop(Exception):
    pass


def game_running():
    return 'rocketstation.exe' in subprocess.run(['tasklist', '/FI', 'IMAGENAME eq rocketstation.exe'], capture_output=True, text=True).stdout


def ps(script, *args):
    return subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', os.path.join(TS_ROOT, script)] + list(args),
                          capture_output=True, text=True)


class Rig:
    def __init__(self, results=RESULTS):
        if not TS_ROOT or not os.path.isdir(TS_ROOT):
            raise SystemExit('Set TR_TEST_SERVER to the dedicated test server folder (the one holding setup.ps1 and '
                             'tsclient.py); it is %r now.' % TS_ROOT)
        sys.path.insert(0, TS_ROOT)
        import tsclient
        self.ts = tsclient
        self.client = None
        self.results = results
        os.makedirs(results, exist_ok=True)
        self.calls = open(os.path.join(results, 'calls.jsonl'), 'a', encoding='utf-8')
        self.watch_file = os.path.join(results, 'watch.txt').replace('/', '\\')

    def install(self):
        """The mod build under test and the test driver, through the test server's own setup."""
        stage = os.path.join(os.environ['TEMP'], 'tr-leak-mods')
        if os.path.exists(stage):
            shutil.rmtree(stage)
        tr, lc = os.path.join(stage, 'TerraformingReloaded'), os.path.join(stage, 'TRLiveCheck')
        shutil.copytree(os.path.join(REPO, 'About'), os.path.join(tr, 'About'))
        shutil.copy(os.path.join(REPO, 'src', 'bin', 'Release', 'TerraformingReloaded.dll'), tr)
        shutil.copytree(os.path.join(REPO, 'tools', 'LiveCheck', 'About'), os.path.join(lc, 'About'))
        shutil.copy(os.path.join(REPO, 'tools', 'LiveCheck', 'bin', 'Release', 'TRLiveCheck.dll'), lc)
        command = "& '%s' -Mods @('%s','%s'); exit $LASTEXITCODE" % (os.path.join(TS_ROOT, 'setup.ps1'), tr, lc)
        r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command], capture_output=True, text=True)
        if r.returncode != 0:
            raise Stop('setup failed: ' + r.stdout[-400:] + r.stderr[-400:])

    def start(self, world=None, save=None):
        if game_running():
            raise Stop("the owner's game is running")
        os.environ['TR_LIVECHECK_INJECT'] = '0'
        os.environ['TR_LIVECHECK_WATCH'] = self.watch_file
        os.environ['TR_LIVECHECK_WATCH_EVERY'] = '2'
        open(os.path.join(TS_ROOT, 'test.lock.busy'), 'w').close()      # keeps the idle watchdog off
        r = ps('start-server.ps1', *((['-New', '-World', world] if world else ['-Save', save]) + ['-Wait']))
        if r.returncode != 0:
            raise Stop('server did not start: ' + r.stdout[-400:])
        self.client = self.ts.client()
        self.ts.ensure_running(self.client)
        self.wait_ticks(10)

    def stop(self):
        ps('stop-server.ps1')
        try:
            os.remove(os.path.join(TS_ROOT, 'test.lock.busy'))
        except OSError:
            pass

    def check_game(self):
        if game_running():
            self.stop()
            raise Stop("the owner's game started: the test server was stopped")

    def call(self, method, params=None):
        started = time.time()
        try:
            reply, error = self.client.call(method, params or {}), None
        except self.ts.ModError as e:
            reply, error = None, {'code': e.code, 'message': e.message}
        self.calls.write(json.dumps({'t': started, 'method': method, 'params': params, 'reply': reply, 'error': error}) + '\n')
        self.calls.flush()
        if error:
            raise Stop('%s: %s %s' % (method, error['code'], error['message']))
        return reply

    def console(self, command):
        return self.call('run_console_command', {'command': command})

    def place(self, prefab, at, **turn):
        p = dict(prefab=prefab, at=[round(v, 3) for v in at], **turn)
        dry = self.call('place_structure', {'placements': [p], 'free': True})
        if dry.get('ready') is False:
            raise Stop('place %s at %s: %s' % (prefab, at, json.dumps(dry.get('problems'))[:400]))
        job = self.call('place_structure', {'placements': [p], 'free': True, 'dry_run': False, 'confirm': True, 'wait': True})
        for _ in range(120):
            state = self.call('place_structure', {'job_id': job.get('job_id')})
            status = state.get('status')
            if status in ('applied', 'applied_with_differences', 'applied_unchecked'):
                placed = state.get('placed') or (state.get('result') or {}).get('placed') or []
                if not placed:
                    raise Stop('placed nothing: ' + json.dumps(state)[:400])
                return str(placed[0]['reference_id'])
            if status in ('stopped', 'refused', 'gas_lost'):
                raise Stop('place %s: %s' % (prefab, json.dumps(state)[:400]))
            time.sleep(0.5)
        raise Stop('place job never finished')

    def tick(self):
        rows = watch_rows()
        return rows[-1]['tick'] if rows else 0

    def wait_ticks(self, n):
        target = self.tick() + n
        deadline = time.time() + n * 2.0 + 120
        while self.tick() < target:
            self.check_game()
            if time.time() > deadline:
                raise Stop('the world stopped ticking (paused?)')
            time.sleep(1.0)


# ---- the driver's lines -----------------------------------------------------------------------------------

HEAD = re.compile(r'watch tick (\d+) \| planet O2 (\S+) N2O (\S+) CH4 (\S+) \| fire (.+?) \| held (\S+) \| peak (\S+) K \| now (\S+) K \| fire heat (\S+) K \| ext (\S+) K \| world cells (\d+) \| edge cells (\d+) \| lerp (\S+) \| sun (\S+)')
RING = re.compile(r'ring<(\S+) c(\d+) lit(\d+) O2 (\S+) N2O (\S+) CH4 (\S+) T(\d+)')
STATION = re.compile(r'st (\S+) c(\d+) lit(\d+) ox (\S+) T(\d+)')
BURN = re.compile(r'watchburn tick (\d+) \| (\S+) \| lit (\d+) \| held (\S+) mol \(max (\S+) a cell\) \| burnt (\S+) mol \| (\S+) J')


def num(s):
    try:
        return float(s)
    except ValueError:
        return float('nan')


def _text(path):
    try:
        return open(path, encoding='utf-8', errors='replace').read()
    except OSError:
        return ''


def watch_rows(path=LOG, since=0):
    out = []
    for line in _text(path).splitlines():
        m = HEAD.search(line)
        if not m or int(m.group(1)) < since:
            continue
        g = m.groups()
        row = {'tick': int(g[0]), 'O2': num(g[1]), 'N2O': num(g[2]), 'CH4': num(g[3]), 'fire': g[4], 'held': g[5], 'peak': num(g[6]),
               'now': num(g[7]), 'fireK': num(g[8]), 'extK': num(g[9]), 'world': int(g[10]), 'edge': int(g[11]), 'lerp': num(g[12]),
               'sun': num(g[13]), 'rings': [], 'stations': {}}
        for r in RING.finditer(line):
            row['rings'].append({'r': r.group(1), 'cells': int(r.group(2)), 'lit': int(r.group(3)), 'O2': num(r.group(4)),
                                 'N2O': num(r.group(5)), 'CH4': num(r.group(6)), 'T': int(r.group(7))})
        for s in STATION.finditer(line):
            row['stations'][s.group(1)] = {'cells': int(s.group(2)), 'lit': int(s.group(3)), 'ox': num(s.group(4)), 'T': int(s.group(5))}
        out.append(row)
    return out


def burn_rows(path=LOG, since=0):
    return [{'tick': int(m.group(1)), 'station': m.group(2), 'lit': int(m.group(3)), 'held': num(m.group(4)), 'held_max': num(m.group(5)),
             'burnt': num(m.group(6)), 'J': num(m.group(7))}
            for m in BURN.finditer(_text(path)) if int(m.group(1)) >= since]


# ---- the site ---------------------------------------------------------------------------------------------

def write_watch(rig, r):
    rx, ry, rz = r
    lines = ['R:%g,%g,%g' % (rx, ry, rz)] + ['%s:%g,%g,%g' % (n, rx + d, ry, rz) for n, d in STATIONS]
    with open(rig.watch_file, 'w', encoding='ascii') as f:
        f.write('\n'.join(lines) + '\n')


def build_site(rig, at, kit=KIT):
    rx, ry, rz = at
    site = {'R': [rx, ry, rz], 'stations': {}}
    write_watch(rig, at)
    for name, d in STATIONS:
        ids = {}
        for role, prefab, across, up in kit:
            try:
                ids[role] = rig.place(prefab, (rx + d, ry + up, rz + across))
            except Stop as e:
                ids[role] = None
                print('  %s %s not placed: %s' % (name, role, e))
        site['stations'][name] = ids
    for name in ('s8', 'far'):
        tank = site['stations'][name].get('tank_small')
        if tank:
            rig.console('addgas Oxygen %s %s 293' % (tank, SECONDARY_MOL))
    with open(os.path.join(rig.results, 'site.json'), 'w', encoding='utf-8') as f:
        json.dump(site, f, indent=1)
    return site


def load_site(rig):
    with open(os.path.join(rig.results, 'site.json'), encoding='utf-8') as f:
        return json.load(f)


def objects(site):
    return [(st, role, rid) for st, ids in site['stations'].items() for role, rid in ids.items() if rid]


def health(rig, site):
    ids = [rid for _, _, rid in objects(site)]
    out = {}
    for i in range(0, len(ids), 200):
        reply = rig.call('thing_health', {'reference_ids': ids[i:i + 200]})
        for r in reply.get('results', []):
            if r.get('ok'):
                out[str(r['reference_id'])] = {'ratio': r.get('damage_ratio'), 'broken': r.get('is_broken'), 'burn': (r.get('damage') or {}).get('burn')}
            else:
                out[str(r.get('reference_id'))] = {'gone': True}
    return out


# ---- time of day ------------------------------------------------------------------------------------------

TIMES = {'midday': lambda a: a < 40.0, 'dusk': lambda a: 80.0 <= a <= 90.0, 'night': lambda a: a > 97.0}


def set_time(rig, when, limit=400):
    """Moves the orbit on (the console's orbit simulate) until the sun stands where `when` wants it."""
    want = TIMES[when]
    for _ in range(limit):
        rows = watch_rows()
        if rows and want(rows[-1]['sun']):
            return rows[-1]['sun']
        rig.console('orbit simulate 2 minutes')
        rig.wait_ticks(2)
    raise Stop('the sun never reached %s' % when)


# ---- one run ----------------------------------------------------------------------------------------------

def release(rig, site, gas, mol, kind):
    """Puts the gas out at R: a burst (a small tank filled past its burst pressure), a vent release (an
    in-line tank behind a passive vent, filled, so it empties through the vent), or the planet itself."""
    rel = site['release']
    gases = gas.split('+')
    if kind == 'planet':
        for g in gases:
            rig.console('terraform gas add %s %s' % (g, mol / len(gases)))
        return
    target = rel['burst'] if kind == 'burst' else rel['tank']
    kelvin = 293.0
    if kind == 'burst':
        # A 10 L pipe bursts above 60,795 kPa. Gas at 293 K bursts it from about 250 mol; a smaller release is put in
        # hot enough to reach 1.2 times that, so it bursts too (the heat comes out with it).
        kelvin = max(293.0, 1.2 * 60795.0 * 10.0 / (mol * 8.3144))
    for g in gases:
        if mol > 0:
            rig.console('addgas %s %s %s %.1f' % (g, target, mol / len(gases), kelvin))


def run_one(rig, site, r, results):
    name = '%s_%s_%s_%s_%s' % (r['time'], r['gas'].replace('+', '-'), int(r['mol']), r['kind'], r['switch'])
    path = os.path.join(results, name + '.json')
    rig.console('terraform set PlanetAirBurns %s confirm' % r['switch'])
    sun = set_time(rig, r['time'])
    before = health(rig, site)
    start = rig.tick()
    release(rig, site, r['gas'], r['mol'], r['kind'])
    released_at = rig.tick()
    out_since = None
    while True:
        rig.wait_ticks(10)
        rows = watch_rows(since=released_at)
        if not rows:
            continue
        last = rows[-1]
        quiet = not last['fire'].startswith(('heat', 'spark')) and all(x['lit'] == 0 for x in last['rings'])
        out_since = (out_since or last['tick']) if quiet else None
        # With the switch off a planet holding oxidiser keeps feeding the outdoor cells, so nothing goes quiet:
        # 600 ticks (five minutes) show the steady state.
        limit = r.get('max_ticks', 1800 if r['switch'] == 'on' else 600)
        if (out_since and last['tick'] - out_since >= 240) or last['tick'] - released_at > limit:
            break
    after = health(rig, site)
    log = {'run': r, 'name': name, 'sun_at_release': sun, 'released_at': released_at, 'rows': watch_rows(since=start),
           'burns': burn_rows(since=start), 'health_before': before, 'health_after': after}
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(log, f)
    return log


# ---- the site as built on the test server -----------------------------------------------------------------
# Learnt building it (2026-10-01): devices need support (a frame below, or a plate behind them); cell centres sit
# on odd metres; vents stand facing +y on a frame with their pipe and power ports toward -z at z-0.3; a station
# battery's output port is port 2, 0.5 m to +x; batteries start switched off; `power chargeall` fills them;
# place_cables needs coils from a container (none on a server with no player), so cable pieces are placed one by
# one with place_structure (straight facing +z; corner facing +z/up +y joins +z and -x, facing -z joins +x and
# -z); the tanks a player builds from kits outdoors are in-line ones here (StructureTankSmallInLine 6,000 L,
# StructureTankBigInLine 50,000 L); a gas pipe bursts above 60,795 kPa.

STATION_X = (('s2', 2), ('s4', 4), ('s8', 8), ('s16', 16), ('s32', 32), ('far', -100))


def powered_vent(rig, x, z, mode):
    """An active vent on a frame with a pipe stub, powered from its own battery through placed cable pieces."""
    ids = {}
    ids['vent_frame'] = rig.place('StructureFrame', (x, 299, z))
    ids['vent'] = rig.place('StructureActiveVent', (x, 301, z), facing='+y')
    ids['vent_pipe'] = rig.place('StructurePipeStraight', (x, 300, z - 0.5), facing='+z')
    ids['battery_frame'] = rig.place('StructureFrame', (x, 299, z + 4))
    ids['battery'] = rig.place('StructureBattery', (x, 301, z + 4))
    for dz in (3.5, 3.0, 2.5, 2.0, 1.5, 1.0):
        rig.place('StructureCableStraight', (x + 0.5, 300, z + dz), facing='+z')
    rig.place('StructureCableCorner', (x + 0.5, 300, z + 0.5), facing='+z', up='+y')
    rig.place('StructureCableCorner', (x, 300, z + 0.5), facing='-z', up='+y')
    rig.call('write_logic', {'reference_id': ids['battery'], 'logic_type': 'On', 'value': 1})
    rig.call('write_logic', {'reference_id': ids['vent'], 'logic_type': 'Mode', 'value': mode})
    rig.call('write_logic', {'reference_id': ids['vent'], 'logic_type': 'On', 'value': 1})
    return ids


def build_site2(rig, R=(1301, 301, 1), stations=STATION_X, release=None):
    rx, ry, rz = R
    site = {'R': list(R), 'stations': {}, 'release': {}}
    with open(rig.watch_file, 'w', encoding='ascii') as f:
        f.write('\n'.join(['R:%g,%g,%g' % R] + ['%s:%g,%g,%g' % (n, rx + d, ry, rz) for n, d in stations]) + '\n')
    rel = site['release']
    if release:
        rel.update(release)
    else:
        # A passive vent on a frame, piped to a 6,000 L in-line tank whose port joins 1 m below and 1 m behind its
        # centre; and above it a lone pipe that a large fill bursts.
        rel['frame'] = rig.place('StructureFrame', (rx, 299, rz))
        rel['vent'] = rig.place('StructurePassiveVent', (rx, 301, rz), facing='+y')
        for dz in (-0.5, -1.0, -1.5):
            rig.place('StructurePipeStraight', (rx, 300, rz + dz), facing='+z')
        rel['tank'] = rig.place('StructureTankSmallInLine', (rx, 301, rz - 2.5), facing='+z')
    if not rel.get('burst'):
        rel['burst'] = rig.place('StructurePipeStraight', (rx, 303, rz), facing='+z')
    for name, d in stations:
        x = rx + d
        ids = {}
        def put(role, prefab, at, **turn):
            try:
                ids[role] = rig.place(prefab, at, **turn)
            except Stop as e:
                ids[role] = None
                print('  %s %s not placed: %s' % (name, role, str(e)[:200]))
        try:
            ids.update(powered_vent(rig, x, rz, 1))
        except Stop as e:
            print('  %s vent assembly failed: %s' % (name, str(e)[:200]))
        put('tank_small', 'StructureTankSmallInLine', (x, 301, rz - 5), facing='+z')
        put('cable', 'StructureCableStraight', (x, 301, rz + 8), facing='+z')
        put('cable_heavy', 'StructureCableStraightH', (x, 301, rz + 10), facing='+z')
        put('pipe', 'StructurePipeStraight', (x, 301, rz + 12), facing='+z')
        put('pipe_insulated', 'StructureInsulatedPipeStraight', (x, 301, rz + 14), facing='+z')
        put('solar_frame', 'StructureFrame', (x, 299, rz + 16))
        put('solar', 'StructureSolarPanel', (x, 301, rz + 16), facing='+y')
        put('wall', 'StructureWallIron', (x, 301, rz + 18))
        put('frame', 'StructureFrame', (x, 301, rz + 20))
        site['stations'][name] = ids
        print(name, sum(1 for v in ids.values() if v), 'placed')
    rig.console('power chargeall')
    for name in ('s8', 'far'):
        tank = site['stations'].get(name, {}).get('tank_small')
        if tank:
            rig.console('addgas Oxygen %s %s 293' % (tank, SECONDARY_MOL))
    with open(os.path.join(rig.results, 'site.json'), 'w', encoding='utf-8') as f:
        json.dump(site, f, indent=1)
    return site


def fresh_run(rig, r, save='leak-vulcan', results=None):
    """One run from the saved site: restart the server on the save, so every run starts from the same world."""
    results = results or rig.results
    name = '%s_%s_%s_%s_%s' % (r['time'], r['gas'].replace('+', '-'), int(r['mol']), r['kind'], r['switch'])
    if os.path.exists(os.path.join(results, name + '.json')):
        print('done already:', name)
        return None
    rig.stop()
    rig.start(save=save)
    site = load_site(rig)
    t0 = time.time()
    log = run_one(rig, site, r, results)
    errors = [line for line in _text(LOG).splitlines() if re.search(r'^\[(Error|Fatal)|Exception', line)]
    log['log_errors'] = errors[:200]
    path = os.path.join(results, name + '.json')
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(log, f)
    print('%s: %.0f s, %d rows, %d error lines in the server log' % (name, time.time() - t0, len(log['rows']), len(errors)))
    return log


PRIORITY = (
    [dict(time='midday', gas='Oxygen', mol=0, kind='gas', switch=s) for s in ('on', 'off')]
    + [dict(time='midday', gas='Oxygen', mol=m, kind=k, switch=s) for m in (1000, 5000) for k in ('gas', 'burst') for s in ('on', 'off')]
    + [dict(time='night', gas='Oxygen', mol=m, kind=k, switch=s) for m in (1000, 5000) for k in ('gas', 'burst') for s in ('on', 'off')]
    + [dict(time=t, gas='Oxygen+NitrousOxide', mol=5000, kind='gas', switch=s) for t in ('midday', 'night') for s in ('on', 'off')]
    + [dict(time='night', gas='Oxygen', mol=3000000, kind='planet', switch='on', max_ticks=1200)]
    + [dict(time='dusk', gas='Oxygen', mol=m, kind='gas', switch=s) for m in (1000, 5000) for s in ('on', 'off')]
    + [dict(time='midday', gas='Oxygen', mol=m, kind=k, switch=s) for m in (50, 200, 20000) for k in ('gas', 'burst') for s in ('on', 'off')]
    + [dict(time='night', gas='Oxygen', mol=m, kind='gas', switch=s) for m in (50, 200, 20000) for s in ('on', 'off')]
)


def matrix(rig, runs=PRIORITY):
    done = []
    for r in runs:
        try:
            if fresh_run(rig, r) is not None:
                done.append(r)
        except Stop as e:
            print('STOPPED at %s: %s' % (r, e))
            if 'owner' in str(e):
                raise
    return done


if __name__ == '__main__':
    wanted = sys.argv[1:]
    runs = [r for r in PRIORITY if not wanted or any(w in '%s_%s_%s_%s_%s' % (
        r['time'], r['gas'].replace('+', '-'), int(r['mol']), r['kind'], r['switch']) for w in wanted)]
    rig = Rig()
    try:
        matrix(rig, runs)
    finally:
        rig.stop()
