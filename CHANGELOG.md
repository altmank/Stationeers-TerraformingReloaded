# Changelog

## 0.9.1

- The `terraform` readout now shows the clouds and the ice caps: how much gas has frozen or
  condensed out of the air, which gas it is, and how close a cloud is to raining or snowing it back.
  Until now gas that left the sky had nowhere visible to go, and the only way to see it was a
  developer window that crashes partway through drawing itself.
- The full-size setting is called `Unmodded baseline` instead of `Shipped`, which meant nothing to
  anyone. If your config says `Shipped`, set it again.
- The time estimates were wrong and are now worked out properly. Adding gas is priced by the ice you
  have to mine, removing it by how fast vents can draw the planet through filters, and the two are
  added only where the route stops them overlapping. Venus drops from 208 hours to 40, Europa from
  748 to 220, Mimas from 516 to 360. Every world turns out to be limited by ice arriving, not by
  taking gas out.
- The store page and readme say what each world's project actually is, what each gas does to the
  temperature, and what a large base means in moles an hour.

## 0.9.0

First release. Everything is in, and every part of it is checked against the game by automated runs
rather than by long play.

- Outdoor air is finite. Gas vented outdoors stays on the planet, air drawn in is taken from it, and the
  planet's pressure, temperature, sky and weather follow. Progress is saved in the normal save file.
- Every world can be made habitable. Mars uses the game's own response. Elsewhere the mod supplies one,
  measured from each world's starting air, so an untouched world reads exactly as it does without the mod.
  Venus and Vulcan cool as their greenhouse air comes out, and thick air evens out a world's day and night.
  The Moon and Mimas get a starting temperature from their sunlight.
- The game's own rules still apply: a gas freezes out of the air below its freezing point and melts back
  when the planet warms, storms still come (milder on a world you have cooled), and fuel beside oxygen
  outdoors burns.
- Planet size sets how long it takes and nothing else: Short, Standard (default, about 75 hours for a mega
  base on Mars), Long, Unmodded baseline, or a custom share.
- `terraform` console command: status, `size <share> confirm`, `reset confirm`, `curves export`,
  `curves reload`. The status shows where gas that has frozen out of the air is being held, in the clouds
  and in the ice caps, and how close a cloud is to raining or snowing it back. The reset puts the planet
  back as the world ships and is the clean way to remove the mod.
- `terraform size <share> confirm` changes how big the planet you are playing is, and so how long
  terraforming it takes, without touching its air: pressure, mix and temperature come out exactly where
  they were, because the planet, its clouds and its ice caps all move together, and the air in your base
  and in every outdoor cell is left alone. Planet size is a number you have to pick before you have any
  feel for it, and the only other way to change your mind throws away everything you have done. On a
  planet wet enough to be near a sea it says first that the rescale would raise one or drain one, because
  the amount of liquid the game floods the outdoors at is fixed and does not follow planet size. The
  setting is left alone: it is still the default for a new world, and a saved planet keeps its own size.
- Custom worlds work without being added to the mod (WORLDS.md). The temperature response can be reshaped
  without a restart (CURVES.md).
- Multiplayer: the host's planet is sent to clients; clients without the mod can still join.
- Settings carry labels and ranges in the StationeersLaunchPad config editor.
- If a game update changes what the mod depends on, it switches the finite planet off, says why in the
  log, and keeps the temperature of a planet you have already changed.
- Fixes to the game's dormant planet code that only matter once it runs: saves no longer drain or
  double-count the planet, building over an outdoor cell no longer duplicates its gas, stored heat fades
  instead of building up for ever, bad gas mixtures cannot poison the save, rain and snow no longer
  overwrite running weather, the ice caps melt when the planet they sit on warms, the `terraform` readout
  no longer carries the offset of a storm that has ended, and the new-game menu shows the right pressure.
