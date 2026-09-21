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
        public override string HelpText => "Shows the planet atmosphere and the state of Terraforming Reloaded. 'curves export' and 'curves reload' are for tuning the temperature response. 'size <share> confirm' changes how big the planet you are playing is, and so how long terraforming it takes, without touching its air (host only). 'reset confirm' puts the whole planet back as the world ships, which is also how to remove the mod cleanly (host only).";

        public override string[] Arguments => new[] { "[status | size <share> confirm | reset confirm | curves export | curves reload]" };

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
            text.AppendLine(string.Format(c, "  pressure {0:0.###} kPa, temperature {1:0.#} K, gas {2:0.000} mol, liquid {3:0.000} mol",
                PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble(),
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
            if (Guards.RejectedGives > 0)
            {
                text.AppendLine("  bad mixtures refused: " + Guards.RejectedGives);
            }
            return text.ToString().TrimEnd();
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
