using System;
using System.Globalization;
using System.IO;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;
using TerraformingReloaded.Patching;
using Util.Commands;
using Weather;

namespace TerraformingReloaded
{
    /// <summary>Console command: what the planet holds and what the mod is doing about it.</summary>
    public sealed class TerraformCommand : CommandBase
    {
        public override string HelpText => "Shows the planet atmosphere and the state of Terraforming Reloaded. 'set' lists the settings the world you are playing keeps for itself, and 'set <key> <value>' changes one for this world only; the config only decides what a new world starts with (host only). 'size <share> confirm' changes how big the planet you are playing is, and so how long terraforming it takes, without touching its air (host only). 'reset confirm' puts the whole planet back as the world ships, which is also how to remove the mod cleanly (host only). 'curves export' and 'curves reload' are for tuning the temperature response.";

        public override string[] Arguments => new[] { "[status | set [<key> [<value> [confirm]]] | size <share> confirm | reset confirm | curves export | curves reload]" };

        public override bool IsLaunchCmd => false;

        public override CommandScope Scope => CommandScope.InGame;

        public override string Execute(string[] args)
        {
            if (!EnforceScope("terraform"))
            {
                return null;
            }
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            switch (verb)
            {
                case "status":
                    return Status();
                case "size":
                    return Size(args);
                case "set":
                    return Set(args);
                case "reset":
                    return Reset(args.Length > 1 && args[1].ToLowerInvariant() == "confirm");
                case "curves":
                    return Curves(args.Length > 1 ? args[1].ToLowerInvariant() : "");
                default:
                    return "Invalid syntax";
            }
        }

        /// <summary>Where a player-edited curves file lives. Set by the plugin.</summary>
        public static string CurvesPath;

        private static string Curves(string verb)
        {
            if (string.IsNullOrEmpty(CurvesPath))
            {
                return "Curves file location is unknown.";
            }
            switch (verb)
            {
                case "export":
                    // Never over a file someone may have spent an evening on.
                    if (File.Exists(CurvesPath))
                    {
                        return "Already there, left alone: " + CurvesPath;
                    }
                    File.WriteAllText(CurvesPath, Climate.DefaultXml);
                    return "Wrote the built-in curves to " + CurvesPath + ". Edit it, then: terraform curves reload";
                case "reload":
                    try
                    {
                        Climate.Load(CurvesPath);
                    }
                    catch (Exception e)
                    {
                        return "Curves not loaded: " + e.Message;
                    }
                    return File.Exists(CurvesPath)
                        ? "Loaded " + CurvesPath + ". If it could not be read, the log says why and the built-in curves are in use."
                        : "No file at " + CurvesPath + ", so the built-in curves are in use. Create one with: terraform curves export";
                default:
                    return "terraform curves export | reload. See CURVES.md in the mod folder.";
            }
        }

        // The range the CustomPlanetSize setting accepts, so the two ways to size a planet agree.
        private const double MinShare = 0.0001;
        private const double MaxShare = 10.0;

        /// <summary>
        /// terraform size &lt;share&gt; confirm: rescales the planet being played to that share of the
        /// size this world ships at. Planet size decides how long terraforming takes, and a player
        /// picks the setting before they have any feel for what it means; this is how to change their
        /// mind without throwing away what they have done, which terraform reset confirm would.
        ///
        /// The setting is deliberately left alone. It is the default for a new world; a save owns its
        /// own size, and making the setting apply live would rescale every save a player loads.
        /// </summary>
        private static string Size(string[] args)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (NetworkManager.IsClient)
            {
                return "Can only be run on the server";
            }
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            double shipped = WorldSetting.Current?.Data?.GlobalAtmosphereData?.Volume?.Value ?? 0.0;
            if (tank == null || !(shipped > 0.0))
            {
                return "No planet loaded, so there is no size to change.";
            }
            double now = tank.Volume.ToDouble() / shipped;
            if (args.Length < 2)
            {
                return string.Format(c, "This planet is {0:0.####} of the size this world ships at. To change it: terraform size <share> confirm, where <share> is between {1:0.####} and {2:0.####} (0.05 is a twentieth of the shipped planet, and takes a twentieth as long to terraform).",
                    now, MinShare, MaxShare);
            }
            // TryParse turns away anything that is not a number; NaN is refused on its own, and zero,
            // negatives and an infinity (which some runtimes do parse by name) fall outside the range.
            if (!double.TryParse(args[1], NumberStyles.Float, c, out double share)
                || double.IsNaN(share) || share < MinShare || share > MaxShare)
            {
                return string.Format(c, "'{0}' is not a planet size. A size is a share of the size this world ships at, between {1:0.####} and {2:0.####}.",
                    args[1], MinShare, MaxShare);
            }
            double factor = share / now;
            if (!(factor > 0.0) || double.IsNaN(factor) || double.IsInfinity(factor))
            {
                return "This planet's size could not be read, so it was left alone.";
            }
            if (Math.Abs(share - now) <= 1e-9 * Math.Max(1.0, now))
            {
                return string.Format(c, "This planet is already {0:0.####} of the size this world ships at; nothing was changed.", now);
            }
            // Asked here as well as inside the rescale, so the prompt never promises something that
            // would then be turned away.
            string refused = Planet.RescaleRefusal();
            if (refused != null)
            {
                return refused;
            }
            // It changes how much work is left, so it asks once, like the reset does.
            if (args.Length < 3 || args[2].ToLowerInvariant() != "confirm")
            {
                return string.Format(c, "This rescales the planet you are playing, from {0:0.####} to {1:0.####} of the size this world ships at: it ends up {2:0.###} times its present size, so terraforming it takes {2:0.###} times as long. "
                    + "Your air is kept exactly as it is: pressure, mix and temperature come out where they are now, and the air in your base and in every outdoor cell is left alone.{3} "
                    + "To go ahead: terraform size {1:0.####} confirm", now, share, factor, SeaPart(tank, factor));
            }

            // Both readings and the rescale under one hold of the tank lock. A planet tick moves gas
            // in and out of the tank, so read outside it the two figures can straddle one.
            string before = null;
            string after = null;
            string problem = null;
            Planet.UnderTankLock(() =>
            {
                before = SizeLine(tank, shipped);
                problem = Planet.Rescale(factor);
                after = SizeLine(tank, shipped);
            });
            if (problem != null)
            {
                return problem;
            }
            return "Planet rescaled. Every outdoor cell holds the air it held a moment ago; what changed is how much planet is behind it." + Environment.NewLine
                + "  before  " + before + Environment.NewLine
                + "  now     " + after + Environment.NewLine
                + string.Format(c, "  The planet size setting is still {0:0.####} and still applies to a new world, not to this one.", Settings.PlanetSize);
        }

        /// <summary>
        /// The one thing a rescale does not leave where it was. The game floods the outdoors with a
        /// sea 2 to 10 m deep once the planet holds more liquid than
        /// GlobalAtmosphereLiquid.RenderThreshold, which is a fixed number of litres and does not
        /// follow planet size, and everything outdoors under that sea counts as under water.
        /// Multiplying the planet's liquid can put it on the other side of that line. How deep the sea
        /// is does not move, being a share of the planet's own volume. Rescaling back undoes it, so
        /// this is said in the prompt rather than used to refuse. Empty on an ordinary dry planet.
        /// </summary>
        private static string SeaPart(GlobalGasMix tank, double factor)
        {
            double threshold = GlobalAtmosphereLiquid.RenderThreshold.ToDouble();
            double litres = tank.VolumeOfLiquid().ToDouble();
            if ((litres > threshold) == (litres * factor > threshold))
            {
                return "";
            }
            return string.Format(CultureInfo.InvariantCulture,
                " IT WOULD {0} THE SEA: this planet's liquid goes from {1:N0} to {2:N0} litres, and the game floods the outdoors above {3:N0} litres whatever size the planet is, so {4}. Rescaling back undoes it.",
                litres * factor > threshold ? "RAISE" : "DRAIN", litres, litres * factor, threshold,
                litres * factor > threshold ? "everything outdoors below 2 to 10 m would end up under water" : "the sea outside would go away");
        }

        /// <summary>
        /// The figures a rescale moves beside the one it must not. The pressure is worked out from the
        /// tank rather than read from PlanetaryAtmosphereSimulation.GlobalPressure, which the game
        /// rebuilds once a tick from a one-cell copy of the planet: printed either side of a rescale
        /// that would be one reading shown twice, and could not show a mistake. This is the game's own
        /// ideal gas relation on the tank itself, and it agrees with the readout (2.1435 against the
        /// 2.144 kPa status reported on Mars).
        /// </summary>
        private static string SizeLine(GlobalGasMix tank, double shipped)
        {
            return string.Format(CultureInfo.InvariantCulture, "size {0:0.####} of shipped, {1:N0} outdoor cells, {2:N3} mol, {3:0.###} kPa",
                tank.Volume.ToDouble() / shipped,
                (tank.Volume / Chemistry.GridVolume).ToDouble(),
                tank.TotalQuantity().ToDouble(),
                IdealGas.Pressure(tank.TotalQuantityGas(), PlanetaryAtmosphereSimulation.AggregateTemperature, tank.VolumeForGas()).ToDouble());
        }

        // ---- terraform set ----------------------------------------------------------------------

        /// <summary>
        /// One world setting as the console sees it, under the same key the config entry has, so one
        /// settings reference covers both. A value is a bool for a switch, and a double? for a
        /// number, where null is "none" for the two settings the config flags with 0.
        /// </summary>
        private sealed class WorldKey
        {
            public string Name;
            public string Label;
            public string Unit = "";
            public Range? Limits;           // null for a switch
            public string ZeroMeans;        // what 0 means when the setting reads 0 as none
            public Func<object> Get;        // the value in force for this world
            public Func<object> Config;     // what a new world would start with
            public Action<object> Set;      // called under the tank lock
        }

        private static WorldKey Switch(string name, string label, Func<bool> get, Func<bool> config, Action<bool> set)
        {
            return new WorldKey { Name = name, Label = label, Get = () => get(), Config = () => config(), Set = v => set((bool)v) };
        }

        private static WorldKey Number(string name, string label, string unit, Range limits, Func<double> get, Func<double> config, Action<double> set)
        {
            return new WorldKey
            {
                Name = name, Label = label, Unit = unit, Limits = limits,
                Get = () => (double?)get(), Config = () => (double?)config(), Set = v => set(((double?)v).Value),
            };
        }

        /// <summary>
        /// Every setting a world keeps its own copy of, in the order the config editor shows them.
        /// Adding one to the world file means adding it here too, or it has no way to change.
        /// </summary>
        private static readonly WorldKey[] WorldKeys =
        {
            Switch("DynamicSky", "Sky follows the air", () => Effective.DynamicSky, () => Settings.DynamicSky, v => Effective.DynamicSky = v),
            Number("GhgResponseScale", "Greenhouse strength", "", Limits.GhgResponseScale,
                () => Effective.GhgResponseScale, () => Settings.GhgResponseScale, v => Effective.GhgResponseScale = v),
            Number("DensityResponseScale", "Air density strength", "", Limits.DensityResponseScale,
                () => Effective.DensityResponseScale, () => Settings.DensityResponseScale, v => Effective.DensityResponseScale = v),
            Number("AirlessAlbedo", "Airless world reflectivity", "", Limits.AirlessAlbedo,
                () => Effective.AirlessAlbedo, () => Settings.AirlessAlbedo, v => Effective.AirlessAlbedo = v),
            new WorldKey
            {
                Name = "MaxPressureKPa", Label = "Pressure ceiling", Unit = "kPa", Limits = Limits.MaxPressureKPa, ZeroMeans = "no ceiling",
                Get = () => Effective.MaxPressureKPa,
                Config = () => Settings.MaxPressureKPa > 0.0 ? (double?)Settings.MaxPressureKPa : null,
                Set = v => Effective.CeilingByConsoleCommand((double?)v),
            },
            Switch("WeatherOnWeatherlessWorlds", "Rain or snow on worlds with no weather",
                () => Effective.WeatherOnWeatherlessWorlds, () => Settings.WeatherOnWeatherlessWorlds, v => Effective.WeatherOnWeatherlessWorlds = v),
            new WorldKey
            {
                Name = "ExternalHeatHalfLifeMinutes", Label = "Added heat half-life", Unit = "min", Limits = Limits.ExternalHeatHalfLifeMinutes, ZeroMeans = "never fades",
                Get = () => Effective.ExternalHeatHalfLifeMinutes,
                Config = () => Settings.ExternalHeatHalfLifeMinutes > 0.0 ? (double?)Settings.ExternalHeatHalfLifeMinutes : null,
                Set = v => Effective.ExternalHeatHalfLifeMinutes = (double?)v,
            },
            Number("MaxExternalOffsetKelvin", "Added heat limit", "K", Limits.MaxExternalOffsetKelvin,
                () => Effective.MaxExternalOffsetKelvin, () => Settings.MaxExternalOffsetKelvin, v => Effective.MaxExternalOffsetKelvin = v),
            Switch("StormsStopWhenStripped", "Stripping the air stops storms",
                () => Effective.StormsStopWhenStripped, () => Settings.StormsStopWhenStripped, v => Effective.StormsStopWhenStripped = v),
            Number("StrippedAtmosphereShare", "Stripped below (% of start)", "%", Limits.StrippedAtmosphereShare,
                () => Effective.StrippedAtmosphereShare, () => Settings.StrippedAtmosphereShare, v => Effective.StrippedAtmosphereShare = v),
            Switch("StormsStopWhenAtmosphereIsMild", "Mild air stops storms",
                () => Effective.StormsStopWhenAtmosphereIsMild, () => Settings.StormsStopWhenAtmosphereIsMild, v => Effective.StormsStopWhenAtmosphereIsMild = v),
            Number("MildAtmosphereColdestKelvin", "Coldest air", "K", Limits.MildAtmosphereColdestKelvin,
                () => Effective.MildAtmosphereColdestKelvin, () => Settings.MildAtmosphereColdestKelvin, v => Effective.MildAtmosphereColdestKelvin = v),
            Number("MildAtmosphereHottestKelvin", "Hottest air", "K", Limits.MildAtmosphereHottestKelvin,
                () => Effective.MildAtmosphereHottestKelvin, () => Settings.MildAtmosphereHottestKelvin, v => Effective.MildAtmosphereHottestKelvin = v),
            Number("MildAtmosphereMinPressureKpa", "Minimum pressure", "kPa", Limits.MildAtmosphereMinPressureKpa,
                () => Effective.MildAtmosphereMinPressureKpa, () => Settings.MildAtmosphereMinPressureKpa, v => Effective.MildAtmosphereMinPressureKpa = v),
            Number("MildAtmosphereMaxPressureKpa", "Maximum pressure", "kPa", Limits.MildAtmosphereMaxPressureKpa,
                () => Effective.MildAtmosphereMaxPressureKpa, () => Settings.MildAtmosphereMaxPressureKpa, v => Effective.MildAtmosphereMaxPressureKpa = v),
            Number("MildAtmosphereMaxToxinsKpa", "Most toxins", "kPa", Limits.MildAtmosphereMaxToxinsKpa,
                () => Effective.MildAtmosphereMaxToxinsKpa, () => Settings.MildAtmosphereMaxToxinsKpa, v => Effective.MildAtmosphereMaxToxinsKpa = v),
            Switch("MildAtmosphereStopsSolarStorms", "Mild air stops solar storms too",
                () => Effective.MildAtmosphereStopsSolarStorms, () => Settings.MildAtmosphereStopsSolarStorms, v => Effective.MildAtmosphereStopsSolarStorms = v),
            Switch("TraceGasGatheringEnabled", "Trace gases gather (experimental)",
                () => Effective.TraceGasGatheringEnabled, () => Settings.TraceGasGatheringEnabled, v => Effective.TraceGasGatheringEnabled = v),
            Number("TraceGasGathering", "Speed at the trace line (below it: doubles per factor of ten, up to 10x)", "x", Limits.TraceGasGathering,
                () => Effective.TraceGasGathering, () => Settings.TraceGasGathering, v => Effective.TraceGasGathering = v),
            Number("TraceGasLine", "Trace below", "mol per cell", Limits.TraceGasLine,
                () => Effective.TraceGasLine, () => Settings.TraceGasLine, v => Effective.TraceGasLine = v),
        };

        /// <summary>
        /// terraform set [&lt;key&gt; [&lt;value&gt; [confirm]]]: shows or changes a setting of the
        /// world being played, and records it beside that world's save. The only way a world's own
        /// settings move: the config is what a new world starts with and never reaches a world in
        /// play, so tuning it for a new save cannot change or damage this one.
        ///
        /// Only a change that deletes something for good asks first: setting or lowering the
        /// pressure ceiling, lowering the added heat limit, and making added heat fade sooner.
        /// Everything else can be put back with the same command, so it happens at once.
        /// </summary>
        private static string Set(string[] args)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (NetworkManager.IsClient)
            {
                return "Can only be run on the server";
            }
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tank == null)
            {
                return "No world is being played, so there are no world settings to change.";
            }
            if (args.Length < 2)
            {
                return ListWorldKeys(c);
            }
            WorldKey key = FindWorldKey(args[1]);
            if (key == null)
            {
                return "'" + args[1] + "' is not a world setting. The world settings are: " + string.Join(", ", Array.ConvertAll(WorldKeys, k => k.Name)) + ".";
            }
            object now = key.Get();
            if (args.Length < 3)
            {
                // Reporting comes before refusing: a player asking what a setting is should always
                // be told, and a reason they cannot change it is part of the answer.
                string cannot = Sidecar.RecordRefusal();
                return string.Format(c, "{0} ({1}) for this world is {2}. It can be {3}. A new world starts with {4}, from the config. To change it: terraform set {1} <value>.{5}",
                    key.Label, key.Name, Show(key, now), Allowed(key, c), Show(key, key.Config()),
                    cannot != null ? " " + cannot : "");
            }
            if (!Parse(key, args[2], c, out object asked))
            {
                return string.Format(c, "'{0}' is not a value for {1}. It can be {2}.", args[2], key.Name, Allowed(key, c));
            }
            if (Same(now, asked))
            {
                return key.Label + " for this world is already " + Show(key, now) + "; nothing was changed.";
            }
            // Asked before any prompt, so it never promises something that would then be turned away.
            string refused = Sidecar.RecordRefusal();
            if (refused != null)
            {
                return refused;
            }
            bool confirmed = args.Length > 3 && args[3].ToLowerInvariant() == "confirm";
            if (!confirmed)
            {
                string prompt = Prompt(key, now, asked, tank, c);
                if (prompt != null)
                {
                    return prompt;
                }
            }

            // The change goes in under the same lock the planet tick holds: the tick reads these in
            // the upkeep it runs while holding it, and a nullable double is two writes, so an
            // unlocked change could be read half done.
            //
            // The write to disk does NOT. A FileStream that no one else may share, plus an XmlWriter
            // flush, takes as long as the disk, the antivirus or the cloud sync feels like taking,
            // and the planet tick would be stopped for all of it. The lock is over before the file
            // is touched, so the worst a slow disk can do is leave the change holding for this
            // session only, which is what Record already says when it fails.
            Planet.UnderTankLock(() => key.Set(asked));
            string problem = Sidecar.Record();
            StringBuilder text = new StringBuilder();
            text.AppendLine(key.Label + " for this world: " + Show(key, now) + " -> " + Show(key, asked) + ".");
            text.AppendLine("  " + (problem ?? "recorded in " + Sidecar.FilePath));
            if (key.Name == "MaxPressureKPa")
            {
                text.Append(GateLine());
            }
            if (key.Name == "DynamicSky" && !(bool)asked)
            {
                text.AppendLine("  The sky stops following the air now, and keeps the look it has until this world is loaded again.");
            }
            text.Append("  The config's " + key.Name + " is " + Show(key, key.Config()) + ", and only decides what a new world starts with.");
            return text.ToString();
        }

        private static WorldKey FindWorldKey(string name)
        {
            foreach (WorldKey key in WorldKeys)
            {
                if (string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }
            return null;
        }

        private static string ListWorldKeys(CultureInfo c)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("World settings for the world you are playing, " + (Sidecar.FilePath != null
                ? "recorded in " + Sidecar.FilePath
                : "not recorded yet: " + (Sidecar.Source ?? "this world has not been saved yet")));
            if (Sidecar.Source != null && Sidecar.FilePath != null)
            {
                text.AppendLine("  " + Sidecar.Source);
            }
            foreach (WorldKey key in WorldKeys)
            {
                object config = key.Config();
                object now = key.Get();
                text.AppendLine(string.Format(c, "  {0,-32} {1,-14} {2}{3}", key.Name, Show(key, now), key.Label,
                    Same(now, config) ? "" : "; a new world would start with " + Show(key, config)));
            }
            text.Append("To change one: terraform set <key> <value>. A change that deletes something for good asks first. The config only decides what a new world starts with.");
            return text.ToString();
        }

        private static string Show(WorldKey key, object value)
        {
            if (value is bool on)
            {
                return on ? "on" : "off";
            }
            double? number = (double?)value;
            if (!number.HasValue)
            {
                return key.ZeroMeans ?? "none";
            }
            // Seven places, because the trace line can be set to a ten-thousandth of a mole or less.
            return number.Value.ToString("0.#######", CultureInfo.InvariantCulture) + (key.Unit.Length > 0 ? (key.Unit == "%" ? "%" : " " + key.Unit) : "");
        }

        private static string Allowed(WorldKey key, CultureInfo c)
        {
            if (!key.Limits.HasValue)
            {
                return "on or off";
            }
            Range limits = key.Limits.Value;
            return string.Format(c, "from {0:0.#######} to {1:0.#######}{2}{3}", limits.Min, limits.Max,
                key.Unit.Length > 0 ? " " + key.Unit : "",
                key.ZeroMeans != null ? ", where 0 means " + key.ZeroMeans : "");
        }

        /// <summary>
        /// A value as typed. TryParse turns away anything that is not a number, NaN is refused on
        /// its own, and a negative or an infinity (which some runtimes do parse by name) falls
        /// outside the range. 0 is how a player says "none" at a console that has no word for
        /// nothing, and it stops here: what a rule is handed either is a value or is not one.
        /// </summary>
        private static bool Parse(WorldKey key, string typed, CultureInfo c, out object value)
        {
            value = null;
            string word = typed.ToLowerInvariant();
            if (!key.Limits.HasValue)
            {
                if (word == "on" || word == "true" || word == "yes" || word == "1")
                {
                    value = true;
                    return true;
                }
                if (word == "off" || word == "false" || word == "no" || word == "0")
                {
                    value = false;
                    return true;
                }
                return false;
            }
            if (key.ZeroMeans != null && word == "none")
            {
                value = (double?)null;
                return true;
            }
            if (!double.TryParse(typed, NumberStyles.Float, c, out double number) || !key.Limits.Value.Holds(number))
            {
                return false;
            }
            value = key.ZeroMeans != null && number == 0.0 ? null : (double?)number;
            return true;
        }

        private static bool Same(object a, object b)
        {
            if (a is bool x && b is bool y)
            {
                return x == y;
            }
            double? p = (double?)a;
            double? q = (double?)b;
            if (!p.HasValue || !q.HasValue)
            {
                return p.HasValue == q.HasValue;
            }
            return Math.Abs(p.Value - q.Value) <= 1e-9 * Math.Max(1.0, Math.Abs(p.Value));
        }

        /// <summary>
        /// The question a change that deletes something for good asks before it happens, or null
        /// when the change can simply be put back and so happens at once.
        /// </summary>
        private static string Prompt(WorldKey key, object now, object asked, GlobalGasMix tank, CultureInfo c)
        {
            double? was = now as double?;
            double? want = asked as double?;
            switch (key.Name)
            {
                case "MaxPressureKPa":
                    // Setting a ceiling where there was none, or lowering one. Raising or clearing
                    // one deletes nothing.
                    if (want.HasValue && (!was.HasValue || want.Value < was.Value))
                    {
                        return CeilingPrompt(c, tank, was, want);
                    }
                    return null;
                case "MaxExternalOffsetKelvin":
                    if (want.Value < was.Value)
                    {
                        return string.Format(c, "Added heat is shifting this planet by {0:0.##} K right now, and heat from freezing and thawing by {1:0.##} K. Lowering the limit from {2} to {3} cuts either shift beyond {3} on the next planet tick; the heat cut is gone for good and the loss is saved, and raising the limit again does not bring it back.{4} "
                            + "This is recorded for the world you are playing and nothing else. To go ahead: terraform set {5} {6} confirm",
                            PlanetaryAtmosphereSimulation.ExternalInputOffset.ToDouble(), PlanetaryAtmosphereSimulation.LatentOffset.ToDouble(),
                            Show(key, now), Show(key, asked), GateNote(), key.Name, want.Value.ToString("0.###", c));
                    }
                    return null;
                case "ExternalHeatHalfLifeMinutes":
                    // Fading sooner, or fading at all where it never did.
                    if (want.HasValue && (!was.HasValue || want.Value < was.Value))
                    {
                        return string.Format(c, "Added heat is shifting this planet by {0:0.##} K right now, and heat from freezing and thawing by {1:0.##} K. With a half-life of {2} instead of {3}, both fade faster from the next planet tick; what fades is gone for good and the loss is saved, and a longer half-life later does not bring it back.{4} "
                            + "This is recorded for the world you are playing and nothing else. To go ahead: terraform set {5} {6} confirm",
                            PlanetaryAtmosphereSimulation.ExternalInputOffset.ToDouble(), PlanetaryAtmosphereSimulation.LatentOffset.ToDouble(),
                            Show(key, asked), Show(key, now), GateNote(), key.Name, want.Value.ToString("0.###", c));
                    }
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>The heat prompts' version of <see cref="GatePart"/>: nothing is cut while the mod is not running this planet.</summary>
        private static string GateNote()
        {
            return Gate.Enabled()
                ? ""
                : " Terraforming Reloaded is not running this planet (" + Gate.Describe() + "), so nothing is cut until it is.";
        }

        /// <summary>
        /// What the ceiling is worth while the mod is not running this planet, and nothing when it
        /// is. A ceiling is recorded for the world either way, and that is deliberate: it is a
        /// setting, not an action, so a player standing the mod down for a session should still be
        /// able to set the ceiling their world will have when it runs again. What must not happen
        /// is the prompt promising that air disappears on the next tick when Guards.Upkeep is not
        /// running at all, so the state of the gate is named in the prompt and in the answer.
        /// That is the difference from terraform size, which refuses outright: a rescale acts now,
        /// and acting now while the per-tick upkeep is off would leave the planet half scaled.
        /// </summary>
        private static string GatePart()
        {
            return Gate.Enabled()
                ? ""
                : " Terraforming Reloaded is not running this planet (" + Gate.Describe()
                    + "), so no air is deleted by any ceiling until it is.";
        }

        private static string GateLine()
        {
            return Gate.Enabled()
                ? ""
                : "  Terraforming Reloaded is not running this planet (" + Gate.Describe()
                    + "), so nothing is deleted until it is; the ceiling is recorded for when it is."
                    + Environment.NewLine;
        }

        /// <summary>
        /// What a ceiling is worth saying as. None is a state, not a number: the rule asks whether
        /// there is a ceiling, never whether the ceiling is zero.
        /// </summary>
        private static string Ceiling(double? kpa)
        {
            return kpa.HasValue ? string.Format(CultureInfo.InvariantCulture, "{0:0.###} kPa", kpa.Value) : "none";
        }

        /// <summary>
        /// The pressure the rule itself compares against: the game's per-tick planet readout, the
        /// same member the upkeep reads. A planet of no volume has none, and the figure comes out
        /// NaN (D19), so it is guarded the way the status readout is.
        /// </summary>
        private static string PressureNow(CultureInfo c)
        {
            double pressure = PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble();
            return double.IsNaN(pressure) || double.IsInfinity(pressure)
                ? "Its pressure cannot be read (this world's planet has no usable volume)."
                : string.Format(c, "It is at {0:0.###} kPa right now.", pressure);
        }

        /// <summary>
        /// Everything a player needs to decide, before air starts disappearing: what it is now and
        /// what it would be, where the planet is, whether that bites and how hard, that it cannot be
        /// undone, that the cut goes on until the planet is under the ceiling at the hottest hour of
        /// the day, and that this world is the only one it touches.
        /// </summary>
        private static string CeilingPrompt(CultureInfo c, GlobalGasMix tank, double? now, double? asked)
        {
            StringBuilder text = new StringBuilder();
            text.Append(string.Format(c, "The pressure ceiling for this planet is {0} and you are asking for {1}; 0 means no ceiling. {2} ",
                Ceiling(now), Ceiling(asked), PressureNow(c)));

            double pressure = PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble();
            bool known = !double.IsNaN(pressure) && !double.IsInfinity(pressure);
            bool raising = !asked.HasValue || (now.HasValue && asked.Value > now.Value);
            if (asked.HasValue && known && pressure > asked.Value)
            {
                // What the rule does is scale the whole planet by ceiling over pressure, every tick
                // it is over. The first tick's cut is what can be put a number on here.
                double share = 1.0 - asked.Value / pressure;
                text.Append(string.Format(c, "THAT IS BELOW WHERE THE PLANET IS, so the next planet tick deletes {0:0.##}% of what it holds, about {1:N0} mol, and that air is gone for good and the loss is saved. ",
                    share * 100.0, tank.TotalQuantity().ToDouble() * share));
                text.Append(string.Format(c, "Its pressure rises and falls over the day, so the cut goes on every tick it is over {0:0.###} kPa, until the planet stays under that at the hottest hour. ", asked.Value));
            }
            else if (asked.HasValue && known)
            {
                text.Append(string.Format(c, "That is above where the planet is, so 0.00% of its air, 0 mol, goes now; it starts deleting air whenever the planet goes over {0:0.###} kPa, which its pressure does as the day warms, and what goes then is gone for good and the loss is saved. ", asked.Value));
            }
            else if (asked.HasValue)
            {
                text.Append("How much air that would delete cannot be worked out while the pressure cannot be read. ");
            }
            if (raising)
            {
                text.Append("Nothing is deleted by raising or clearing a ceiling, and air an earlier ceiling took does not come back. ");
            }
            // So the prompt never promises a deletion that cannot happen: with the mod stood down
            // the per-tick upkeep is not running, and nothing goes until it is.
            string gate = GatePart();
            if (gate.Length > 0)
            {
                text.Append(gate.TrimStart()).Append(" ");
            }
            text.Append("This is recorded for the world you are playing and nothing else; the setting in the config is not changed. ");
            text.Append(string.Format(c, "To go ahead: terraform set MaxPressureKPa {0:0.###} confirm", asked ?? 0.0));
            return text.ToString();
        }

        private static string Reset(bool confirmed)
        {
            if (NetworkManager.IsClient)
            {
                return "Can only be run on the server";
            }
            // It cannot be undone except by loading an earlier save, so it asks once.
            if (!confirmed)
            {
                return "This puts the whole planet back as the world ships: starting air, empty clouds, empty ice caps, no stored heat. "
                    + "Everything you have terraformed is lost; your base and the air inside it are untouched. "
                    + "To go ahead: terraform reset confirm";
            }
            string problem = Planet.ResetToShipped();
            if (problem != null)
            {
                return problem;
            }
            return "Planet restored to how this world ships. To remove the mod cleanly: save now, quit, then disable it.";
        }

        public static string Status()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder text = new StringBuilder();
            text.AppendLine("Terraforming Reloaded " + Plugin.PluginVersion);
            text.AppendLine("  planet: " + Gate.Describe() + ", tick " + GameManager.GameTickCount
                + (WorldManager.IsGamePaused ? ", game paused" : "") + (GameManager.GameTickPaused ? ", simulation tick paused" : ""));
            text.AppendLine("  self-test: " + SelfTest.Summary);
            WorldSettings(text, c);
            text.AppendLine("  temperature response: " + Climate.Describe());

            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tank == null)
            {
                text.Append("  no planet loaded");
                return text.ToString();
            }

            double cells = (tank.Volume / Chemistry.GridVolume).ToDouble();
            double shipped = WorldSetting.Current?.Data?.GlobalAtmosphereData?.Volume?.Value ?? 0.0;
            if (shipped > 0.0)
            {
                double size = tank.Volume.ToDouble() / shipped;
                text.AppendLine(string.Format(c, "  planet size: {0:0.####} of shipped ({1:N0} outdoor cells)", size, cells)
                    + (Math.Abs(size - Settings.PlanetSize) > 1e-9 * Math.Max(1.0, size)
                        ? string.Format(c, "; the setting is {0:0.####} and applies to a new world or after terraform reset confirm", Settings.PlanetSize)
                        : ""));
            }
            // A planet of no volume has no pressure: the game divides by that volume and the figure
            // comes out NaN (D19). Say what is wrong instead of printing it; the rest of the line is
            // still real.
            double pressure = PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble();
            string pressurePart = double.IsNaN(pressure) || double.IsInfinity(pressure)
                ? "no pressure (this world's planet has no usable volume)"
                : string.Format(c, "pressure {0:0.###} kPa", pressure);
            text.AppendLine(string.Format(c, "  {0}, temperature {1:0.#} K, gas {2:0.000} mol, liquid {3:0.000} mol",
                pressurePart,
                PlanetaryAtmosphereSimulation.AggregateTemperature.ToDouble(),
                tank.TotalQuantityGas().ToDouble(), tank.TotalQuantityLiquid().ToDouble()));
            text.AppendLine(string.Format(c, "  temperature parts (K): sun angle {0:0.#}, sun distance {1:0.#}, greenhouse {2:0.#}, density {3:0.#}, weather {4:0.#}, latent {5:0.##}, external {6:0.##}",
                PlanetaryAtmosphereSimulation.SolarAngleTemperature.ToDouble(),
                PlanetaryAtmosphereSimulation.SolarDistanceOffsetTemperature.ToDouble(),
                PlanetaryAtmosphereSimulation.GhgIndexOffset.ToDouble(),
                PlanetaryAtmosphereSimulation.DensityOffsetTemperature.ToDouble(),
                WeatherPart(),
                PlanetaryAtmosphereSimulation.LatentOffset.ToDouble(),
                PlanetaryAtmosphereSimulation.ExternalInputOffset.ToDouble()));

            text.AppendLine("  per outdoor cell (mol):");
            foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
            {
                if (Mole.MatterState(type) == AtmosphereHelper.MatterState.None)
                {
                    continue;
                }
                double moles = tank.Get(type).ToDouble();
                if (moles != 0.0)
                {
                    text.AppendLine(string.Format(c, "    {0,-24} {1:0.######}", type, moles / cells));
                }
            }
            text.AppendLine("  " + TraceGases.Describe(tank, c));
            Reservoirs(text, c);
            // After the reservoirs, because the line about rain being held back is about the clouds
            // printed just above it.
            Storms.Describe(text, c);
            if (Guards.RejectedGives > 0)
            {
                text.AppendLine("  bad mixtures refused: " + Guards.RejectedGives);
            }
            return text.ToString().TrimEnd();
        }

        /// <summary>
        /// The settings that decide how this particular world behaves, and where they came from.
        /// They are the values in force, not the config: a world keeps what was recorded beside its
        /// save, so tuning the config for a new save cannot change or damage an older one. When they
        /// did not come from the file, the reason is on its own line, because that is the case where
        /// the config and what is running disagree.
        /// </summary>
        private static void WorldSettings(StringBuilder text, CultureInfo c)
        {
            text.AppendLine("  world settings: " + (Sidecar.FilePath != null
                ? Sidecar.FilePath + ", version " + Sidecar.FileVersion
                : "no file; " + (Sidecar.Source ?? "this world has not been saved yet")));
            if (Sidecar.Source != null && Sidecar.FilePath != null)
            {
                text.AppendLine("    " + Sidecar.Source);
            }
            text.AppendLine(string.Format(c, "    pressure ceiling {0}, added heat limit {1:0.##} K, added heat half-life {2}, greenhouse {3:0.##}, air density {4:0.##}, airless reflectivity {5:0.##}",
                Effective.MaxPressureKPa.HasValue ? string.Format(c, "{0:0.###} kPa", Effective.MaxPressureKPa.Value) : "none",
                Effective.MaxExternalOffsetKelvin,
                Effective.ExternalHeatHalfLifeMinutes.HasValue ? string.Format(c, "{0:0.##} min", Effective.ExternalHeatHalfLifeMinutes.Value) : "never fades",
                Effective.GhgResponseScale, Effective.DensityResponseScale, Effective.AirlessAlbedo));
            text.AppendLine("    sky follows the air " + (Effective.DynamicSky ? "on" : "off")
                + ", rain or snow on worlds with no weather " + (Effective.WeatherOnWeatherlessWorlds ? "on" : "off")
                + (Effective.TraceGasGatheringEnabled
                    ? string.Format(c, ", trace gases gather below {1:0.######} mol per cell at {0:0.##}x at the line", Effective.TraceGasGathering, Effective.TraceGasLine)
                    : ", trace gases do not gather")
                + "; the storm settings are under storms below; all of them with terraform set");
        }

        /// <summary>
        /// The three reservoirs in the order <see cref="Planet.ReservoirMixes"/> returns them, with
        /// what to call each and whether the game ever compares what it holds against its own volume.
        /// </summary>
        private static readonly string[] ReservoirNames = { "liquid clouds", "ice clouds", "ice caps" };

        private static readonly bool[] ReservoirEmptiesWhenFull = { true, true, false };

        /// <summary>
        /// The three stores the game keeps beside the planet's own air: what has condensed into the
        /// liquid clouds, what has frozen into the ice clouds, and what has frozen down into the ice
        /// caps. A gas that freezes out of the sky parks in the caps and melts back as the planet
        /// warms, so it is not gone; with a finite planet that cycle is something a player manages on
        /// a cold world, and the only other place to see it is a developer window that throws part way
        /// through drawing.
        ///
        /// A cloud empties itself into the planet and starts rain or snow when it fills, and what the
        /// game compares is that cloud's VolumeOfLiquid() against its own Volume
        /// (PlanetaryAtmosphereSimulation.TickPlanetarySimulation), so those are the two litre figures
        /// printed. Everything that reaches a reservoir arrives as a liquid, so that volume is all of
        /// what it holds. The ice caps are compared against nothing, ever: they carry a volume the mod
        /// keeps in proportion with the planet, but no game code reads it, so printing it would show a
        /// limit that does not exist.
        ///
        /// Read-only and off the simulation thread, like the rest of the readout, so the figures lag a
        /// tick while gas is moving. It prints one line rather than throwing when this game build's
        /// fields did not resolve.
        /// </summary>
        private static void Reservoirs(StringBuilder text, CultureInfo c)
        {
            if (!Planet.ReservoirsKnown)
            {
                text.AppendLine("  reservoirs: this game build's clouds and ice caps could not be read");
                return;
            }
            GlobalGasMix[] mixes = Planet.ReservoirMixes();
            text.AppendLine("  reservoirs (gas that has left the air; a cloud empties into the planet as rain or snow when it fills):");
            for (int i = 0; i < mixes.Length && i < ReservoirNames.Length; i++)
            {
                if (mixes[i] == null)
                {
                    text.AppendLine(string.Format(c, "    {0,-14} not loaded", ReservoirNames[i]));
                    continue;
                }
                text.AppendLine(string.Format(c, "    {0,-14} {1,14:N3} mol   {2,-28}", ReservoirNames[i],
                    mixes[i].TotalQuantity().ToDouble(), GasList(mixes[i], c))
                    + (ReservoirEmptiesWhenFull[i]
                        ? string.Format(c, " {0,11:N0} of {1:N0} L", mixes[i].VolumeOfLiquid().ToDouble(), mixes[i].Volume.ToDouble())
                        : " no limit"));
            }
        }

        /// <summary>
        /// What one reservoir holds, gas by gas, in the order and style of the per outdoor cell list
        /// above. Only what is there is listed. Air and Fuel are named in the gas enum but are
        /// mixtures rather than gases, and GlobalGasMix.Get throws on them, which is what breaks the
        /// game's own World Setting Tools window; the matter state check is what steps over them.
        /// </summary>
        private static string GasList(GlobalGasMix mix, CultureInfo c)
        {
            StringBuilder list = new StringBuilder();
            foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
            {
                if (Mole.MatterState(type) == AtmosphereHelper.MatterState.None)
                {
                    continue;
                }
                double moles = mix.Get(type).ToDouble();
                if (moles != 0.0)
                {
                    list.Append(list.Length > 0 ? ", " : "").Append(string.Format(c, "{0} {1:N3}", type, moles));
                }
            }
            return list.Length > 0 ? list.ToString() : "empty";
        }

        /// <summary>
        /// The weather term the temperature formula would use right now. The game assigns
        /// PlanetaryAtmosphereSimulation.WeatherOffset only while a weather event is running, and clears
        /// it only when the world unloads, so once a storm ends that static keeps the storm's value for
        /// the rest of the session (DEFECTS.md D18). GetGlobalGasMixTemperature asks whether an event is
        /// running every time it runs, so the readout asks it too and the parts keep summing to the
        /// temperature above. Both members are plain static reads, so this cannot throw.
        /// </summary>
        private static double WeatherPart()
        {
            return WeatherManager.IsWeatherEventRunning && WeatherManager.CurrentWeatherEvent != null
                ? PlanetaryAtmosphereSimulation.WeatherOffset.ToDouble()
                : 0.0;
        }
    }
}
