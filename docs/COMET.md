# Comet impacts

Design for the comet feature on branch `comet-impact`. The prototype (0.15.0) is a console-called comet that
adds its gas at impact and plays a show for the host only. This document is the next step: what the impact
does to the world, how every player sees and hears it, and how it is checked. How comets are made and
brought down by rocket is `COMET-ROCKETS.md`.

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

A save in the middle of an event drops it. The gas is in or it is not. A comet brought down by rocket is the
exception: its fall is saved, and its event is made again at the load (below, and `COMET-ROCKETS.md`, stage 6).

## 2. Where it lands

The host picks the impact point. It starts from where the host is looking, turned by `turn=` if given, at
`range=` if given. It must be at least the **safe distance** from every structure and every player in the
world:

- safe distance = 1,500 m + 500 m x the comet's scale (`CometRules.Scale`, the log scale the 0.16 show was drawn at),
  and never less than 1.25 times the larger of the drawn fireball and ground zero (`CometRules.SafeDistance`), so
  nobody stands in what is drawn burning or in what the impact destroys (0.22.0): 1,750 m at 1e4 mol, 2,000 m at
  1e5, 2,250 m at 1e6, 2,500 m at 1e7; from 1.97e7 mol the fireball sets it, 3,276 m at 3.6e7, 4,929 m at 1e8,
  12,382 m from 1e9 up;
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
| 1e8 mol, huge | 4,929 m | 306 kPa | also composite walls (300) |
| 1e9 mol, largest | 12,382 m | 225 kPa | 200 kPa windows and glass doors |

Reinforced windows and walls (8,000 kPa and up) hold against every comet at the safe distance. A base
further away than the nearest allowed point gets less: the peak falls with the square of the distance. Until
0.22.0 the safe distance was the log scale's alone and the two largest rows read 2,750 m (982 kPa: also shutters
and hatches) and 3,000 m (3,830 kPa: also airlocks); the drawn fireball then covered the nearest allowed point.

This is a game rule, not a physical model. A real blast of this energy at these distances is a few kPa, and
the game's windows take 200. It also ignores the planet's air, so a thin-aired Mars feels it as much as Venus
does. Both are deliberate: LU's spec is that comet size sets the damage.

The constants live in one place, so they can be tuned.

## 4. Structures

Nearer the impact ground zero destroys, the fireball burns and rocks fall (`COMET-ROCKETS.md`, "Near the
impact"); the wave below acts everywhere past ground zero. All of it is one pass over every structure, at the
impact (0.22.0).

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
around the player all apply as for any game sound. How loud it is follows the wave's peak where the listener is
when the show starts (`CometRules.Loudness`, 0.22.0): full where the wave throws a player (50 kPa), and below
that as loudness goes with sound pressure, to the 0.6 (Stevens' power law: half as loud for every 10 dB less),
never under a tenth. From the console safe distance a 1e4 comet is 0.26 as loud, 1e5 0.55, 1e6 and up full.
Two routes:

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

- depth: the dust's optical depth over its cap goes as the yield over the cap's area, W / W^(1/2), the cap
  growing as the cloud's top, W^(1/4); the sky dims by 1 - e^(-depth) of at most 55 %, stated 15 % at 1e5 mol
  (`CometRules.SkyDepth`, 0.22.0): 5 % at 1e4, 35 % at 1e6, 53 % at 1e7, 55 % from 1e8. The 0.16 show's log
  scale had 15 % for a small comet up to 55 % for the largest;
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

## As built (0.16.0)

Built to sections 1 to 9. What differs from the text above, and why. Nothing here has run in the game yet:
the code builds, `tools/PatchCheck` binds the sky patches to their targets and runs the rules' check with
the wrong rules it must catch, and every game API below was read in the decompile. Section 10 is still to do.

**Where it lives.** `src/Comets/CometRules.cs` holds every number of sections 2, 3, 5 and 8 and their check
(the loudness of section 7 too, from 0.22.0); `CometReactions.cs` section 6's strengths; `CometEvent.cs` the
event; `CometDirector.cs` the event in force and its delivery; `CometNet.cs` the instant message;
`CometSite.cs` section 2; `CometWave.cs` sections 4 and 5, whose structures and players come from the impact's
one pass, `CometZonePass.cs` (0.22.0); `CometSound.cs` section 7; `CometShow.cs` the timeline and the drawing; `src/Patching/CometSky.cs` section
8; `src/Sync.cs` layout 5.

**1. The event.**
- The comet first shows 1 s after the command and lands 15 s after that, so the impact is still 16 s after
  the command, as in the prototype and as `tools/CometShot` times its shots.
- The host works out the reaction strengths (section 6) and sends them with the event, so no client judges
  them from its own copy of the planet's air.
- `terraform comet stop` is sent to clients too, as the same event marked called off. A client remembers
  the last event called off, so a planet sync written just before the stop cannot start it again.
- The host keeps the event in force, and in the planet sync, until its darker sky has cleared (impact plus
  330 s), so a player who joins late still gets the sky. A second comet can be sent once the first show
  has ended, which replaces it: 345 s after first sight since the show is drawn from the comet's yield
  ("Size from yield"), 115 s before.
- The message is processed on the main thread: LaunchPadBooster reads its packets from
  `NetworkManager.ManagerUpdate` (its transpiler on `ReceiveEvents` calls `ReceiveUnknownEvent`, then
  `ConnectionState.ReceiveMessage` calls `Process`). Read in the installed `LaunchPadBooster.dll`. The event
  is handed to the director all the same, as the planet sync's is, and acted on in the director's Update.
- The planet sync only writes anything while the planet's coupling is on (`Gate.Enabled()`, as before), so
  on a world where the mod stands down a player who joins late gets no comet; the message still reaches
  everyone already joined.
- A client with LaunchPadBooster but without this mod logs an unknown message and drops it.
- Leaving the world, by any route, drops the event, the show and the darker sky.
- A dedicated server, or any machine with no camera or no graphics device, plays the timeline without
  drawing or sounding anything: the gas, the breaks and the suit damage still happen there.
- Particle systems are seeded from the event, as is every other random choice in the show. Different frame
  rates still step them differently, so two machines see the same show, not the same particles.

**2. Where it lands.**
- `range=` left out lands at the safe distance from where the host stands, the nearest allowed point,
  rather than the prototype's 1,500 m + 900 m x scale.
- The search: outward along the bearing in 100 m steps to 20 km, then bearings 15, 30, ... 180 degrees
  either side, nearer first. Nothing within 20 km: the comet is refused and the reply says why.
- Structures are judged in 100 m squares, each standing at its centre with half its diagonal (71 m) added to
  the safe distance, so the check stays cheap on a large base and never lets one closer. Every player
  counts, online or not.
- The approach is judged from the impact back up the path, every 100 m, using the height above the impact's
  ground, until the path is higher than the safe distance. Both sides it can come in from are tried.
- Whose view it starts from (`CometViewpoint`, 0.22.1): the player who ran the command, when a joined player
  sent it with the game's `serverrun`; on a game a person plays, its camera, then its own character; on a
  dedicated server, the first player online, then a camera only where frames are drawn (the screenshot rig's),
  then the world origin facing north. A player faces where their character looks, the turn their own game sends
  with their position. Until 0.22.1 a dedicated server took its scene's main camera first: the game keeps that
  camera in a batch-mode scene, never drawn and standing wherever the scene left it (the screenshot rig's log
  finds it as `Camera.main` in batch mode), so a console comet on a dedicated server would be aimed from there
  and not from the player; read from the code, not yet seen on a `-nographics` server. The rule is checked at
  load with the comet rules and by `tools/PatchCheck`, which hands it five wrong rules it must catch.

**The ground (0.16.1).** `src/Comets/CometGround.cs` holds the rule, `CometTerrain.cs` feeds it from the game.
- **Height.** The host reads the impact's height from the voxel terrain, the way the game's own spawn point
  finds the surface: down the column from the top of the terrain (voxel 1023) to the top of the highest
  voxel of density 127/255 or more. It reads every 8th voxel, then voxel by voxel above the first solid
  one, so the answer is exact unless a layer thinner than 8 m floats clear of the ground. Not a ray: the
  game gives terrain colliders only to the finest level of detail, tens of metres around each player, so
  a ray 2 km out always missed and the 0.16.0 comet landed at the host's own height.
- **Outside the voxel world** (Mars2 is 4,096 m across, -2,048 to +2,048) there are no voxels to read and
  the game draws no terrain. The height is read from the nearest column on the world's edge. That is the
  last ground the player can see in that direction, so the impact continues it; the world has no base
  height of its own to fall back on (0 to 1023 is the voxel range, not a ground level), and the host's
  height is unrelated to the impact. Only with no voxel terrain loaded at all, or a column with no solid
  voxel, does it fall back to the host's ground. The site log says which.
- **Past the game's extent nothing survives a load.** A structure built more than 8,192 m from the origin on
  any axis stands while the world is played, but the base game deletes it when the save loads: once a world
  has loaded, `XmlSaveLoad` (`XmlSaveLoad.cs:1184`) calls `GameManager.DeleteOutOfBoundsObjects`
  (`GameManager.cs:1210-1218`), which destroys every thing whose `WorldGrid.OutOfBounds()` holds
  (`WorldGrid.cs:51-58`): a grid coordinate past `WorldGrid.WORLD_EXTENTS`, 81,920 tenths of a metre. Between
  the voxel world's edge and that line, structures load back as saved. A comet may land out to 20 km; the mod
  changes nothing here.
- The height travels in the event's impact position (section 1), so every machine lands it at the same
  point. Nothing new is sent.
- **Drawn lower.** The terrain shader lowers ground with its distance from the camera: past 144 m by
  0.001 x ((d - 144) x curvature)^2 metres, plus a fold term and less a lift in the orbital view, at a
  position recentred with the terrain there (`TerrainCurvature.Drop`). Mars2's curvature is 0.3: 232 m at
  1,750 m, 310 at 2,000, 500 at 2,500, 734 at 3,000, as measured with `tools/CometShot`. Every machine
  works it out for its own camera, every frame, from the shader's own settings, and moves the show's
  drawn frame (`TR Comet/Drawn`) by the drop at the impact. Everything that stands on the ground or flies
  down to it is in that frame: the head and its tail, sparks and smoke, every glow, the impact and its
  cloud. Their particles simulate in that frame, so those already out move with it. The approach moves as
  one and stays straight; the game curves nothing in the sky either.
- Left in the real frame: the gust of dust past the player, seen near the player, where the ground is drawn
  where it is. The debris rain moved into the drawn frame with "Size from yield": it now falls over the
  falling rocks zone around the impact, not around the player, so those far out land on the ground as drawn
  and those near the player stop where they hit the ground there. The head's short glowing trail keeps its points
  where they were drawn, so it lags by the change in drop over its 2.5 s if the camera moves fast.
- The sounds of the head and of the explosion come from where they are drawn. The wave and the sound's
  arrival stay at the real distance: they are physics.
- Checked by `CometGround.Check` at load and in `tools/PatchCheck`, with eight wrong rules it must catch;
  `tools/CometShot` logs, at each shot after the impact, its own read of the voxels and the game's own
  `TerrainCurvature.Drop` against where the show drew the impact.

**3. The wave.** As written, with one addition: the wave stops growing at 1e9 mol
(`CometRules.WaveMolesCap`), where the show stops growing. The command takes up to 1e12 mol, and without
the cap a comet past about 3e9 mol would break reinforced windows at the safe distance, against this
section's own rule. The wave is worked out no closer than 100 m to the impact, so it stays finite.

**4. Structures.**
- The breakable test also skips anything air already passes through (`CanAirPass`), which covers open doors
  and gratings, as the game's own stress check does.
- Facing the outside: for a structure on a face, the two cells either side of each face it blocks are
  looked up in the world's rooms (`RoomController.World.GetRoom`); for one that fills a cell rather than a
  face, the six cells next to it are. No room on one side is outdoors.
- Only windows creak: glass walls taken past 80 % of their limit and not broken. Glass doors that hold do
  not.
- The fail sound of a structure that is not a wall comes from its prefab name (Glass, Composite, else
  metal); a wall's from its material.

**5. Players.**
- The hardest push holds the part away from the impact at 18.3 m/s, so with the upward 6 to 8 m/s the whole
  never passes the game's 20 m/s: 14 to 18.3 away rather than 14 to 20. The gentlest band pushes upward 0.5
  to 1.5 m/s.
- The suit damage is the game's `Human.OnNetworkCollision` with an impulse of 2 to 4, rising with the peak.
  The game scales it by 3 for a suit and passes up to 6 times that through as stun (the space suit), so the
  worst case is 72 stun, under the 100 that knocks a player out. A player already stunned can still be
  pushed over it. A player just respawned (`Invulnerability`) is spared. Only players online are hurt.
- Outdoors is not in a room, not in a slot, and not locked to a seat.
- `preview` changes nothing in the world: no gas, no breaks, no suit damage, no push and no break sounds.
  It still draws, sounds, shakes the camera and darkens the sky.

**6. Reactions.** Burning is hypergolic share plus the smaller of the fuel share and the oxidiser share plus
4 times the planet air's oxidiser share (a fireball draws in several times its own volume of air), clamped
to 1. Steam counts water, steam and polluted water; dry ice carbon dioxide; toxic pollutant and
hydrochloric acid; ionised nitrogen and helium; a liquid counts as its gas. Worked cases are checked by
`tools/PatchCheck`.

**7. Sound.**
- The ground route is the `LocalPlayerInternal` group: its filter follows the helmet's own air with a floor
  of 1,500 Hz, so the thump reaches a player in a vacuum. The air route is `External`. Either falls back to
  the next group if a game update renames it, and the log says which were found.
- The air rush starts at a third of the approach, the sonic boom at 60 %, the roar is timed to end at the
  impact.
- The far rocket landing is a raw clip (`Sfx_RocketLandFar`) found by name among the clips the game has
  loaded; if it is not loaded the tail is the rumble alone, and the log says so.
- The storm loop is the game's `Storm` outdoors and `StormSheltered` indoors, crossfaded.
- A player who joins after a moment has passed does not hear it.

**8. The sky.** As written. The postfixes are an Extra (`comet sky`) and, like every Extra, are applied
only once the mod's required patches are in; on a game build the mod stands down for, there is no darker
sky.

**9. Visual fidelity.**
- Soft particles are the legacy `Particles/Additive` and `Particles/Alpha Blended` shaders with
  `SOFTPARTICLES_ON` and a 30 m fade. `Particles/Additive (Soft)` is a blend mode, not a depth fade, and
  takes no tint, so the show could not fade it.
- Draw order is by render queue: fire glow (3050) before smoke (3100), so the smoke covers it from every
  side; the comet, flash, shock front, sparks, ejecta and debris (3150) over everything.
- By day the glow is drawn down to 55 % and smoke up to 128 % of its night opacity, and the column and cap
  emit up to 35 % more.
- The whole show is held under 6,000 particles. How that holds for a show tens of kilometres across is
  under "Size from yield", Cost.
- The rig does not time frames itself. The show logs its own frame time, mean and worst, for the
  approach, the first 10 s after the impact and the rest, when it ends, and the rig's log carries that line.
- The head is the prototype's glow, not a model.

## Comets brought down by rocket (0.21.0)

A rocket at Entry brings a parked comet down with Deorbit (`COMET-ROCKETS.md`, "As built (stage 6)"). Its
impact is this event, with these differences:

- **Where it lands** is the burn's landing. Section 2's safe distance, its search and its approach check are the
  console command's alone: a rocket comet can land on a base, the player's own included (LU's decision).
- **When**: the burn starts a fall of `EntryWindowSeconds`; the event starts 15 s before its end, so the comet
  shows and lands as the console comet does, and every player was warned at the burn.
- **The way in** is along the comet's bearing from the pad, outward, at the console comet's 24 degrees.
- **Its size** is the moles that reach the planet for an ice comet, and the moles of air it takes for a mineral
  comet, which shows no reaction.
- **The gas** is not the show's: the fall delivers it on the host at its end, an ice comet's gas in and a mineral
  comet's air out, and the falling comet is gone in the same call. The show adds nothing.
- **A save during the fall** keeps it; at the load it falls on to land at the same clock time, and this event is
  made again for it. A save after the impact holds the gas and no fall.
- `terraform comet stop` ends a rocket comet's show and not its fall: the comet still lands and its gas arrives.

## Size from yield

The 0.16 show drew everything from a log scale, (log10(mol) - 3) / 2 held to 0.4 to 3: at 1e7 mol a 240 m
fireball and a 2.2 km column, and 1e8 only a quarter bigger. The wave rule (section 3) already says 1e7 is a
megaton-class blast (256 kPa at 2.5 km). Now the show is drawn from the yield the wave implies, by the
scaling laws of large explosions, so what is drawn and what is done agree. `src/Comets/CometYield.cs` holds
the rule and its check; `CometShow` draws from its `CometSizes`.

### The yield

The wave, dP = 400 x (n / 1e7)^(2/3) x (2000 / d)^2 kPa, depends on the moles n and the distance d only
through d / n^(1/3). That is cube-root blast scaling, Z = d / W^(1/3), with the yield W proportional to n.
So the exponent is 1 by the rule's own form, not by a fit; only the constant is fitted.

- **Reference curve:** Kinney and Graham (Explosive Shocks in Air, 1985), free-air TNT,
  P / P0 = 808 (1 + (Z / 4.5)^2) / sqrt((1 + (Z / 0.048)^2)(1 + (Z / 0.32)^2)(1 + (Z / 1.35)^2)),
  Z in m / kg^(1/3), P0 = 101.325 kPa.
- **Nuclear surface burst:** W of nuclear yield on the ground is a free-air TNT burst of W: half a nuclear
  yield goes to blast (Glasstone and Dolan 1977, 1.22) and the ground reflects it, doubling it (3.34). So the
  nuclear laws below take W as it is.
- **Fit:** least squares in the logarithm of the peak, at 41 distances from 500 m to 20 km (even on a log
  scale) and 17 comets from 1e5 to 1e9 mol (quarter decades), over the points where the wave is between
  200 kPa (the weakest limit that breaks) and 10,000 kPa (the strongest wall): **2.194e-4 kt a mole**, shipped
  as 2.19e-4. A million moles is 219 kt, ten million 2.19 Mt. That band is where the wave decides what breaks
  and who is thrown hard, so the drawn yield matches the damage where the damage is. Fitting all of 500 m
  to 20 km instead gives 2.32e-4 (6 % more), and a mismatch there of x1.45 on average against the band's x1.08.
- **Residual**, the wave over the curve (above 1 the wave is stronger):

| Comet | 500 m | 2 km | 20 km | 500 m to 20 km, rms / worst | 200 to 10,000 kPa: points, rms / worst |
|---|---|---|---|---|---|
| 1e5 mol | 1.10 | 1.16 | 0.16 | x2.22 / 0.16 | 3, x1.14 / 1.17 |
| 1e6 mol | 0.92 | 1.30 | 0.34 | x1.49 / 0.34 | 11, x1.08 / 1.16 |
| 1e7 mol | 1.02 | 1.05 | 0.68 | x1.19 / 0.68 | 19, x1.07 / 1.15 |
| 3.6e7 mol | 1.26 | 0.94 | 0.95 | x1.17 / 1.32 | 21, x1.08 / 1.16 |
| 1e8 mol | 1.61 | 0.91 | 1.16 | x1.21 / 1.61 | 22, x1.08 / 1.17 |
| 1e9 mol | 3.48 | 1.08 | 1.30 | x1.55 / 3.48 | 21, x1.08 / 1.16 |

  Within the band the two agree to 8 % on average and 17 % at worst, for every size. Outside it they part,
  because the wave falls as d^-2 everywhere and a real blast does not: far out, weak, it falls more slowly
  (a 1e5 comet at 20 km: the wave 0.19 kPa, the curve 1.2 kPa); close in, strong, faster (1e9 at 500 m: the
  wave 138,000 kPa, the curve 40,000). Neither end changes what breaks.

### The laws

W in kilotons, a nuclear surface burst. Each is held against its source by `CometYield.Check`.

| What | Law | Source | 1e7 mol (2.19 Mt) |
|---|---|---|---|
| Fireball, largest radius | R = 180 ft x (2W)^0.4 = 54.9 m x (2W)^0.4 | Glasstone and Dolan: 180 W^0.4 ft in free air (1 Mt: 5,700 ft across); a surface burst as twice the yield, its hemisphere holding a sphere's energy | 1,570 m |
| Thermal pulse peak | t_max = 0.0417 W^0.44 s | Glasstone and Dolan, chapter VII | 1.23 s |
| Fireball full grown, flash over | 10 t_max | Glasstone and Dolan's 1 Mt fireball is 5,700 ft across about 10 s after; 10 t_max = 8.7 s | 12.3 s |
| Flash shape | P / P_max = 2x^2 / (1 + x^4), x = t / t_max; about 90 % of the energy is out by 10 t_max | a fit to Glasstone and Dolan's thermal pulse curve (from memory; check against their figure) | |
| Flash brightness | peak irradiance ∝ W / t_max / d^2 ∝ W^0.56 / d^2, stated against 1e7 mol at 2.5 km | energy over the pulse's length; the thermal share is the same at every yield | 1 at 2.5 km |
| Cloud top | H = 22.5 km x (W / 1 Mt)^(1/4) | exponent: a buoyant thermal in stratified air rises as its energy to the 1/4, z ∝ (F / N^2)^(1/4) (Morton, Taylor and Turner 1956). Constant: the geometric mean through Ivy Mike (10.4 Mt, 41 km), Castle Bravo (15 Mt, about 40 km) and Tsar Bomba (50 Mt, about 65 km), off by -1.5 %, +10.7 %, -8.0 % | 27.4 km |
| Cloud rise | top(t) = R + (H - R)(1 - e^(-t / 150 s)) | Castle Bravo was at 14 of 40 km a minute in (model 33 %, measured 35 %), Ivy Mike at 17 of 41 km at 90 s (45 %, 41 %). A stratified thermal reaches its height in a time set by the air's stability, not its energy, so one time for every yield | 10.1 km at 1 min |
| Cap width | 1.5 x the cloud's top | Tsar Bomba's cap, 95 km across a cloud about 65 km high; a self-similar thermal keeps the ratio. Caps go on spreading afterwards (Bravo's reached 100 km, Mike's 161 km); the show ends before | 41 km |
| Stem | radius 0.35 R at the ground, widening as b = (6/5) α z with α = 0.1: half-angle 6.8 degrees | a buoyant plume's entrainment (Morton, Taylor and Turner 1956); the 0.16 show's 7 degrees, chosen by eye, was the same | |
| Shock front | leaves at 340 m/s | the wave's own speed (section 3) | |
| Ground dust | out to where the wave throws a player, 50 kPa: d = 2000 x sqrt(400 (n / 1e7)^(2/3) / 50) m, leaving at the speed of sound and slowing to stop there | the game's own threshold: there is no law for a land burst's surge to cite | 5.7 km |
| Thrown rocks | up to v = sqrt(g x R_rocks), which lands exactly at R_rocks at 45 degrees and nothing beyond; the highest throw tops out at R_rocks / 4; under the world's own gravity, no drag | ballistics; R_rocks is the falling rocks zone, 7.07 r0 | 2.8 km |
| Head | size 2 x (n / 1e7)^(1/3) (the 0.16 head at 1e7), tail length with it; light ∝ n^(2/3), at most 4 times 1e7's | a body's diameter is the cube root of its mass; a meteor's light is the kinetic energy it sheds, which goes with the area it ablates over | as 0.16 |
| Show length | until the cloud is at 90 % of its height, 150 x ln 10 = 345 s, and never past the darker sky's clearing, 330 s after the impact | the event already lasts that long (section 1), so no new lifetime | 330 s |

Every length a 0.16 element took from its 120 m x scale radius it now takes from R, the cap's width, the
dust's reach or the rocks' reach; every time from the fireball's growth, the cloud's rise, the wave's travel
or the fall. The proportions inside an element (a puff a third of the cap's width, a stem 0.35 R at its root)
are the drawing's own and the same at every size.

### The table

From `tools/PatchCheck`, which prints it, at the shipped zone settings:

| Comet | W | Fireball (law / zone) | Grown by | Cloud top | Cap width | Dust | Rocks | Show after impact | Far clip from the safe distance |
|---|---|---|---|---|---|---|---|---|---|
| 1e5 mol | 21.9 kt | 249 m (249 / 249) | 1.6 s | 8.7 km | 13.0 km | 1.2 km | 610 m | 330 s | 19 km |
| 1e6 mol | 219 kt | 625 m (625 / 625) | 4.5 s | 15.4 km | 23.1 km | 2.6 km | 1.3 km | 330 s | 32 km |
| 1e7 mol | 2.19 Mt | 1,570 m (1,570 / 1,570) | 12.3 s | 27.4 km | 41.1 km | 5.7 km | 2.8 km | 330 s | 56 km |
| 3.6e7 mol | 7.88 Mt | 2,621 m (2,621 / 2,621) | 21.6 s | 37.7 km | 56.6 km | 8.7 km | 4.3 km | 330 s | 77 km |
| 1e8 mol | 21.9 Mt | 3,943 m (3,943 / 3,943) | 33.9 s | 48.7 km | 73.0 km | 12.2 km | 6.1 km | 330 s | 101 km |
| 1e9 mol | 219 Mt | 9,906 m (9,906 / 9,906) | 93.3 s | 86.6 km | 129.8 km | 26.3 km | 13.2 km | 330 s | 190 km |

Calculated, not seen: `tools/CometShot` on bt is what shows them.

### The thin air

Not adjusted anywhere. The cloud's rise is the one place a documented relation says how: the thermal's
buoyancy is F = g E / (ρ c_p T) = g E (γ - 1) / (γ P), so at the same stability its height goes as P^(-1/4).
On the game's shipped Mars (about 2.1 kPa) that is 2.6 times Earth's height: a 1e7 cloud at 72 km, a 1e9 at
225 km, and on an airless world no limit at all. It is left out because:

- the yield is the wave's, and the wave ignores the planet's air by design (section 3). A cloud that grew in
  thin air while the damage did not would put the show and the damage apart again;
- the relation assumes the cloud rises a small part of the air's scale height. From about 1e5 mol up every
  cloud here already rises past Earth's 8 km, and past Mars's 11 km by a wider margin;
- holding it to anything finite would need a limit no physics gives.

The fireball, the flash and the dust are not adjusted for the air either. The rocks fall under the world's own
gravity (`Physics.gravity`), which is ballistics, not the air: their flight goes as one over the square
root of the gravity, so on the Moon they fly 1.5 times as long as on Mars.

### Against the damage

- The fireball's heat reaches exactly as far as the fireball is drawn (0.22.0, `COMET-ROCKETS.md` stage 7): the
  heat zone is the law's radius times `FireballHeatFactor` (1), and above 1 the fireball is drawn as far as its
  heat. `CometYield.Check` and `CometZones.Check` sweep 1 to 1e12 mol at the shipped settings and at the factor's
  bounds.
- The rocks land inside the falling rocks zone, the fastest at its edge, and the rain of them falls over that
  zone and nowhere else: a player outside it sees it fall in the distance.
- The dust stops exactly where the wave stops throwing a player, and the shock front travels with the wave.
- **Closed in 0.22.0.** The law's fireball was 1.3 times (1e5) to 2.4 times (1e9) stage 2's fireball zone
  (2.24 r0), so a player between the two stood in drawn fire and took no heat; and from about 3.7e7 mol the
  drawn fireball reached past the console comet's safe distance (1e8's 3.9 km over the nearest allowed point
  at 2.75 km, 1e9's 9.9 km over the one at 3 km). Now the heat zone is the drawn fireball, and the safe distance
  is never inside 1.25 times it or ground zero (section 2). The sound's loudness and the sky's depth follow the
  yield too (sections 7 and 8). What is left: the drawn ball rises with the cloud from the first second, so its
  foot on the ground reaches 0.89 R (1e5) to 0.23 R (1e9) of the heat zone, its largest radius aloft
  (`COMET-ROCKETS.md`, Open).

### The camera

- The far clip is raised, never lowered, every frame, to 1.05 times the distance to the far corner of the
  show's bounds: out to the largest of 1.6 times the cap's half-width, 1.2 times the dust's reach and 1.05
  times the rocks', up to the cloud's top plus 0.6 times the cap's half-width, plus the drawn drop at the
  impact and the eye's height above it. During the approach it also covers 1.3 times the distance to the
  comet's start. Restored when the show ends, as before. `CometYield.Check` holds it past the farthest
  corner for every size from the safe distance, from 5,100 m and from 20 km, with and without the drop there.
- The drawn frame still applies: the cap and the fireball are drawn in frames of their own (`Cap`, `Ball`)
  that climb with the cloud inside `TR Comet/Drawn/Impact`, so they move with the drawn ground as everything
  else there does.

### The look

The physical sizes above stay; what changed is how a near view of them reads. Seen from its safe distance a
1e7 comet's fireball is wider than the view and its cloud is overhead, so the 0.16 drawing (many soft, evenly
grey puffs, a base surge whose puffs grew to kilometres) read as a flat grey wall. `src/Comets/CometLook.cs`
holds each layer as a share of the size it draws, so every comet keeps the same proportions, with fixed counts:
a bigger comet is drawn with bigger puffs, not more of them. `CometArt` makes two new textures for it.

| Layer | Drawn as | Size | Count |
|---|---|---|---|
| Fireball | one camera-facing face, `FireballSheet`: a disc darker and redder toward its limb, broken into bright boiling cells with dark lanes, stepped through 16 frames a cycle (1.5 s, or half its growth time if longer) and turning slowly; white-hot while it grows, then orange, the comet's colours, dull red, fading as the cloud closes over it | radius from 0.2 R to R, 90 % grown by the end of the thermal pulse | 1 |
| Boils | bulges off the face's edge, the same texture, coloured as the face is now | 0.3 to 0.55 R | 40 |
| Stem | `BillowSheet` puffs born at the root and climbing to the cap over their 60 s life, each grown evenly to the column's width where it is: 0.35 R at the root widening as the plume law gives | 0.7 to 1.05 of the column's diameter where they are | 260 |
| Cap top | a ring of cloud (donut, middle at 0.5 and tube 0.32 of the cap's radius, lifted 0.1), sunlit: lighter shades of the gas bands | 0.3 to 0.48 of the cap's radius | 200 |
| Cap underside | the same ring 0.14 lower, darker shades, its tint warm while the fireball glows below it and cooling with it | as the top | 150 |
| Base surge | `BillowSheet` puffs rolling out along the ground at the speed of sound to the dust's reach | 0.2 to 0.38 R, growing to twice that, so never higher than the fireball; at most 0.45 of the screen each | 220 |

- **Soft, lit puffs.** `BillowSheet` is drawn by `src/Comets/CometBillow.cs`, plain arithmetic with its own
  noise, so the check measures the very texture the game draws. A puff is one mass: about forty billows whose
  densities add (so they merge, never meeting at a line), thinning over most of its radius, with noise eating
  into the thin edge so it frays. It is lit by the cloud between each point and the light above it, plus a
  little for each billow's face turned to the light, so the top is bright, the underside in shadow and the
  billows read inside it. Each frame differs from the next by under 0.02 in mean opacity, since a particle steps
  through them without blending them, and the last runs on into the first. 192 texels a frame, 768 square with
  mipmaps, worked out on a worker thread from the comet's first sight and made into a texture at the impact.
  The 0.16 sheet was nine hard-edged balls, each lit as a sphere, with the front one's shading taken whole at
  every texel: a crisp outline, a hard seam wherever two balls crossed, stepped at 128 texels a frame, and a
  jump where its frames wrapped. Seen at 2.5 km, where a puff spans most of the screen, that is the pile of
  balls the rig showed.
- **Puffs blend.** A cloud puff is drawn at 0.55 over the middle of its life (0.16: 0.95, fading over the whole
  of it), in over its first 8 to 10 % and out from 60 %. At its middle that is 0.45 opaque at night and 0.58 in
  full day, where 0.16's was 0.81 and fully opaque, so the puff behind still shows through and where many lie behind
  each other the cloud is dense. Systems using it turn their puffs at most a quarter radian and mirror half of
  them left to right, so neighbours differ and the light stays on top. Every cloud layer picks each puff's
  colour from the gas bands split into shades (darker and more opaque, lighter and thinner), so the cloud is in
  masses rather than one alpha.
- **The cloud stands to the end.** The stem and the cap are fed in full until 60 s before the show ends, taper to
  nothing 30 s before it, and the whole cloud fades out over the last 30 s (`CometLook.CloudFade`,
  `CloudFeed`). Before, feeding tapered from 80 s before the cap's longest puff could fade and every puff faded
  over its whole life, so the cloud thinned from about 220 s and was gone before 300 s, at 86 % of its height,
  against a show meant to run to 330 s.
- **Not soft particles.** The puffs neither write depth nor fade against each other: the legacy particle
  shaders fade only against the ground and the buildings in the depth buffer. The seams were in the texture.
- **The cap grows as one.** Its two rings are drawn at a radius of one in a frame the cap's radius scales each
  frame, so a puff made when the cap was the fireball's width grows with it to the full cap.
- **Screen share.** Each layer's puffs are held to a share of the screen's height (0.8 for the stem and cap, 0.45
  for the surge), so a puff passing the eye never covers the view and the sky shows at the sides.
- **The flash.** The overlay peaks at 0.9 x how squarely the eye faces it (0.35 to 1) x (0.4 + 0.6 x the thermal
  irradiance saturated, I / (1 + I)), up in 0.03 s and down with a 0.08 s fall; the thermal pulse after it is warm
  and held to 0.3. The flash core's glow and the flash light fall over 0.15 s. `CometLook.Check` holds, for
  comets from 1e3 to 1e9 mol seen from 100 m to 20 km, that the overlay is 0.6 or more, the core 1 or more and the
  light 6 or more for at most 0.3 s each, and that the thermal pulse never reaches 0.6: the ground reads through
  everything after the first few tenths of a second. These thresholds are the look's own, not measured.
- **Particles.** The cloud's layers hold 1,030 between them (0.16: 2,560), the head's 1,800 and the rest at their
  largest 1,050, so the cap of 6,000 never has to shrink a system.
- **Logged.** Each event logs its layers at full size (count, puff sizes, reach, screen share) and every system's
  particle limit, and at 0.5 s, the fireball's growth time, 5, 30, 120 and 300 s after the impact: the
  fireball's radius, height and heat, the stem's length and widths and how fast its puffs climb, the cap's height,
  width and underside warmth, the flash, the light, the sky's dimming and the live particle count.

### Cost

- **Per frame:** the same systems as 0.16, plus a handful of shape lengths, radii and start sizes set each
  frame for the stem and the cap, and the far clip. No per-tick work and nothing that grows with the size.
- **Particles:** the cap of 6,000 across the show is unchanged; the cloud is drawn with 1,030 puffs where 0.16
  used 2,560 (see "The look"), and the dry ice, toxic and ionised layers with 200, 160 and 200.
- **Overdraw:** every puff in an element is sized to that element, with fewer and larger puffs than 0.16, so a
  pixel the cloud covers is covered by fewer layers, and no puff of the stem, the cap or the surge covers more
  than its share of the screen however near it passes. What a bigger show adds is the share of the
  screen it covers, at most all of it, and no puff draws past the screen's edges, so none costs more than one
  screen. Puffs hundreds of metres across turn more slowly, and fade into the ground over a tenth of their
  size rather than the art's 30 m. The show logs its frame time, mean and worst, at its end, and the rig's log
  carries the line.
- **The puff's texture:** 768 by 768 with mipmaps, 3.1 MB, against 0.16's 512 by 512 without, 1 MB. Made once
  a session, on a worker thread, so the frame the cloud first appears in does not wait for it.
- **Length:** 345 s from first sight, against 115. A second console comet waits until then; the event in force
  already lasted until 330 s after the impact.

### How it is proved

- `CometYield.Check` runs at load, chained into the comet rules' own verdict, so a failure turns comets off as
  the other rules' do, and in `tools/PatchCheck`, which hands it eight wrong rules it must catch: a yield as
  the 2/3 power of the moles, as their logarithm, a fifth too big; a fireball drawn by the law alone inside
  its zone; a fireball and a cloud as the cube root of the yield; the 0.16 far clip of four times the impact's
  distance; a show that outlasts the darker sky. It refits the yield itself, so retuning the wave without
  refitting fails the build.
- Every machine logs, per event, `[TR comet] event N drawn from its yield: yield W (kt), fireball R m (law,
  zone) full grown at t, flash peak, cloud top, cap across, dust, rocks, show s after the impact, head scale;
  the show ends s after first sight`; the show's own line adds the far clip it set.
- `CometLook.Check` runs at load beside the yield's, chained into the same verdict, and in `tools/PatchCheck`,
  which hands it eleven wrong looks it must catch: a flash fading over a second, a thermal pulse that holds the
  view white, stem puffs half as wide again as the stem, a surge whose puffs may fill the screen, cap puffs
  drawn from the fireball rather than the cap, cloud puffs as opaque as 0.16's, a puff cut out as a disc, a puff
  full to its rim with nothing eaten from its edge, a puff lit from below, a puff whose frames jump where they
  wrap, and a cloud that fades over the show's last 90 s. PatchCheck prints the layers at 1e5, 1e7 and 1e9 mol
  and what it measures of the puff.
- The puff is measured at 64 texels a frame over all sixteen frames, against these, which are the look's own
  and not measured from anything: inside a fifth of its radius at least 0.75 opaque (shipped 0.86); from 90 %
  to 10 % of that over at least a third of the radius (0.36 to 0.71); nothing at the rim of its circle or
  outside it; opacity changing by at most 11 per radius and shade by at most 6, in all but the steepest 1 % of
  texels (7.5 and 2.1; a port of the 0.16 sheet, with a stand-in for Unity's noise, measures 18.4 and 7.4, and the edge 0.40 to 0.69); the top half
  at least 0.06 lighter than the bottom (0.24); at most 0.02 from one frame to the next (0.015) and no more than
  1.5 times that from the last to the first (0.012). One puff at its middle is at most 0.7 opaque in full day and
  at least 0.3 at night, and the cloud stands in full for the first 80 % of the show and is gone at its end.
- `tools/CometShot` makes the scene's camera controller the game's instance before it puts a sun in. Without it
  the sky's per-frame rebuild (`AtmosphericScattering.ManagerUpdate`, which reads the main camera's height
  through `CameraController.Instance`, set only off batch mode) threw every frame from the rig's sun on, before
  it set anything, so the rig's sky was never rebuilt and the darker sky never drawn in its frames. A played
  game, a client or a dedicated server cannot meet it: off batch mode the instance is set when the game
  starts, and a dedicated server never has a sun, so the rebuild returns before that line.
- `tools/CometShot` waits for the terrain its camera asked for: every block the game's own rule
  (`LodManager.CalculateLods`) gives its requester at every level in the requester's requested set, no request,
  mesh or collider work left, the mesh counts unchanged for a second, and ground under the camera. The game's
  busy flag alone is false while a request is still being worked out, so a dozen finest-level meshes another
  requester had built at the start location passed for ready at 0.0 s. A camera outside the voxel world asks for
  terrain around the nearest point of it.
- `tools/CometShot` takes `-Times` items of the form `every=<s> until=<s>` with an optional `from=<s>` (for
  example `2,8,14,every=0.5 from=15 until=20,every=5 until=345`), and its timeout grows to the last shot;
  `-Fov` sets the camera's vertical field of view and `-Back` stands it that many metres further back along
  its view. Both default to 0, which leaves the camera as it was.
