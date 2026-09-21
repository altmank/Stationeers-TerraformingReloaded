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
        public override string HelpText => "Shows the planet atmosphere and the state of Terraforming Reloaded. 'curves export' and 'curves reload' are for tuning the temperature response. 'reset confirm' puts the whole planet back as the world ships, which is also how to remove the mod cleanly (host only).";

        public override string[] Arguments => new[] { "[status | reset confirm | curves export | curves reload]" };

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
