using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using HarmonyLib;
using Weather;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Applies the patch set in an order that cannot leave the game half converted.
    ///
    /// The seven tank methods are all or nothing: takes that debit the tank beside gives that do not
    /// credit it would bleed the planet dry, so every call site is counted before anything is
    /// touched, and again after. The guards that stop the known duplication and the unbounded heat
    /// are required as well. Everything else is an extra, and a failure there costs only itself.
    /// </summary>
    public static class Patcher
    {
        private static readonly string[] TankMethods =
        {
            "CloneGlobalGasMix", "GetGlobalMoles", "TakeGlobalGasMix", "TakeGlobalMoles",
            "GiveToGlobal", "AddEnergy", "RemoveEnergy",
        };

        public sealed class Report
        {
            public bool Armed;

            /// <summary>All seven tank methods rewritten and counted. Armed also needs the save guard.</summary>
            public bool TankConverted;
            public readonly List<string> Applied = new List<string>();
            public readonly List<string> Failed = new List<string>();

            /// <summary>The exception behind each failed extra, for tools/PatchCheck.</summary>
            public readonly Dictionary<string, Exception> Errors = new Dictionary<string, Exception>();
        }

        public static Report Apply(Harmony harmony)
        {
            Report report = new Report();
            Type simulation = typeof(PlanetaryAtmosphereSimulation);

            Gate.VanillaGetter = AccessTools.PropertyGetter(simulation, "IsGlobalInteraction");
            Guards.TankLock = AccessTools.Field(simulation, "GlobalInteraction")?.GetValue(null);

            // The temperature rule does not depend on the switch. A terraformed Venus reads 322 K only while
            // it runs; if a game update makes the planet stand down, dropping the rule too would put a
            // breathable Venus back at 737 K under a player with no suit. So it is applied on every path.
            bool temperatureApplied = false;
            Action applyTemperature = () =>
            {
                if (temperatureApplied)
                {
                    return;
                }
                temperatureApplied = true;
                Extra(report, "temperature response", () =>
                {
                    Type data = typeof(GlobalAtmosphereData);
                    MethodInfo ghg = AccessTools.DeclaredMethod(data, "GetGHGTemperatureOffset", new[] { typeof(float), typeof(float) });
                    MethodInfo density = AccessTools.DeclaredMethod(data, "GetDensityOffset", new[] { typeof(float), typeof(double) });
                    MethodInfo solar = AccessTools.DeclaredMethod(data, "GetSolarAngleTemperature", new[] { typeof(float) });
                    MethodInfo distance = AccessTools.DeclaredMethod(data, "GetSolarDistanceTemperatureOffset", new[] { typeof(float), typeof(float) });
                    MethodInfo formula = AccessTools.DeclaredMethod(typeof(GlobalGasMix), "GetGlobalGasMixTemperature", new[] { data, typeof(float), typeof(float) });
                    MethodInfo oneArgument = AccessTools.DeclaredMethod(typeof(GlobalGasMix), "GetGlobalGasMixTemperature", new[] { data });
                    Need(formula);
                    // The response is worked out against the formula as it stands: a base, a sun-distance
                    // term, a greenhouse term and a density term, summed. If a game update drops or
                    // replaces one, stay out rather than add to a formula that means something else.
                    if (!SelfTest.StillCalls(formula, new[] { Need(ghg), Need(density), Need(solar), Need(distance) }, out string missing))
                    {
                        throw new InvalidOperationException("the planet temperature formula no longer uses " + missing);
                    }
                    // Only the three-argument overload is patched; that is right only while the other calls it.
                    if (!SelfTest.StillCalls(Need(oneArgument), new[] { formula }, out _))
                    {
                        throw new InvalidOperationException("the planet temperature shortcut no longer goes through the full formula");
                    }
                    harmony.Patch(formula, postfix: Body(typeof(Climate), nameof(Climate.TemperaturePostfix)));
                    if (!Planet.ReservoirsKnown)
                    {
                        Log.Warn("The planet's cloud and ice cap fields were not found, so on worlds the mod warms or cools, gas that froze out may not melt back.");
                    }
                });

                // On its own: the response above is live whether or not the readout can be corrected.
                Extra(report, "temperature readout", () =>
                {
                    harmony.Patch(Need(AccessTools.DeclaredMethod(simulation, "CacheTemperatureCurveOffsets")), postfix: Body(typeof(Climate), nameof(Climate.ReadoutPostfix)));
                });
            };

            // ---- resolve and count everything required before touching anything ------------------
            List<MethodBase> tank = new List<MethodBase>();
            List<string> problems = new List<string>();

            if (Gate.VanillaGetter == null)
            {
                problems.Add("PlanetaryAtmosphereSimulation.IsGlobalInteraction is gone");
            }
            else
            {
                foreach (string name in TankMethods)
                {
                    MethodInfo method = AccessTools.DeclaredMethod(simulation, name);
                    if (method == null)
                    {
                        problems.Add(name + " is gone");
                        continue;
                    }
                    int sites;
                    try
                    {
                        sites = Gate.CountCallSites(method);
                    }
                    catch (Exception e)
                    {
                        problems.Add(name + " could not be read: " + e.Message);
                        continue;
                    }
                    if (sites != 1)
                    {
                        problems.Add($"{name} asks the question {sites} times, expected 1");
                        continue;
                    }
                    tank.Add(method);
                }
            }

            if (Guards.TankLock == null)
            {
                problems.Add("the tank lock is gone");
            }
            string switchedOn = SelfTest.CheckSwitchStillOff(Gate.VanillaGetter);
            if (switchedOn != null)
            {
                problems.Add(switchedOn);
            }
            MethodInfo tick = AccessTools.DeclaredMethod(simulation, "TickPlanetarySimulation");
            MethodInfo give = AccessTools.DeclaredMethod(simulation, "GiveToGlobal", new[] { typeof(GasMixture) });
            MethodInfo create = AccessTools.DeclaredMethod(simulation, "CreateGlobalAtmosphere");
            MethodInfo divide = AccessTools.DeclaredMethod(typeof(AtmosphericEventInstance), "DivideWorldAtmosphere");
            MethodInfo deregister = AccessTools.DeclaredMethod(typeof(AtmosphericsManager), "Deregister", new[] { typeof(Atmosphere) });
            Require(tick, "TickPlanetarySimulation", problems);
            Require(give, "GiveToGlobal(GasMixture)", problems);
            Require(create, "CreateGlobalAtmosphere", problems);
            Require(divide, "AtmosphericEventInstance.DivideWorldAtmosphere", problems);
            MethodInfo save = AccessTools.DeclaredMethod(typeof(Assets.Scripts.Serialization.XmlSaveLoad), "GetWorldData");
            Require(deregister, "AtmosphericsManager.Deregister(Atmosphere)", problems);
            Require(save, "XmlSaveLoad.GetWorldData", problems);

            if (problems.Count > 0)
            {
                report.Failed.AddRange(problems);
                Log.Error("This game build does not match what the mod expects, so the planet is left as shipped: " + string.Join("; ", problems));
                applyTemperature();
                return report;
            }

            // ---- required set ---------------------------------------------------------------------
            // Guards first: if one of them fails the tank methods are never rewritten, and a guard
            // with nothing to guard does nothing, because each one checks Gate.Enabled().
            try
            {
                harmony.Patch(tick, prefix: Body(typeof(Guards), nameof(Guards.TickPrefix)), finalizer: Body(typeof(Guards), nameof(Guards.TickFinalizer)));
                harmony.Patch(give, prefix: Body(typeof(Guards), nameof(Guards.GivePrefix)));
                harmony.Patch(divide, prefix: Body(typeof(Guards), nameof(Guards.DividePrefix)), finalizer: Body(typeof(Guards), nameof(Guards.DivideFinalizer)));
                harmony.Patch(deregister, prefix: Body(typeof(Guards), nameof(Guards.DeregisterPrefix)));
                harmony.Patch(create, postfix: Body(typeof(Patcher), nameof(WorldStartPostfix)));

                HarmonyMethod transpiler = Body(typeof(Gate), nameof(Gate.Transpiler));
                foreach (MethodBase method in tank)
                {
                    harmony.Patch(method, transpiler: transpiler);
                }
            }
            catch (Exception e)
            {
                report.Failed.Add("required patches: " + e.Message);
                Log.Error("A required patch failed, so the planet is left as shipped. " + e);
                applyTemperature();
                return report;
            }

            foreach (MethodBase method in tank)
            {
                int rewritten;
                lock (Gate.Rewrites)
                {
                    Gate.Rewrites.TryGetValue(method, out rewritten);
                }
                if (rewritten != 1)
                {
                    report.Failed.Add($"{method.Name} rewrote {rewritten} call sites");
                }
            }
            if (report.Failed.Count > 0)
            {
                Log.Error("The tank methods did not all convert, so the planet is left as shipped: " + string.Join("; ", report.Failed));
                applyTemperature();
                return report;
            }

            report.TankConverted = true;

            // Last of the required set, and on its own, because GetWorldData reaches Unity native
            // code: tools/PatchCheck cannot compile it outside the game and needs to tell that apart
            // from a real failure. In the game a failure here leaves the planet as shipped.
            try
            {
                harmony.Patch(save, prefix: Body(typeof(Guards), nameof(Guards.SavePrefix)));
            }
            catch (Exception e)
            {
                report.Failed.Add("save consistency: " + e.Message);
                report.Errors["save consistency"] = e;
                Log.Error("The save guard failed, so the planet is left as shipped. " + e.Message);
                applyTemperature();
                return report;
            }

            report.Applied.Add("planet tank (7 methods), tick upkeep, give filter, build-over fix, save consistency");
            report.Armed = true;
            Gate.Arm(true);

            // ---- extras ---------------------------------------------------------------------------
            Extra(report, "weather guard", () =>
            {
                MethodInfo schedule = AccessTools.DeclaredMethod(typeof(WeatherManager), "ScheduleWeatherEvent", new[] { typeof(WeatherEvent) });
                harmony.Patch(Need(schedule), prefix: Body(typeof(Guards), nameof(Guards.ScheduleWeatherPrefix)));
            });

            applyTemperature();

            Extra(report, "wall vent", () =>
            {
                MethodInfo vent = AccessTools.DeclaredMethod(typeof(Assets.Scripts.Objects.Pipes.WallVent), "OnAtmosphericTick");
                if (AccessTools.Field(typeof(Assets.Scripts.Objects.Pipes.WallVent), "_facingGrid") == null
                    || AccessTools.Field(typeof(Assets.Scripts.Objects.Pipes.WallVent), "_rearGrid") == null)
                {
                    throw new MissingFieldException("WallVent no longer has the two grids it mixes");
                }
                harmony.Patch(Need(vent), prefix: Body(typeof(Guards), nameof(Guards.WallVentPrefix)));
            });

            Extra(report, "planet size", () =>
            {
                MethodInfo createPlanet = AccessTools.DeclaredMethod(typeof(GlobalGasMix), "Create", new[] { typeof(GlobalAtmosphereData) });
                Need(createPlanet);
                if (!Planet.CanResize)
                {
                    throw new MissingMethodException("GlobalGasMix.Volume has no setter");
                }
                // The size applies to the planet being played, not to every mix built from a world file.
                MethodInfo regenerate = AccessTools.DeclaredMethod(simulation, "RegenerateGlobalFromData");
                foreach (MethodInfo builder in new[] { Need(create), Need(regenerate) })
                {
                    harmony.Patch(builder, prefix: Body(typeof(Planet), nameof(Planet.BuildPrefix)), finalizer: Body(typeof(Planet), nameof(Planet.BuildFinalizer)));
                }
                harmony.Patch(createPlanet, postfix: Body(typeof(Planet), nameof(Planet.CreatePostfix)));
            });

            if (Settings.DynamicSky)
            {
                Extra(report, "dynamic sky", () =>
                {
                    MethodInfo update = AccessTools.DeclaredMethod(typeof(AtmosphericScattering), "ManagerUpdate");
                    MethodInfo blend = AccessTools.DeclaredMethod(typeof(AtmosphericScattering), "UpdateAtmosphericScatteringToGlobalAtmosphere");
                    Need(update);
                    Need(blend);
                    if (Gate.CountCallSites(update) != 1)
                    {
                        throw new InvalidOperationException("the sky update does not ask the question exactly once");
                    }
                    // Throttle first, so the sky is never switched on without it.
                    harmony.Patch(blend, prefix: Body(typeof(Guards), nameof(Guards.SkyPrefix)), postfix: Body(typeof(Guards), nameof(Guards.SkyPostfix)));
                    harmony.Patch(update, transpiler: Body(typeof(Gate), nameof(Gate.Transpiler)));
                });
            }

            return report;
        }

        /// <summary>CreateGlobalAtmosphere runs at every world start, host and client, new and loaded.</summary>
        public static void WorldStartPostfix()
        {
            try
            {
                WorldSetting world = WorldSetting.Current;
                // A custom world can declare a planet with no volume; every per-cell share would divide by it.
                GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
                bool sized = tank != null && tank.Volume.ToDouble() > 0.0 && !double.IsInfinity(tank.Volume.ToDouble());
                if (world != null && !sized)
                {
                    Log.Warn("This world's GlobalAtmosphere has no usable Volume, so its planet is left as shipped.");
                }
                Gate.SetWorldAllowed(world != null && !world.IsTutorial && sized);
                Climate.Invalidate();
                SelfTest.Arm();
                Planet.NoteShippedReservoirs();
            }
            catch (Exception e)
            {
                Gate.SetWorldAllowed(false);
                Log.Error("World start check failed, planet left as shipped for this world. " + e.Message);
            }
        }

        private static void Extra(Report report, string name, Action apply)
        {
            try
            {
                apply();
                report.Applied.Add(name);
            }
            catch (Exception e)
            {
                report.Failed.Add(name + ": " + e.Message);
                report.Errors[name] = e;
                Log.Warn($"Optional part '{name}' is off for this game build: {e.Message}");
            }
        }

        private static void Require(MethodBase method, string name, List<string> problems)
        {
            if (method == null)
            {
                problems.Add(name + " is gone");
            }
        }

        private static MethodInfo Need(MethodInfo method)
        {
            return method ?? throw new MissingMethodException("a method this part patches is gone");
        }

        private static HarmonyMethod Body(Type type, string name)
        {
            return new HarmonyMethod(AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.Name, name));
        }
    }
}
