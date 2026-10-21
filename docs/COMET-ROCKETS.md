# Comets by rocket

Design for how a player makes a comet and brings it down, on branch `comet-impact`. The impact itself (the
show, the pressure wave, structures, players, sound, sky) is `COMET.md`. Nothing here is built yet.

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
- Each cycle moves 10 % of the site's deposit into the comet. **Tuning number.**
- Cycle time and head wear follow the game's mining: `(10 - 0.4 x Density) / head speed` seconds, and heads
  wear as they do mining.
- The site panel shows the comet's moles and how much of the site it holds.
- The deposit is the limit. A Mars ice site holds 6,000 to 100,000 units of ice at 24 mol each, so 0.14M to
  2.4M mol (calculated from the game's site rules, not measured).
- The comet's mass is its moles times each gas's molar mass. Water at 2.3M mol is about 41 t.

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

Worked example: an 18 t comet with 2 engines going to Frozen Vapors (383 Δv) burns for about 225 s and uses
about 16,000 mol of fuel. Doubling the comet doubles the fuel.

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
  and 1.3 s on Venus, calculated from the code), so each world's orbit is set from a real time between passes.

### The sky rule: from the comet's place in the sky to a point on the ground

The point under the comet is in the compass direction the comet is in, at a distance set by its height:

distance from the pad = R x (90° - its height in the sky) / 180°

| Comet | Point |
|---|---|
| straight overhead | the launch pad of the rocket that set the comet's target |
| on the horizon | half of R out |
| straight below, the planet's far side | R out, the edge of the disc |

R is the disc radius. Nothing lands out of play: the far side of the planet maps to the edge of the disc.

### Skipping the skill

Burn at any moment, with no target or with a target out of reach. The comet comes down near wherever the sky
rule puts it, a random point in the disc around the pad. A small comet usually does no harm; a big one has a
real chance of wrecking the base.

Chance that a random landing breaks the base's windows, for an 8 km disc, from the wave rule in `COMET.md`:

| Comet | Wave breaks windows out to | Chance |
|---|---|---|
| 1e6 mol | 1.3 km | about 3 % |
| 1e7 mol | 2.8 km | about 12 % |
| 1e8 mol | 6.1 km | about 58 % |

Ground zero on the pad itself is about 0.4 %. Calculated, not measured.

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

The pressure wave only breaks what is over its limit. Closer in, an impact does more. Zones grow with the cube
root of the comet's mass; r0 is 150 m at 1e6 mol. **Tuning numbers.**

| Zone | Radius | What happens |
|---|---|---|
| Ground zero | r0 | every structure destroyed whatever its limit; anyone there dies |
| Fireball | about 2.5 x r0 | heat damage to structures outside and to players |
| Debris rain | about 8 x r0 | falling rocks; each one that lands on a structure or player is a small game explosion |
| Pressure wave | as `COMET.md` | breaks what is over its limit |

| Comet | Ground zero | Fireball | Debris rain |
|---|---|---|---|
| 1e6 mol | 150 m | 375 m | 1.2 km |
| 1e7 mol | 320 m | 810 m | 2.6 km |
| 1e8 mol | 700 m | 1.7 km | 5.6 km |

A rocket comet is capped at its site's contents times K, so with K = 5 and the biggest ice site the largest is
about 36M mol, ground zero about 500 m. The 1e8 row and above happen only through the console command.

## Ore comets: what they leave

To come: a mineral comet leaves a large mound of concentrated ore at the impact. Nothing about how is decided.
The impact records its point, its kind and its makeup, so this stage can be added without changing the rest.
Adding terrain falls under the cost rule below; a one-off change at the impact is affordable if it is kept
small.

## Mineral comets

Same loop. At impact a mineral comet blasts the planet's own air away, in proportion to its mass. **Open:**
the constant. It is the tool for thinning a thick world such as Venus.

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
Proposed: Standard, K about 5, which makes a median comet about 2.3M mol. **Open:** LU has not decided.

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
- **Ranges live in one place**, as `Settings.Limits` does today, so the config and any saved value are checked
  against the same bounds.

| Setting | What it sets | Default derived from |
|---|---|---|
| CutSharePerCycle | share of the site moved into the comet per Cut cycle | to be derived: mining cycle time and head wear, so a whole site costs a set number of head swaps |
| GasArrivingShare | share of the gas left after the trip that reaches the planet | to be derived |
| DiscRadiusMetres | R in the sky rule | to be derived: far enough that the safe distance fits inside, near enough that the impact can be seen (the game's far clip and the measured terrain curvature), and how much of the disc may lie past the voxel world's edge (Mars2 is 4096 m across; past it the impact takes its nearest edge's height, since 0.16.1) |
| SecondsBetweenPasses | real time from one pass to the next | to be derived: long enough to plan a pass, short enough not to wait an evening |
| LineShiftPerOrbit | how far the line moves between passes | to be derived from the orbit's inclination and the world's turn rate |
| CorrectionMetresPerDeltaV | reach per unit of reserve Δv | to be derived |
| EntryWindowSeconds | how long the deorbit burn can run, so thrust caps usable reserve | to be derived |
| GroundZeroMetresAt1e6 | r0 at 1e6 mol; other zones scale from it | to be derived |
| FireballRadiusFactor, DebrisRadiusFactor | zone radii as multiples of r0 | to be derived |
| MaxDamagingRocks | the debris cap | from the cost rule |
| MineralRemovalPerKg | air a mineral comet removes per kilogram | to be derived |
| CometSizeMultiplier (K) | a comet's gas as a multiple of its site's | 5, from the balance table, until measured |

Names are working names. The table is filled in as each piece is built.

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

## Open

- The tuning numbers: Cut share per cycle, disc radius, orbit time per world, correction constant, entry
  window, zone radii.
- Fuel one rocket can carry per trip.
- The Rig payload item.
- A fixed 70 % arriving.
- The mineral comet's removal constant.
- K and the default planet size.
- Whether an 8 km landing, past Mars's roughly 4 km of terrain, looks right on the far curved ground.
