# Per-world settings

Design doc. Nothing is built yet. Work items live in `TODO.md`.

## The problem

One global config decides how a particular world behaves. Tune a setting for a new save, load an older
one, and that older world changes or is damaged. `MaxPressureKPa` is the worst case: it deletes air
above the ceiling for good and saves the loss.

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

`double?` today, `bool?` from the first version bump: three of the nine storm settings are booleans.
The rule is **every field is nullable**, not every field is a `double?`. Both shapes round-trip
identically through `XmlSerializer`. The file is always written whole.

- **Absent** means the schema version that wrote it did not know about the field. Fall back.
- **Present** means recorded, use it.
- **Null** means off, for the two fields where the config uses 0 as a flag.

Because the file is always written whole, absence only ever means a version gap. That keeps "the
player meant zero" and "this version did not have the field" apart without `FieldSpecified`
companions, and it makes migration free: a v2 field is simply absent in a v1 file.

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

- **Read**: prefix on `PlanetaryAtmosphereSimulation.CreateGlobalAtmosphere`. Skip entirely when
  `NetworkManager.IsClient`; a client is handed the host's planet state by `Sync` and needs none of
  these values.
- **Write, new world**: postfix on `SaveHelper.CreateSaveDirectory`, the one place a world folder is born.
- **Write, a loaded world with no file**: inside the read prefix, into the folder that already exists.
  `CreateSaveDirectory` never fires on a load, so this is the only place that case can be handled.
- **Write, a setting changed**: the console command, or a live config edit (see below).

**How a value reaches the code that reads it.** Not by overwriting `Settings`. A second plain static
class holds the values in force for the world being played, and the patches read that instead.
`Settings` stays the config and is never written by the sidecar, because BepInEx writes it whenever
the config editor moves a slider, and overwriting it would make the editor show numbers that are not
in force and would need restoring on leaving a world. The effective values are assigned whole at
every world start, so there is nothing to restore.

**A live config edit, while a world is in play, writes through.** It updates the values in force and
marks the file dirty, debounced so a dragged slider does not write sixty times a second. Without this
the three response scales would silently stop being "takes effect at once", which is what the config
editor and `docs/SETTINGS.md` both promise. `MaxPressureKPa` is exempt: it is restart-flagged, so its
apply never fires mid-game, and it moves only by the console command.

Resolve the folder as the game does: `StationSaveUtils.GetSavePathSavesSubDir()` joined with
`XmlSaveLoad.Instance.CurrentStationName`. Not `CurrentWorldSave.RootDir`, which is a temp extraction
the game deletes after loading, and not the working directory.

## Reading

Version first, before anything else.

| Case | Behaviour |
| --- | --- |
| Version higher than ours | Stand down for the session and log. Do not guess. |
| Version lower | Migrate forward. |
| Unparseable | Treat as missing. |
| Missing | See below. |

## A new world is not a missing sidecar

Tell them apart, or the rule below silently cancels a ceiling the player just set for the world they
are creating. A new world has no folder and no save name yet; a loaded one has both. The discriminator
is that the game sets `CurrentWorldSave` and `CurrentStationName` together on the load path only.

**A new world takes the config in full**, ceiling included, and its file is written by the
`CreateSaveDirectory` postfix. The rule below is for loads.

## Missing sidecar

A world from before this existed, a workshop import, or a folder copied by hand.

**A setting that destroys state falls back to its no-op value. A setting that only changes behaviour
falls back to the current config.**

So `MaxPressureKPa` is written null regardless of what the config says, and everything else takes the
config. Then the sidecar is written and the fact logged once.

Without that rule the fallback would cause the damage it exists to prevent: a player who sets a
100 kPa ceiling for a new world and then loads an old Venus at 9,000 kPa would have 99% of its
atmosphere deleted over the next in-game day, permanently.

The way back is a console command, `terraform ceiling <kPa> confirm`, following the pattern
`terraform size <share> confirm` already set: refuse on a client, require `confirm`, and name the
pressure the planet is at and the share of air at stake in the prompt.

## Which settings are world-scoped

| Setting | Tier | Why |
| --- | --- | --- |
| `MaxPressureKPa` | destroys | Scales the whole tank down every tick it is over the cap. The moles are gone and the reduced tank is saved |
| `MaxExternalOffsetKelvin` | destroys | The clamped value is written back into a saved energy counter, so lowering it permanently deletes banked heat |
| `ExternalHeatHalfLifeMinutes` | destroys, lesser | Also writes to a saved counter. Fading is intended, but a shorter half-life changes a world's equilibrium for good |
| `GhgResponseScale` | reversible | Read-time only, but the temperature it produces drives phase change, and cap contents are saved. Set it back and the matter comes back |
| `DensityResponseScale` | reversible | Same |
| `AirlessAlbedo` | reversible | Same, and narrower: it only feeds the airless base, so only the Moon and Mimas |

Not world-scoped: `Enabled`, `DynamicSky`, `SyncIntervalSeconds`, `StatusLogSeconds`,
`WeatherOnWeatherlessWorlds`, `PlanetSize`, `CustomPlanetSize`.

The nine `Storms` settings cannot be judged yet, because nothing reads them. They go in when the rules
land, as a schema version bump.

## Not an in-save block

LaunchPad can persist a typed block inside `world.xml`, which would travel with every copy, every
workshop import and every cloud sync for free. Rejected: it writes an `xsi:type` that `XmlSerializer`
throws on, so removing the mod makes the save unloadable. That breaks the documented exit route of
`terraform reset confirm`, save, then disable.

## Everything is best effort

The mod currently writes one file, into BepInEx's own config directory. This is the first time it
writes into the player's saves folder. Every write is wrapped, a failure logs once and the session
continues on the in-memory snapshot, and nothing is ever deleted or moved. A read-only folder, a full
disk or a cloud sync mid-write must not stop a world loading.

## Known limits

**Steam Cloud syncs it.** Auto-Cloud covers the saves folder with pattern `*`, recursive, and fires at
launch and exit, so the sidecar and the save move together.

**One sidecar, many saves in a folder.** Load a three-hour-old autosave after changing a setting and
you get the current snapshot, not the one in force when that autosave was written. The alternative is
per-save sidecars, which delete saves.

**A host and a client can read different outdoor temperatures.** The client skips the file, but it
still evaluates the three response scales locally for its own readout. Today both sides read the same
global config and agree; afterwards a host with a sidecar and a client on defaults will not. The
planet state itself is synced and unaffected. Fixing it means a new message kind in `Sync` carrying
the three values, so an older client ignores rather than misreads it.

**A workshop save does not carry its file.** The upload is the single `.save`, not the folder, so an
imported world lands in the missing-sidecar path. That is correct, and needs nothing.

**The response curves file is still global.** `BepInEx/config/TerraformingReloaded.curves.xml` changes
every world's temperature response and has no version field. On the roadmap as an open question.
