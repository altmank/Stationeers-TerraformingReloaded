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
| Fly | the map | sets the comet's destination to the low-orbit hub | nothing new: rocket travel |
| Bring down | the orbit hub | burns while the comet is where they want it in the sky | where it lands |

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

### The comet in the sky

At the low-orbit hub the comet becomes a body in the game's own sky, the system that moves the moons. It rises,
crosses the sky and sets, and its path shifts from one orbit to the next as the planet turns, so some passes go
over a base and some do not.

- Its position is worked out from the game's shared clock and its orbit, so every player sees it in the same
  place with nothing sent per frame.
- The game puts the observer at the planet's centre, so it looks the same from anywhere on the map.
- The game compresses sky time by the world's day length (a "2 hour" orbit loops in 97 s of real time on Mars
  and 1.3 s on Venus, calculated from the code). Each world's orbit is picked for a sensible real time between
  passes. **Tuning number.**
- It waits there for as long as it takes, as a rocket can.

### Where a burn lands

When the deorbit burn fires, the comet lands in the compass direction it is in the sky, at a distance set by
its height:

distance = R x (90° - its height in the sky) / 180°

| Comet when you burn | Lands |
|---|---|
| straight overhead | on the launch pad of the rocket that fired the burn |
| on the horizon | half of R out |
| straight below, the planet's far side | at R, the edge of the disc |

R is the disc radius, about 8 km. **Tuning number.** It never lands out of play: the far side of the planet
maps to the edge of the disc, so every landing can be seen.

The fuel left in the comet at the hub can move the landing point sideways, in a direction set through logic.
How far = the reserve Δv x a correction constant, and thrust caps how much of the reserve can be spent during
entry. **Tuning numbers.**

### Tracking, or not

- **By eye.** The comet is visible in the sky. Nearly overhead means do not burn.
- **Celestial Scanner.** The game's scanner shows a dot, the name and the distance for every sky body, through
  terrain, so the comet is easy to find. It draws no orbit lines.
- **Ground Telescope.** It locks onto the comet within 5° and reads it within 2°. Its aim angles are the
  comet's direction and height, so they give the landing point directly; it also reads where the comet is in
  its orbit and the orbit period. During any storm the orbit readings go blank, which a chip that burns on its
  own has to allow for.
- **Not tracking.** Burn at any moment. The comet is wherever it happens to be, so it lands at a random point
  in the disc. A small comet usually does no harm; a big one has a real chance of wrecking the base.

Chance that an untracked burn breaks the base's windows, for an 8 km disc, from the wave rule in `COMET.md`:

| Comet | Wave breaks windows out to | Chance |
|---|---|---|
| 1e6 mol | 1.3 km | about 3 % |
| 1e7 mol | 2.8 km | about 12 % |
| 1e8 mol | 6.1 km | about 58 % |

Ground zero on the pad itself is about 0.4 %. Calculated, not measured.

### Warning and gas

When a deorbit starts, every player gets the time to impact and their distance from the landing point.

70 % of the gas left after the trip arrives; the rest burns up on entry. **Open:** LU has not confirmed a fixed
share.

### What goes wrong

| Plan | Result |
|---|---|
| Not enough fuel for the route | stops partway and is lost |
| Burn without tracking | lands at a random point in the disc |
| Burn with the comet high in the sky | lands near the pad |
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

## Open

- The tuning numbers: Cut share per cycle, disc radius, orbit time per world, correction constant, entry
  window, zone radii.
- Fuel one rocket can carry per trip.
- The Rig payload item.
- A fixed 70 % arriving.
- The mineral comet's removal constant.
- K and the default planet size.
- Whether an 8 km landing, past Mars's roughly 4 km of terrain, looks right on the far curved ground.
