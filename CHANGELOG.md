# Changelog

## 0.1.1

- New `terraform size <share> confirm`: changes how big the planet you are playing is, and so how
  long terraforming it takes, without touching its air. Pressure, mix and temperature come out
  exactly where they were, because the planet, its clouds and its ice caps all move together; the air
  in your base and in every outdoor cell is left alone. It prints the size, outdoor cells, moles and
  pressure before and after. On a planet wet enough to be near a sea it warns first that the rescale
  would raise one or drain one: the amount of liquid the game floods the outdoors at is fixed and
  does not follow planet size. It is turned away while the mod is not running the planet, because
  then nothing keeps the clouds and the ice caps in proportion to it.
  Planet size is a number you have to pick before you have any feel for it, and
  until now the only way to change your mind was `terraform reset confirm`, which throws away
  everything you have done. The planet size setting is left alone: it is still the default for a
  new world, and a saved planet still keeps its own size.
- `terraform` status: the weather line no longer shows the offset of a storm that has already ended,
  so the temperature parts add up to the temperature beside them again. The planet's own temperature
  was always right; only the readout was stale, because the game keeps the last storm's figure until
  the world is unloaded.

## 0.1.0

First version. Alpha: checked by automated runs against the game, never played through.

- Outdoor air is finite. Gas vented outdoors stays on the planet, air drawn in is taken from it, and the
  planet's pressure, temperature, sky and weather follow. Progress is saved in the normal save file.
- Every world can be made habitable. Mars uses the game's own response. Elsewhere the mod supplies one,
  measured from each world's starting air, so an untouched world reads exactly as it does without the mod.
  Venus and Vulcan cool as their greenhouse air comes out, and thick air evens out a world's day and night.
  The Moon and Mimas get a starting temperature from their sunlight.
- The game's own rules still apply: a gas freezes out of the air below its freezing point and melts back
  when the planet warms, storms still come (milder on a world you have cooled), and fuel beside oxygen
  outdoors burns.
- Planet size sets how long it takes and nothing else: Short, Standard (default, about 85 hours for a mega
  base on Mars), Long, Shipped, or a custom share.
- `terraform` console command: status, `reset confirm`, `curves export`, `curves reload`. The reset puts the
  planet back as the world ships and is the clean way to remove the mod.
- Custom worlds work without being added to the mod (WORLDS.md). The temperature response can be reshaped
  without a restart (CURVES.md).
- Multiplayer: the host's planet is sent to clients; clients without the mod can still join.
- Settings carry labels and ranges in the StationeersLaunchPad config editor.
- If a game update changes what the mod depends on, it switches the finite planet off, says why in the
  log, and keeps the temperature of a planet you have already changed.
- Fixes to the game's dormant planet code that only matter once it runs: saves no longer drain or
  double-count the planet, building over an outdoor cell no longer duplicates its gas, stored heat fades
  instead of building up for ever, bad gas mixtures cannot poison the save, rain and snow no longer
  overwrite running weather, the ice caps melt when the planet they sit on warms, and the new-game menu
  shows the right pressure.
