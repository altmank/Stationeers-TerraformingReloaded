# Comets by rocket

Design for how a player makes a comet and brings it down, on branch `comet-impact`. The impact itself (the
show, the pressure wave, structures, players, sound, sky) is `COMET.md`. Built so far: the settings and
the pure rules (stage 2 of *Proof as it is built*, 0.17.0), the comet as an object on the space map that
flies to Entry by the rocket rule, saved with the world and sent to every player (stage 3, 0.18.0), the
rocket actions that make it and send it: Cut, Rig, Fuel, Burn and Launch (stage 4, 0.19.0), the parked
comet in the game's own sky, aimed by a rocket at Entry, seen by the Ground Telescope and shown on the Map
motherboard (stage 5, 0.20.0), the deorbit and the landing: a rocket at Entry brings the comet down, every
player is warned, and the impact brings its gas or takes air away (stage 6, 0.21.0), and what the impact does
near where it lands: ground zero, the fireball's heat, the wave and the falling rocks, in one pass (stage 7,
0.22.0).

Game facts below are from the decompile; file and line references are kept out of this public repo.

## What LU decided (2026-10-20 and 21)

- Comets come from the space map's existing ice and mineral sites. Discovery and survey are unchanged.
- Ice comets add gas to the planet. Mineral comets remove it. Both are in scope now.
- The comet is made by new rocket actions. Its flight comes from drill heads and engines; a new rocket part is
  a later upgrade, not this design.
- More work at the site makes a bigger comet, but one site must never deliver a third of a planet.
- The comet is a real object on the map. Between nodes it moves exactly as a rocket does: no stops on the way,
  no coasting, no rescue if it runs dry. Only the last step, from orbit to the ground, is new.
- Landing it well is the challenge, and a player can also skip that and see what happens. Nothing prevents a
  comet hitting a base, the player's own included. A supermassive comet brought down badly is a catastrophe.
- It never lands out of sight on the far side of the planet. Risk and the show stay part of every landing.
- Only part of the gas reaches the planet.
- Interactions happen only between objects standing still on the map.
- Every mechanic has to be cheap to compute (below).

## The loop

| Stage | Where | What the player does | What it sets |
|---|---|---|---|
| Cut | the site | runs Cut with drill heads | the comet's size, so its gas and its mass |
| Rig | the site | delivers engines as payloads, one per Rig | the comet's thrust |
| Fuel | the site | transfers fuel from the rocket, or burns the comet's own ice into fuel | how far it can go, and the reserve |
| Fly | the map | sets the comet's destination to Entry, the planet's orbit node | nothing new: rocket travel |
| Bring down | Entry, the planet's orbit node | sets a target, then burns at the right moment in the comet's pass | where it lands |

## Cut

- A rocket with a miner and drill heads sits at an ice or mineral site and runs Cut.
- Each cycle adds 0.1 % of K times the site's gas to the comet (`CutSharePerCycle`, below), so a whole comet is
  1,000 cycles, times the drill head's yield for the site's kind as the game's mining uses it (an Ice head's 1.2
  makes a whole comet in 834 cycles; a head that mines nothing of that kind cuts nothing).
- Cycle time and head wear follow the game's mining: `(10 - 0.4 x Density) / head speed` seconds, and heads
  wear as they do mining.
- The site panel shows the comet's moles and how much of the site it holds.
- The deposit is the limit. A Mars ice site holds 6,000 to 100,000 units of ice at 24 mol each, so 0.14M to
  2.4M mol (calculated from the game's site rules, not measured).
- The site's gas is what the game counts it to hold: `TotalOreAtLocation` (the figure the game shows, one
  significant figure) times the moles in a unit of the ice its miners cut there.
- The comet's weight in flight is what was cut from its site, by the game's molar masses (`Mole.MolarMass`,
  not the physical ones: water 108 g/mol, oxygen and methane 16, nitrogen 64, carbon dioxide 44), times
  `CometMassScale` (0.39); the extra gas K gives weighs nothing (stage 4 decisions, below). A median Mars comet,
  2.3M mol of water at Standard size, is cut from 460,000 mol of the site and weighs 18.4 t in flight; at the
  game's molar masses all of it would be 248 t.
- A mineral site makes a mineral comet: units of ore, `CutSharePerCycle` of K times the site's units a cycle,
  each weighing `MineralKgPerUnit` (2.592 kg) before the scale.

## Rig

- Each Rig action installs one engine on the comet, carried there as a payload.
- An engine adds 500 kg. The Pumped Gas engine gives 16.39 kN at 36 mol/s (thrust measured in game; fuel rate
  from the code). Other engine types are not checked.
- Engines buy time, not range: every engine burns fuel at the same rate per unit of thrust, so fuel per unit
  of speed gained does not change with engine count. More engines make the trip shorter, add mass, and give
  more correction at the end (below).
- **The payload is the game's own Pumped Gas engine kit** (`ItemKitGovernedGasRocketEngine`), carried inside a
  Payload Delivery Container in the rocket's payload bay. Rig is the game's Deploy action, offered at a site
  where a comet waits: when the bay's deploy finishes, every kit in the container becomes an engine on the
  comet, as many as the kits would build, and the kits are used up. The container stays in the bay.

## Fuel

Two ways, and the choice matters:

- **Haul it (Fuel).** While the rocket runs Fuel at the comet's site, the 2:1 methane and oxygen in its tanks
  and pipe networks (never an engine's chamber) move into the comet as a Turbo Volume Pump would move them:
  each second, what pairs two to one in `FuelLitresPerSecond` (200) litres of the rocket's stores. What is not
  premix (a methane excess, nitrogen, anything else) stays. It runs until the player stops it, so each trip has
  to keep back what it needs to fly home: the action does not guess that for the player. A big fuel rocket
  hands over about 100,000 mol a trip on Mars (stage 4 decisions, below).
- **Burn the comet.** An ice comet holding methane and oxygen can feed its own engines: the drill heads turn
  its ice into propellant, a drill cycle at a time, with the game's timing and head wear, each cycle asking a
  Cut cycle's share of the comet as it stands. It saves trips, and every mole burned is a mole the planet never
  gets. The site itself is not mined.

Mineral comets, and ice that cannot burn (pure carbon dioxide), have to be hauled for.

## Fly

The comet obeys the game's rocket travel rule and nothing else:

- speed gained each tick = thrust / total mass x tick time, and the hop's progress grows by that over the
  hop's Δv;
- a hop's Δv is twice the map distance (x 1.5 on Stationeer difficulty); any discovered site is 200 from its
  node, Entry to the low-orbit hub is 2;
- total mass is the comet, its engines and the fuel still in it;
- it moves only while its engines burn. It flies the whole route to its destination in one go, through the
  nodes on the way, and stops when it arrives;
- if the fuel runs out on the way, it stops where it is, for good, like a rocket. No other rocket can reach a
  point between nodes, so the comet is lost.

Its icon moves on the map as a rocket's does.

### As built (stage 3, 0.18.0)

- **The comet is the mod's own map object.** It waits at its site; sent, it takes the route the game's
  pathfinder gives from the site to Entry (`SpaceMapPathFinder.GetPath`, whose list comes back with the first
  hop last), each hop's delta-v as the game works it out (`NodeTransit.GetDistance`, the difficulty's
  multiplier included). The route's node ids and the hops are kept with the comet when it sets off.
- **Where it is, at any moment, is a pure function of the time it has flown** (`CelestialRules.PositionAt`):
  the delta-v the engines have given by then by the rocket equation (`CelestialRules.DeltaVAfter`), counted
  through the hops in order. Once the route is flown it parks at Entry with the fuel it did not burn; once the
  fuel is gone short of Entry it stands where that left it, for good. Entry is always found by its role,
  `SpaceMap.Current.EntryNode`, never by id.
- **The clock is the game's own** (`OrbitalSimulation.System.TotalRealTimeSeconds`): the one the sky runs on,
  saved with the world and sent to every player in every state update.
- **Saved inside the .save**, as an entry of its own, `tr-comets.xml`, written after the terrain
  (`SaveHelper.AddZipFile`) from what was gathered on the main thread with the rest of the save
  (`XmlSaveLoad.GetWorldData`), and read back once the map and its sites are rebuilt (`SpaceMap.LoadGameSave`).
  A load does not keep every node's id (decisions, below), so each node a comet refers to is found again by
  what the save knew it as and renumbered to the id the load gives it. A flight is saved as the seconds it had flown; it goes back on the clock when the
  game restores its own (`OrbitalSimulation.DeserializeSave`) and flies on from those seconds, so it carries
  on from exactly the place it was saved at. A comet whose site or route is no longer on the map is dropped at the load,
  by name in the log.
- **Every player** gets the whole list, each comet as the state it is in or set off with, the moment it
  changes (a mod message) and with the planet sync (layout 7, `MULTIPLAYER.md`). Each machine places each
  flight by the same rule on the shared clock.
- **The map icon** is the game's rocket icon, tinted icy blue, with an asteroid picture from the game's own
  map icons and a tooltip that names the comet, placed by the same plain interpolation the game places a
  rocket's with, a little below any rocket at the same node.
- **A site a comet holds stays on the map**: the one it waits at, and every node on its route until it
  arrives. The host's two ways of removing a site skip a held one, and the map's remove button is greyed out
  over it.
- **Debug verbs, until Cut, Rig and Fuel** (stage 4): `terraform comet make <site> <mol> <gas>[:<share>] ...
  engines=<n> fuel=<mol>` puts a comet at a discovered site (`terraform comet make` alone lists them by number
  and name); `terraform comet send <id>` sends it; `terraform comet list` shows each comet, where it is, its
  fuel, and when it reaches Entry or runs dry by the rule. `make` and `send` are host only and refuse unless
  the save part is installed. Fuel is moles of the 2:1 premix.

Worked example: an 18 t comet with 2 engines going from Mars Orbit to the Frozen Vapors node (383 Δv) burns
for 223.5 s and uses 16,092 mol of fuel. That is the exact integral: the mass falls as the fuel burns, at the
game's 16 g/mol for the 2:1 premix, and `CelestialRules.Check` holds it to a flight stepped as the game steps
one (fuel drawn every 0.5 s, thrust every 0.02 s) within 0.2 %. Thrust over a fixed mass gives 225 s. Doubling
the comet doubles the fuel. A discovered site adds 200 Δv to its node, and a comet is heavy: a 2.3M mol water
comet (248 t) with 2 engines needs 4,484 s and 322,800 mol from a Frozen Vapors site (583 Δv).

## Bring it down (the new part)

Approved by LU 2026-10-21. "Entry" below is the game's name for the planet's orbit node: the first stop on the
space map after launch, and the only place the game lets a payload be dropped to the ground.

### What it is judged against

1. Landing well is a skill, and skill lowers the risk.
2. A player can skip the skill, and then their base has a real chance of being hit.
3. It never lands out of sight. Every landing is a show, and some risk always remains.
4. It is built from how the game already works.
5. It is cheap to compute and simple to build.
6. In multiplayer, other players' bases are not at risk just because they exist.
7. A supermassive comet brought down badly is a catastrophe.

### The mechanic: aim with coordinates, deliver with timing

1. **It parks at Entry**, where the game already brings things from orbit to the ground. It waits there for as
   long as it takes, as a rocket can.
2. **The player sets a target with PositionX and PositionZ**, the logic values the payload drop uses, on an
   avionics unit fitted during Rig. Rig needs it anyway: without avionics nothing can set the comet's
   destination or fire its burn.
3. **Each orbit the comet passes over the map along a line**, and the line shifts sideways every orbit as the
   planet turns. The line comes from the comet's real place in the game's sky (below).
4. **When the player burns sets where on that line it comes down**: the point under the comet at that moment,
   by the sky rule below.
5. **Fuel left at Entry steers it from that point toward the target**, up to a reach set by the reserve and
   capped by engine thrust during entry.
6. **It lands** on the target if the target is within reach, otherwise as close as the fuel gets it.
7. **It is shown** by the comet in the sky for everyone; on the game's Map motherboard (the map screen in a
   computer) as two icons, the target and "lands here if you burn now", which slides along the line during a
   pass; and by logic readouts, so a chip can time the burn.

### The comet in the sky

At Entry the comet becomes a body in the game's own sky, the system that moves the moons.

- Its position is worked out from the game's shared clock and its orbit, so every player sees it in the same
  place with nothing sent per frame.
- The game puts the observer at the planet's centre, so it looks the same from anywhere on the map. It rises,
  crosses the sky and sets, and its path shifts from one orbit to the next as the planet turns.
- The game compresses sky time by the world's day length (a "2 hour" orbit loops in 97 s of real time on Mars
  and 1.3 s on Venus, calculated from the code), so each world's orbit is set from a real time between passes:
  `OrbitSeconds`, 372 s by default, turned into the world's own period by `CelestialRules.OrbitPeriodDays`
  (0.3189 d on Mars, Phobos's own).
- Its orbit is tilted to the planet's equator (`OrbitInclinationDegrees`, 60 by default). Between passes the
  ground turns 360 x 372 / 1,200 = 112 degrees, and the tilt decides how far that moves the line.

### The sky rule: from the comet's place in the sky to a point on the ground

The point under the comet is in the compass direction the comet is in, at a distance set by its height:

distance from the pad = R x (90° - its height in the sky) / 180°

| Comet | Point |
|---|---|
| straight overhead | the launch pad of the rocket that set the comet's target |
| on the horizon | half of R out |
| straight below, the planet's far side | R out, the edge of the disc |

R is the disc radius. Nothing lands out of play: the far side of the planet maps to the edge of the disc.
`DiscRadiusMetres` is R on a world drawn with Mars's and Venus's curvature, 0.3; a world drawn more curved takes
R x 0.3 / curvature (`CelestialRules.DiscRadius`), since how far a column stays in sight goes as one over the
curvature: 3,400 m on Vulcan, 3,060 m on the Moon and Mimas, 6,120 m on Europa.

Steering: the fuel left at Entry gives a Δv, the reach is that Δv times `CorrectionMetresPerDeltaV`, but no more
Δv than the engines give in the fall (`EntryWindowSeconds` of burning), and the landing is the sky point moved
straight toward the target by the reach or the whole gap, kept inside the disc (`CelestialRules.ReachMetres`,
`CelestialRules.Landing`). An 18 t comet with 2 engines steers up to 2.1 km; a 248 t one with 2 engines 165 m.

### Skipping the skill

Burn at any moment, with no target or with a target out of reach. The comet comes down near wherever the sky
rule puts it, a random point in the disc around the pad. A small comet usually does no harm; a big one has a
real chance of wrecking the base.

Chance that a random landing breaks the windows of a base at the pad, for the 5,100 m disc, from the wave rule
in `COMET.md`, taking the comet's direction at a random moment as equally likely anywhere in the sky (an
approximation: its real path favours some directions). By the sky rule a direction within an angle a of
straight overhead lands within R x a / 180 of the pad, so the chance of landing within d is
(1 - cos(180 x d / R)) / 2:

| Comet | Wave breaks windows out to | Chance | Ground zero | Chance it is on the pad |
|---|---|---|---|---|
| 1e6 mol | 1.3 km | about 16 % | 186 m | 0.3 % |
| 1e7 mol | 2.8 km | about 59 % | 401 m | 1.5 % |
| 1e8 mol | 6.1 km | every landing | 863 m | 7 % |

Calculated, not measured.

### How each goal is met

| Goal | How |
|---|---|
| 1. Skill | pick a target on a pass that comes near it, carry the fuel to cover the rest, burn at the right moment |
| 2. Skipping it | burn whenever: it lands where the sky puts it, near the pad, with a real chance of hitting the base |
| 3. Always seen, always some risk | every landing is within R of the pad; a big comet's wave and debris reach far even when well aimed |
| 4. The game's own pieces | the payload drop's coordinates, the drop from Entry, rocket fuel and thrust, the game's sky, the Map motherboard |
| 5. Cheap | one sky position per frame, one landing calculation per burn, the existing impact pass |
| 6. Multiplayer | the line stays near the burning player's own pad, so another base is only at risk if it is close to that pad |
| 7. Catastrophe | a big comet gets little reach per unit of fuel and has the biggest wave, so a careless burn wrecks things |

### Rejected, and why

| Option | Fails on |
|---|---|
| Coordinates alone, exact like the payload drop | 1, 2: no risk at all |
| A random natural landing point | 1: luck, not skill |
| A beacon on the ground | LU rejected it |
| The sky rule alone, with no target | 1: a time can be chosen, a place cannot |
| The far side lands out of sight | 3: LU rejected it |
| A push bar at the site, coasting, a catcher, tracking precision | LU rejected them earlier (no cause for the comet to move, distance for free, two rockets, no game precedent) |

### Tracking

- **By eye.** Nearly overhead means the point is near the pad.
- **Map motherboard.** The two icons above.
- **Celestial Scanner.** Shows a dot, the name and the distance for every sky body, through terrain. No orbit
  lines.
- **Ground Telescope.** Locks onto the comet within 5° and reads it within 2°. Built facing north, its
  Horizontal is the comet's compass bearing and its Vertical the comet's angle down from straight up (90 less
  its height), so they give the point directly. During any storm the orbit readings go blank, which a chip
  that burns on its own has to allow for.

### As built (stage 5, 0.20.0)

- **The comet is a body in the game's own sky while it is parked at Entry** (`CometSkyBodies`): a
  `CelestialBody` made from a template of its own (id `TRComet` and the comet's id, so its CelestialHash is the
  game's hash of that; named "Comet" and its id), circling the planet being played, in the planet's own list
  of the bodies that circle it and in the sky's two lists of every body, after the world's own bodies, in
  comet id order. The game has no way to add a body once a world is loaded; this uses its public pieces (a
  template, the body, the planet's list) and its two private lists of every body, by reflection. Every
  machine, a dedicated server too, makes the body from its own copy of the comets, makes it again after every
  world load (which empties those lists), and takes it out when the comet is no longer parked.
- **Its orbit** (`CometSkyRules.OrbitFor`): once round in `OrbitSeconds` real seconds (Orbit.Period 0.3190 d
  on Mars, by `CelestialRules.OrbitPeriodDays`), round, at Phobos's radius of 9,376 km, its pole
  `OrbitInclinationDegrees` from the planet's axis. The game takes every orbit's tilt and node from its sky's
  own frame, whose y is the pole of the planet's orbit, with the node in radians, so the pole is turned into
  those: the telescope's Inclination reads the tilt from that frame (34.15° on Mars, whose axis leans 25.8°
  from it), as it does for the game's own moons. Each parked comet starts the golden angle, 137.5°, further
  round its orbit than the last by id, so two never share a place in the sky.
- **The frame**, from the game's code, and checked against the Sun in the game (`-CometSky`). A body's
  direction is the scene rotation of the planet's position less the body's: a quarter turn back about x, then
  the latitude about x, then the day's angle plus the longitude about the planet's axis
  (`OrbitalSimulation.GetSceneRotation`); the observer is at the planet's centre. The direction is in world
  space: y is up (the sun's light is placed along it, the temperature takes the sun's angle from it, and day
  is the sun above y = 0), z is north and x east (the grid's own North and East faces, `Grid3.Face`), and the
  game's own readout of the sun takes its horizontal angle as atan2(x, z). So the comet's bearing is
  atan2(x, z) clockwise from north and its height asin(y) (`CometSkyRules.BearingOf`), the same on every
  machine. One thing the stage 2 derivation of the tilt took as the latitude is a little less in the game: the
  pole the sky turns about stands asin(a_y sin latitude) high, a_y the planet's axis along the frame's y, so
  12.1° on Mars at latitude 13.5°. Every pole stands no higher than the latitude, so 60° still brings passes
  straight overhead on every shipped world; on Mars the farthest of a day's passes then comes within 72.1° of
  overhead rather than 73.5°.
- **The aim.** Every payload bay on a rocket standing at Entry aims the first comet parked there, by id, at
  its PositionX and PositionZ, the payload drop's own target. Its disc is centred on the ground launch pad
  nearest that target (the target itself on a map with none); where the comet would come down if it were
  brought down now is the sky rule's point from the direction the game's sky shows it in, moved toward the
  target by the reach of the fuel it parked with (`CometAim`). The game neither saves nor sends a bay's target,
  so the host reads every one each frame and sends them with the comets (`MULTIPLAYER.md`); a chip sets it
  again after a load, as it would for a payload drop.
- **The bay's readouts**, on the host where logic runs, through values no rocket device uses: TargetX and
  TargetZ, where it lands if brought down now; DistanceKm, from there to the nearest structure on the ground
  (rockets in space aside); TimeToDestination, the seconds until the comet is next at its highest
  (`CometSkyRules.NextHighestSeconds`), the moment in a pass its point comes nearest the pad; Maximum, the
  metres its spare fuel can move the landing. Each is NaN on a bay that is not at Entry, or with no comet
  parked. PositionX and PositionZ stay the game's own.
- **The Map motherboard** shows two icons for each aiming bay, with the map's trackables switch on, as it shows
  beacons: the target in orange, and where it lands now in the comet's icy blue, which slides along the line as
  the comet passes. A click on the map names them in the map's own text, with where each stands, how far the
  landing is from the target and the seconds to the comet's next highest. Each machine draws them from its own
  comets and targets; nothing is saved.
- **The comet in the sky, to the eye**: a small glow with a tail turned away from the sun, drawn by the mod
  180 m out along the body's direction, inside the game's sun and star spheres, and hidden below the horizon.
  The Celestial Scanner marks and names it as it does every body, from the same body.
- **The Rocket Celestial Tracker** reaches only the first twelve bodies of the sky's list; the comet comes
  after the world's own, so on Mars (twelve bodies) it cannot track it, and on a world with fewer it can. When
  the list changes anywhere but at its end, every tracker pointing at or past the change goes back to the first
  body, so none points past the end of a shorter list. A list made shorter waits for the game tick's own
  main-thread moment (stage 6, below), so no tracker can count the old list and index the new.

### Warning and gas

When a deorbit starts, every player gets the time to impact and their distance from the landing point. A share
of the gas left after the trip arrives; the rest burns up on entry (configurable, below).

### What goes wrong

| Plan | Result |
|---|---|
| Not enough fuel for the route | stops partway and is lost |
| Burn without a target or timing | lands at a random point in the disc |
| Target out of reach of the fuel | lands as close as the fuel gets it |
| Misjudged timing, or a chip caught by a storm | lands where the sky put it, maybe near a base |
| Supermassive comet, any landing | its wave and debris reach most of the disc |

The `COMET.md` rule that a comet never lands near a structure stays for the console command only. A rocket
comet lands wherever it is brought down.

### As built (stage 6, 0.21.0)

- **Deorbit is a rocket action at Entry**, rocket mode 13, the next byte the game's `RocketMode` leaves free, with a
  button beside the other comet actions on the rocket map screen. It is offered at Entry while a comet is parked
  there and in the sky, and while no other comet is coming down or still has its show playing: one comet comes
  down at a time, because the impact event is one at a time. It fires once and puts the rocket back to idle, as
  Launch does.
- **The burn** is for the comet the rocket's payload bay aims (stage 5's aim: the first parked comet in the sky,
  by id; the rocket's first bay, by id, that aims it). The comet lands exactly where the bay's readout said in the
  frame the burn used, the aim of the last frame (`CometAim`): the sky point from the direction the game's sky
  shows the comet in (`Celestial.WorldVector`), moved toward the bay's PositionX and PositionZ by the reach of the
  fuel it parked with, capped by the engines' thrust over `EntryWindowSeconds`, inside the disc
  (`CelestialRules.Descend`). The burn spends the fuel that steer takes: the Δv of the metres steered at
  `CorrectionMetresPerDeltaV`, by the rocket equation from the comet's mass with all its fuel aboard, never more
  than the engines burn in the fall (`CelestialRules.SteerFuelMoles`, the inverse of the reach: steering the
  whole reach spends exactly what the reach was worked out from). Leaving orbit costs nothing more. What fuel is
  left goes down with the comet and is not delivered: it is not the comet's gas.
- **Untracked.** A bay whose PositionX or PositionZ is not a number (a chip writes NaN) aims untracked: the comet
  comes down at the sky point alone, spending nothing on steering, in the disc around the launch pad nearest the
  world's origin. A bay nobody has set reads 0, 0, the payload drop's own default target, so it aims at the
  world's origin, as a payload drop would. On the Map motherboard an untracked bay has only the "lands here" icon.
- **It leaves the map at once and the sky at the game tick's own main-thread moment.** The Rocket Celestial
  Tracker counts the sky's list of every body and then indexes it, in two reads, from the logic tick
  (`RocketCelestialTracker.OnLogicTick`, `OrbitalSimulation.GetCelestialCount` then `GetCelestial`), and a chip
  reading its CelestialHash does the same; a list made shorter between the two reads is indexed past its end.
  The logic tick runs only in the part of the game tick that leaves the main thread
  (`GameManager.GameTick`: `UniTask.SwitchToThreadPool`, then `LogicStack.LogicStackTick`, then
  `UniTask.SwitchToMainThread`), and the game tick is one loop, started once per world
  (`GameManager.StartGameTick`), that awaits each switch, so none of its off-thread work overlaps the main-thread
  moment just before it leaves, where it sets the sea's level (`GlobalAtmosphereLiquid.UpdateLiquid`). A list
  that would be shorter waits for a postfix there (`CometSkyBodies`), with the trackers past the change set back
  to the first body first. Everything else that reads the list either runs on the main thread (tooltips, the
  tracker's buttons and its setter) or only goes through the list it holds (the telescope's occlusion thread),
  which is never changed in place. The part is refused unless the game tick's own code still has those four
  calls in that order (`CometSkyBodies.SafePointOrder`, at load from the runtime's view of the method and in
  `tools/PatchCheck` from Mono.Cecil's). A list made no shorter, a comet joining the sky, is published at once.
- **It falls for `EntryWindowSeconds`** (50 s), and its impact is the console comet's event (`COMET.md`, the same
  show, wave, breaks, pushes and sound), timed so the comet first shows 15 s before the end of the fall and lands
  at the end. Nothing decides where it lands but the burn: no safe distance, no turn. It comes in along the
  comet's bearing from the pad, outward, at the console comet's 24 degrees. Its size is the moles that arrive for
  an ice comet, and for a mineral comet the moles of air it takes, worked out from the air at the burn (on a
  world with no air, the carbon dioxide its mass would be). A mineral comet shows no reaction; its plume takes the
  colours of the air it throws up.
- **The warning.** Each machine warns its own player once, on the console as the game prints a chat message
  (`ConsoleWindow.Print`), the moment it learns of the fall: "Comet 3 is coming down in 50 s, 1,234 m from you, to
  the north-east. It brings 70,000 mol." A machine with no player, a dedicated server, says where it lands
  instead. The host's log also names every player it knows of with their distance. The game has no warning of its
  own to every player: a storm's only warnings are a weather station's logic value and its sound alert. A player
  who joins during a fall is warned when the comet list reaches them, and everyone again after a load.
- **The gas**, on the host, at the end of the fall, in one call on the main thread under the tank lock. An ice
  comet adds `GasArrivingShare` of each of its gases, after anything Burn turned into fuel, at the planet's own
  temperature, so the stored heat in kelvin is kept (`Planet.AddGas`). A mineral comet takes `MineralRemovalPerKg`
  times its mass before `CometMassScale` (its ore units times `MineralKgPerUnit`) of the planet's air, from every
  gas the tank holds as a gas in its share, over the air's mean molar mass by the game's figures
  (`CelestialRules.MineralRemoval`), with the stored heat kept in kelvin (`Planet.RemoveGas`); the air is read as
  it is at the impact. A sea, liquid in the tank, is not air and stays. The show delivers nothing for a rocket
  comet. The falling comet is gone in the same call, so the gas is in exactly when the comet is gone.
- **A save during the fall** keeps it, in the comets entry (version 3), as the seconds it had fallen. At the load,
  once the game has put its clock back, it falls on from there and lands at the same clock time, and its impact
  event is made again: the game saves no event. A save between the Deorbit order and the burn, one server tick,
  keeps neither: the game's own save has the rocket idle, and the mod does not put Deorbit back, since the bay's
  target it would aim by is not kept by the game either.
- **Every player** gets the falls with the comet list (layout 4, `MULTIPLAYER.md`): the comet, its kind, where and
  when it lands and its size. The event reaches them as the console comet's does; only the host changes the air.
- `terraform comet make <site> <units> Ore engines=<n> fuel=<mol>` makes a mineral comet (debug), and
  `terraform comet list` shows each fall. `terraform comet stop` ends a rocket comet's show but not its fall: it
  still lands, and its gas still arrives.

## Near the impact

The pressure wave only breaks what is over its limit. Closer in, an impact does more. Ground zero and the
falling rocks end where the wave falls to a game threshold, so they grow with the cube root of the comet's moles
as the wave does and stop growing at 1e9 mol where the wave stops (`CelestialRules.GroundZeroMetres`). The
fireball's heat reaches as far as the fireball is drawn, the radius its yield gives it (`COMET.md`, "Size from
yield"), so what is seen burning and what burns agree (`CometZones.ReachFor`, `CometZones.ZoneAt`). r0 is 186 m
at 1e6 mol (settings below).

| Zone | Radius | What sets it | What happens |
|---|---|---|---|
| Ground zero | r0 | the wave past the strongest wall, 10,000 kPa | every structure destroyed whatever its limit, indoors or out; anyone there dies |
| Fireball | the drawn fireball, 54.9 m x (2W)^0.4 | the fireball's largest radius, Glasstone and Dolan | a structure facing the outside, and a player outdoors, burned by the game's lava rate for the fireball's growth; then the wave as below |
| Falling rocks | 7.07 x r0 | the wave past ordinary windows, 200 kPa | the wave as below; a place with a structure or a player on it may get a rock, the game's own small explosion |
| Pressure wave | as `COMET.md` | | breaks what is over its limit, makes windows near it creak |

A zone a nearer one covers is empty: below about 2,300 mol ground zero is wider than the fireball, so a tiny
comet has no fireball zone.

| Comet | Ground zero | Fireball | Burn in it | Falling rocks | Chance of a rock at the outer edge |
|---|---|---|---|---|---|
| 1e5 mol | 86 m | 249 m | 41 | 610 m | 6.8 % |
| 1e6 mol | 186 m | 625 m | 112 | 1.3 km | 10.7 % |
| 1e7 mol | 401 m | 1.6 km | 308 | 2.8 km | 17 % |
| 1e8 mol | 863 m | 3.9 km | 847 | 6.1 km | 27 % |
| 1e9 mol | 1.9 km | 9.9 km | 2,333 | 13.2 km | 43 % |

From `tools/PatchCheck`, which prints it. For scale, by the game's own health (`Thing.ThingHealth`): a Composite
Window 220, an Iron Wall 216, a Reinforced Composite Window 450, a solar panel 445, an Airlock 1,571.

A rocket comet is capped at its site's contents times K. The biggest ice site on any shipped map holds 3.05M
mol (Europa's; 2.79M on Mars's and the Moon's), so at Standard size, K = 5, the largest rocket comet is 15.2M mol:
ground zero 461 m, the fireball 1,858 m, a burn of 370 in it, the falling rocks out to 3.3 km. The 1e8 row and
above happen only through the console command.

### As built (stage 7, 0.22.0)

- **One pass, at the impact** (`CometZonePass`), on every machine: every structure in the world once
  (`GridController.AllStructuresPool`), its distance from the impact to its centre picking its row, then on the host
  every player online once. Each effect is a moment of the show, when the wave gets there (`d / 340` s), and a rock
  when it comes down. The pass logs `[TR comet] event N impact at ...; zones in one pass over S structures and P
  players in X ms: ...` with what it found, and keeps a report for `tools/LiveCheck` (`CometZoneReport`). Clients
  make the same list for the sounds and change nothing; the game sends the damage, the removals, the deaths and
  its own explosions.
- **Ground zero** removes a structure by the game's own removal of one its damage destroys
  (`Structure.OnDamageDestroyed`), whatever its pressure limit, indoors or out, leaving its wreckage where it has
  any. Only what the game makes indestructible stays. A player there is given their whole health as Burn, the
  game's own death by damage; one just respawned is spared, as the wave's suit damage spares them.
- **The fireball** burns a structure that faces the outside (the wave's own test, `COMET.md` section 4) with the
  game's lava rate, 25 a second (`OcclusionManager.LavaDamage`, the one heat the game deals to anything by where
  it is), for the fireball's growth and thermal pulse, `CometSizes.FireballSeconds`, ten times the pulse's peak:
  `25 x 0.417 x W^0.44` Burn, the same all through the fireball. A player outdoors in it is burned as standing in
  lava burns one (`Human.ApplyLavaDamage`): the suit and the helmet each by that much times how much it lets lava
  burn it (a HARM suit a quarter), or the body with no suit. The wave then acts there as everywhere past ground
  zero, so a window in the fireball is burned and then broken; what the burn alone takes past its health the
  game breaks or removes.
- **The wave** is `COMET.md` section 4's, unchanged, applied in the fireball, the falling rocks and beyond.
- **The falling rocks.** The ground is judged in 2 m squares, one large grid cell; a square with a structure
  facing the outside or a player outdoors in it is a place a rock can land on something, the first thing found
  standing for it. Each place is given a rock by its chance, drawn from the event's seed nearest first, until
  `MaxDamagingRocks` have come down (`CometZones.Rocks`). The chance is 1 at the falling rocks' inner edge, past
  the fireball, and thins as an ejecta blanket does, as the inverse cube of the distance (McGetchin, Settle and
  Head 1973). A rock is the game's own smallest explosion that damages anything, a suit bursting
  (`Explosion.Explode(200, position, 2.3)`), made of the game's own pieces as `Explosion.Explode` makes it (its
  flash, its event to every player, `Explosion.DamageThing`) with two differences (0.22.2): the structure or
  player it lands on takes the whole 200 Brute wherever its colliders are, and each other thing within 1 m takes
  the force falling to nothing at 2.3 m, once, not once for each of its colliders. The game's own sphere of 1 m
  from a structure's middle finds no collider on a hollow one: a frame's beams stand 0.92 m out along its edges, 1.22
  m from its middle at their nearest, so on the first `-CometZones` run on bt four rocks
  that landed on frames did nothing, most likely frames left with their first build state's beams. It lands on the place's structure or player, when a 45 degree throw that
  lands there comes down, `sqrt(2 d / g)` under the world's gravity, never before the wave: 18 to 27 s after the
  impact across a 1e6 comet's falling rocks on Mars, as the show draws its rain from 4 to 35 s. Rocks over open
  ground are only drawn.
- **Mineral comets** are the same: their size is the moles of air they take.
- `CometZones.Check` runs at load, chained into the comet rules' verdict, and in `tools/PatchCheck`, with ten
  wrong rules it must catch. `tools/LiveCheck/run.ps1 -CometZones` is the in-game proof, for bt.

## Ore comets: what they leave

To come: a mineral comet leaves a large mound of concentrated ore at the impact. Nothing about how is decided.
The impact records its point, its kind and its makeup, so this stage can be added without changing the rest.
Adding terrain falls under the cost rule below; a one-off change at the impact is affordable if it is kept
small.

## Mineral comets

Same loop. At impact a mineral comet blasts the planet's own air away, in proportion to its mass: 0.7 kg of air
per kg of comet by default (`MineralRemovalPerKg`), taken from each gas in its share of the air
(`CelestialRules.MineralRemovedMoles`). It is the tool for thinning a thick world such as Venus. The game gives
an ore unit no mass that counts (a rocket's cargo weighs 1 kg per filled slot, not by what it holds), so a unit
Cut moves into a mineral comet weighs `MineralKgPerUnit`, 2.592 kg, what a unit of water ice weighs (stage 4
decisions). Its removal of air at the impact goes by that mass, before `CometMassScale`, which is the comet's
weight in flight only.

## Cost rule

Every mechanic states what it costs per comet and where its cap is. Anything that runs every tick, or whose
cost grows with area, is out unless it is cheap.

- **One pass on the host at impact.** Every structure gets its distance once, which picks its effect from one
  table (destroyed, heat damage, wave check, untouched), applied when the wave reaches it. One pass over the
  players. Built in stage 7 (*Near the impact*): the pass logs its count and its time.
- **Nothing per tick afterwards** except the darker sky, which is built.
- **Clients** get the one event already sent; the game syncs structure damage and its own explosions.
- **Damaging rocks:** at most about 40 per comet, only where the pass found a structure or player within a few
  metres, each a game explosion of 3 m or less. Rocks over empty ground are visual only.
- **Out:** hot comet gas in the air near the impact (millions of air cells), fire spreading from it (runs every
  tick), digging terrain (cost grows with the cube of the radius).
- **The sky body** costs what the game's moons cost.

## Readouts

Logic readouts for the comet: fuel and Δv left, Δv still needed for the route, and the correction the reserve
can give. The Ground Telescope reads the rest. The game defines `TargetX`, `TargetZ` and `DistanceKm` but no
rocket device uses them, so they are free for this.

Built in stage 5, on the payload bay of a rocket at Entry, the device that aims the comet: TargetX and TargetZ
(where it lands if brought down now), DistanceKm (from there to the nearest structure), TimeToDestination
(seconds to the comet's next highest) and Maximum (the reach of its spare fuel, metres); see *As built (stage
5)* above. Fuel and Δv left, and Δv still needed, are not read out yet: the location panel's comet line shows
them, and a comet in flight has no device of its own to carry them.

## Balance

A comet is K x its site's gas. The table was worked at 60 % arriving; at the 70 % above, every count is
about 15 % lower. Comets needed to bring Mars to about 50 kPa:

| Planet size | K = 1 | K = 5 | K = 20 |
|---|---|---|---|
| Standard (1/20) | ~110 | ~23 | ~6 |
| Long (1/4) | ~560 | ~110 | ~28 |

Calculated from the game's site rules and the losses above, not measured. Planet size and K trade against
each other, but the wave follows each comet's absolute moles, so on a bigger planet every comet hits harder.
K = 5 at Standard size is the default, derived from LU's third-of-a-planet rule (the config table below),
which makes a median comet about 2.3M mol there. K follows the planet's size (stage 3 decisions, below): 1 on
Short, 25 on Long, so the rule holds on every size.

## What building it needs that the game does not do

- **The sky body.** The game has no way to add one after load, but its public code is enough. The mod must
  save it, send it to every player and re-add it after every load, on the host, each client and a dedicated
  server (telescope logic runs there). It must be added so the telescope's background thread cannot see the
  list change under it. Built in stage 5: nothing of the body itself is saved or sent, because the comet list
  already is and the body follows from it; the lists are replaced whole, never changed in place.
- **The rocket's celestial tracker** only handles the first 12 sky bodies. On Mars the comet would be the 13th,
  so that tracker cannot aim at it. The Ground Telescope has no such limit.
- **New rocket actions** mean patching the rocket's action lookup, its progress bars and the actions panel,
  with the save keeping the game's own mode and a sidecar for ours, so a save still loads without the mod.

## Configuration: Celestial terraforming

LU, 2026-10-21: every tuning number gets a calculated, fact-based default and is configurable, all in one config
section, `Celestial terraforming`.

- **Each default is derived, not picked.** Beside each one, this document gives the game facts and the
  arithmetic it comes from. Where no game fact applies, it says what the number is set to achieve and how that
  was worked out.
- **The host's values rule.** The host decides every comet; values that clients compute with (the orbit, the
  sky rule, the disc) are sent to them with TR's planet sync, as other settings already are.
- **Ranges live in one place.** Each setting is declared once, with its key, default and range
  (`Comets.CelestialSettings`, `src/Comets/CelestialTuning.cs`); the config editor, a world's settings file, the
  planet sync and `terraform set` all check against it.
- **Each world keeps its own.** They are world settings like the rest (`SIDECAR.md`): the config decides what a
  new world starts with, and `terraform set <Key> <value>` changes the world being played. All of them are sent
  with the planet sync (layout 6, `MULTIPLAYER.md`).
- **Held to their derivation.** `CelestialRules.Check`, at load and in `tools/PatchCheck`, fails if a default
  drifts from the arithmetic below wherever the arithmetic can be written down.

| Setting | What it sets | Default | Derived from |
|---|---|---|---|
| CutSharePerCycle | share of K times the site's gas added to the comet per Cut cycle | 0.001 | A drill head holds 100 and loses 1 with a chance of 0.1 / Health each cycle, so a Basic head (Health 1) lasts 1,000 cycles on average and an Ice head (Health 2) 2,000. 1 / 1,000 makes a whole comet one Basic head's life and half an Ice head's: with the head made for ice, no trip home for a new head on average. It is also the 1,000 cycles that mining out a middling Frozen Vapors site takes (Density 3, Richness 2, Size 2.5, by the game's depletion rule), so a comet costs the drilling of a typical site: 1,000 x (10 - 0.4 x 3) s = 2.4 h with one head of speed 1 |
| CometSizeMultiplier | K on a Standard-size planet: a comet's gas, once the whole site is cut, as a multiple of the site's. The K in force follows the planet's size (`CelestialRules.SizeMultiplier`) | 5 | LU's rule that one site never delivers a third of a planet, read as a third of the air the planet needs before you can breathe outside without a suit. At Standard size (250,000 outdoor cells) that need is 125 mol per cell on Mars and 121 on the Moon (`BALANCE.md`), so a third is 10.08M mol for the Moon. The biggest ice site on Mars's or the Moon's map holds 2,785,190 mol (Density 8, Richness 3, Size 7: 116,050 units of 24 mol by the game's own sum, which it shows rounded to 100,000). At 70 % arriving, K x 0.7 x 2.785M under 10.08M gives K under 5.17: the largest whole K is 5. What a planet needs grows with its size and a site does not, so the K in force is this times the planet's size over 1/20, never under 1: Short (1/100) 1, Long (1/4) 25. The third then holds on every shipped size: Short 1.95M mol against 2.02M, Long 48.7M against 50.4M; `CelestialRules.Check` holds it at all three |
| OrbitSeconds | real seconds for one orbit of a comet waiting at Entry, on every world; one pass per orbit | 372 | Phobos, the fastest body in Mars's sky, goes round it in 360 x 0.3189 / (687 x 4.494e-4) = 371.9 real seconds (its period, Mars's year, and Mars's sky clock `OrbitalSimulation.TimeScale`), and the Ground Telescope tracks it. A comet no faster across the sky stays trackable. Half the orbit, 186 s, is above the horizon (the game puts the observer at the planet's centre): long enough to read a telescope and burn. 3.2 passes in a 20 minute day. Each world's `Orbit.Period` is 372 x its own period x its TimeScale / 360 (`CelestialRules.OrbitPeriodDays`) |
| OrbitInclinationDegrees | the orbit's tilt to the planet's equator; with the orbit time it sets how far the line moves between passes (the design's `LineShiftPerOrbit`, which is not a number of its own) | 60 | Between passes the ground turns 360 x 372 / 1,200 = 112 degrees (`CelestialRules.TurnPerOrbitDegrees`). A pad's zenith sits 90 - latitude from the planet's axis and the orbit's pole i from it, so over a day the angle between them sweeps from their difference to their sum, and passes go straight overhead when that sweep crosses 90 degrees, which is when i is at least the latitude (`CelestialRules.ClosestPass`). The shipped world farthest from its equator is Mimas, at -58.4; the next whole ten above it is 60, so every shipped world gets passes straight overhead. On Mars (13.5) a day's passes then come within 0 to 73.5 degrees of overhead at their highest, so the line's nearest point sweeps from the pad out to 0.41 R, 2.1 km |
| DiscRadiusMetres | R in the sky rule, on a world drawn with curvature 0.3 (Mars, Venus); others take R x 0.3 / curvature | 5100 | **Far enough:** a landing aimed while the comet is in sight (above the horizon, so within R / 2 by the sky rule, where a telescope can follow it) must reach the safe distance of the biggest rocket comet, K x 3,048,381 mol (Europa's map's biggest site) = 15.2M mol, scale 2.09, safe distance 2,546 m (`COMET.md`). R / 2 at least 2,546 m, rounded up to the next 100 m, gives 5,100. **Near enough to be seen:** the terrain shader draws ground lower by 0.001 x ((d - 144) x 0.3)^2, so over unbroken ground a column H high at d is seen by a standing player (eye 1.8 m) while d is at most 144 + sqrt(1.8 / k) + sqrt(H / k), k = 9e-5. The show's column and cap reach 1,220 m per unit of scale (`CometShow`: plume 1,100 x scale, cap at plume plus radius 120 x scale). The smallest show (scale 0.4, 488 m) is seen out to 2,614 m, past R / 2 = 2,550 m, so every landing made with the comet in sight is seen; at the edge, 5,100 m, the column clears the drawn horizon from 2.6M mol up. The game's far clip does not bind: the show raises it to four times the impact's distance. **Past the voxel world's edge:** Mars2's terrain is 4,096 m across, so for a pad near its middle the disc beyond about 2 km is mostly past the edge, where nothing is built or stands and the impact takes the nearest edge's height (0.16.1); the half a tracked landing reaches lies mostly over the terrain. **More curved worlds:** the sight distance goes as one over the curvature, so R x 0.3 / curvature: 3,400 m on Vulcan (0.45), 3,060 m on the Moon and Mimas (0.5), 6,120 m on Europa (0.25) |
| CorrectionMetresPerDeltaV | reach in metres per m/s of Δv the reserve can give | 25 | A steady burn through the whole fall of T seconds moves the comet a T^2 / 2 sideways for a Δv of a T: T / 2 metres per m/s, 25 at T = 50. Exact for the largest steer (the cap below); a short burn early in the fall would drift up to T per m/s, so this errs short |
| EntryWindowSeconds | how long the deorbit burn can run, so thrust caps the usable reserve | 50 | A landing rocket starts down at its leg's Δv, 400 m/s (2 x the map's OrbitDistance of 200), from the game's default re-entry height of 25,000 m (ReEntryProfile Low). Falling freely under Mars's gravity of 3.7 m/s^2 it reaches the ground in (-400 + sqrt(400^2 + 2 x 3.7 x 25,000)) / 3.7 = 50.6 s (`CelestialRules.FallSeconds`). Vulcan and Venus (gravity clamped to 5.5) 47.2 s, the Moon 56.1 s, Mimas 58.3 s: one figure, Mars's, rounded |
| GroundZeroMetresAt1e6 | r0 at 1e6 mol; the zones grow from it with the cube root of the moles | 186 | The wave rule (`COMET.md` section 3), dP = 400 x (n / 1e7)^(2/3) x (2000 / d)^2 kPa. The strongest limit any structure has is the Reinforced Wall's 10,000 kPa; at 1e6 mol the wave passes it inside d = 2000 x sqrt(400 x 0.1^(2/3) / 10,000) = 185.7 m. Ground zero, where everything goes whatever its limit, is where the wave already breaks everything. A fixed peak's distance goes as n^(1/3), so the zones' cube root is the wave's own |
| FireballHeatFactor | how far the fireball's heat reaches, as a multiple of the fireball drawn from the comet's yield; above 1 the fireball is drawn as far as its heat | 1 | What is seen burning and what burns agree: the heat reaches exactly as far as the fireball is drawn, its largest radius by Glasstone and Dolan's 180 W^0.4 ft for a surface burst (`CometYield.FireballLawMetres`), 625 m at 1e6 mol and 1,570 m at 1e7. It replaces stage 2's FireballRadiusFactor (2.24 x r0, where the wave falls to the full-force push), which the drawn fireball is 1.3 (1e5) to 2.4 (1e9) times (decisions, stage 7) |
| DebrisRadiusFactor | the reach of the falling rocks as a multiple of r0 | 7.07 | Where the wave falls to 200 kPa, the weakest limit that breaks (ordinary windows and glass doors, `COMET.md`) and where the hard push starts (`CometRules.ThrownHardKPa`): sqrt(10,000 / 200) = 7.071 times r0, 1,315 m at 1e6 mol. No rock does harm outside the ground the wave already damages. It stays wider than the drawn fireball at every size up to the wave's cap (1.3 times at 1e9), and the show throws its rocks to exactly this edge (`COMET.md`, "Against the damage") |
| MaxDamagingRocks | the cap on rocks that hit | 40 | No game fact sets it; set to keep the cost rule. The show draws its debris rain at 7 pieces a second, falling off over 12 s, from 4 to 35 s after the impact: 7 x 12 x (1 - e^(-31 / 12)) = 78 pieces. A rock that hits must be one a player could have seen fall, so at most 78; half of them, 40, is at most 1.3 game explosions of 3 m or less a second over the 31 s, each the cost of one grenade |
| GasArrivingShare | share of the gas left after the trip that reaches the planet | 0.7 | No game fact: a rocket that crashes in the game puts all its fuel into the world (`Rocket.Explode`), which LU ruled out ("only part of the gas reaches the planet"). Kept at the approved design's 70 % (Decisions, below). K is derived through it |
| CometMassScale | a comet's weight in flight as a share of what the ice or ore cut from its site weighs; the extra gas K gives weighs nothing | 0.39 | The largest hundredth at which the heaviest comet a Mars map can make (K x a D8 R3 S7 site of silanol ice, 2.4M mol cut, 372.4 t, from Lost Moon Fragments, 2,058.5 delta-v, two engines) takes no more time hauling its fuel in a big fuel rocket than cutting it took: 6.84 trips of 100,302 mol in 994 s against 1,000 cycles of 6.8 s. The fuel rocket and the trips are in the stage 4 decisions, below |
| MineralKgPerUnit | what a unit of ore cut into a mineral comet weighs, before CometMassScale | 2.592 | A unit of water ice by the game's figures: 24 mol, every shipped ice site's unit, at the game's 108 g/mol for water. No game fact gives ore a mass that counts |
| FuelLitresPerSecond | litres of the rocket's fuel stores whose 2:1 premix the Fuel action moves into the comet each second | 200 | A Turbo Volume Pump moves 100 L a 0.5 s tick at its Maximum (LIVE, atmospherics.md), the pump a fuel rocket hands its fuel across with |
| MineralRemovalPerKg | kilograms of the planet's air a mineral comet removes per kilogram of comet | 0.7 | No game fact: the game gives an ore unit no mass that counts. Set for symmetry: a mineral comet takes away the mass of air an ice comet of the same mass brings, 0.7 kg per kg at 70 % arriving, so thinning a thick world costs as much comet as thickening a thin one by the same mass of air. On Venus (the game's molar masses: carbon dioxide 44, hydrochloric acid 36, nitrogen 64 g/mol, mean 43.1) that is 16.3 mol per kg |

Fixed, not settings, because they are game facts: the Pumped Gas engine's 16.39 kN (measured) at 36 mol/s, its
500 kg, the 2:1 methane to oxygen burn (`Combustion.ResultMethaneOxygen`), the gases' molar masses
(`Mole.MolarMass`), each world's curvature, sky clock and day. `tools/PatchCheck` reads the game's own molar
masses and burn ratio from the installed game and checks the worked examples above against them.

## Decisions made while building

Stage 2 (0.17.0). Each: the decision, the goal it serves, the alternative and why it lost. Goals are numbered as
in *What it is judged against*.

| Decision | Serves | Alternative, and why it lost |
|---|---|---|
| The thirteen settings are world settings, recorded in each world's file and changed with `terraform set` | `SIDECAR.md`'s rule that every setting that affects a world lives in that world's own file | Config only: tuning a new world would change the next comet of every older world |
| A comet weighs by the game's molar masses (water 108 g/mol), so it is about six times heavier than this design first assumed | 4, the game's own pieces: a rocket's fuel weighs by the same figures, and a comet flies by the rocket's rule | Physical molar masses (water 18): a comet and the rocket feeding it would weigh their gas by two different rules |
| A whole comet is 1,000 Cut cycles (0.1 % a cycle), not 10 cycles (10 %) | LU's "more work at the site makes a bigger comet"; 4, the game's mining and head wear | 10 %: a whole comet in about 90 s, a head barely worn, so the work never decides anything |
| K = 5, from the third-of-a-planet rule at Standard size and the site's true contents | LU's rule that one site never delivers a third of a planet | K = 6 from the 100,000 units the game shows: the site really holds 116,050, and K = 6 brings 16 % over the third. Not decided: on Short the rule holds only at K = 1; that is LU's call with the default planet size |
| The disc shrinks with the world's drawn curvature | 3, never out of sight, on every world | One radius everywhere: on Vulcan and the Moon most of the disc's far half would be below the drawn horizon. Cost: there, the biggest comets' safe distance lies beyond the tracked half and needs reserve to reach |
| R = 5,100 m, twice the biggest rocket comet's safe distance | 1, a tracked landing can be made at a safe distance; 3, every tracked landing is seen; 2, a blind burn often lands near the pad | The 8 km of the earlier table: a blind burn near the base far less likely, and small comets out of sight at the edge |
| The orbit's tilt is the setting, and the line's shift between passes follows from it and the day | the approved mechanic: the line comes from the comet's real place in the game's sky | A line-shift number of its own: it would move the line by a rule the sky does not follow, so the telescope and the line would disagree |
| 60 degrees of tilt | 1 and 6: every shipped world's pad gets passes straight overhead, so the line can be brought to any part of the disc near the pad | An equatorial orbit: off the equator no pass ever comes overhead, and the line hardly moves from pass to pass |
| 372 s an orbit, Phobos's | 1: trackable by the Ground Telescope, as Phobos is; a pass in view for three minutes | 120 s, for a line that moves only 36 degrees a pass: the comet would cross the sky three times faster than anything the game shows |
| Steering is T / 2 metres per m/s, capped by the engines' Δv in the fall | 7, a big comet steers little; exact at the cap | T per m/s: twice the largest steer the kinematics allow |
| One fall time, Mars's 50 s, for every world | 5, simple | Per world from its gravity: 47 to 58 s, a spread smaller than the tuning matters |
| The zones end where the wave crosses game thresholds (10,000, 2,000 and 200 kPa); in stage 7 the fireball's moved to the drawn fireball (below) | 4, the game's own limits; the zones and the wave never disagree | The earlier 150 m, 2.5 and 8 picked by eye: ground zero inside where the wave already breaks everything, and rocks that could hit beyond it |
| The engines are counted at the measured 16.39 kN, not the 4 % more `RocketsBurnCompletely` gives a real engine | 4: the one thrust measured in the game | The modelled 16.95 kN: not measured |
| 70 % arriving, as the approved design text has it | LU's "only part of the gas reaches the planet" | 60 %, the earlier balance table's figure: no fact favours it, and every comet count there is about a sixth higher |
| A mineral comet takes 0.7 kg of air per kg | symmetry with an ice comet, so Venus costs as much comet per tonne of air as Mars | A momentum or energy estimate from the game's 590 m/s impact: 0.01 to 0.1 kg per kg, too little for a tool LU wants for thinning Venus |
| 40 damaging rocks, half the 78 the show draws | the cost rule | All 78: twice the explosions for rocks that land only where the pass already found something |
| All thirteen values ride the planet sync, not only those a client computes with | 5, one rule rather than a choice per setting; 108 bytes a planet update | Sending only the orbit and the disc: each later stage would have to revisit the list |
| No `terraform comet sky` command in this stage | 5, no shipped code without a use | A console readout of the sky rule: its proof in the game comes with the sky body, stage 5, where a telescope reading gives it real inputs |

### Stages 3 and 4 (from reference/comet-map-actions.md)

| Decision | Goal served | Alternative, and why it lost |
|---|---|---|
| The comet is the mod's own map object that copies the rocket travel step, not a real `Rocket` | 4, 5 | A real `Rocket` with no parts: an empty rocket network is never saved and is deregistered (the comet vanishes on load), thrust and mass come from real engine devices, and every list of rockets (map icons, Transfer, panels, launch mounts) would pick it up |
| No avionics unit on the comet. Its destination is always Entry. The rocket at the site starts its flight with a Launch action, and a rocket at Entry aims it with its own payload bay's PositionX/PositionZ and fires the burn with a Deorbit action | 4, 5 | An avionics unit fitted at Rig: the game has no avionics item, only the kit that builds the device, so the unit would be mod state with no logic surface of its own. The payload bay already has PositionX/Z, the drop's own aim |
| Engines are Rigged by carrying Pumped Gas engine kits inside a Payload Delivery Container and using the game's Deploy action at the comet's site | 4, 5 | A new Rig action and a new payload item: Deploy never exists at a site in the game, so reusing it there is unambiguous and needs one prefix |
| Cut is a subclass of the game's mining action; the yield goes into the comet instead of ice | 4 | A separate Cut cycle: it would have to copy the miner's timing and head wear, which the game ties to the mining action |
| The comet's state lives in an extra file inside the `.save` zip, not in the per-world sidecar | 5, and saves stay consistent | The sidecar is one file per world, so loading an older autosave would pair a newer comet with an older site. The game extracts and then deletes unknown entries without the mod, and the file rolls back with its save |
| New rocket modes are saved as a game mode, with the real mode in the mod's save file | saves stay loadable | Saving an undefined mode throws after the save file is opened, so the save fails; loading an unknown mode name without the mod fails the whole save |
| Clients compute the comet's map position from the departure event (start time on the shared clock, route, mass, thrust, fuel), sent once by the instant message and in the planet sync for joiners | 5, multiplayer | Sending its progress every frame: the travel rule is deterministic, so one event is enough |
| Fuel is taken on the host from the rocket's tanks and pipe networks, never the engine chamber, in a postfix on the rocket's atmosphere step | 4 | Taking it elsewhere races the engines drawing propellant |
| The size multiplier K follows the planet size: 5 at Standard (1/20), times the world's size over 1/20, never under 1 (Short 1, Long 25) | LU's rule that one site never delivers a third of a planet, on every size: what a planet needs scales with its size, so K must too | One K for every size: on Short a K = 5 comet brings more than the whole planet needs |
| Fuel per trip is judged in stage 4, when a rocket's real fuel capacity is measured. A 248 t water comet needs about 323,000 mol from Frozen Vapors with two engines; if that is more trips than the work of Cut, the comet's mass gets a configurable scale with a derived default, as the game already leaves mined cargo weightless | 4 and the player's experience: the Fuel stage must be work, not a wall | Deciding a scale now: there is no measured fuel capacity yet to derive it from |

### Stage 3, as built (0.18.0)

| Decision | Goal served | Alternative, and why it lost |
|---|---|---|
| A flight is kept as the state it set off with (gas, mass, engines, fuel, route, hops) and its start on the clock; where it is is the rule's at the time flown, worked out wherever it is needed | 5, and saves and players agree: a save, a load and every client compute the same place from the same numbers | Stepping its progress every frame and keeping that: a value to send every frame, and machines that drift apart |
| The clock is `OrbitalSimulation.System.TotalRealTimeSeconds` | 5: the game saves it and sends it to every player in every state update, and the impact show already runs on it | `NetworkTime.time`: not saved, so a flight could not carry on after a load |
| The mass falls continuously as the fuel burns, not at each 0.5 s draw | 5: one formula of the time flown | The game's 0.5 s draws: a flight would depend on where in the draw cycle it was sent; the continuous rule is within 0.2 % of the game's ticks, checked against them |
| Delta-v gained past a node carries into the next hop | 5: a pure function of time | The game zeroes Progress at a node, losing at most one 0.02 s fixed step of delta-v, under 0.01 % of a hop; copying that makes the place depend on frame timing |
| Each hop's delta-v is the game's `NodeTransit.GetDistance` when it set off, kept with the comet | 4: the game's own figure, difficulty included; a flight already under way is not changed by anything later | Working the hops out again at a load: a later change of difficulty would move a comet mid-flight |
| A save keeps the seconds a flight had flown, and the flight goes back on the clock when the game restores its own clock (`OrbitalSimulation.DeserializeSave`), flying on from those seconds: the moment it went back on the clock and the seconds flown then are kept apart, not folded into a start time | saves stay exact: the place after the load is the place at the save, to the last bit (`CometMap.CheckSave` in `tools/PatchCheck`) | Keeping its start on the clock: the map is rebuilt before the clock comes back, a save from before the game kept its clock restarts it, and a start worked back from a large clock moves the place by a rounding error |
| The save writes every fraction as text of 17 significant digits | the same | The serializer's own double format: whether it reads back to the last bit depends on the runtime |
| A load renumbers every node a comet refers to. Each node in the save's map is found again on the rebuilt map without its id (Entry and the low-orbit hub by role, a static node by its template, a site as the load rebuilt it from its own record, noted in a postfix on `SpaceMapNode.Create(SiteNodeReference)`), and once the game has given every node its id for the session (`Referencable.AssignNewIdToDuplicates`, near the end of the load) each comet's site and route move to those ids. Found on bt: the load counts no space map node when it sets the next free id (`Referencable.FindAndSetNextReferenceId`), the low-orbit hub takes a fresh id first (`SpaceMap.CreateLowOrbitHub`, id 0), so a site saved with the highest id in the save loses its id to the hub (`Referencable.RegisterAs` refuses a taken id) and gets a new one at the end of the load: Water Vapor 268 came back 269, the hub took 268 | saves stay consistent | Keeping the game's id, as `RocketSaveData` does: a rocket saved at such a site would come back linked to the hub by the same rule (from the code, not seen in play) |
| Entry is found by its role wherever it is needed; the route keeps its id only as a record | the low-orbit hub's id changes at every load, and nothing about a comet should rest on an id the game does not keep | Entry by id: stable today, but the only node the comet's arrival rests on |
| A comet that runs dry stays where it is for good, and one that arrives stays parked at Entry; nothing in stage 3 removes a comet | LU's "no rescue if it runs dry"; Entry is where stage 6 brings it down from | A way to clear comets: nothing needs one until Cut and Rig make them in play |
| Every player gets the whole list after any change, and with the planet sync; a joining player's game replaces its list with the host's each time, settles a flight at the same clock but logs nothing | 5: a few hundred bytes now and then, one rule | Messages per kind of change: more code for a list that is small |
| A comet holds its site while it waits and every node of its route until it arrives; the host's two ways of removing a site skip a held one, and the remove button is greyed out | the comet keeps a place on the map to stand at or fly between | Guarding `SpaceMapNode.DeRegister` itself: it also runs on players' games when the host removes a node, and in the game's clean-up after a load |
| No comet is made unless its save is installed | saves stay consistent: a comet that could not be saved would vanish at the next load | Making it anyway with a warning |
| The size multiplier is K on a Standard planet, and the K in force is that times the planet's size over 1/20, never under 1, worked out from the planet as it is now, so it follows `terraform size` too | LU's third-of-a-planet rule on every size | A K fixed when the world is made: a rescale would leave it wrong. 0 meaning "follow the size": two meanings in one number |
| `terraform comet make`, `send` and `list` are debug verbs; a site is named by its number in the verb's own list, its name or its template id, and only discovered sites count | testing stage 3 live before Cut, Rig and Fuel exist | Making comets at any node: comets come from sites |
| The map icon is the game's rocket icon tinted icy blue, with an asteroid picture from the game's own map icons, placed a little below any rocket at the same node | told apart from rockets at a glance | Art of its own: none in the repo yet |

The map icon is drawn by every machine from its own copy of the comets, as rockets' are; it is the one part of
stage 3 a headless run cannot show.

### Stage 4, as built (0.19.0)

The actions. Cut, Fuel, Burn and Launch are new rocket modes, the byte values 9 to 12 the game's `RocketMode`
leaves free; Rig is the game's own Deploy. Each is offered at a site by `SpaceMapNode.HasAction` and
`IsActionAvailable` (answered before the game's own switch, which throws on a mode it does not know), started
by `GetAction`, listed in the location panel by `Rocket.ActionProgressables` (the miners for Cut and Burn, and a
line for the comet), and has a button on the rocket map screen copied from the game's Mine button. One answer,
`CometActions.Refusal`, decides every one of those questions, on the host and on each client from its own copy
of the comets.

| Action | Where | What runs it | What it does |
|---|---|---|---|
| Cut | an ice or mineral site, not mined out, its comet not full | `CutAction`, a subclass of the game's `RocketMine`: the miners' own timing and head wear | each cycle `MineableDeposit.MineDeposit` (prefix) depletes the site as the game does (`OnDepositMined`, first), grows the site's comet by `CelestialRules.CutAdds`, makes the comet on the first cycle, and returns 0 units with the miner's own last prefab, so no ice is made and a stack the miner was part way through is kept |
| Rig | a site with a comet waiting | the game's `RocketDeploy` and the payload bay's own 20 s deploy | `DeliveryPayload.OnDeploy` (prefix) turns each Pumped Gas engine kit in the container into an engine, 500 kg each |
| Fuel | a site with a comet waiting | `FuelAction`; the transfer is a postfix on `Rocket.RocketAtmospherics`, on the game tick just before the engines draw | `CelestialRules.PremixTransfer` from each tank and pipe network, gas and liquid in their shares, into the comet's fuel |
| Burn | a site with an ice comet holding methane and oxygen | `BurnAction`, a `RocketMine` that skips the mined-out check | each cycle, through the same prefix, `CelestialRules.AfterBurn`: methane and oxygen two to one into fuel; the site is not touched |
| Launch | a site with a comet that has engines and fuel | `LaunchAction` | sends the comet to Entry (stage 3's send) once, logs the rule's arrival time or where its fuel runs out, and puts the rocket back to idle |

The save. A comet mode is never written into the game's own save: a postfix on `RocketSaveData(Rocket)` writes
any mode the game does not define as None, so the save cannot throw and the unmodded game loads it as an idle
rocket. The real mode goes into the comets entry (`tr-comets.xml`, version 2), by the rocket's ReferenceId,
which the game restores with the rocket, and goes back to the rocket once the loaded game is running, where
the game checks it as any mode it is given. The comet itself gains its kind, its ore and its Cut cycles in the
entry; a version 1 entry reads as ice comets with no cycles.

The network. The modes travel as the game's own do, a raw byte, which the game does not check. Each comet now
carries its kind, ore and Cut cycles (its own layout byte, 2). An action that changes a comet every tick (Cut,
Fuel, Burn) sends the list to every player at most every 2 s.

| Decision | Goal served | Alternative, and why it lost |
|---|---|---|
| **A comet weighs what was cut from its site, times `CometMassScale` = 0.39; the extra gas K gives weighs nothing.** Measured from the game: a big fuel rocket (LU's tanker hull, 1,120 kg dry, 8,120 L of gas capsule tanks and lines) filled the game's way (`RocketCommand.Refuel`: 30 MPa at 215 K, 16.78 mol/L) carries 136,272 mol; on Mars it climbs to orbit in 287 s on 10,325 mol, flies out by the rocket rule, keeps back the tanker's 100 kg (6,250 mol) to land on, and hands over 114,143 mol at a Frozen Vapors site (a 630 s trip), 102,664 at the Oxygenated Ice Cluster and 100,302 at Lost Moon Fragments (994 s), not counting refilling on the ground. With every mole of K's gas weighing, as stage 3 had it, the median comet (2.3M mol of water, 248 t) needs 2.8 trips from Frozen Vapors, but the biggest Mars comets (K x a D8 R3 S7 site's 2.4M mol) need 11.5 trips (oxygen ice), 51 (water ice) and 87 (silanol ice at Lost Moon, 1,862 t): a wall. The default is the largest hundredth at which the heaviest of them, silanol from Lost Moon with two engines, takes no more time hauling its fuel (6.84 trips, 6,797 s) than cutting it took (1,000 cycles at density 8 with one head of speed 1, 6,800 s); then the median comet needs 0.22 trips, oxygen ice 0.94, water ice 3.99. K's gas weighing nothing makes the Fuel stage the same work on every planet size, as K makes Cut the same work | the Fuel stage is work, not a wall; LU's "more work makes a bigger comet" stays with Cut | All of the comet weighing at the game's molar masses: 87 trips for the heaviest. A scale on all of it without K's exemption: 0.08 at Standard, but five times the hauling on Long. The game's own precedent, mined cargo weighing nothing (0): no comet would be heavy, and a big comet must steer little (goal 7). `CelestialRules.Check` holds the default to this arithmetic |
| A mineral comet's ore weighs `MineralKgPerUnit` = 2.592 kg a unit, before the scale: what a unit of water ice weighs by the game's figures (every shipped ice site gives 24 mol a unit, water 108 g/mol) | `MineralRemovalPerKg`'s symmetry: a mineral comet weighs, and so takes away, what an ice comet cut the same way brings | No game fact: the game gives an ore unit no mass that counts. A unit of the heaviest ice (silanol, 3.72 kg): an ice the maps hold little of |
| Fuel moves `FuelLitresPerSecond` = 200 litres of the rocket's stores a second: what a Turbo Volume Pump moves, 100 L a 0.5 s tick (LIVE `Maximum`, atmospherics.md), the pump a fuel rocket hands its fuel over with. A full tanker's 102,000 mol cross in about 90 to 110 s | 4, the game's own pieces | One engine's draw, 36 mol/s: a trip's fuel would take 47 minutes to cross |
| Fuel takes only what pairs two to one, from tanks and pipe networks, gas and liquid in their shares, and never an engine's chamber; it runs until the player stops it | the engines burn 2:1 (`Combustion.ResultMethaneOxygen`); the chamber is reset every tick (research section 9) | Keeping back a reserve for the trip home: the mod would have to guess the landing the game's autopilot flies; a player's chip can read the tanks and set the mode |
| Burn is a drill action with the game's timing and head wear, each cycle asking `CutSharePerCycle` times the head's ice yield of the comet as it stands; the site is not mined | the design: "the drill heads turn its ice into propellant"; head wear is its cost | A rate of its own, with no heads: burning would cost nothing but time. Burn as part of Fuel: no way to choose between hauling and burning. A mole of fuel weighs its full 16 g while the comet's own weighs it scaled, so a mole burnt adds 9.8 g to the comet in flight on Short and 14.8 g at Standard |
| The site's gas is `TotalOreAtLocation`, the game's own rounded figure, times a unit of the first ice its miners cut | 4: the figure the game shows the player | The unrounded sum the K derivation used (116,050 units against 100,000): a comet bigger than the site the player is shown, and the third-of-a-planet rule only holds more surely with the smaller one |
| Rig is the game's Deploy, and the payload the game's Pumped Gas engine kit in a Payload Delivery Container; a kit counts as many engines as it would build | 4, 5 | A new kit or a new action: nothing the game does not already have |
| The comet modes are written as None in the game's save, the real mode in the comets entry by the rocket's ReferenceId | saves stay loadable, with and without the mod | Writing Mine for Cut: the unmodded game would mine the site for real, and with the mod a mode the entry could not restore would mine too |
| Launch sends the comet whether or not its fuel covers the route, and says which | LU's "no rescue if it runs dry": a bad plan has a bad outcome | Refusing a comet short of fuel: the rule's arrival is a prediction the player can read first, in the comet's line and in `terraform comet list` |
| **The disc follows the planet's size above Standard**: it grows by twice what K adds to the biggest rocket comet's safe distance, so half of it is that safe distance, rounded up as at Standard, on every size: 5,450 m on Long (half 2,725 m against 2,720 m), 5,750 m on a full-size planet, by stage 4's safe distance (8,852 and 15,406 m by stage 7's, which keeps the viewer outside the drawn fireball); never below the setting, so 5,100 m on Short (`CelestialRules.DiscRadiusAt`). The cost: on Long the smallest show (scale 0.4, seen out to 2,614 m) can land just past where it clears the drawn horizon at the edge of the tracked half | goal 1, a tracked landing at a safe distance, on every size | Leaving the disc as set: on Long the biggest comets could not be brought down at a safe distance while in sight. Shrinking it on Short too: no goal needs it, and small comets are easier to see nearer |
| A site's comet is the first one waiting there; Cut grows it, makes one only when none waits, and refuses one of the other kind (a debug-made ice comet at a mineral site) | one comet per site in play; Cut never mixes ice and ore | A comet per rocket: two rockets cutting one site would make two half comets |
| The comet actions are offered only once every one of their patches is in, the save's guard last, and only with the comets' save installed | saves stay loadable; one patch the game no longer fits costs all four actions, never a broken save | Offering each action whose own patch went in: a mode could then reach a save with nothing to keep it out |
| While an action changes a comet every tick the host sends the whole list at most every 2 s, and at once for anything else | 5, a few hundred bytes now and then | Every change at once: Fuel changes the comet twice a second |
| The rocket in `-CometActions` is spawned from the game's prefabs and joined with the calls the game's load makes (`RocketData`, `AdoptFromLoad`, `RefreshRocket`), stood at sites with `SetCurrentNode`, its parts' power held on by the driver | the proof calls the actions' own code paths in a real rocket | Building it into the hull's cells: a headless run has no player to place parts, and the cells' exact geometry is prefab data |

### Stage 5, as built (0.20.0)

| Decision | Goal served | Alternative, and why it lost |
|---|---|---|
| The parked comet is the game's own `CelestialBody`, made from a template of its own and joined to the planet's list of the bodies circling it and to the sky's two private lists of every body | 4, the game's own sky: the telescope, the Celestial Scanner and the sky's own placing all work on it unchanged | A body of the mod's own drawn on the sky: no telescope could see it |
| Every list is replaced whole and published by one reference write, never changed in place. The Ground Telescope looks through the list of every body from the occlusion thread (`GroundTelescope.OnThreadUpdate`, `OrbitalSimulation.RaycastSky`), which the game runs beside the main thread with no lock, and the Rocket Celestial Tracker counts and indexes it from the logic tick on the game tick's thread; both read the list's field afresh each time, so either holds the old list or the new, and neither is touched again. The game writes the lists only while a world loads | the telescope's thread never sees the list change under it (the research's "collection modified" risk) | The game's own `Celestial.Add`, which appends in place: an append while the occlusion thread is going through the list throws there, in the thread that runs every structure. Waiting for a moment the thread does not run: there is none, it loops on its own |
| A body's direction is set before it is published | a telescope never sees it with no direction | Leaving it to the next frame's placing: Unity takes the angle to a zero vector as 0, so for that frame every telescope would lock onto it whatever its aim |
| Nothing of the body is saved or sent. Every machine makes it from the comet list it already has: the orbit from the comet's id, the world and the host's settings, its place from the sky's clock, which the game sends | 5, multiplayer: the same place on every machine with nothing more on the wire | Sending the body or its elements: everything they are made of is already sent |
| The orbit's pole is `OrbitInclinationDegrees` from the planet's axis, tilted toward the side away from the sky frame's own pole, and turned into the frame's inclination and node (in radians, as the game uses them), which is what the telescope reads | the approved design, an orbit tilted to the planet's equator, and stage 2's derivation of the 60° | The tilt as the frame's own inclination: the tilt to the equator would then depend on how far each world's axis leans (25.8° on Mars), and the derivation that brings passes overhead everywhere would not hold |
| The orbit's radius is Phobos's 9,376 km on every world | 5: the game has no radius or mass for the planet a low orbit could be worked out from, and the radius changes only the telescope's distance readings | Kepler's law from the period: the sky's clock gives a 372 s orbit a period of 23.8 days on Venus, so a radius of 166,000 km |
| Comets parked together start the golden angle (137.5°) apart round their orbit, by id | two never stand in the same place in the sky, which the telescope could not tell apart | All at the same start: they would orbit as one |
| The comet bodies come after every body of the world, in comet id order | the sky's list is the same on every machine, so a tracker's index (saved and sent) means the same body | The order comets arrived in: a player who joins later would have them in another order |
| When the list changes anywhere but at its end, every Rocket Celestial Tracker pointing at or past the change is set back to the first body | a tracker never asks for an index past the end of a shorter list, which throws each logic tick | Leaving the index: the game's own setter guards only a new index, not a list that shrinks under an old one |
| No game prefab: a glow and a tail drawn by the mod 180 m out along the body's direction, hidden below the horizon | 3, the comet is seen; 5, cheap | A rocky body prefab: placed at the true scaled distance, 0.6 m from the camera at Phobos's radius, and sized to a nucleus a few kilometres across, under a pixel. A star sprite: from a pool of twenty shared with the world's own, a point with no tail, and rebound to its body by id when first shown |
| Every payload bay on a rocket at Entry aims, each with its own target; they aim the first comet parked there, by id | 6, each player's rocket aims for itself; 5, one rule | One aiming rocket per comet: the game has no rule to pick one |
| The disc is centred on the ground launch pad nearest the bay's target, or on the target where the map has no pad | 6, a player aiming near their own base gets their own pad; nothing to keep | The pad the rocket launched from: the game keeps no such pad for a rocket in space |
| The bay's readouts are TargetX and TargetZ (the landing now), DistanceKm (to the nearest structure), TimeToDestination (to the next highest) and Maximum (the reach, metres), NaN where nothing is aimed | the design's free values, on the device that aims | The avionics: it reads out the rocket, not the drop; a device of the mod's own: nothing in the game to build it from |
| The time to the next pass is to the comet's next highest point in the sky | the moment in a pass when its point comes nearest the pad, which is what a burn is timed by | The time until it rises: says nothing about where it would land |
| The structures on the ground are gathered once a second and the nearest worked out every frame | 5, the cost rule; the reading follows a landing that slides tens of metres a second | A scan of every structure every frame: grows with the base |
| The bays' targets go with the comet list (layout 3), sent when one changes, at most every 2 s | the icons stand in the same place on every machine | Not sending them: a joining player's game has no target, since the game neither saves nor sends a bay's |
| The Map motherboard's icons are drawn into the panel's own trackable icons in a postfix, not added to the game's list of trackables, and named in the map's own text when it is clicked | 4, the panel's own icon; nothing saved | An entry in the game's list of trackables: the panel draws only beacons from it, and the handheld Tracker steps through it as world things, so it would be a blank stop there. A beacon spawned in the world: a world thing, saved |
| The icons show only with the map's trackables switch on | as the panel's beacons | Always: a switch the player set would not hide them |
| The Ground Telescope's Vertical is the angle down from straight up, not the height | the game's dish: Vertical 0 points up and 90 along the horizon (`LargeRotatable.SetDishRotation`) | The design's "height": a chip set by it would point at the wrong part of the sky |
| `-CometSky` aims the telescope every frame by its own Horizontal and Vertical at the rule's bearing of the body's direction | the proof judges the telescope's own lock and readings, and that the bearing is what the dish takes | Aiming once at a predicted direction: the dish takes its direction a step late (`LargeRotatable`), so a single long slew ends off by that step. The lock is judged once the telescope's own Horizontal and Vertical have put the dish within 1° of the comet for 2 s, not after a fixed time: the dish turns 180 x its MovementSpeed degrees a second (3.6°/s, measured on bt), so a big turn takes most of a minute, and the time allowed is twice the longest turn plus 30 s |

### Stage 6, as built (0.21.0)

| Decision | Goal served | Alternative, and why it lost |
|---|---|---|
| Deorbit is a new rocket mode (13) offered at Entry, fired once like Launch | 4, the game's own action machinery; saves stay loadable by the stage 4 guard | Deploy at Entry with a flag: Deploy at Entry is the game's own payload drop |
| The landing is the aim of the last frame, the same values the bay reads out | the readout a player or a chip timed the burn by is where it lands | Working the sky point out again at the burn: it would differ from the readout by however far the comet moved since |
| The burn spends what the steer takes, by the rocket equation; leaving orbit costs nothing more; leftover fuel is not delivered | the design's "fuel left at Entry steers it"; "every mole burned is a mole the planet never gets" | A deorbit Δv of its own: no figure in the design to derive it from. Delivering the leftover premix: the comet's gas is what was cut |
| A steer toward a target past the disc is charged for the whole steer, though the landing stops at the edge | the burn burnt toward the target; the disc is the landing's rule, not the burn's | Charging only to the edge: the burn would know where the landing rule will stop it |
| Untracked is a target that is not a number; a bay nobody has set aims at the world's origin, 0, 0 | 4, the payload drop's own default target; a chip clears a target with NaN | Remembering whether a chip ever wrote the target: the game neither saves nor sends it, so another state to keep and send |
| An untracked disc is centred on the pad nearest the world's origin | the same pad on every machine; 3, it stays near a pad | The rocket's own pad: the game keeps none for a rocket in space |
| One comet comes down at a time, until its show has ended | the impact event is one at a time: a second would end the first's show and its wave | Several events at once: a director of many, for no goal |
| A shorter sky list waits for the game tick's main-thread moment, a postfix on `GlobalAtmosphereLiquid.UpdateLiquid`, refused unless the tick's own code still orders it before the logic tick | closes stage 5's window: no tracker indexes past a shorter list | A lock: the game's readers take none. Never shortening (a dead body left in the list): the Celestial Scanner draws every body, through the ground |
| The fall is `EntryWindowSeconds`, and the show starts 15 s before its end, as the console comet's does; for the first 35 s the comet is in neither the sky nor a show | 4, 5: the console comet's show unchanged | A show as long as the fall: the show's timeline is built around its 15 s approach |
| The gas is delivered by the fall, at the end of it, not by the show | the gas is in exactly when the comet is gone, whatever a save or a late-joining show does | The show's delivery: dropped at a save during the show, and skipped by a show that starts late |
| A fall survives a save and lands at the same clock time; its event is made again | the comet is a real object until it lands; LU's "no rescue": a comet brought down comes down | Dropping it, as a save drops a console comet's event: the gas would be lost |
| Deorbit is not put back after a load | the bay's target it aims by is not kept by the game | Putting it back: it would fire at the world's origin before a chip set the target again |
| Each machine warns its own player on the console, as a chat message is printed | "every player, their own distance"; 6, the game's own message route | The game's alert window: it blocks the screen. One chat message from the host: one text, so no player's own distance |
| A mineral comet's size is the moles of air it takes, its air read at the impact | the wave and the zones go by moles; an ice comet of the same mass is the same size; the air it takes is the air there when it lands | Its ore mass: not a size the wave rule takes. The air at the burn: it can change in the 50 s |
| The air a mineral comet takes is every gas in the tank, not its liquids | the design's "air"; a sea is not blasted away | The whole tank: a sea would lose its water to a comet |
| A comet's id is never given again in the same session | the sky body's hash, the warnings and the landings are by id | The lowest free id: a comet made during a fall would share its id |
| `terraform comet stop` ends a rocket comet's show and not its fall | LU's "no rescue": it is a debug verb for the show | Calling the fall off too: a comet brought down could be taken back |
| The impact comes in along the comet's bearing from the pad, at the console comet's 24 degrees | it comes from the side of the sky it was in | A random side: unrelated to where the comet was |
| The falls ride the comet list (layout 4), sent at once and with the planet sync, and only what a player's game shows and warns with: the comet, its kind, size, impact and fall on the clock | 5, one list for every comet; a player who joins mid-fall is warned too | A message of their own: a second route to keep in step, and none for a player who joins later |
| The impact's height is read from the voxels as the console comet's is; with none to read, the launch pad's height | 4, the console comet's own ground rule | The host's own height: a dedicated server has none |

### Stage 7, as built (0.22.0)

| Decision | Goal served | Alternative, and why it lost |
|---|---|---|
| **The fireball's heat reaches as far as the fireball is drawn**: its yield's largest radius, 54.9 m x (2W)^0.4, times `FireballHeatFactor` (1). 249 m at 1e5 mol, 625 m at 1e6, 1,570 m at 1e7, 3,943 m at 1e8, 9,906 m at 1e9 | LU's standing intent that what is seen and what is done agree | Stage 2's 2.24 x r0 (where the wave falls to 2,000 kPa): the drawn fireball is 1.3 (1e5, 249 against 193 m) to 2.4 (1e9, 9,906 against 4,166 m) times it, so a player in drawn fire took no heat. Drawing the fireball at that zone and keeping the law for its timing: a 2 Mt fireball drawn 898 m across where Glasstone and Dolan give 1,570 m |
| The setting is a new key, `FireballHeatFactor`, a multiple of the drawn fireball; `FireballRadiusFactor` is gone | a world's file that recorded 2.24 cannot be read as a multiple of a different radius | The old key with a new meaning: an older world would burn out to 2.24 times its fireball. A world's file with only the old key takes the config's value for the new one (`SIDECAR.md`) |
| Ground zero and the falling rocks stay at their wave thresholds, 10,000 and 200 kPa | 4, the game's own limits; nothing drawn contradicts them | Following the drawing there too: the show throws its rocks to exactly the falling rocks' edge, and ground zero is inside the drawn fireball at every size above 2,300 mol, so neither disagrees with what is drawn |
| **The burn is the game's lava rate, 25 a second, for the fireball's growth and thermal pulse (10 t_max)**, the same all through the fireball: 41 at 1e5, 112 at 1e6, 308 at 1e7, 370 for the largest rocket comet, 847 at 1e8, 2,333 at 1e9 | 4, the game's one heat damage by place; one number per comet, so it can be judged | The thermal pulse's power-weighted length, 2.22 t_max: 25 at 1e6, a 219 kt fireball barely scorching a window. The drawn glow, heat times time until it fades (`CometLook.BallHeat`): 268 at 1e6 and 561 at 1e7, everything of the game's usual health gone, no different from ground zero. Falling off with distance by the drawn ball's foot on the ground: the ball rises with the cloud from the first second, so its foot reaches only 0.89 R (1e5) to 0.23 R (1e9), a rule of the drawing's own and not the law's (Open, below) |
| The burn and the rocks only reach what faces the outside, by the wave's own test, and players outdoors | the rooms around a structure shield it, as they do from the wave | Every structure: a window deep inside a base would burn through the walls around it |
| The wave still acts in the fireball and the falling rocks, after the burn | a window in the fireball breaks as one just outside it does | The heat in place of the wave: a window 300 m from a 1e6 comet would be scorched and stand while one at 1.2 km breaks |
| Ground zero removes a structure by the game's own removal of one its damage destroys, whatever its limit, indoors or out | "every structure destroyed whatever its limit" | The wave's break: a structure with a broken model would stand as a wreck at the impact, and only those facing the outside would go |
| A player at ground zero is given their whole health as Burn | the game's own death by damage, synced as any death | Setting them dead directly: skips the damage route the game shows and logs |
| **Rocks land on places, not at random**: the ground in 2 m squares, a square with a structure facing the outside or a player outdoors a place; each place by its chance, nearest first, until `MaxDamagingRocks` | the cost rule (only where something is, at most the cap); a big comet's rocks fall most where the fireball ends | Landing the show's 78 drawn rocks at random: over a 1e6 comet's falling rocks (4.2 km^2) a given square is hit about once in 13,000 comets, so damaging rocks would almost never happen. Every place hit: no thinning with distance, and the cap taken by whatever the pass met first |
| The chance is 1 at the inner edge and falls as the inverse cube of the distance, an ejecta blanket's thinning (McGetchin, Settle and Head 1973): 6.8 % at the outer edge for 1e5, 10.7 % for 1e6, 43 % for 1e9 | a falling rock's density from crater ejecta, cheap | A flat chance: as many rocks at the outer edge as by the fireball |
| A rock is the game's suit-burst explosion, `Explosion.Explode(200, position, 2.3)`, made of its own pieces, landing on the place's structure or player: that one takes the whole 200 Brute wherever its colliders are, each other thing within 1 m the force falling to nothing at 2.3 m, each thing once | the cost rule's 3 m or less; the game's own smallest explosion that damages anything; a rock that lands on something damages it | A grenade (1,600 at 4.3 m): past the cost rule. A circuit housing's (50 at 1 m): it damages nothing, since the game damages only inside the whole metres below the radius. `Explosion.Explode` itself (0.22.0): it finds things by their colliders inside a 1 m sphere, so a hollow structure (a frame's beams are 1.22 m from its middle at their nearest) took nothing, as on bt, and a thing with many colliders took 200 for each |
| A rock comes down when a 45 degree throw that lands there would, `sqrt(2 d / g)` under the world's gravity, never before the wave; on a world with no gravity 30 s after the wave | ballistics, as the show throws them; 18 to 27 s on Mars for a 1e6 comet, inside the drawn rain's 4 to 35 s | All at the wave: rocks landing before they could have flown there |
| **The console comet's safe distance keeps section 2's table and is never inside 1.25 times the larger of the drawn fireball and ground zero**: from 1.97e7 mol up the fireball sets it, 3,276 m at 3.6e7, 4,929 m at 1e8, 12,382 m at 1e9 | the viewer outside what is drawn burning and what is destroyed: the fireball's boils reach 1.18 R (`CometLook`), and 1.25 stands past them | Section 2's table alone: from 3.7e7 mol the drawn fireball covered the nearest allowed point (1e8's 3.9 km against 2.75 km, 1e9's 9.9 km against 3 km). The fireball alone, with no floor: a 1e5 comet would land 311 m off and its wave there, 770 kPa, break every window, wall and hatch |
| The peak at the safe distance falls from 1e8 up: 306 kPa at 1e8 (windows and composite walls break; shutters at 350 hold), 225 kPa at 1e9 (windows only), where section 3 had 982 and 3,830 kPa | follows from the row above; reinforced windows still hold at every size | Keeping the old peaks: would need the comet nearer than its own fireball |
| The landing disc on planets above Standard follows the new safe distance through its own rule: Standard and Short unchanged (5,100 m), Long 8,852 m (was 5,450), a full-size planet 15,406 m (was 5,750) | the disc's derivation, a tracked landing reaching a safe distance | Leaving the disc on the old safe distance: on Long the biggest comet (76M mol, fireball 3.5 km) could not be brought down outside its own drawn fireball while in sight. Cost: on Long a blind burn lands near the pad less often (Open) |
| **Loudness is the wave's peak where the listener is**, to the 0.6 against the 50 kPa that throws a player (Stevens' power law, half as loud for every 10 dB less), never under 0.1; at the console safe distance 0.26 (1e4), 0.55 (1e5), full from 1e6 | the sound follows the wave a listener feels, from the yield and the distance | The 0.16 log scale, 0.45 + 0.2 x scale: a comet heard the same 2 km and 20 km off. The yield alone at a fixed distance: a rocket comet across the map as loud as one at the pad |
| **The darker sky's depth is 0.55 (1 - e^(-tau)), tau as the yield to the 1/2** (the dust over the cap's area, the cap growing as W^(1/4)), 0.15 at 1e5: 0.053 at 1e4, 0.349 at 1e6, 0.527 at 1e7, 0.55 from 1e8 | the sky follows the yield as the rest of the show does | The 0.16 log scale: 0.25 at 1e6 and 0.35 at 1e7, and a 1e4 comet as dark as a 1e5 one |
| The storm loop after the sound passes follows the loudness alone | one rule for every sound | The 0.16 log scale on top of the loudness |

### Stage 8, as built (0.22.1)

| Decision | Goal served | Alternative, and why it lost |
|---|---|---|
| The harness has its own dedicated server and its own client, copied into `G:\TRStage8`, each with its own data folder by StationeersLaunchPad's `SavePathOverride` | bt's other servers, its own game and its real saves are never touched | LiveCheck's `-TestServer`: it refuses whenever bt's servers run, stops its server the moment a client starts, and forces two other mods and a password |
| One driver plugin for both ends, its role from the environment, the two ends trading what they saw through small files in the run's folder | the mod's own networking is what is proved, so the harness adds none: it never touches `Mod.Networking` | A driver message of its own over LaunchPadBooster: a second path between the games beside the one under test |
| Values are compared at a clock one end names: the server writes what it has at its clock, the client works its own out at that same clock by the mod's rules | the same, to the last bits, on every machine, whatever the frames and the network delay | Comparing what each shows in its own frame: never the same moment, so only a tolerance as wide as a frame's motion |
| The client joins with the game's own `join` command 1.25 s after the game's data has loaded | the way StationeersLaunchPad's own auto-join does it; the game's `-join` launch argument runs before the main menu exists | `-join` on the command line |
| The console comet is called by the client with the game's `serverrun`, and a joined player's command is aimed from that player (`CometInvoker`) | "where you are looking" for a player on a dedicated server | The first player online only: on a server with several players the comet would come down by whoever joined first |
| In phase load the server is held paused until the client has joined again | the fall lands with the client there, at the clock it was saved to land at | Letting it run: 42 s of the fall left, less than a client takes to load |
| `MaxDamagingRocks` is 0 for the run | the suit damage judged is the wave's alone | Leaving rocks on: a rock may land on the player, whose damage then cannot be told from the wave's |

## Proof as it is built

LU, 2026-10-21: prove each mechanic works as it is built, and that it still works once joined to the rest.

Each piece is done only when all three hold:

1. **The rule is checked on its own.** Every rule that decides an outcome (the sky rule, the reach, the travel
   arithmetic, the zones, the losses) is a pure function with a self-check that runs at load and in
   `tools/PatchCheck`, with mutants that must fail, as `CometRules` and `FireRule` are checked today.
2. **It works in the game.** A headless run on bt drives it end to end and prints the line that proves it, read
   back from the log. A rendered run on bt shoots it where it is visual.
3. **It survives integration.** After each piece, the earlier pieces' runs are run again on the same build, and
   the standing TR regression (`run.ps1`, `-SaveLoad`, `-Reset`, one `-Model`) still passes. Save and load in
   the middle of every stage is part of each run.

Build order, each stage proven before the next starts:

| Stage | Piece | Proof in the game |
|---|---|---|
| 1 | the two show bugs: impact height and the curved distant ground | rig shot: the impact sits on the ground the player sees |
| 2 | the config section, and the pure rules | PatchCheck with mutants |
| 3 | the comet as a map object, flying by the rocket rule, saved and loaded | a comet with set mass, thrust and fuel reaches Entry in the calculated time, and one short of fuel stops where calculated |
| 4 | Cut, Rig, Fuel, Burn and Launch actions | the comet's moles, mass and fuel after each action match the rules |
| 5 | the sky body, the telescope, the Map motherboard icons | the telescope locks on and reads the calculated values; the icon sits where the sky rule says |
| 6 | the deorbit and the landing | landings land where the rule says, with and without reserve, and an untracked burn lands in the disc |
| 7 | the impact zones | structures at measured distances end as the table says |
| 8 | multiplayer | a dedicated server and a rendered client on bt see the same comet, icon and impact |

Stage 8 is the multiplayer proof, `tools/Stage8` (0.22.1), written, not yet run: a dedicated server and a rendered
client of their own on bt, judged line by line (`tools/Stage8/README.md`). Building it confirmed, from the code,
that a console comet on a dedicated server was aimed from the scene's main camera, which a batch-mode scene keeps
and never draws; 0.22.1 aims it from the player who ran it, else the first player online (`CometViewpoint`,
checked at load and by `tools/PatchCheck` with five wrong rules, `COMET.md` section 2). The run also covers what no
headless run can: stage 3's map icon, stage 5's Map motherboard icons and sky body on a client, stage 6's warning
on a player's own console, and stage 7's push and suit damage on a player.

Stage 7 is in 0.22.0, proof (1) and (2) written, (2) not yet run: `CometZones` (the zones, the heat zone at the drawn
fireball, the burn, the rocks' chance, cap, order and time) is checked at load, chained into the comet rules'
verdict, and by `tools/PatchCheck`, which hands it ten wrong rules it must catch (the heat at stage 2's 2.24 r0, the
zones judged outermost first, a burn that fades toward the edge, one for the pulse's peak alone, one past the
fireball's edge, rocks past the cap, rocks past the falling rocks, a flat chance, the farthest first, rocks before
the wave), and `CometRules` (the safe distance outside 1.25 times the drawn fireball and ground zero, the loudness,
the sky's depth) four more (the 0.16 safe distance, the bare fireball with no margin, a loudness that ignores the
listener, the 0.16 sky). `tools/LiveCheck/run.ps1 -CometZones` is the in-game proof (`VERIFICATION.md`), for bt: a
console comet and an untracked rocket comet, each landing by a line of real structures, every one's end state
judged, the pass judged against the world's structure count, and every state read back after a save and a load.
Players at ground zero, in the fireball and under the rocks need a run with players, stage 8.

Stage 6 is in 0.21.0, proof (1) and (2) written, (2) not yet run: `CelestialRules.SteerFuelMoles`, `Descend`,
`ArrivingGas` and `MineralRemoval` are checked at load and by `tools/PatchCheck`, which hands the check seven more
wrong rules it must catch (a burn that spends the whole reserve whatever it steers, one worked as thrust over the
full mass, an untracked burn steered toward the pad, a steer charged only up to the disc's edge, an ice comet
whose gas arrives whole, a mineral comet that takes all its air from one gas, and one that takes the same moles
of every gas); `CometSkyBodies.SafePointOrder` is fed the game tick's own calls from Mono.Cecil, and four wrong
orders it must refuse; the warning has its own check; the save's check carries two falls through a save and a
load to the same clock time. `tools/LiveCheck/run.ps1 -CometLand` is the in-game proof (`VERIFICATION.md`), for
bt. What a player reads on their own console needs a run with players, stage 8.

Stage 5 is in 0.20.0, proof (1) and (2) written, (2) not yet run: `CometSkyRules` (Unity's rotations worked
in doubles, the day's angle, the scene rotation, a body seen from the planet, a round orbit's place, the
orbit's pole from the planet's axis, bearings, the telescope's angles and the time to the comet's next
highest) is checked at load, where the sky body is refused without it, and by `tools/PatchCheck`, which hands
the check thirteen wrong rules it must catch (the scene rotation in the wrong order, the latitude flipped, the
longitude left out, a body seen from itself, a day of one degree of orbit, a backward turn counted forward,
the node in degrees, the tilt from the frame instead of the equator, a bearing from east, a height along north,
the telescope tipped up by the height, the next pass as the comet rising, and the anomaly's ratio upside down)
and binds the three parts to their targets. `tools/LiveCheck/run.ps1 -CometSky` is the in-game proof
(`VERIFICATION.md`), for bt; it holds the game's own Sun to the rule's frame. The glow and the Map
motherboard's drawing need a rendered run.

Stage 4 is in 0.19.0, proof (1) and (2) written, (2) not yet run: `CelestialRules.CutAdds`, `FlightKg`,
`PremixTransfer`, `BurnWanted`, `AfterBurn`, `FuelNeeded` and `DiscRadiusAt`, and the defaults of
`CometMassScale`, `MineralKgPerUnit` and `FuelLitresPerSecond`, are checked at load and by `tools/PatchCheck`,
which hands the check nine more wrong rules it must catch (a Cut past the cap, a Cut that ignores the head, K's
gas weighing, fuel that moves what is not premix, fuel moved one to one, fuel that ignores the stores' volume,
a Burn that takes nothing from the comet, fuel needed without its own weight, and a disc that ignores the
planet's size), and binds the actions' patches to their targets. `tools/LiveCheck/run.ps1 -CometActions` is the
in-game proof (`VERIFICATION.md`), for bt.

Stage 3 is in 0.18.0, proof (1) and (2) written, (2) not yet run: `CelestialRules.PositionAt`,
`DeltaVAfter` and `SizeMultiplier` are checked at load and by `tools/PatchCheck`, which also hands the check
six more wrong rules it must catch (a flight whose mass does not fall, one that gains speed once its fuel is
gone, progress counted over the whole route rather than the hop, a comet that runs dry and is still put at
the end, a size multiplier that ignores the planet's size, and one that falls under 1); PatchCheck binds the
save, the icon and the guard to their targets. `tools/LiveCheck/run.ps1 -CometFlight` is the in-game proof
(`VERIFICATION.md`), for bt. The map icon cannot be checked headless, where the map is never drawn; it needs
a rendered run.

Stage 2 is in 0.17.0: `src/Comets/CelestialTuning.cs` (the settings) and `src/Comets/CelestialRules.cs` (the
rules), checked at load (the log says so) and by `tools/PatchCheck`, which also hands the check nineteen wrong
rules it must catch and reads the game's own molar masses and burn ratio under the worked examples.

## Open

- Burning the comet: no single ice site on Mars's map holds both methane and oxygen (volatiles come with
  carbon dioxide, oxygen with nitrogen), so a comet can feed its own engines only if Cut mixes two sites. The
  hydrazine site's ice burns alone in the game, but the rule here burns only methane with oxygen.
- The default planet size. K follows it now, so it no longer decides whether the third-of-a-planet rule holds.
- `CometMassScale` (0.39) and the disc following the planet's size were decided in stage 4 (above); both are
  LU's to revisit.
- What a mineral comet's impact leaves (a mound of ore) is still to come; its removal of air is built (stage 6).
- Between a comet's burn and its show, 35 s, nothing of it is seen; the warning says when it lands.
- A map icon of its own for a comet: it borrows an asteroid picture from the game's map icons.
- That a Ground Telescope built facing north looks north at Horizontal 0 and straight up at Vertical 0 is read
  from the code; how the dish sits in its model is prefab data. `-CometSky` aims one by the rule and must lock.
- The glow's size and brightness, and how it looks by day, are untuned: a rendered run on bt shoots it.
- Fuel and Δv left, and Δv still needed, are not read out on any device yet (*Readouts*).
- Whether a landing past Mars's 4 km of terrain looks right on the far curved ground.
- The drawn fireball rises with the cloud from the first second, so its foot on the ground reaches only 0.89 R
  (1e5) to 0.23 R (1e9) of the heat zone, which is its largest radius aloft. Holding the ball on the ground through
  its growth would make the foot reach R; a look change, LU's call.
- On a Long planet the disc is now 8,852 m (stage 7's safe distance), so a blind burn lands near the pad less
  often than the 5,450 m disc had it; LU's to revisit with the default planet size.
- What the players' heat, kill and rocks look like from a player's seat: stage 8.
