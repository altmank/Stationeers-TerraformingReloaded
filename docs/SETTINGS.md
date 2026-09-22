# Settings

Every config entry the mod binds, exactly as it is in `src/Plugin.cs` today. Generated from the
source, not retyped. Descriptions are the full strings the config editor shows.

| Section | Key | Label | Description | Default |
| --- | --- | --- | --- | --- |
| General | Enabled | Enabled | Master switch. Off leaves the game exactly as shipped. | true |
| General | DynamicSky | Sky follows the air | Let the sky thin and thicken with the planet air. | true |
| Pace | PlanetSize | Planet size | How big the planet is, which sets how long terraforming takes and nothing else: the air, pressure and temperature you start with are the same at any size. Hours are a mega base (four ice rockets mining most of the time) reaching air you can breathe without a suit on Mars; a base with one ice rocket is about five times slower. Short: about 15 hours. Standard: about 75 hours. Long: about 375 hours. Unmodded baseline: the game's own size, about 1,500 hours. Custom: use the number below. Applies when a planet is created: a new world, or after terraform reset confirm. A saved planet keeps its size; to change the one you are playing, without touching its air, use terraform size <share> confirm. | Standard |
| Pace | CustomPlanetSize | Custom planet size | Planet size as a share of the shipped planet, used when Planet size is Custom. 0.05 is one twentieth. Time to terraform scales in proportion. | 0.05 |
| Climate | GhgResponseScale | Greenhouse strength | Strength of the greenhouse response on worlds that ship without one. 0 turns it off. Mars uses its own. On worlds that start hot under greenhouse air (Venus, Vulcan) the warming side is fixed by where the world starts and where bare rock would be, so this only changes their cooling side. Takes effect at once. | 1.0 |
| Climate | DensityResponseScale | Air density strength | How quickly thickening air evens out day and night on worlds that ship without a density response. 0 turns it off, above 1 it bites sooner. Air thick enough to end the swing ends it at any strength. Mars uses its own. Takes effect at once. | 1.0 |
| Climate | AirlessAlbedo | Airless world reflectivity | Share of sunlight an airless world (Moon, Mimas) reflects. Sets the temperature its first air settles toward: lower is warmer. | 0.3 |
| Climate | MaxPressureKPa | Pressure ceiling (kPa) | Ceiling on the planet air pressure. 0 means no ceiling. DESTRUCTIVE: whenever the planet is above it, at its hottest hour, the excess air is deleted for good and the loss is saved. Set it below a world's starting pressure and most of its air is gone within a day. | 0.0 |
| Climate | WeatherOnWeatherlessWorlds | Weather on worlds without any | Allow rain and snow from filled clouds on worlds that ship with no weather at all. | false |
| Heat | ExternalHeatHalfLifeMinutes | Added heat half-life (min) | Heat your base and vented gas add to the planet fades like a planet radiating to space. Real-time minutes for it to halve. 0 never fades, which lets it build without limit. | 60.0 |
| Heat | MaxExternalOffsetKelvin | Added heat limit (K) | Most that added heat may shift the planet temperature, in kelvin, either way. | 50.0 |
| Multiplayer | SyncIntervalSeconds | Sync interval (s) | How often the host sends the planet state to clients. | 5.0 |
| Diagnostics | StatusLogSeconds | Status to log every (s) | Write the terraform status to the log this often, in seconds. 0 is off. The console command shows the same thing. | 0.0 |
