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
            // Deliberately not one of the problems above, and no Gate.Fault: if the per-world
            // settings file cannot be trusted the planet is still fine, it just has to run on the
            // config with the one setting that deletes air switched off.
            //
            // The whole block is wrapped, the Disable call included. If what failed was Sidecar's
            // own static initialiser then touching the type again rethrows the cached
            // TypeInitializationException, and an exception escaping from here would come out of
            // Patcher.Apply and stand the entire mod down over a settings file.
            try
            {
                string sidecar = SelfTest.CheckSidecarRoundTrip();
                if (sidecar != null)
                {
                    Sidecar.Disable("the per-world settings file did not survive its own check at startup");
                    Log.Error("Per-world settings are off for this session, so every world runs on the config with the pressure ceiling disabled and nothing is written beside a save: "
                        + sidecar + ". Please report this with the log.");
                }
            }
            catch (Exception e)
            {
                Log.Error("The per-world settings check could not be run, so per-world settings may be off for this session; the planet is unaffected. " + e);
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
                // The sidecar prefix is required, not an extra: if it does not apply, every world
                // runs on the raw config, which is the damage the per-world file exists to prevent.
                // First of everything on this method, so the values in force are settled before the
                // planet is built and before anything else at world start reads them.
                harmony.Patch(create,
                    prefix: Body(typeof(Sidecar), nameof(Sidecar.WorldStartPrefix), Priority.First),
                    postfix: Body(typeof(Patcher), nameof(WorldStartPostfix)));

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

            // The storm rules (docs/STORMS.md). Its own patch and its own Extra, because it is the
            // predicate the scheduler asks before it picks an event, and answering that is what
            // stops a suppressed world rolling the game's shared Random every frame for ever. The
            // rules still hold without it, through the prefix above; what is lost is only that cost.
            Extra(report, "storm scheduling", () =>
            {
                MethodInfo can = AccessTools.DeclaredMethod(typeof(WeatherManager), "CanScheduleWeatherEvent", Type.EmptyTypes);
                harmony.Patch(Need(can), prefix: Body(typeof(Guards), nameof(Guards.CanScheduleWeatherPrefix)));
            });

            // The toxin bound of the mild rule measures a list the game hand-writes in one property
            // and publishes nowhere. If that property no longer reads all five, the bound stands
            // down and the other four still apply: a stale list would judge air by gases the game
            // has stopped calling toxic. Not an Extra, because nothing is patched either way.
            try
            {
                string toxins = SelfTest.CheckToxinList();
                if (toxins != null)
                {
                    Storms.StandDownToxinBound(toxins);
                    SelfTest.Notes.Add("the toxin bound of the mild storm rule is off: " + toxins);
                    Log.Warn("This game build no longer counts the same gases as toxic, so the storm rules judge air without a toxin bound: " + toxins);
                }
            }
            catch (Exception e)
            {
                Storms.StandDownToxinBound("the game's toxin list could not be read");
                Log.Warn("The game's toxin list could not be read, so the storm rules judge air without a toxin bound. " + e.Message);
            }

            applyTemperature();

            // An extra, so it fails soft: a world that could not record its settings when its folder
            // was born records them the next time it is loaded, from inside the read prefix.
            Extra(report, "per-world settings", () =>
            {
                MethodInfo folder = AccessTools.DeclaredMethod(typeof(Assets.Scripts.Serialization.SaveHelper), "CreateSaveDirectory",
                    new[] { typeof(string), typeof(System.IO.DirectoryInfo).MakeByRefType() });
                harmony.Patch(Need(folder), postfix: Body(typeof(Sidecar), nameof(Sidecar.SaveDirectoryPostfix)));
            });

            // Whether a world is being created or loaded, taken from the game rather than guessed
            // at. An extra, and safe as one: without it every world start reads as a load, so a
            // world being created takes the missing-file rule and its ceiling is off, which is the
            // harmless direction. The one call that decides it is also where both of a new world's
            // CreateGlobalAtmosphere calls happen, so the flag is scoped to that call and cannot
            // survive into the next world start.
            Extra(report, "new or loaded world", () =>
            {
                MethodInfo initialize = AccessTools.DeclaredMethod(typeof(Assets.Scripts.Objects.World), "Initialize",
                    new[] { typeof(string), typeof(bool), typeof(string) });
                harmony.Patch(Need(initialize),
                    prefix: Body(typeof(Sidecar), nameof(Sidecar.WorldInitializePrefix), Priority.First),
                    finalizer: Body(typeof(Sidecar), nameof(Sidecar.WorldInitializeFinalizer)));
            });

            // Leaving a world puts the world-scoped settings back to the config, so the main menu
            // and the new-world screen do not show the last world played. Climate's temperature
            // postfixes gate on the config's master switch, not on a world being in play, so
            // without this the new-world menu's temperature range depends on what was played before.
            Extra(report, "world settings reset", () =>
            {
                MethodInfo clear = AccessTools.DeclaredMethod(simulation, "Clear");
                harmony.Patch(Need(clear), postfix: Body(typeof(Sidecar), nameof(Sidecar.WorldEndedPostfix)));
            });

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
                // One reason, named, so the status readout says which of the three applies (D19).
                string refusal = null;
                if (world == null)
                {
                    refusal = "no world started";
                }
                else if (world.IsTutorial)
                {
                    refusal = "a tutorial world";
                }
                else if (!sized)
                {
                    refusal = "this world's planet has no usable volume";
                }
                Gate.SetWorldAllowed(refusal == null, refusal);
                Climate.Invalidate();
                Storms.Invalidate();
                SelfTest.Arm();
                Planet.NoteShippedReservoirs();
            }
            catch (Exception e)
            {
                Gate.SetWorldAllowed(false, "the world start check failed");
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

        /// <summary>The same, ordered against the other patches on that method. See HarmonyLib.Priority.</summary>
        private static HarmonyMethod Body(Type type, string name, int priority)
        {
            HarmonyMethod body = Body(type, name);
            body.priority = priority;
            return body;
        }
    }
}
