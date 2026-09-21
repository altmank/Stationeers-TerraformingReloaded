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
        public const string PluginVersion = "0.9.0";

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
                v => Settings.DynamicSky = v, null, "Sky follows the air", 1, null, true);

            ConfigEntry<PlanetSizePreset> preset = null;
            ConfigEntry<double> custom = null;
            Action applySize = () =>
            {
                if (preset == null || custom == null)
                {
                    return;
                }
                switch (preset.Value)
                {
                    case PlanetSizePreset.Short: Settings.PlanetSize = 0.01; break;
                    case PlanetSizePreset.Standard: Settings.PlanetSize = 0.05; break;
                    case PlanetSizePreset.Long: Settings.PlanetSize = 0.25; break;
                    case PlanetSizePreset.UnmoddedBaseline: Settings.PlanetSize = 1.0; break;
                    default: Settings.PlanetSize = custom.Value; break;
                }
            };
            preset = Bind("Pace", "PlanetSize", PlanetSizePreset.Standard,
                "How big the planet is, which sets how long terraforming takes and nothing else: the air, pressure and temperature you start with are the same at any size. "
                + "Hours are a mega base (four ice rockets mining most of the time) reaching air you can breathe without a suit on Mars; a base with one ice rocket is about five times slower. "
                + "Short: about 15 hours. Standard: about 75 hours. Long: about 375 hours. Unmodded baseline: the game's own size, about 1,500 hours. Custom: use the number below. "
                + "Applies when a planet is created: a new world, or after terraform reset confirm. A saved planet keeps its size; to change the one you are playing, without touching its air, use terraform size <share> confirm.",
                _ => applySize(), null, "Planet size", 5);
            custom = Bind("Pace", "CustomPlanetSize", 0.05,
                "Planet size as a share of the shipped planet, used when Planet size is Custom. 0.05 is one twentieth. Time to terraform scales in proportion.",
                _ => applySize(), new AcceptableValueRange<double>(0.0001, 10.0), "Custom planet size", 6, "%.4f");
            applySize();

            Bind("Climate", "GhgResponseScale", Settings.GhgResponseScale,
                "Strength of the greenhouse response on worlds that ship without one. 0 turns it off. Mars uses its own. On worlds that start hot under greenhouse air (Venus, Vulcan) the warming side is fixed by where the world starts and where bare rock would be, so this only changes their cooling side. Takes effect at once.",
                v => Settings.GhgResponseScale = v, new AcceptableValueRange<double>(0.0, 5.0), "Greenhouse strength", 10, "%.2f");
            Bind("Climate", "DensityResponseScale", Settings.DensityResponseScale,
                "How quickly thickening air evens out day and night on worlds that ship without a density response. 0 turns it off, above 1 it bites sooner. Air thick enough to end the swing ends it at any strength. Mars uses its own. Takes effect at once.",
                v => Settings.DensityResponseScale = v, new AcceptableValueRange<double>(0.0, 5.0), "Air density strength", 11, "%.2f");
            Bind("Climate", "AirlessAlbedo", Settings.AirlessAlbedo,
                "Share of sunlight an airless world (Moon, Mimas) reflects. Sets the temperature its first air settles toward: lower is warmer.",
                v => Settings.AirlessAlbedo = v, new AcceptableValueRange<double>(0.0, 0.95), "Airless world reflectivity", 12, "%.2f");
            Bind("Climate", "MaxPressureKPa", Settings.MaxPressureKPa,
                "Ceiling on the planet air pressure. 0 means no ceiling. DESTRUCTIVE: whenever the planet is above it, at its hottest hour, the excess air is deleted for good and the loss is saved. Set it below a world's starting pressure and most of its air is gone within a day.",
                v => Settings.MaxPressureKPa = v, new AcceptableValueRange<double>(0.0, 10000.0), "Pressure ceiling (kPa)", 13, "%.0f", restart: true);
            Bind("Climate", "WeatherOnWeatherlessWorlds", Settings.WeatherOnWeatherlessWorlds,
                "Allow rain and snow from filled clouds on worlds that ship with no weather at all.",
                v => Settings.WeatherOnWeatherlessWorlds = v, null, "Weather on worlds without any", 14);

            Bind("Heat", "ExternalHeatHalfLifeMinutes", Settings.ExternalHeatHalfLifeMinutes,
                "Heat your base and vented gas add to the planet fades like a planet radiating to space. Real-time minutes for it to halve. 0 never fades, which lets it build without limit.",
                v => Settings.ExternalHeatHalfLifeMinutes = v, new AcceptableValueRange<double>(0.0, 10000.0), "Added heat half-life (min)", 20, "%.0f");
            Bind("Heat", "MaxExternalOffsetKelvin", Settings.MaxExternalOffsetKelvin,
                "Most that added heat may shift the planet temperature, in kelvin, either way.",
                v => Settings.MaxExternalOffsetKelvin = v, new AcceptableValueRange<double>(0.0, 500.0), "Added heat limit (K)", 21, "%.0f");

            Bind("Multiplayer", "SyncIntervalSeconds", Settings.SyncIntervalSeconds,
                "How often the host sends the planet state to clients.",
                v => Settings.SyncIntervalSeconds = v, new AcceptableValueRange<double>(1.0, 120.0), "Sync interval (s)", 30, "%.0f");

            Bind("Diagnostics", "StatusLogSeconds", Settings.StatusLogSeconds,
                "Write the terraform status to the log this often, in seconds. 0 is off. The console command shows the same thing.",
                v => Settings.StatusLogSeconds = v, new AcceptableValueRange<double>(0.0, 3600.0), "Status to log every (s)", 40, "%.0f");
        }

        private ConfigEntry<T> Bind<T>(string section, string key, T fallback, string description, Action<T> apply,
            AcceptableValueBase range = null, string label = null, int order = 0, string format = null, bool restart = false)
        {
            var tags = new System.Collections.Generic.List<object> { new System.Collections.Generic.KeyValuePair<string, int>("Order", order) };
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
                entry.SettingChanged += (_, __) => apply(entry.Value);      // a restart setting really does wait for the restart
            }
            return entry;
        }
    }
}
