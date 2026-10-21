# Comet impacts

Design for the comet feature on branch `comet-impact`. The prototype (0.15.0) is a console-called comet that
adds its gas at impact and plays a show for the host only. This document is the next step: what the impact
does to the world, how every player sees and hears it, and how it is checked. How comets are found and sent
(the rocket side) is not designed yet.

## What LU asked for (2026-10-20)

- Stays in Terraforming Reloaded.
- Better visual fidelity, with a test rig (run on bt, not on LU's PC).
- Explosions that vary with the reaction the comet's material makes.
- No modelled comet head.
- Good sound design.
- The comet's size sets a pressure wave. A structure whose limit the wave exceeds (windows first) breaks the
  moment the wave reaches it.
- A player outdoors: small comet, a minor push; medium, thrown a bit; huge, thrown hard and the suit damaged
  at once.
- Multiplayer: everyone sees and hears it, wherever they stand.
- No crater. The comet always lands off in the distance, never on a base.
- The sky goes darker for a while afterwards.

## 1. One event, played on every machine

The host decides everything and sends it once. Every machine, host included, plays the same event from the
same numbers against a clock they share.

**The event** (sent host to clients):
- a sequence id;
- the start time on the shared clock, `OrbitalSimulation.System.TotalRealTimeSeconds`, a little ahead of
  now so clients have it before the comet appears;
- the comet: moles, each gas and its share, arrival temperature, preview flag;
- the geometry, as world positions: impact point, start point, travel direction;
- a seed for every random choice in the show, so it looks the same everywhere.

**Delivery:**
- An instant message through LaunchPadBooster's mod networking, so the start is not held back by the
  planet sync interval.
- The current event also rides in TR's planet sync as layout byte 5. A player who joins during an event, or
  whose message was lost, gets it there and picks the show up at the time it has reached. They skip what has
  already happened; they do not replay the approach.

**Who does what:**

| | Every machine | Host only |
|---|---|---|
| Visuals | yes | |
| Sound | yes | |
| Darker sky | yes | |
| Pushing the local player | yes, its own body only | |
| Gas into the planet | | yes |
| Breaking structures | | yes |
| Suit damage | | yes |

Pushing has to run on the player's own machine. The game lets each client move its own body and overwrites
any force the host puts on someone else's.

A save in the middle of an event drops it. The gas is in or it is not.

## 2. Where it lands

The host picks the impact point. It starts from where the host is looking, turned by `turn=` if given, at
`range=` if given. It must be at least the **safe distance** from every structure and every player in the
world:

- safe distance = 1,500 m + 500 m x the show scale: 1,750 m at 1e4 mol, 2,000 m at 1e5, 2,250 m at 1e6,
  2,500 m at 1e7, 2,750 m at 1e8, 3,000 m from 1e9 up;
- if the point is too close to anything, it moves outward along the same bearing, then tries other bearings,
  until it fits;
- a `range=` that is too close is moved out, and the reply says so.

The approach path is checked for the same distance, so the comet never passes low over a base.

## 3. The pressure wave

The wave leaves the impact at 340 m/s and reaches a point `d` metres away `d / 340` seconds after the impact.
Its peak overpressure there, in kPa:

```
dP(d) = 400 x (moles / 1e7)^(2/3) x (2000 / d)^2
```

That gives, at the safe distance (where the nearest base is at most):

| Comet | Safe distance | dP there | Breaks |
|---|---|---|---|
| 1e5 mol, small | 2,000 m | 19 kPa | nothing |
| 1e6 mol, medium | 2,250 m | 68 kPa | nothing |
| 1e7 mol, large | 2,500 m | 256 kPa | 200 kPa windows and glass doors |
| 1e8 mol, huge | 2,750 m | 982 kPa | also composite walls (300), shutters (350), hatches (500) |
| 1e9 mol, largest | 3,000 m | 3,830 kPa | also airlocks (1,000) |

Reinforced windows and walls (8,000 kPa and up) hold against every comet at the safe distance. A base
further away than the nearest allowed point gets less: the peak falls with the square of the distance.

This is a game rule, not a physical model. A real blast of this energy at these distances is a few kPa, and
the game's windows take 200. It also ignores the planet's air, so a thin-aired Mars feels it as much as Venus
does. Both are deliberate: LU's spec is that comet size sets the damage.

The constants live in one place, so they can be tuned.

## 4. Structures

At impact the host goes through every structure (`GridController.AllStructuresPool`) and keeps those that:

- can break (`MaxPressureDelta > 0`, not indestructible, not already broken, not an open door);
- face the outside: at least one side of the face is outdoors (no room);
- get a peak above their limit.

Each one breaks at its own arrival time, nearest first. The break is the game's own: damage set to the
maximum on the host, which syncs and switches to the broken model or removes the piece, and lets the air out.
A window deep inside a base is shielded by the rooms around it and is left alone.

Sound: the game's break sound plays only from a code path that is silent on the host, so every machine plays
it itself:
- `GlassWallFail`, `MetalWallFail` or `CompositeWallFail` by material, at the structure, at the arrival time;
- `GlassWallStress` (a creak) for windows the wave takes past 80 % of their limit without breaking them.

Clients work out the same list from their own copy of the world, so no list is sent.

## 5. Players outdoors

A player counts as outdoors when not in a room and not seated or inside something. At the wave's arrival:

| Peak at the player | Push (velocity change, away from the impact plus upward) | Host also |
|---|---|---|
| below 50 kPa | 1 to 4 m/s, slight upward part | |
| 50 to 200 kPa | 6 to 12 m/s, upward 3 to 5 m/s | |
| above 200 kPa | 14 to 20 m/s (the game caps players at 20), upward 6 to 8 m/s | suit and helmet damage through the game's own collision hit |

Within each band the push grows with the peak. The upward part is needed: a player standing still has their
speed halved every physics step while on the ground. The suit damage is the game's collision path, so it
leaks air, takes damage and plays the game's sounds, and duct tape fixes it. No knockout: being knocked out
drops what the player holds and asks them to respawn.

Every player gets the camera shake, scaled by the peak, and a gust of wind sound, louder outdoors.

## 6. Explosions by reaction

The comet's mix, with the planet's air, decides which reactions happen. Each one is a layer of the show,
drawn as strongly as its share. A mixed comet shows several at once.

| Reaction | When | What it looks like |
|---|---|---|
| Steam | water or ice in the comet | a white condensation dome flashes out with the shock, then a white billowing column, less fire |
| Burning | fuel (volatiles, hydrogen, hydrazine) meets oxidiser (oxygen or nitrous oxide, from the comet or the planet's air) | a second, larger fireball a second or two after impact, rolling orange-red flame, a longer glow, more light |
| Dry ice | carbon dioxide | a dense grey-white fog rolling out along the ground |
| Toxic | pollutant, hydrochloric acid | a sickly yellow-green haze that settles low and lingers |
| Ionised | nitrogen, helium, inert gases | an electric blue-violet shimmer in the cloud, a sharper flash |

Fuels and oxidisers are TR's own lists (`FireGases`). The gas colours stay as in the prototype.

## 7. Sound

Everything goes through the game's mixer, so the player's volume settings, pausing, the helmet and the air
around the player all apply as for any game sound. Two routes:

- **Through the air** (the External group): roar, crack, boom, gust, glass. The air around the listener
  muffles it the way the game muffles everything.
- **Through the ground** (a local group the air does not filter): a deep thump and a rumble felt through the
  feet. It still arrives in a vacuum, so an airless world gets a thud and no boom.

The sequence:

1. **Approach:** the game's falling-rocket air rush, played between the listener and the comet so it is loud
   enough, rising; under it a synthesised low roar.
2. **Entry:** the game's large sonic boom as the comet comes through the sky.
3. **Impact:** silent. The flash comes first; sound has to travel.
4. **Sound arrives** (`d / 340` s later): the ground thump first, then the game's distant rocket explosion and
   large explosion, then the long tail of the far rocket landing and a synthesised sub-bass rumble.
5. **Wave arrives** (same moment for the player): heavy wind gust, glass breaking and creaking at the
   structures it reaches, nearest first.
6. **After:** falling debris pops (flare air bursts) at random points in the sky; the storm loop fading in and
   out, muffled when the player is indoors.

## 8. The darker sky

After impact the dust spreads and the sky dims to a depth set by the comet's size, then recovers:

- depth: 15 % dimmer for a small comet up to 55 % for the largest;
- it falls over the first 30 s and recovers over 5 minutes;
- it dims the sky's scattering, the ambient light and the sun's light, on every machine;
- visual only: solar panels are not affected.

Done by postfixes on `AtmosphericScattering.ManagerUpdate` and `OrbitalSimulation.SetSolarIntensity`. They
work whether or not the dynamic sky is on, and every value they scale is rebuilt by the game each frame, so
nothing drifts.

## 9. Visual fidelity

- **Daylight:** in the prototype the glow washes out against a bright sky. More of the show becomes opaque
  smoke that reads by day, and the glow carries the night.
- **Animated smoke:** a frame sheet generated in code, so puffs roll instead of sliding.
- **Soft particles:** the game's soft additive particle shader, so glow does not cut hard lines into the
  ground.
- **Draw order:** fire glow stays inside the smoke, not on top of it from every angle.
- **Frame cost:** particle counts are capped. The rig reports frame time.

## 10. How it is checked

- **The rig** (`tools/CometShot`, run on bt by the bt operator): screenshots at set times and angles, day and
  night, one run per reaction, and the log of every decision (impact point, distances, wave peaks).
- **Structures and players:** on bt's dedicated test server. Build windows, doors and walls at measured
  distances, fire a comet, and check what broke against section 3's table. Then do the same with a rendered
  client joined, to check that the client sees, hears and is pushed.
- **Gas:** the planet gains exactly the comet's moles (LiveCheck's balance, after the event).
- **Arithmetic:** the wave formula, the safe distance and the push table get a self-check like the mod's other
  rules.
