# Settings

Every config entry the mod binds. Keep it in step with `src/Plugin.cs` and `src/Settings.cs`.

Settings show in the StationeersLaunchPad config editor with these labels, and are also in
`BepInEx/config/xceled.stationeers.terraformingreloaded.cfg`.

**Read from** is where the value that actually matters comes from:

- **Config** means the mod reads the setting itself, so the config is the thing to change.
- **The world** means each world keeps its own copy, in `terraforming-reloaded.xml` beside its save.
  The config only decides what a new world starts with, and fills in anything the world's file does
  not record. Changing the config never reaches a world being played: `terraform set <Key> <value>`
  changes the world you are in. See [SIDECAR.md](SIDECAR.md).
- **The save** means the value was baked into the world's own data when its planet was created.
  The console changes the one you are playing.

Every setting read from the world has this sentence added to the end of its description in the
config editor: *This is what a NEW world starts with. Each world keeps its own; to change the world
you are playing, use terraform set &lt;Key&gt; &lt;value&gt;.*

| Section | Key | Label | Description | Default | Read from | Can you change it mid-game? |
| --- | --- | --- | --- | --- | --- | --- |
| General | Enabled | Enabled | Master switch. Off leaves the game exactly as shipped. Needs a restart. | true | Config | No, it waits for a restart |
| General | DynamicSky | Sky follows the air | Let the sky thin and thicken with the planet air. | true | The world | Yes, with `terraform set DynamicSky <on/off>`. Switched off, the sky keeps its look until the world is loaded again |
| Pace | PlanetSize | Planet size | How big the planet is, which sets how long terraforming takes and nothing else: the air, pressure and temperature you start with are the same at any size. | Standard | The save | Yes, with `terraform size <share> confirm`, which rescales the planet in place |
| Pace | CustomPlanetSize | Custom planet size | Planet size as a share of the shipped planet. Shows the size of the preset chosen above; used when Planet size is Custom. 0.05 is one twentieth. Time to terraform scales in proportion. | 0.05 | The save | Yes, with `terraform size <share> confirm` |
| Climate | GhgResponseScale | Greenhouse strength | Strength of the greenhouse response on worlds that ship without one. 0 turns it off. Mars is not affected by this. On worlds that start hot under greenhouse air (Venus, Vulcan) the warming side is fixed by where the world starts and where bare rock would be, so this only changes their cooling side. | 1.0 | The world | Yes, with `terraform set GhgResponseScale <value>` |
| Climate | DensityResponseScale | Air density strength | How quickly thickening air evens out day and night on worlds that ship without a density response. 0 turns it off, above 1 it bites sooner. Air thick enough to end the swing ends it at any strength. Mars is not affected by this. | 1.0 | The world | Yes, with `terraform set DensityResponseScale <value>` |
| Climate | AirlessAlbedo | Airless world reflectivity | Share of sunlight an airless world (Moon, Mimas) reflects. Sets the temperature its first air settles toward: lower is warmer. | 0.3 | The world | Yes, with `terraform set AirlessAlbedo <value>` |
| Climate | MaxPressureKPa | Pressure ceiling (kPa) | Ceiling on the planet air pressure. 0 means no ceiling. DESTRUCTIVE: whenever a planet is above its ceiling, at its hottest hour, the excess air is deleted for good and the loss is saved. Set it below a world's starting pressure and most of its air is gone within a day. | 0.0 | The world | Yes, with `terraform set MaxPressureKPa <kPa>`. Setting or lowering a ceiling asks for `confirm` first |
| Climate | WeatherOnWeatherlessWorlds | Rain or snow on worlds with no weather | Let filled clouds rain and snow on worlds that ship with no weather of their own, such as Mimas. Clouds only fill once you have given the world air. | true | The world | Yes, with `terraform set WeatherOnWeatherlessWorlds <on/off>` |
| Heat | ExternalHeatHalfLifeMinutes | Added heat half-life (min) | Heat your base and vented gas add to the planet fades like a planet radiating to space. Real-time minutes for it to halve. 0 never fades, which lets it build without limit. | 60.0 | The world | Yes, with `terraform set ExternalHeatHalfLifeMinutes <min>`. A shorter half-life asks for `confirm` first |
| Heat | MaxExternalOffsetKelvin | Added heat limit (K) | Most that added heat may shift the planet temperature, in kelvin, either way. | 50.0 | The world | Yes, with `terraform set MaxExternalOffsetKelvin <K>`. A lower limit asks for `confirm` first |
| Storms | StormsStopWhenStripped | Stripping the air stops storms | Once most of a world's starting air is gone, it stops scheduling its own storm. Does not apply to solar storms. | true | The world | Yes, with `terraform set StormsStopWhenStripped <on/off>` |
| Storms | StrippedAtmosphereShare | Stripped below (% of start) | Share of the air the world started with, below which storms stop. 0 means every last mole. | 5.0 | The world | Yes, with `terraform set StrippedAtmosphereShare <value>` |
| Storms | StormsStopWhenAtmosphereIsMild | Mild air stops storms | A world whose air is temperate, thick and clean stops scheduling its own storm. | true | The world | Yes, with `terraform set StormsStopWhenAtmosphereIsMild <on/off>` |
| Storms | MildAtmosphereColdestKelvin | Coldest air (K) | Coldest the air may get across a day, in kelvin, and still count as mild. | 263.15 | The world | Yes, with `terraform set MildAtmosphereColdestKelvin <K>` |
| Storms | MildAtmosphereHottestKelvin | Hottest air (K) | Hottest the air may get across a day, in kelvin, and still count as mild. | 323.15 | The world | Yes, with `terraform set MildAtmosphereHottestKelvin <K>` |
| Storms | MildAtmosphereMinPressureKpa | Minimum pressure (kPa) | Least air pressure that counts as mild. | 20.0 | The world | Yes, with `terraform set MildAtmosphereMinPressureKpa <kPa>` |
| Storms | MildAtmosphereMaxPressureKpa | Maximum pressure (kPa) | Most air pressure that counts as mild. | 607.95 | The world | Yes, with `terraform set MildAtmosphereMaxPressureKpa <kPa>` |
| Storms | MildAtmosphereMaxToxinsKpa | Most toxins (kPa) | Most toxic gas allowed, in kilopascals, measured at the hottest point of the day. | 1.0 | The world | Yes, with `terraform set MildAtmosphereMaxToxinsKpa <kPa>` |
| Storms | MildAtmosphereStopsSolarStorms | Mild air stops solar storms too | Air shields radiation, so a mild world stops solar storms as well. Off by default: on the Moon they give four times normal solar power. | false | The world | Yes, with `terraform set MildAtmosphereStopsSolarStorms <on/off>` |
| Trace gases | TraceGasGatheringEnabled | Trace gases gather (experimental) | EXPERIMENTAL, off by default. A gas the planet's air holds only a trace of gathers in the outdoor air beside your base, where it burns off or reaches your intakes, instead of lingering thinly over the whole planet for hours. Every mole comes out of the planet's air; nothing is created. The two settings below tune it. | false | The world | Yes, with `terraform set TraceGasGatheringEnabled <on/off>` |
| Trace gases | TraceGasGathering | Trace gases gather (x) | When trace gases gather, a gas just under the trace line is drawn into the outdoor air beside your base this many times faster than any other gas, and twice as fast again for every factor of ten it sits further below, up to 2000 times. 1 turns it off. | 200.0 | The world | Yes, with `terraform set TraceGasGathering <value>` |
| Trace gases | TraceGasLine | Trace below (mol per cell) | Below how much a gas counts as a trace, in moles per outdoor cell (8,000 litres) of the planet's air. The default is a hundred times the least the game keeps in a cell, and well under the thinnest gas any world starts with. 0 turns gathering off. | 0.001 | The world | Yes, with `terraform set TraceGasLine <mol>` |
| Multiplayer | SyncIntervalSeconds | Sync interval (s) | How often the host sends the planet state to clients. | 5.0 | Config | Yes, at once |
| Diagnostics | StatusLogSeconds | Status to log every (s) | Write the terraform status to the log this often, in seconds. 0 is off. The console command shows the same thing. | 0.0 | Config | Yes, at once |
