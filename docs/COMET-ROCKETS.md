# Comets by rocket

Design for how a player makes a comet and brings it down, on branch `comet-impact`. The impact itself (the
show, the pressure wave, structures, players, sound, sky) is `COMET.md`. Built so far: the settings and
the pure rules (stage 2 of *Proof as it is built*, 0.17.0), and the comet as an object on the space map that
flies to Entry by the rocket rule, saved with the world and sent to every player, made and sent for now with
debug console verbs (stage 3, 0.18.0).

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
  1,000 cycles.
- Cycle time and head wear follow the game's mining: `(10 - 0.4 x Density) / head speed` seconds, and heads
  wear as they do mining.
- The site panel shows the comet's moles and how much of the site it holds.
- The deposit is the limit. A Mars ice site holds 6,000 to 100,000 units of ice at 24 mol each, so 0.14M to
  2.4M mol (calculated from the game's site rules, not measured).
- The comet's mass is its moles times each gas's molar mass as the game defines it (`Mole.MolarMass`), which
  is not the physical one: water 108 g/mol, oxygen and methane 16, nitrogen 64, carbon dioxide 44. Water at
  2.3M mol is 248 t.

## Rig

- Each Rig action installs one engine on the comet, carried there as a payload.
- An engine adds 500 kg. The Pumped Gas engine gives 16.39 kN at 36 mol/s (thrust measured in game; fuel rate
  from the code). Other engine types are not checked.
- Engines buy time, not range: every engine burns fuel at the same rate per unit of thrust, so fuel per unit
  of speed gained does not change with engine count. More engines make the trip shorter, add mass, and give
  more correction at the end (below).
- **Open:** which item is the payload (the engine kit itself, or a new kit).

## Fuel

Two ways, and the choice matters:

- **Haul it.** The rocket transfers fuel from its own tanks into the comet. Each trip has to keep enough
  fuel to fly home. **Open:** how much fuel one rocket can carry; that sets how many trips a Fuel stage takes.
- **Burn the comet.** An ice comet holding methane and oxygen can feed its own engines: the drill heads turn
  its ice into propellant. It saves trips, and every mole burned is a mole the planet never gets.

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
- **Ground Telescope.** Locks onto the comet within 5° and reads it within 2°. Its aim angles are the comet's
  direction and height, so they give the point directly. During any storm the orbit readings go blank, which a
  chip that burns on its own has to allow for.

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

## Near the impact

The pressure wave only breaks what is over its limit. Closer in, an impact does more. Each zone ends where the
wave falls to a game threshold, so the zones grow with the cube root of the comet's moles as the wave does, and
stop growing at 1e9 mol where the wave stops (`CelestialRules.GroundZeroMetres`, `CelestialRules.ZoneAt`). r0 is
186 m at 1e6 mol (settings below).

| Zone | Radius | Where the wave there is | What happens |
|---|---|---|---|
| Ground zero | r0 | past the strongest wall, 10,000 kPa | every structure destroyed whatever its limit; anyone there dies |
| Fireball | 2.24 x r0 | past the full-force push, 2,000 kPa | heat damage to structures outside and to players |
| Debris rain | 7.07 x r0 | past ordinary windows, 200 kPa | falling rocks; each one that lands on a structure or player is a small game explosion |
| Pressure wave | as `COMET.md` | | breaks what is over its limit |

| Comet | Ground zero | Fireball | Debris rain |
|---|---|---|---|
| 1e6 mol | 186 m | 417 m | 1.3 km |
| 1e7 mol | 401 m | 898 m | 2.8 km |
| 1e8 mol | 863 m | 1.9 km | 6.1 km |

A rocket comet is capped at its site's contents times K. The biggest ice site on any shipped map holds 3.05M
mol (Europa's; 2.79M on Mars's and the Moon's), so at Standard size, K = 5, the largest rocket comet is 15.2M mol, ground zero
461 m. The 1e8 row and above happen only through the console command.

## Ore comets: what they leave

To come: a mineral comet leaves a large mound of concentrated ore at the impact. Nothing about how is decided.
The impact records its point, its kind and its makeup, so this stage can be added without changing the rest.
Adding terrain falls under the cost rule below; a one-off change at the impact is affordable if it is kept
small.

## Mineral comets

Same loop. At impact a mineral comet blasts the planet's own air away, in proportion to its mass: 0.7 kg of air
per kg of comet by default (`MineralRemovalPerKg`), taken from each gas in its share of the air
(`CelestialRules.MineralRemovedMoles`). It is the tool for thinning a thick world such as Venus. **Open:** the
mass of a mineral comet. The game gives an ore unit no mass that counts (a rocket's cargo weighs 1 kg per filled
slot, not by what it holds), so the mass per unit Cut moves into a mineral comet is still to be set.

## Cost rule

Every mechanic states what it costs per comet and where its cap is. Anything that runs every tick, or whose
cost grows with area, is out unless it is cheap.

- **One pass on the host at impact.** Every structure gets its distance once, which picks its effect from one
  table (destroyed, heat damage, wave check, untouched), applied when the wave reaches it. One pass over the
  players.
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
  list change under it.
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
| FireballRadiusFactor | the fireball's radius as a multiple of r0 | 2.24 | Where the wave falls to the full-force push, 2,000 kPa (`CometRules.FullForceKPa`), which throws a player hard and damages the suit: sqrt(10,000 / 2,000) = 2.236 times r0, 417 m at 1e6 mol |
| DebrisRadiusFactor | the reach of the falling rocks as a multiple of r0 | 7.07 | Where the wave falls to 200 kPa, the weakest limit that breaks (ordinary windows and glass doors, `COMET.md`) and where the hard push starts (`CometRules.ThrownHardKPa`): sqrt(10,000 / 200) = 7.071 times r0, 1,315 m at 1e6 mol. No rock does harm outside the ground the wave already damages |
| MaxDamagingRocks | the cap on rocks that hit | 40 | No game fact sets it; set to keep the cost rule. The show draws its debris rain at 7 pieces a second, falling off over 12 s, from 4 to 35 s after the impact: 7 x 12 x (1 - e^(-31 / 12)) = 78 pieces. A rock that hits must be one a player could have seen fall, so at most 78; half of them, 40, is at most 1.3 game explosions of 3 m or less a second over the 31 s, each the cost of one grenade |
| GasArrivingShare | share of the gas left after the trip that reaches the planet | 0.7 | No game fact: a rocket that crashes in the game puts all its fuel into the world (`Rocket.Explode`), which LU ruled out ("only part of the gas reaches the planet"). Kept at the approved design's 70 % (Decisions, below). K is derived through it |
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
| The zones end where the wave crosses game thresholds (10,000, 2,000 and 200 kPa) | 4, the game's own limits; the zones and the wave never disagree | The earlier 150 m, 2.5 and 8 picked by eye: ground zero inside where the wave already breaks everything, and rocks that could hit beyond it |
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
| 4 | Cut, Rig and Fuel actions | the comet's moles, mass and fuel after each action match the rules |
| 5 | the sky body, the telescope, the Map motherboard icons | the telescope locks on and reads the calculated values; the icon sits where the sky rule says |
| 6 | the deorbit and the landing | landings land where the rule says, with and without reserve, and an untracked burn lands in the disc |
| 7 | the impact zones | structures at measured distances end as the table says |
| 8 | multiplayer | a dedicated server and a rendered client on bt see the same comet, icon and impact |

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

- Fuel one rocket can carry per trip. A comet is heavy by the game's molar masses: a 2.3M mol water comet
  (248 t) with 2 engines needs about 323,000 mol to come in from a Frozen Vapors site.
- Burning the comet: no single ice site on Mars's map holds both methane and oxygen (volatiles come with
  carbon dioxide, oxygen with nitrogen), so a comet can feed its own engines only if Cut mixes two sites. The
  hydrazine site's ice burns alone in the game, but the rule here burns only methane with oxygen.
- The mass per unit Cut moves into a mineral comet.
- The Rig payload item.
- The default planet size. K follows it now, so it no longer decides whether the third-of-a-planet rule holds.
- On Long, K = 25 makes the biggest rocket comet about 76M mol, whose safe distance (2,720 m by `COMET.md`'s
  rule, calculated) lies a little past the half of the disc a telescope can track (2,550 m). The disc was
  derived at Standard size; whether it should follow the planet's size too is LU's call.
- A map icon of its own for a comet: it borrows an asteroid picture from the game's map icons.
- Whether a landing past Mars's 4 km of terrain looks right on the far curved ground.
