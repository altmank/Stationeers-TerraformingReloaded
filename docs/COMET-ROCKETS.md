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
- The comet is a real object on the map. Between nodes it moves exactly as a rocket does. Only the last step,
  from orbit to the ground, is new.
- Landing must be deliberate, and must not hit a base just because bases are spread around in multiplayer. A
  supermassive comet brought down badly is a catastrophe, and that is wanted.
- Only part of the gas reaches the planet.
- Interactions happen only between objects standing still on the map.

## The loop

| Stage | Where | What the player does | What it sets |
|---|---|---|---|
| Cut | the site | runs Cut with drill heads | the comet's size, so its gas and its mass |
| Rig | the site | delivers engines as payloads, one per Rig | the comet's thrust |
| Fuel | the site | transfers fuel from the rocket, or burns the comet's own ice into fuel | how far it can go, and the reserve |
| Fly | the map | sets the comet's destination to the low-orbit hub | nothing new: rocket travel |
| Bring down | the orbit hub | deorbits it toward a beacon on the ground | where it lands |

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
  more steering at the end (below).
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

The comet waits at the low-orbit hub for as long as it takes, as a rocket can. It shows on the map, and as a
bright point in the sky for every player.

**The beacon.** The player builds and powers a beacon in open ground. That is where they want the comet.

**The natural point.** On arrival the comet gets a natural impact point: a random spot in open ground, never
closer than 500 m to any structure. It is fixed from then on and shown to the player.

**Steering.** The deorbit burn spends the comet's remaining fuel to move the landing from the natural point
toward the beacon.

- How far it can move = the reserve Δv x a steering constant. **Tuning number**, set so a median comet needs a
  real reserve.
- Thrust caps how much of the reserve can be spent during entry: usable Δv = thrust / mass x the entry
  window. **Tuning number.** A heavy comet with few engines cannot steer much, however much fuel it has.
- The comet lands on the beacon if it can reach it, otherwise as far along the line as it gets.

**Refused:** a deorbit is refused if the beacon is closer to any structure than the safe distance for that
comet's size (`COMET.md`, section 2). The 500 m floor is only for comets that cannot reach their beacon.

**Warning.** When a deorbit starts, every player gets the time to impact and their distance from the landing
point.

**Gas that arrives.** 70 % of what is left after the trip; the rest burns up on entry. **Open:** LU has not
confirmed a fixed share.

| Plan | Result |
|---|---|
| Not enough fuel for the route | stops partway and is lost |
| Enough to arrive, no reserve | lands at its natural point, which can be near anyone's base |
| Reserve short of the gap | lands part way between the natural point and the beacon |
| Enough reserve and thrust | lands on the beacon |
| Supermassive comet, little reserve | lands at its natural point, maybe 500 m from a base: the catastrophe |

## Mineral comets

Same loop. At impact a mineral comet blasts the planet's own air away, in proportion to its mass. **Open:**
the constant. It is the tool for thinning a thick world such as Venus.

## Readouts

Logic readouts for the comet: fuel and Δv left, Δv still needed for the route, the distance from the natural
point to the beacon, and how far the reserve can steer it. The game defines `TargetX`, `TargetZ` and
`DistanceKm` but no rocket device uses them, so they are free for this.

## Balance

A comet is K x its site's gas. The table was worked at 60 % arriving; at the 70 % above, every count is
about 15 % lower. Comets needed to bring Mars to about 50 kPa:

| Planet size | K = 1 | K = 5 | K = 20 |
|---|---|---|---|
| Standard (1/20) | ~110 | ~23 | ~6 |
| Long (1/4) | ~560 | ~110 | ~28 |

Calculated from the game's site rules and the losses above, not measured. Planet size and K trade against
each other, but the wave follows each comet's absolute moles, so on a bigger planet every comet hits harder.
Proposed: Standard, K about 5, which makes a median comet about 2.3M mol, breaking nothing at the safe
distance. **Open:** LU has not decided.

## Open

- The tuning numbers: Cut share per cycle, steering constant, entry window.
- Fuel one rocket can carry per trip.
- The Rig payload item.
- A fixed 70 % arriving.
- The mineral comet's removal constant.
- K and the default planet size.
- New rocket actions mean patching the rocket's action lookup, its progress bars and the actions panel, with
  the save keeping the game's own mode and a sidecar for ours, so a save still loads without the mod.
