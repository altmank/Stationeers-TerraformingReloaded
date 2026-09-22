using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using HarmonyLib;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Checks that the game still behaves the way the mod depends on, beyond "the methods exist".
    /// Patcher's counting proves the code has the expected shape; this proves the planet answers.
    ///
    /// Two stages. Shape checks run once at load, before anything is patched. The round trip runs
    /// once per world on the first live planet tick, under the tank lock: take one outdoor cell of
    /// air from the planet and give it back. With the switch working the planet drops by exactly
    /// what was taken and returns to where it was. If a game update breaks the take or the give,
    /// the unmodded failure is silent (gas vanishes or appears), so this is the alarm for it, and
    /// on a failure the planet is left as shipped for the rest of the session.
    /// </summary>
    public static class SelfTest
    {
        public enum Verdict { NotRun, Passed, Skipped, Failed }

        private static volatile int _pending;
        private static volatile string _summary = "not run yet";

        public static Verdict RoundTrip { get; private set; } = Verdict.NotRun;

        /// <summary>One line for the status readout.</summary>
        public static string Summary => _summary;

        /// <summary>Notes from the load-time checks that did not stop the mod, for the status readout.</summary>
        public static readonly List<string> Notes = new List<string>();

        /// <summary>
        /// The game answering its own question with "yes" means the developers have switched the
        /// planet on themselves. The mod's fixes were written against the dormant code, so it must
        /// not stack on top of whatever they shipped. Returns the problem, or null.
        /// </summary>
        public static string CheckSwitchStillOff(MethodInfo vanillaGetter)
        {
            try
            {
                if (vanillaGetter != null && (bool)vanillaGetter.Invoke(null, null))
                {
                    return "the game now switches the planet on by itself; this mod is no longer needed as built and stands down";
                }
            }
            catch (Exception e)
            {
                return "IsGlobalInteraction could not be read: " + (e.InnerException ?? e).Message;
            }
            return null;
        }

        /// <summary>
        /// The per-world settings file, round-tripped in memory through exactly the writer the mod
        /// uses on disk. Returns the problem, or null.
        ///
        /// Everything checked here is a property of this runtime's XmlSerializer rather than of the
        /// mod's own code, which is why it is measured at load instead of reasoned about: whether a
        /// bare nullable double really writes xsi:nil and reads back as null, whether a recorded
        /// zero survives as a zero rather than as an absence, and whether the namespace that nil
        /// attribute needs is declared. If any of that were untrue a file would quietly read as
        /// all-defaults and set the greenhouse strength to 0 on every world that had one.
        ///
        /// A failure is not a reason to stand the planet down, so it does not join the patch
        /// problems and does not set Gate.Fault. The sidecar is switched off for the session
        /// instead, which makes every world take the missing-file fallback, and the mod runs on.
        /// </summary>
        public static string CheckSidecarRoundTrip()
        {
            try
            {
                SidecarFile written = new SidecarFile
                {
                    Version = Sidecar.SchemaVersion,
                    MaxPressureKPa = null,
                    MaxExternalOffsetKelvin = 12.5,
                    ExternalHeatHalfLifeMinutes = null,
                    GhgResponseScale = 0.0,
                    DensityResponseScale = 1.0,
                    AirlessAlbedo = 0.3,
                };
                string xml = Sidecar.ToXml(written);
                if (!xml.Contains("xsi:nil=\"true\""))
                {
                    return "a setting that is not set does not write as xsi:nil";
                }
                if (!xml.Contains("xmlns:xsi="))
                {
                    return "the written file does not declare the xmlns:xsi namespace its own nil attributes need";
                }

                SidecarFile read = Sidecar.FromXml(xml);
                if (read == null)
                {
                    return "what was written came back as nothing";
                }
                if (read.MaxExternalOffsetKelvin != 12.5)
                {
                    return "a recorded value did not survive the round trip (12.5 came back as "
                        + (read.MaxExternalOffsetKelvin.HasValue
                            ? read.MaxExternalOffsetKelvin.Value.ToString("R", CultureInfo.InvariantCulture)
                            : "nothing") + ")";
                }
                if (read.MaxPressureKPa.HasValue || read.ExternalHeatHalfLifeMinutes.HasValue)
                {
                    return "a setting that is not set came back set";
                }
                // The one that matters most: a real zero must not read as an absence, or the
                // fallback would silently switch the greenhouse response off on a world that meant it.
                if (!read.GhgResponseScale.HasValue || read.GhgResponseScale.Value != 0.0)
                {
                    return "a recorded zero came back as nothing rather than as zero";
                }

                SidecarFile partial = Sidecar.FromXml(OneElement);
                if (partial == null || partial.MaxExternalOffsetKelvin != 7.0)
                {
                    return "a file holding one setting did not read that setting back";
                }
                if (partial.MaxPressureKPa.HasValue || partial.ExternalHeatHalfLifeMinutes.HasValue
                    || partial.GhgResponseScale.HasValue || partial.DensityResponseScale.HasValue
                    || partial.AirlessAlbedo.HasValue)
                {
                    return "a file holding one setting invented values for the rest";
                }
                return null;
            }
            catch (Exception e)
            {
                return "it threw: " + (e.InnerException ?? e).Message;
            }
        }

        /// <summary>
        /// A file with one setting in it and nothing else, as a version that predated the other five
        /// fields would have written. Every field this build knows and that file does not must come
        /// back unset, so the fallback rule can tell a version gap from a recorded value.
        /// </summary>
        private const string OneElement =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<TerraformingReloaded xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" "
            + "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" Version=\"1\">\r\n"
            + "  <MaxExternalOffsetKelvin>7</MaxExternalOffsetKelvin>\r\n"
            + "</TerraformingReloaded>";

        /// <summary>
        /// True when <paramref name="method"/> still calls every one of <paramref name="callees"/>.
        /// Used to confirm the temperature formula still adds up the parts the mod adjusts.
        /// </summary>
        public static bool StillCalls(MethodBase method, IEnumerable<MethodInfo> callees, out string missing)
        {
            HashSet<MethodBase> seen = new HashSet<MethodBase>();
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
            {
                if (instruction.operand is MethodBase callee)
                {
                    seen.Add(callee);
                }
            }
            foreach (MethodInfo callee in callees)
            {
                if (callee == null || !seen.Contains(callee))
                {
                    missing = callee?.Name ?? "a method that is gone";
                    return false;
                }
            }
            missing = null;
            return true;
        }

        /// <summary>Called at every world start: the next live tick runs the round trip again.</summary>
        internal static void Arm()
        {
            if (RoundTrip == Verdict.Failed)
            {
                return;                 // the game build has not changed; keep saying why the planet is off
            }
            RoundTrip = Verdict.NotRun;
            _summary = "not run yet";
            _pending = 1;
        }

        /// <summary>
        /// Called from the planet tick with the tank lock held and the gate open. When it passes the
        /// planet is as it was found, to within the rounding of taking and giving back one cell's air.
        /// </summary>
        internal static void RunIfPending()
        {
            if (_pending == 0)
            {
                return;
            }
            _pending = 0;
            try
            {
                GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
                if (tank == null)
                {
                    Finish(Verdict.Skipped, "no planet");
                    return;
                }
                // Nothing to take yet (the Moon, Mimas): wait for the first air, quietly.
                if (tank.TotalQuantityGas().ToDouble() <= 0.0)
                {
                    if (RoundTrip != Verdict.Skipped)
                    {
                        Finish(Verdict.Skipped, "no air on the planet yet; runs when there is");
                    }
                    _pending = 1;
                    return;
                }
                MoleEnergy heat = PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset;
                double before = tank.TotalQuantity().ToDouble();

                GasMixture taken = PlanetaryAtmosphereSimulation.TakeGlobalGasMix(Chemistry.GridVolume);
                double moved = taken.GetTotalMolesGassesAndLiquids.ToDouble();
                double between = tank.TotalQuantity().ToDouble();
                if (moved > 0.0)
                {
                    PlanetaryAtmosphereSimulation.GiveToGlobal(taken);
                }
                double after = tank.TotalQuantity().ToDouble();
                // The give books the difference between the gas and the planet as heat; this gas is
                // the planet's own, so put the counter back rather than keep rounding noise.
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset = heat;

                if (!(moved > 0.0))
                {
                    Finish(Verdict.Skipped, "the take came back empty");
                    return;
                }
                double tolerance = Math.Max(1e-6, before * 1e-9);
                if (Math.Abs(before - between - moved) > tolerance)
                {
                    Fail($"a take of {moved:0.######} mol lowered the planet by {before - between:0.######}");
                    return;
                }
                if (Math.Abs(after - before) > tolerance)
                {
                    Fail($"after giving back what was taken the planet is off by {after - before:0.######} mol");
                    return;
                }
                Finish(Verdict.Passed, $"take and give of {moved:0.###} mol balanced");
            }
            catch (Exception e)
            {
                Fail("it threw: " + e.Message);
            }
        }

        private static void Finish(Verdict verdict, string text)
        {
            RoundTrip = verdict;
            _summary = verdict.ToString().ToLowerInvariant() + ": " + text;
            Log.Info("Self-test " + _summary);
        }

        private static void Fail(string text)
        {
            RoundTrip = Verdict.Failed;
            _summary = "FAILED: " + text;
            Gate.Fault("self-test failed");
            Log.Error("Self-test FAILED, so the planet is left as shipped until the game restarts: " + text
                + ". This usually means a game update changed the planet code. Please report it with this log.");
        }
    }
}
