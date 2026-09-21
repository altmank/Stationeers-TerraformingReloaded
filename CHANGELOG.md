# Changelog

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
