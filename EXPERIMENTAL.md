# Experimental and optional features

Each of these is a per-world setting. Change it for the world you are playing with `terraform set <key> <value>` in
the console (F3). The StationeersLaunchPad config editor sets what a new world starts with. In multiplayer the
host's setting applies to everyone.

| Feature | Setting | Default |
|---|---|---|
| [Comets](#comets) | `CometsEnabled` | off, experimental |
| [Trace gases gather at your base](#trace-gases-gather-at-your-base) | `TraceGasGatheringEnabled` | off, experimental |
| [The planet's air can burn](#the-planets-air-can-burn) | `PlanetAirBurns` | off |
| [Gas released in space is deleted](#gas-released-in-space-is-deleted) | `SpaceDeletesGas` | off |
| [Rocket engines burn all their propellant](#rocket-engines-burn-all-their-propellant) | `RocketsBurnCompletely` | on |

## Comets

**Experimental, off by default. Enable at your own risk.** Comets are still in development: not fully balanced
and not finished. Turn them on with `terraform set CometsEnabled on`.

- **Make one by rocket.** At an ice or mineral site on the space map, new rocket actions build a comet: **Cut** it
  with a Rocket Miner, **Rig** it by deploying Pumped Gas engine kits from a Payload Delivery Container, **Fuel** it
  from your rocket's tanks, then **Launch** it toward the planet. Send it short of fuel and it stops where it runs
  dry.
- **In orbit it is in the sky.** It crosses the sky like a moon. The Ground Telescope can track it, the Celestial
  Scanner names it, and the Map motherboard shows where it is aimed and where it would land.
- **Bring it down.** A rocket at the planet's orbit aims it with its payload bay's PositionX and PositionZ, then
  **Deorbit** fires the burn. Every player gets a console warning with the time and direction, and it lands 50
  seconds later. Nothing keeps it away from your base.
- **Ice comets add gas to the planet. Mineral comets blast air away.**
- **The impact is dangerous.** Everything at ground zero is destroyed. The fireball burns structures and anyone
  outdoors. Rocks fall further out. Then the pressure wave breaks what it is strong enough to break.
- `terraform comet <mol> <gas>` calls one down in front of you from the console, always at a safe distance. Add
  `preview` to see it without changing the air.
- The comet settings are in the config's `Celestial terraforming` section.
- Turned off again, a world's comets stay as they are and carry on when you turn them back on.

## Trace gases gather at your base

**Experimental, off by default.** Turn it on with `terraform set TraceGasGatheringEnabled on`.

A small spill of a gas the planet barely holds (for example a furnace taken apart outside) normally spreads thinly
over the whole planet and lingers for hours. With this on, that gas collects in the outdoor air beside your base,
where it burns off or reaches your filters. Every mole comes out of the planet's air; nothing is created. The
`Trace gases` config section sets how fast it gathers.

## The planet's air can burn

**Off by default.** Turn it on with `terraform set PlanetAirBurns on confirm`.

When the planet's air holds a fuel and an oxidiser and would light by the game's own rules, it burns as one big
fire. It lights when the hottest hour of the day is hot enough for that air to light by itself, or when a fire
outdoors touches it. The heat is real: a big burn becomes a heat wave. `terraform` shows whether the planet is
burning and what. The `Fire` config section has two more switches that go with it.

## Gas released in space is deleted

**Off by default, and permanent.** Turn it on with `terraform set SpaceDeletesGas on confirm`.

Normally gas released at or above 1,000 m, where rockets in space are, drifts back down to the planet. With this
on it is deleted instead, so a rocket can carry unwanted gas off the world. Deleted gas is gone for good: turning
the setting off later does not bring it back. `terraform` shows how much has been lost to space.

## Rocket engines burn all their propellant

**On by default.** Turn it off with `terraform set RocketsBurnCompletely off`.

The game burns 96 % of a rocket's propellant and blows the rest out with the exhaust. With methane and oxygen that
leaves oxygen in the air under the rocket, and on a world whose air burns, such as Vulcan, every launch and landing
can start fires. With this on, a 2:1 methane and oxygen mix leaves no oxygen behind. Engines also get about 4 %
more thrust.

**Careful with automatic landings:** they plan with the most thrust the rocket has ever had. After flying with
this on, turning it off can bring a landing in too fast.
