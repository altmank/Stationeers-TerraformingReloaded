# Settings

Every config entry the mod binds. Keep it in step with `src/Plugin.cs` and `src/Settings.cs`.

Settings show in the StationeersLaunchPad config editor with these labels, and are also in
`BepInEx/config/xceled.stationeers.terraformingreloaded.cfg`. That editor opens without leaving your
game, so anything answered *yes, at once* can be changed while you play.

**Read from** is where the value that actually matters comes from:

- **Config** means the mod reads the setting itself, so the config is the thing to change.
- **The world** means each world keeps its own copy, in `terraforming-reloaded.xml` beside its save.
  The config sets what a new world starts with; changing it afterwards does not reach into worlds
  that already exist. While you are playing, editing one of these still takes effect at once and is
  recorded into that world's file. See [SIDECAR.md](SIDECAR.md).
- **The save** means the value was baked into the world's own data when its planet was created.
  The console changes the one you are playing.

| Section | Key | Label | Description | Default | Read from | Can you change it mid-game? |
| --- | --- | --- | --- | --- | --- | --- |
| General | Enabled | Enabled | Master switch. Off leaves the game exactly as shipped. | true | Config | No, it waits for a restart |
| General | DynamicSky | Sky follows the air | Let the sky thin and thicken with the planet air. | true | Config | No, it waits for a restart |
| Pace | PlanetSize | Planet size | How big the planet is, which sets how long terraforming takes and nothing else: the air, pressure and temperature you start with are the same at any size. | Standard | The save | Yes, with `terraform size <share> confirm`, which rescales the planet in place |
| Pace | CustomPlanetSize | Custom planet size | Planet size as a share of the shipped planet, used when Planet size is Custom. 0.05 is one twentieth. Time to terraform scales in proportion. | 0.05 | The save | Yes, with `terraform size <share> confirm` |
| Climate | GhgResponseScale | Greenhouse strength | Strength of the greenhouse response on worlds that ship without one. 0 turns it off. Mars is not affected by this. On worlds that start hot under greenhouse air (Venus, Vulcan) the warming side is fixed by where the world starts and where bare rock would be, so this only changes their cooling side. | 1.0 | The world | Yes, at once |
| Climate | DensityResponseScale | Air density strength | How quickly thickening air evens out day and night on worlds that ship without a density response. 0 turns it off, above 1 it bites sooner. Air thick enough to end the swing ends it at any strength. Mars is not affected by this. | 1.0 | The world | Yes, at once |
| Climate | AirlessAlbedo | Airless world reflectivity | Share of sunlight an airless world (Moon, Mimas) reflects. Sets the temperature its first air settles toward: lower is warmer. | 0.3 | The world | Yes, at once |
| Climate | MaxPressureKPa | Pressure ceiling (kPa) | Ceiling on the planet air pressure for a NEW world. 0 means no ceiling. Each world then keeps its own; to change the one you are playing, use terraform ceiling &lt;kPa&gt; confirm. DESTRUCTIVE: whenever a planet is above its ceiling, at its hottest hour, the excess air is deleted for good and the loss is saved. Set it below a world's starting pressure and most of its air is gone within a day. | 0.0 | The world | Yes, with `terraform ceiling <kPa> confirm` |
| Climate | WeatherOnWeatherlessWorlds | Rain or snow on worlds with no weather | Let filled clouds rain and snow on worlds that ship with no weather of their own, such as Mimas. Clouds only fill once you have given the world air. | true | Config | Yes, at once |
| Heat | ExternalHeatHalfLifeMinutes | Added heat half-life (min) | Heat your base and vented gas add to the planet fades like a planet radiating to space. Real-time minutes for it to halve. 0 never fades, which lets it build without limit. | 60.0 | The world | Yes, at once |
| Heat | MaxExternalOffsetKelvin | Added heat limit (K) | Most that added heat may shift the planet temperature, in kelvin, either way. | 50.0 | The world | Yes, at once |
| Storms | StormsStopWhenStripped | Stripping the air stops storms | Once most of a world's starting air is gone, it stops scheduling its own storm. Does not apply to solar storms. | true | Config | **Not yet.** Bound, but nothing reads it |
| Storms | StrippedAtmosphereShare | Stripped below (% of start) | Share of the air the world started with, below which storms stop. 0 means every last mole. | 5.0 | Config | **Not yet.** Bound, but nothing reads it |
| Storms | StormsStopWhenAtmosphereIsMild | Mild air stops storms | A world whose air is temperate, thick and clean stops scheduling its own storm. | true | Config | **Not yet.** Bound, but nothing reads it |
| Storms | MildAtmosphereColdestKelvin | Coldest air (K) | Coldest the air may get across a day, in kelvin, and still count as mild. | 263.15 | Config | **Not yet.** Bound, but nothing reads it |
| Storms | MildAtmosphereHottestKelvin | Hottest air (K) | Hottest the air may get across a day, in kelvin, and still count as mild. | 323.15 | Config | **Not yet.** Bound, but nothing reads it |
| Storms | MildAtmosphereMinPressureKpa | Minimum pressure (kPa) | Least air pressure that counts as mild. | 20.0 | Config | **Not yet.** Bound, but nothing reads it |
| Storms | MildAtmosphereMaxPressureKpa | Maximum pressure (kPa) | Most air pressure that counts as mild. | 607.95 | Config | **Not yet.** Bound, but nothing reads it |
| Storms | MildAtmosphereMaxToxinsKpa | Most toxins (kPa) | Most toxic gas allowed, in kilopascals, measured at the hottest point of the day. | 1.0 | Config | **Not yet.** Bound, but nothing reads it |
| Storms | MildAtmosphereStopsSolarStorms | Mild air stops solar storms too | Air shields radiation, so a mild world stops solar storms as well. Off by default: on the Moon they give four times normal solar power. | false | Config | **Not yet.** Bound, but nothing reads it |
| Multiplayer | SyncIntervalSeconds | Sync interval (s) | How often the host sends the planet state to clients. | 5.0 | Config | Yes, at once |
| Diagnostics | StatusLogSeconds | Status to log every (s) | Write the terraform status to the log this often, in seconds. 0 is off. The console command shows the same thing. | 0.0 | Config | Yes, at once |
