using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TerraformingReloaded.Patching;
using Util.Commands;

namespace TerraformingReloaded
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "xceled.stationeers.terraformingreloaded";
        public const string PluginName = "Terraforming Reloaded";
        public const string PluginVersion = "0.10.1";

        /// <summary>The game build the patches were last checked against. Advisory only.</summary>
        private const string TestedGameVersion = "0.2.6428.27798";

        private void Awake()
        {
            Log.InfoSink = Logger.LogInfo;
            Log.WarnSink = Logger.LogWarning;
            Log.ErrorSink = Logger.LogError;

            BindConfig();

            if (!Settings.Enabled)
            {
                Logger.LogInfo("Disabled in config; nothing patched.");
                return;
            }

            try
            {
                Climate.Load(Path.Combine(Paths.ConfigPath, "TerraformingReloaded.curves.xml"));
            }
            catch (Exception e)
            {
                Logger.LogError("Response curves failed to load; worlds without their own curves will not change temperature. " + e);
            }

            Patcher.Report report;
            try
            {
                report = Patcher.Apply(new Harmony(PluginGuid));
            }
            catch (Exception e)
            {
                Gate.Arm(false);
                Logger.LogError("Patching failed; the planet is left as shipped. " + e);
                return;
            }

            if (report.Armed)
            {
                Logger.LogInfo("Active: " + string.Join(", ", report.Applied));
            }

            try
            {
                string version = Assets.Scripts.GameManager.GetGameVersion();
                if (!string.IsNullOrEmpty(version) && !version.Contains(TestedGameVersion))
                {
                    Logger.LogInfo($"Game is {version}; last checked against {TestedGameVersion}. The patches verify themselves, so this is only a note.");
                }
            }
            catch (Exception)
            {
                // A version string is not worth failing over.
            }

            try
            {
                RegisterSync();
            }
            catch (Exception e)
            {
                Logger.LogWarning("Multiplayer sync is off: this StationeersLaunchPad has no mod networking. Single player and hosting are unaffected; clients will not see the changed planet. " + e.Message);
            }

            try
            {
                TerraformCommand.CurvesPath = Path.Combine(Paths.ConfigPath, "TerraformingReloaded.curves.xml");
                CommandLine.AddCommand("terraform", new TerraformCommand());
            }
            catch (Exception e)
            {
                Logger.LogWarning("Console command not registered: " + e.Message);
            }
        }

        private float _nextStatus;
        private bool _statusFailed;

        /// <summary>
        /// Status to the log on a timer, for diagnosing. On the main thread and independent of the
        /// game tick on purpose: it still reports when the simulation is paused or the planet is off,
        /// which is exactly when someone needs it.
        /// </summary>
        private void Update()
        {
            double seconds = Settings.StatusLogSeconds;
            if (seconds <= 0.0 || _statusFailed || UnityEngine.Time.unscaledTime < _nextStatus)
            {
                return;
            }
            _nextStatus = UnityEngine.Time.unscaledTime + (float)Math.Max(1.0, seconds);
            try
            {
                Logger.LogInfo(TerraformCommand.Status());
            }
            catch (Exception e)
            {
                _statusFailed = true;
                Logger.LogWarning("Status readout failed and is now off: " + e);
            }
        }

        // Its own method, never inlined: the LaunchPadBooster types resolve when this is compiled,
        // which is what lets Awake catch their absence.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterSync() => Sync.Register(PluginName, PluginVersion);

        private void BindConfig()
        {
            // StationeersLaunchPad's in-game config editor shows every entry bound here. It reads the
            // value range for bounds, and these tags for the label, the order, the number format and
            // whether to warn that a restart is needed. They are plain BCL pairs, so nothing here
            // depends on LaunchPad.
            Bind("General", "Enabled", Settings.Enabled, "Master switch. Off leaves the game exactly as shipped.",
                v => Settings.Enabled = v, null, "Enabled", 0, null, true);
            Bind("General", "DynamicSky", Settings.DynamicSky, "Let the sky thin and thicken with the planet air.",
                v => Settings.DynamicSky = v, null, "Sky follows the air", 1, world: true);

            // The size slider always shows the size in force, so a player can see what each preset
            // means, and is greyed out and ignored unless the preset is Custom. Showing a preset's size
            // means writing it into the slider's entry, and BepInEx saves every write, so the share a
            // player typed for Custom is kept in a hidden entry of its own and put back when they
            // choose Custom again.
            ConfigEntry<PlanetSizePreset> preset = null;
            ConfigEntry<double> custom = null;
            ConfigEntry<double> typed = null;
            Action<bool> dimCustom = null;
            bool showing = false;
            Action applySize = () =>
            {
                if (preset == null || custom == null || typed == null || showing)
                {
                    return;
                }
                bool isCustom = preset.Value == PlanetSizePreset.Custom;
                double size = isCustom ? typed.Value : PresetSize(preset.Value);
                showing = true;
                try
                {
                    if (custom.Value != size)
                    {
                        custom.Value = size;
                    }
                }
                finally
                {
                    showing = false;
                }
                Settings.PlanetSize = size;
                dimCustom?.Invoke(!isCustom);
            };
            preset = Bind("Pace", "PlanetSize", PlanetSizePreset.Standard,
                "How big the planet is, which sets how long terraforming takes and nothing else: the air, pressure and temperature you start with are the same at any size.",
                _ => applySize(), null, "Planet size", 5);
            custom = Bind("Pace", "CustomPlanetSize", 0.05,
                "Planet size as a share of the shipped planet. Shows the size of the preset chosen above; used when Planet size is Custom. 0.05 is one twentieth. Time to terraform scales in proportion.",
                v =>
                {
                    // Only a player's own edit, made while Custom is chosen, is theirs to keep.
                    if (!showing && typed != null && preset != null && preset.Value == PlanetSizePreset.Custom)
                    {
                        typed.Value = v;
                    }
                    applySize();
                },
                new AcceptableValueRange<double>(0.0001, 10.0), "Custom planet size", 6, "%.4f",
                disabled: preset.Value != PlanetSizePreset.Custom);
            // Seeded from the slider on the first start of this version, which is the best record of
            // what the player last had there, so an existing custom size carries over.
            typed = Bind("Pace", "CustomPlanetSizeTyped", custom.Value,
                "The share typed for Custom, kept while a preset is shown in Custom planet size.",
                _ => applySize(), new AcceptableValueRange<double>(0.0001, 10.0), visible: false);
            dimCustom = Dimmer(custom);
            applySize();

            Bind("Climate", "GhgResponseScale", Settings.GhgResponseScale,
                "Strength of the greenhouse response on worlds that ship without one. 0 turns it off. Mars is not affected by this. On worlds that start hot under greenhouse air (Venus, Vulcan) the warming side is fixed by where the world starts and where bare rock would be, so this only changes their cooling side.",
                v => Settings.GhgResponseScale = v,
                Bounds(Limits.GhgResponseScale), "Greenhouse strength", 10, "%.2f", world: true);
            Bind("Climate", "DensityResponseScale", Settings.DensityResponseScale,
                "How quickly thickening air evens out day and night on worlds that ship without a density response. 0 turns it off, above 1 it bites sooner. Air thick enough to end the swing ends it at any strength. Mars is not affected by this.",
                v => Settings.DensityResponseScale = v,
                Bounds(Limits.DensityResponseScale), "Air density strength", 11, "%.2f", world: true);
            Bind("Climate", "AirlessAlbedo", Settings.AirlessAlbedo,
                "Share of sunlight an airless world (Moon, Mimas) reflects. Sets the temperature its first air settles toward: lower is warmer.",
                v => Settings.AirlessAlbedo = v,
                Bounds(Limits.AirlessAlbedo), "Airless world reflectivity", 12, "%.2f", world: true);
            Bind("Climate", "MaxPressureKPa", Settings.MaxPressureKPa,
                "Ceiling on the planet air pressure. 0 means no ceiling. DESTRUCTIVE: whenever a planet is above its ceiling, at its hottest hour, the excess air is deleted for good and the loss is saved. Set it below a world's starting pressure and most of its air is gone within a day.",
                v => Settings.MaxPressureKPa = v, Bounds(Limits.MaxPressureKPa), "Pressure ceiling (kPa)", 13, "%.0f", world: true);
            Bind("Climate", "WeatherOnWeatherlessWorlds", Settings.WeatherOnWeatherlessWorlds,
                "Let filled clouds rain and snow on worlds that ship with no weather of their own, such as Mimas. Clouds only fill once you have given the world air.",
                v => Settings.WeatherOnWeatherlessWorlds = v, null, "Rain or snow on worlds with no weather", 14, world: true);

            Bind("Heat", "ExternalHeatHalfLifeMinutes", Settings.ExternalHeatHalfLifeMinutes,
                "Heat your base and vented gas add to the planet fades like a planet radiating to space. Real-time minutes for it to halve. 0 never fades, which lets it build without limit.",
                v => Settings.ExternalHeatHalfLifeMinutes = v,
                Bounds(Limits.ExternalHeatHalfLifeMinutes), "Added heat half-life (min)", 20, "%.0f", world: true);
            Bind("Heat", "MaxExternalOffsetKelvin", Settings.MaxExternalOffsetKelvin,
                "Most that added heat may shift the planet temperature, in kelvin, either way.",
                v => Settings.MaxExternalOffsetKelvin = v,
                Bounds(Limits.MaxExternalOffsetKelvin), "Added heat limit (K)", 21, "%.0f", world: true);

            // Storms. Two independent rules, either of which stops a world scheduling its own storm,
            // and every threshold one of them uses (docs/STORMS.md). Their own section because Climate
            // already carries five settings and nine more would swamp it.
            Bind("Storms", "StormsStopWhenStripped", Settings.StormsStopWhenStripped,
                "Once most of a world's starting air is gone, it stops scheduling its own storm. Does not apply to solar storms.",
                v => Settings.StormsStopWhenStripped = v, null, "Stripping the air stops storms", 50, world: true);
            Bind("Storms", "StrippedAtmosphereShare", Settings.StrippedAtmosphereShare,
                "Share of the air the world started with, below which storms stop. 0 means every last mole.",
                v => Settings.StrippedAtmosphereShare = v, Bounds(Limits.StrippedAtmosphereShare), "Stripped below (% of start)", 51, "%.1f", world: true);
            Bind("Storms", "StormsStopWhenAtmosphereIsMild", Settings.StormsStopWhenAtmosphereIsMild,
                "A world whose air is temperate, thick and clean stops scheduling its own storm.",
                v => Settings.StormsStopWhenAtmosphereIsMild = v, null, "Mild air stops storms", 52, world: true);
            Bind("Storms", "MildAtmosphereColdestKelvin", Settings.MildAtmosphereColdestKelvin,
                "Coldest the air may get across a day, in kelvin, and still count as mild.",
                v => Settings.MildAtmosphereColdestKelvin = v, Bounds(Limits.MildAtmosphereColdestKelvin), "Coldest air (K)", 53, "%.2f", world: true);
            Bind("Storms", "MildAtmosphereHottestKelvin", Settings.MildAtmosphereHottestKelvin,
                "Hottest the air may get across a day, in kelvin, and still count as mild.",
                v => Settings.MildAtmosphereHottestKelvin = v, Bounds(Limits.MildAtmosphereHottestKelvin), "Hottest air (K)", 54, "%.2f", world: true);
            Bind("Storms", "MildAtmosphereMinPressureKpa", Settings.MildAtmosphereMinPressureKpa,
                "Least air pressure that counts as mild.",
                v => Settings.MildAtmosphereMinPressureKpa = v, Bounds(Limits.MildAtmosphereMinPressureKpa), "Minimum pressure (kPa)", 55, "%.2f", world: true);
            Bind("Storms", "MildAtmosphereMaxPressureKpa", Settings.MildAtmosphereMaxPressureKpa,
                "Most air pressure that counts as mild.",
                v => Settings.MildAtmosphereMaxPressureKpa = v, Bounds(Limits.MildAtmosphereMaxPressureKpa), "Maximum pressure (kPa)", 56, "%.2f", world: true);
            Bind("Storms", "MildAtmosphereMaxToxinsKpa", Settings.MildAtmosphereMaxToxinsKpa,
                "Most toxic gas allowed, in kilopascals, measured at the hottest point of the day.",
                v => Settings.MildAtmosphereMaxToxinsKpa = v, Bounds(Limits.MildAtmosphereMaxToxinsKpa), "Most toxins (kPa)", 57, "%.2f", world: true);
            Bind("Storms", "MildAtmosphereStopsSolarStorms", Settings.MildAtmosphereStopsSolarStorms,
                "Air shields radiation, so a mild world stops solar storms as well. Off by default: on the Moon they give four times normal solar power.",
                v => Settings.MildAtmosphereStopsSolarStorms = v, null, "Mild air stops solar storms too", 58, world: true);

            Bind("Multiplayer", "SyncIntervalSeconds", Settings.SyncIntervalSeconds,
                "How often the host sends the planet state to clients.",
                v => Settings.SyncIntervalSeconds = v, new AcceptableValueRange<double>(1.0, 120.0), "Sync interval (s)", 30, "%.0f");

            Bind("Diagnostics", "StatusLogSeconds", Settings.StatusLogSeconds,
                "Write the terraform status to the log this often, in seconds. 0 is off. The console command shows the same thing.",
                v => Settings.StatusLogSeconds = v, new AcceptableValueRange<double>(0.0, 3600.0), "Status to log every (s)", 40, "%.0f");

            // The values in force start as the config, because no world is being played yet. The
            // pressure ceiling is not seeded, and cannot be: a ceiling belongs to a world.
            Sidecar.SeedFromConfig();
        }

        /// <summary>
        /// The config editor's bounds for a setting, built from the one declaration in
        /// <see cref="Limits"/>. The same numbers check what comes out of a world's settings file,
        /// so a hand-edited file cannot hold a value the editor itself would refuse, and changing a
        /// range in one place cannot leave the other behind.
        /// </summary>
        private static AcceptableValueRange<double> Bounds(Range limits)
        {
            return new AcceptableValueRange<double>(limits.Min, limits.Max);
        }

        private ConfigEntry<T> Bind<T>(string section, string key, T fallback, string description, Action<T> apply,
            AcceptableValueBase range = null, string label = null, int order = 0, string format = null, bool restart = false,
            bool? disabled = null, bool world = false, bool visible = true)
        {
            if (world)
            {
                // Said in the entry itself, because the config editor is where a player would
                // otherwise expect a change to reach the world they are in.
                description += " This is what a NEW world starts with. Each world keeps its own; to change the world you are playing, use terraform set " + key + " <value>.";
            }
            var tags = new System.Collections.Generic.List<object> { new System.Collections.Generic.KeyValuePair<string, int>("Order", order) };
            if (disabled.HasValue)
            {
                tags.Add(new System.Collections.Generic.KeyValuePair<string, bool>("Disabled", disabled.Value));
            }
            if (!visible)
            {
                tags.Add(new System.Collections.Generic.KeyValuePair<string, bool>("Visible", false));
            }
            if (label != null)
            {
                tags.Add(new System.Collections.Generic.KeyValuePair<string, string>("DisplayName", label));
            }
            if (format != null)
            {
                tags.Add(new System.Collections.Generic.KeyValuePair<string, string>("Format", format));
            }
            if (restart)
            {
                tags.Add(new System.Collections.Generic.KeyValuePair<string, bool>("RequireRestart", true));
                description += " Needs a restart.";
            }
            ConfigEntry<T> entry = Config.Bind(new ConfigDefinition(section, key), fallback, new ConfigDescription(description, range, tags.ToArray()));
            apply(entry.Value);
            if (!restart)
            {
                // A restart setting really does wait for the restart. A world setting reaches the
                // values in force only while no world is being played (Sidecar.ConfigEdited).
                entry.SettingChanged += (_, __) =>
                {
                    apply(entry.Value);
                    if (world)
                    {
                        Sidecar.ConfigEdited();
                    }
                };
            }
            return entry;
        }

        private static double PresetSize(PlanetSizePreset preset)
        {
            switch (preset)
            {
                case PlanetSizePreset.Short: return 0.01;
                case PlanetSizePreset.Long: return 0.25;
                case PlanetSizePreset.UnmoddedBaseline: return 1.0;
                default: return 0.05;
            }
        }

        /// <summary>
        /// Greys an entry out in the config editor, and lets that be changed while the game runs.
        /// StationeersLaunchPad reads a "Disabled" tag when it builds its rows, and rebuilds them
        /// whenever any setting changes, so writing the tag is enough; the array it reads is the one
        /// passed to ConfigDescription, which keeps the reference. An entry bound without the tag
        /// gets a switch that does nothing, so a LaunchPad that ignores the tag costs nothing either:
        /// the setting still has no effect, it just is not shown as having none.
        /// </summary>
        private static Action<bool> Dimmer(ConfigEntryBase entry)
        {
            object[] tags = entry.Description.Tags;
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i] is System.Collections.Generic.KeyValuePair<string, bool> pair && pair.Key == "Disabled")
                {
                    int slot = i;
                    return on => tags[slot] = new System.Collections.Generic.KeyValuePair<string, bool>("Disabled", on);
                }
            }
            return _ => { };
        }
    }
}
