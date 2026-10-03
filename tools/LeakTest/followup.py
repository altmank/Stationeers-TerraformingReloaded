"""The armed hold-back and the trace hold, checked on the leak site with a few short runs.

    python tools/LeakTest/followup.py [name ...]

Each run restarts the test server from the leak site's save (leak-vulcan), turns the three settings to what it
needs with terraform set, and reads the planet, every outdoor cell and the stations through the watch
(tools/LiveCheck/Watch.cs), which also logs the readout's fire and hold lines every 20 ticks. Results go to
results/followup_<name>.json; summary() prints what each run shows.

  armed_on    1,000 mol of oxygen through the vent at midday, fire and armed hold-back on, trace hold off.
  armed_off   the same with the armed hold-back off: the one-tick lead, as the control.
  trace       1.5 mol of oxygen put straight into the planet with the fire off: trace hold on for 300 ticks,
              off for 300, on again; then the fire on with the armed hold-back on and off, 100 ticks each,
              so the readout's hold line and settings line can be seen to follow each switch.
  leftover_on / leftover_off
              200 mol of oxygen through the vent at midday with the fire on and the armed hold-back off, trace
              hold on / off, run on past the fire to see what the burn leaves in the planet.
"""
import json
import os
import re
import sys

from leaktest import (LOG, RESULTS, Rig, Stop, _text, burn_rows, health, load_site, release, set_time,
                      watch_rows)

STATUS = re.compile(r'LiveCheck: status tick (\d+) \|\s*(.*)')


def status_rows(since=0):
    out = []
    for line in _text(LOG).splitlines():
        m = STATUS.search(line)
        if m and int(m.group(1)) >= since:
            out.append({'tick': int(m.group(1)), 'text': m.group(2).strip()})
    return out


def switches(rig, fire, armed, trace, log, why=''):
    on = lambda b: 'on' if b else 'off'
    for key, value in (('PlanetAirBurns', fire), ('PlanetHoldsBackWhileIgnitable', armed), ('PlanetKeepsTraceGas', trace)):
        rig.console('terraform set %s %s confirm' % (key, on(value)))
    log['events'].append({'tick': rig.tick(), 'what': 'fire %s, armed %s, trace %s%s' % (on(fire), on(armed), on(trace), why)})


def leak(rig, site, log, mol, fire, armed, trace, ticks):
    switches(rig, fire, armed, trace, log)
    log['sun'] = set_time(rig, 'midday')
    log['health_before'] = health(rig, site)
    release(rig, site, 'Oxygen', mol, 'gas')
    log['released_at'] = rig.tick()
    log['events'].append({'tick': log['released_at'], 'what': '%d mol of oxygen into the vent tank' % mol})
    rig.wait_ticks(ticks)
    log['health_after'] = health(rig, site)


def trace(rig, site, log):
    switches(rig, False, False, True, log)
    rig.wait_ticks(10)
    rig.console('terraform gas add Oxygen 1.5')
    log['released_at'] = rig.tick()
    log['events'].append({'tick': log['released_at'], 'what': '1.5 mol of oxygen into the planet'})
    rig.wait_ticks(300)
    switches(rig, False, False, False, log, ': the trace hold off')
    rig.wait_ticks(300)
    switches(rig, False, False, True, log, ': the trace hold on again')
    rig.wait_ticks(100)
    switches(rig, True, True, True, log, ': fire and armed hold-back on')
    rig.wait_ticks(100)
    switches(rig, True, False, True, log, ': armed hold-back off')
    rig.wait_ticks(100)


RUNS = {
    'armed_on': lambda rig, site, log: leak(rig, site, log, 1000, True, True, False, 400),
    'armed_off': lambda rig, site, log: leak(rig, site, log, 1000, True, False, False, 400),
    'trace': trace,
    'leftover_on': lambda rig, site, log: leak(rig, site, log, 200, True, False, True, 900),
    'leftover_off': lambda rig, site, log: leak(rig, site, log, 200, True, False, False, 900),
}


def run(rig, name):
    path = os.path.join(RESULTS, 'followup_%s.json' % name)
    rig.stop()
    os.environ['TR_LIVECHECK_STATUS_EVERY'] = '20'
    rig.start(save='leak-vulcan')
    site = load_site(rig)
    log = {'name': name, 'events': []}
    start = rig.tick()
    RUNS[name](rig, site, log)
    log['rows'] = watch_rows(since=start)
    log['burns'] = burn_rows(since=start)
    log['status'] = status_rows(since=start)
    log['log_errors'] = [line for line in _text(LOG).splitlines() if re.search(r'^\[(Error|Fatal)|Exception', line)][:200]
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(log, f)
    print('%s: %d rows, %d burn lines, %d status lines, %d error lines' % (name, len(log['rows']), len(log['burns']), len(log['status']), len(log['log_errors'])), flush=True)


if __name__ == '__main__':
    names = sys.argv[1:] or list(RUNS)
    rig = Rig()
    rig.install()
    try:
        for n in names:
            run(rig, n)
    except Stop as e:
        print('STOPPED: %s' % e, flush=True)
    finally:
        rig.stop()
