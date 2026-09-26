# Changelog

## 0.11.1

- **Trace gas gathering is now experimental and off by default.** Turn it on for a world with
  `terraform set TraceGasGatheringEnabled on`, or in the config (`Trace gases gather (experimental)`)
  for new worlds and for worlds that have never recorded the switch. A world played on 0.11.0 has
  never recorded it, so it takes the config's off until you turn it on.
- **When on, it now helps larger spills, and harder the thinner the gas.** A gas counts as a trace
  below a thousandth of a mole per outdoor cell, ten times the old line: a spill of up to about 250
  moles on a Vulcan of the default size, where before anything over about 25 got no help. That is
  still a tenth of the thinnest gas any shipped world starts with, so no world's own air is touched.
  Just under the line a trace is drawn 100 times faster than other gases, and twice as fast again for
  every factor of ten it sits further below, up to 1,000 times, so the last of a spill does not
  linger.
- **The per-tick limit counts what the air beside the base keeps**, not what it hands straight back,
  so it no longer holds the rule back on an ordinary base. It still stops the whole of a trace
  arriving in a few ticks.
- **To bring a world that recorded 0.11.0's values up to 0.11.1's:** `terraform set TraceGasGathering
  100` and `terraform set TraceGasLine 0.001`.

## 0.11.0

- **A small spill of a gas the planet barely holds now clears around your base instead of lingering
  for hours.** When the planet's air holds only a trace of a gas, by default less than a
  ten-thousandth of a mole per outdoor cell, the outdoor air beside your base draws that gas from the
  planet 50 times faster than any other. Taking a furnace apart outside on Vulcan puts a few dozen
  moles of nitrous oxide into the planet's air. Before this, the air around the base went on burning
  for many hours while the planet let the gas go a trickle at a time, and no filter could catch an
  amount that small. Now it gathers where it is used up: it burns off beside the base several times
  sooner, and an intake there draws it in amounts a filter can separate. Every mole comes out of the
  planet's air; nothing is created or destroyed.
- **Expect the burn beside the base to be brighter while a spill clears**, on a world whose air
  burns. Afterwards it stops sooner than it would have. The same goes for the first air on a world
  that has none: the first few moles of each gas you release gather around your base before they
  spread.
- **Two new settings, in a new `Trace gases` section:** how many times faster a trace gas is drawn
  (1 turns it off) and the amount below which a gas counts as a trace (0 turns it off). Each world
  keeps its own. A world saved with an earlier version uses the config's values until you change
  them for it with `terraform set`. `terraform` lists the gases that are traces on the planet now.

## 0.10.1

- **The sky follows the air for everyone who joins a game with the mod, not only for the host.**
  Until now a joining player saw the world's shipped sky however far the host had changed the air.
  Their sky now follows the planet the host sends them, from the moment it arrives as they join, and
  the host's world decides whether the sky follows the air at all, for every player.
- **A joining player's outdoor temperature now matches the host's.** The host sends its world's
  greenhouse strength, air density strength and airless reflectivity with the planet, and a joining
  player's game works the temperature out with those. Before, it used the joining player's own
  config, so a host who had changed them for the world read a different temperature from everyone
  who joined.
- **Corrected: who can join.** Earlier versions said a player without this mod could still join a
  game that runs it. The connection is only allowed when both games also run another mod that
  syncs through StationeersLaunchPad; otherwise StationeersLaunchPad turns it away, and the same
  goes for a player with this mod joining a host without it. Have everyone install it, on the same
  version.

## 0.10.0

- **Storms now respond to what you have done to a world.** A world stops scheduling its own storms
  once you have taken away most of the air it started with, or once that air has become temperate,
  thick and clean. Neither rule does anything to a world you have not changed: an untouched world is
  at 100 % of its own starting air, and no world the game ships starts anywhere near mild, each one
  being far too cold, far too hot or far too thin. A storm already scheduled still arrives, one that
  is running is never cut short, and rain and snow are untouched.
- **Solar storms follow the opposite rule.** Taking the air away cannot stop radiation, so it never
  stops one. Mild air can, but only if you turn that on: on the Moon a solar storm is four times the
  normal solar power, and taking that away by default would be a surprise.
- **Every number either rule uses is a setting**, in a new `Storms` section, and there is no hidden
  rule: the share of the starting air below which storms stop, the coldest and hottest the air may
  get across a day, the pressure it must stay between, and the most toxic gas allowed. Like every
  setting that affects a world, each world keeps its own copy, changed with `terraform set`.
- **The mild rule is judged at the point in the orbit your world is at**, not year round, so a world
  near the edge of those bounds is calm in one season and stormy in another. `terraform` says which
  season it judged at, so an annual storm season is not a mystery.
- **`terraform` says why weather is or is not happening.** It never answers a bare no: every negative
  names the bound that failed, with the measured value and the bound beside it, and solar storms are
  reported separately because they follow the opposite rule.
- **Each world now keeps its own copy of every setting that affects it**, in a small
  `terraforming-reloaded.xml` beside its save: the pressure ceiling, both response strengths, the
  airless reflectivity, both added-heat settings, whether the sky follows the air, rain on worlds
  with no weather, and every storm setting. Until now one global config decided how every world
  behaved, so tuning it for a new save quietly changed, and in one case damaged, older ones.
- **The config now sets what a new world starts with, and nothing more.** Changing it no longer
  reaches a world you have already made, including the one you are playing. A world that recorded
  nothing for a setting takes it from the config.
- **An existing world loads with its pressure ceiling off, whatever your config says.** That is
  deliberate. A world made before this version carries no record of whether it ever had a ceiling,
  and applying your current one could delete most of its atmosphere in a day. Use
  `terraform set MaxPressureKPa <kPa> confirm` to set it for the world you are playing; the prompt
  tells you the planet's pressure now and how much air the first tick would delete.
- **New: `terraform set <key> <value>`.** Changes a setting for the world you are playing and no
  other, under the same key the config uses. `terraform set` alone lists them all. A change that
  deletes something for good asks first: setting or lowering the pressure ceiling, lowering the added
  heat limit, and making added heat fade sooner. Host only.
- `Sky follows the air` and the pressure ceiling no longer need a restart: the config only decides
  what a new world starts with.
- `terraform` reports which settings the world you are in is running on, and where they came from.
- Rain and snow now fall on worlds that ship with no weather of their own, such as Mimas, once you
  have given them enough air for clouds to fill. This was already possible and off by default; it is
  now on. The game saves no weather at all on such a world, so rain that is due or falling when you
  save is gone when you load; the water it came from is already back in the air, so nothing is lost. **Changing a default only reaches new installs**: BepInEx writes your config file the first
  time it runs, so an existing config keeps the old value until you change it yourself.
- Building into an occupied outdoor cell, and a cloud filling while other weather is already running,
  are both now covered by tests that can fail: each has a run that reproduces the defect with the fix
  taken out.
- Fixed: on a custom world whose planet declares no volume, the mod stood down correctly but said so
  three different wrong ways, one of them printing a pressure of NaN.
- **Custom planet size shows each preset's size**, greyed out, so you can see what Short, Standard, Long and
  Unmodded mean. It is only used when Planet size is Custom, and a size you typed there comes back when
  you choose Custom again.

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
