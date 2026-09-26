# Per-world settings

Built and verified headlessly, not yet released.

## The problem

One global config decides how a particular world behaves. Tune a setting for a new save, load an older
one, and that older world changes or is damaged. `MaxPressureKPa` is the worst case: it deletes air
above the ceiling for good and saves the loss.

So every setting that affects a world lives in that world's own file. The config decides what a new
world starts with, and fills in anything a world's file does not record. It never reaches a world
being played. A world's own settings change only through the console, with `terraform set`.

`PlanetSize` is already safe, but by accident. The game persists the planet's tank volume, so the
per-world value comes back for free. Nothing else has a carrier like that.

## The file

`saves/<World>/terraforming-reloaded.xml`. One per world, in the folder root.

```xml
<?xml version="1.0" encoding="utf-8"?>
<TerraformingReloaded xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                      xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" Version="1">
  <MaxPressureKPa xsi:nil="true" />
  <MaxExternalOffsetKelvin>50</MaxExternalOffsetKelvin>
  <ExternalHeatHalfLifeMinutes>60</ExternalHeatHalfLifeMinutes>
  <GhgResponseScale>1</GhgResponseScale>
  <DensityResponseScale>1</DensityResponseScale>
  <AirlessAlbedo>0.3</AirlessAlbedo>
  <DynamicSky>true</DynamicSky>
  <WeatherOnWeatherlessWorlds>true</WeatherOnWeatherlessWorlds>
  <StormsStopWhenStripped>true</StormsStopWhenStripped>
  <StrippedAtmosphereShare>5</StrippedAtmosphereShare>
  <StormsStopWhenAtmosphereIsMild>true</StormsStopWhenAtmosphereIsMild>
  <MildAtmosphereColdestKelvin>263.15</MildAtmosphereColdestKelvin>
  <MildAtmosphereHottestKelvin>323.15</MildAtmosphereHottestKelvin>
  <MildAtmosphereMinPressureKpa>20</MildAtmosphereMinPressureKpa>
  <MildAtmosphereMaxPressureKpa>607.95</MildAtmosphereMaxPressureKpa>
  <MildAtmosphereMaxToxinsKpa>1</MildAtmosphereMaxToxinsKpa>
  <MildAtmosphereStopsSolarStorms>false</MildAtmosphereStopsSolarStorms>
</TerraformingReloaded>
```

The `xmlns:xsi` declaration is not decoration: without it the file does not parse. `XmlSerializer`
emits both namespaces on the root by default, so writing it correctly is free, but a sample or a
hand-edit that omits them throws.

**Never inside `autosave/`, `quicksave/` or `manualsave/`, and never named `*.save`.**
`SaveHelper.RollSaveFiles` counts every file in a folder, not just saves, so a sidecar beside each
save would halve the player's autosave depth and delete real saves.

`XmlSerializer` over a POCO, the same approach as `Climate.ResponseCurves`, plus the `Version`
attribute that one lacks.

## Every field is nullable

`double?` for a number, `bool?` for a switch. The rule is **every field is nullable**. Both shapes
round-trip identically through `XmlSerializer`, and the self-test checks that a recorded `false`
comes back as `false` rather than as an absence. The file is always written whole.

- **Absent** means the schema version that wrote it did not know about the field. Fall back.
- **Present** means recorded, use it.
- **Null** means off, for the two fields where the config uses 0 as a flag.

Because the file is always written whole, absence only ever means a version gap. That keeps "the
player meant zero" and "this version did not have the field" apart without `FieldSpecified`
companions, and it makes a later field free to add: it is simply absent in an earlier file.

`XmlSerializer` cannot tell an absent element from `xsi:nil`, and it does not need to: inside one
schema version all its fields are always written, so for the two fields the config flags with 0 a
missing element in a file of that version is the recorded state, not a gap. Deleting the
`ExternalHeatHalfLifeMinutes` line from a v1 file by hand therefore reads as "never fades", not as
"take the config". Measured in the live `-Sidecar` run.

It also removes the `default(T)` hazard by construction rather than by validation. An all-defaults
parse of a non-nullable POCO would set `GhgResponseScale` to 0 and silently kill the greenhouse
response.

## The config seam

BepInEx cannot express a nullable double, so the config keeps 0 as a flag and the sidecar does not.
Exactly one function knows that.

| Field | Config 0 means | Sidecar |
| --- | --- | --- |
| `MaxPressureKPa` | no ceiling | **null** |
| `ExternalHeatHalfLifeMinutes` | never fades | **null** |
| `GhgResponseScale` | off | 0, a real value on a real range |
| `DensityResponseScale` | off | 0, same |
| `MaxExternalOffsetKelvin` | no shift allowed | 0, same |
| `AirlessAlbedo` | no reflection | 0, same |

Translation happens on write only. Read does not convert back: consumers ask "is there a ceiling"
rather than "is the ceiling 0", so the sentinel dies at the boundary instead of leaking into the rules.

## Where it is read and written

Asymmetric, because on a new world neither the folder nor the save name exists when the planet is
built, while on a load both do.

- **New or loaded**: prefix and finalizer on `World.Initialize`, which is where the game itself
  decides it. See *A new world is not a missing sidecar*.
- **Read**: prefix on `PlanetaryAtmosphereSimulation.CreateGlobalAtmosphere`. Skip entirely when
  `NetworkManager.IsClient`; a client is handed the host's planet state by `Sync`, and with it the
  four values it evaluates itself (the three response settings and `DynamicSky`).
- **Write, new world**: postfix on `SaveHelper.CreateSaveDirectory`, the one place a world folder is born.
- **Write, a loaded world with no file**: inside the read prefix, into the folder that already exists.
  `CreateSaveDirectory` never fires on a load, so this is the only place that case can be handled.
- **Write, a setting changed**: `terraform set`, and nothing else.
- **Leaving a world**: postfix on `PlanetaryAtmosphereSimulation.Clear`, which the game's own
  teardown calls. Back to the config, ceiling off. Without it the values in force at the main menu
  are the last world played, and `Climate.TemperaturePostfix` gates on `Settings.Enabled` rather than
  on a world being in play, so the new-world screen's temperature range would depend on what was
  played before it.

**How a value reaches the code that reads it.** Not by overwriting `Settings`. A second plain static
class holds the values in force for the world being played, and the patches read that instead.
`Settings` stays the config and is never written by the sidecar, because BepInEx writes it whenever
the config editor moves a slider, and overwriting it would make the editor show numbers that are not
in force and would need restoring on leaving a world. The effective values are assigned whole at
every world start, so there is nothing to restore.

**A config edit never reaches a world being played.** LaunchPad has no save-scoped config: its
profiles decide which mods are enabled, and every config path it has is the global BepInEx one. So
the config editor sets what a new world starts with and nothing more. While no world is being played
the values in force follow the config, so the new-world screen's readouts match what the player is
setting up. In a world, the config is not read at all until the next world starts.

**`terraform set` assigns under the tank lock.** `Guards.Settle` reads the fade half-life on the tick
thread, and a `Nullable<double>` is two non-atomic writes, so an unlocked change can be read half
done: a torn read gives a world a fade at the moment the player asked for none. The write to disk
happens after the lock is released, so a slow disk cannot stall the planet tick.

Resolve the folder as the game does: `StationSaveUtils.GetSavePathSavesSubDir()` joined with
`XmlSaveLoad.Instance.CurrentStationName`. Not `CurrentWorldSave.RootDir`, which is a temp extraction
the game deletes after loading, and not the working directory.

## Reading

Version first, before anything else.

| Case | Behaviour |
| --- | --- |
| Version higher than ours | Stand down for the session and log. Do not guess, and do not write over it. |
| Version lower | Migrate forward. |
| Unparseable | Copy it aside, then treat as missing. |
| A field out of range | Treat that field as not recorded. See below. |
| Missing | See below. |

Every log line here names the full path first. A player has to be able to find the file the mod just
replaced, and four of the reasons a file is unreadable say nothing about which file they mean.

## Fail closed on the ceiling

**A loaded world's pressure ceiling comes only from its own file.** Two paths may produce a non-null
ceiling and no others: a sidecar read successfully for the world now loaded, and the
`terraform set MaxPressureKPa <kPa> confirm`, which is a deliberate act on a world that has a file.

Everything else sets it to null: a missing file, an unreadable one, a future version, the read
throwing, the self-test having disabled the sidecar, a station name whose folder did not resolve, a
client.

A brand new world is the one exception, and it is safe: it takes the config's ceiling because the
player has just chosen it and the world has no history to damage. The case that destroys something is
an *existing* world acquiring a ceiling it never had.

This is structural, not a convention. The ceiling is the only setting here that deletes rather than
changes, this machinery has already been wrong twice in the direction of applying one that should not
apply, and the invariant means every future bug in the read path degrades to "no ceiling" instead of
"some ceiling". No defect in this feature can then delete a player's air.

**How it is enforced.** Three things, so that reintroducing the hazard takes more than an
innocent-looking line:

1. `Effective.MaxPressureKPa` is a private field behind a get-only property. No assignment to it
   compiles anywhere. The only mutators are `NoCeiling()` and three named methods that each say where
   the value came from: `CeilingFromWorldFile`, `CeilingForNewWorld`, `CeilingByConsoleCommand`. All
   three run the value through the config's own range first, so even a caller that is allowed to set
   one cannot set a ceiling the config editor would refuse.
2. `Sidecar.WorldStartPrefix` calls `Effective.NoCeiling()` first, before anything else and *outside*
   its `try`. Every branch below it, the `catch` included, therefore leaves the ceiling off unless
   one of the three explicitly turns it on, and `TakeFromConfig` cannot touch it at all.
3. `tools/ci/check_repo.py` pins the call sites of all three to one each, in the file where each
   belongs, and checks the field is still private with a read-only property. A fourth caller fails
   the build.

## A new world is not a missing sidecar

Tell them apart, or the rule below silently cancels a ceiling the player just set for the world they
are creating.

**Take the game's own flag; do not infer it.** A prefix on `World.Initialize(string, bool newWorld,
string)` records it, and a finalizer clears it again, so it is true for the length of that one call
and false at every other moment of the session. Both of the game's `CreateGlobalAtmosphere` call
sites for a new world are inside it — `GridController.InitializeWorldController` builds the world's
`AtmosphericsController`, whose constructor creates the planet, and `WorldManager
.InitializeWorldEnvironment` creates it again — and the load path reaches both without going through
`World.Initialize`.

Inferring it from whether a name or a folder is set does not work. `FileCommand.NewGameTask` and
`LoadGameCommand.NewGameTask` call `World.StartNewWorld` without `XmlSaveLoad.ClearAll()` first, so a
world started from the console carries the previous world's `CurrentStationName`, which resolves to
the previous world's folder. That world would read another world's settings, and until `NewSave`
fires a `terraform set ... confirm` would write into the other world's file. The menu path does
clear it (`GameManager.ClearGameAll` calls `ClearAll`), which is why this is easy to miss.

The flag is an optional patch, and that is safe: without it every world start reads as a load, so a
world being created takes the missing-file rule and its ceiling is off. The harmless direction.

**A new world takes the config in full**, ceiling included, and its file is written by the
`CreateSaveDirectory` postfix. The rule below is for loads.

## Nothing read from the file is trusted

The file is a plain XML document in the player's saves folder. It gets hand-edited, it gets damaged,
and a value in it is not a setting until it has been checked.

**Every field is checked against the same range the config editor enforces on that setting**, from
one declaration (`Limits` in `src/Settings.cs`) that `Plugin.BindConfig` also builds its
`AcceptableValueRange` from, so the two cannot drift. Values that are not finite are refused too.

**A field that fails is treated as not recorded**, which is already what the file means by a field
the version that wrote it did not know: `MaxPressureKPa` becomes null, the rest fall back to the
config. One log line names the path and every field it refused, with the value and the range.

For the two fields the config flags with 0, the file's range excludes 0: in the file that state is an
absent element, so a 0 there is the config's sentinel leaking through a hand edit and is out of range
exactly like `-5`. This is not the sentinel coming back. The sentinel is a property of the config,
which cannot express null; the file can.

Measured, before the check existed: `<MaxPressureKPa>0</MaxPressureKPa>` scaled the planet to nothing
on the next tick, `-5` drove every gas negative, `-INF` put the tank at NaN, `99999999` was used
although the config caps at 10000, and `<GhgResponseScale>NaN</GhgResponseScale>` made `Climate`'s
cache key never match, so its entry was rebuilt for every outdoor cell of every tick with a
`Log.Error` each time.

**A file that could not be believed is copied aside before it is replaced**, once, to
`terraforming-reloaded.broken.xml` in the same folder. The rule here is that nothing of the player's
is deleted or moved, and replacing a hand edit outright throws away the settings it got right along
with the one it did not. One fixed name, so a world that loads with a broken file every time cannot
fill its own folder with copies.

## Missing sidecar

A world from before this existed, a workshop import, a folder copied by hand, a file that could not
be read, or a world whose folder is not where the game's own station name says it is.

**A setting that destroys state falls back to its no-op value. A setting that only changes behaviour
falls back to the current config.**

So `MaxPressureKPa` is written null regardless of what the config says, and everything else takes the
config. Then the sidecar is written and the fact logged once.

Without that rule the fallback would cause the damage it exists to prevent: a player who sets a
100 kPa ceiling for a new world and then loads an old Venus at 9,000 kPa would have 99% of its
atmosphere deleted over the next in-game day, permanently.

The way back is `terraform set MaxPressureKPa <kPa> confirm`, following the pattern
`terraform size <share> confirm` already set: refuse on a client, require `confirm`, and name the
pressure the planet is at and the share of air at stake in the prompt.

## Which settings are world-scoped

Every setting that affects a world. The tier decides whether `terraform set` asks before it acts, and
what a missing or unreadable file falls back to.

| Setting | Tier | Why |
| --- | --- | --- |
| `MaxPressureKPa` | destroys | Scales the whole tank down every tick it is over the cap. The moles are gone and the reduced tank is saved. `terraform set` asks before setting or lowering one |
| `MaxExternalOffsetKelvin` | destroys | The clamped value is written back into a saved energy counter, so lowering it permanently deletes banked heat. `terraform set` asks before lowering it |
| `ExternalHeatHalfLifeMinutes` | destroys, lesser | Also writes to a saved counter. Fading is intended, but a shorter half-life changes a world's equilibrium for good. `terraform set` asks before shortening it |
| `GhgResponseScale` | reversible | Read-time only, but the temperature it produces drives phase change, and cap contents are saved. Set it back and the matter comes back |
| `DensityResponseScale` | reversible | Same |
| `AirlessAlbedo` | reversible | Same, and narrower: it only feeds the airless base, so only the Moon and Mimas |
| `DynamicSky` | reversible | Only how the sky is drawn. Switched off mid-game, the sky keeps the look it has until the world is loaded again, because the game only sets it from the world's data at a world start |
| `WeatherOnWeatherlessWorlds` | reversible | Decides whether a full cloud may rain or snow. The cloud has already emptied into the air either way |
| The nine `Storms` settings | reversible | Decide whether a storm is scheduled. Suppressing one writes nothing the save carries: `WeatherManager.CreateSaveData` then holds "no event, and a cooldown long past", the state the unmodded game sits in between storms. Turning a rule back on schedules exactly one storm, then the world's own cadence resumes |

| `TraceGasGathering`, `TraceGasLine` | reversible | Decide how much of a trace gas the outdoor cells beside a base draw from the planet each tick. Nothing is deleted: the planet pays for every mole a cell takes, and a cell gives back what it does not use up. Switched off, what the cells hold drains back to the planet |

Not world-scoped:

- `Enabled`, the master switch. Off means nothing is patched at all, which is also the way out if a
  game update breaks the mod, so it has to be decided before any world exists.
- `SyncIntervalSeconds` and `StatusLogSeconds`. How often this machine sends packets and writes its
  log say nothing about a planet.
- `PlanetSize` and `CustomPlanetSize`. The game already saves the planet's size inside the world, and
  `terraform size <share> confirm` changes it.

## Not an in-save block

LaunchPad can persist a typed block inside `world.xml`, which would travel with every copy, every
workshop import and every cloud sync for free. Rejected: it writes an `xsi:type` that `XmlSerializer`
throws on, so removing the mod makes the save unloadable. That breaks the documented exit route of
`terraform reset confirm`, save, then disable.

## Everything is best effort

The mod currently writes one file, into BepInEx's own config directory. This is the first time it
writes into the player's saves folder. Every write is wrapped, a failure logs once and the session
continues on the in-memory snapshot, and nothing is ever deleted or moved. A read-only folder, a full
disk or a cloud sync mid-write must not stop a world loading. The copy taken beside a file that could
not be read follows the same rule: if the copy fails the write goes ahead anyway, because a
read-only folder must not leave the world with no settings file at all.

The `XmlSerializer` is built on first use rather than in a static initialiser. The read prefix is a
required patch, so a static initialiser that threw would become a cached
`TypeInitializationException` raised before the prefix body is entered, on every world start, with
nothing able to catch it. Lazily it is an ordinary exception inside a `try`, and the world loads on
the config.

## How it is checked

`tools\LiveCheck\run.ps1 -Sidecar`. Two phases: the first creates a world, which is the only moment
`CreateSaveDirectory` can record what it was created with; the second loads that world and rewrites
its settings file one shape at a time, calling the mod's own `Sidecar.WorldStartPrefix` on each, so
what is judged is the real read path over a real file in a real world folder.

The config is made to ask for a 500 kPa ceiling, and for five other distinctive values, throughout,
and every line prints the config's ceiling beside the one in force: a run in which the config was not
asking cannot be read as a pass. Nineteen cases — a recorded ceiling of `0`, `-5`, `99999999`,
`-INF`, every field negative, response scales of `NaN` and `INF`, a half-life of `0`, one field only,
no file, a file that is not XML, a world with no name, a folder that moved, a file from a version
this build does not understand, and one read after the session has stood down.

Then a control, because "the ceiling was off" is worth nothing if the rule never runs:
`terraform set MaxPressureKPa 0.5 confirm`, the one way a loaded world may acquire one. It deleted 83.3 % of the
planet in two ticks. Through all nineteen refused files before it, the planet held
2,379,749.934 mol and varied by 0.000.

`tools\LiveCheck\run.ps1 -Sessions` plays several worlds in one game session, switching with the
console's own `file start`, because both transition bugs found so far were state carried from one
world into the next. World A is created and changed with `terraform set`; lowering the heat limit has
to ask first, a value that is not on or off and an unknown key have to be refused. Then the config is
changed the way the config editor changes it, through the BepInEx entry. World B is created and has
to start from the new config; A, loaded again, has to still have what `terraform set` gave it; B,
loaded again, has to still have what it started with. At every step the values in force and the
values in the file are both read and compared.

## Known limits

**Steam Cloud syncs it.** Auto-Cloud covers the saves folder with pattern `*`, recursive, and fires at
launch and exit, so the sidecar and the save move together.

**One sidecar, many saves in a folder.** Load a three-hour-old autosave after changing a setting and
you get the current snapshot, not the one in force when that autosave was written. The alternative is
per-save sidecars, which delete saves.

**A client takes the host's values for what it works out itself.** The client skips the file. The
three response settings and `DynamicSky` arrive from the host with the planet state, under a new
section byte so an older reader skips rather than misreads them (MULTIPLAYER.md, *What the mod
adds*); until the first state arrives the client runs on its own config, which is as it joins. The
other world settings are never read on a client.

**A workshop save does not carry its file.** The upload is the single `.save`, not the folder, so an
imported world lands in the missing-sidecar path. That is correct, and needs nothing.

**The response curves file is still global.** `BepInEx/config/TerraformingReloaded.curves.xml` changes
every world's temperature response and has no version field. On the roadmap as an open question.
