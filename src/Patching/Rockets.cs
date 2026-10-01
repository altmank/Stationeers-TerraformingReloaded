using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Rocket engines burn all of their propellant, when a world has
    /// <see cref="Effective.RocketsBurnCompletely"/> on.
    ///
    /// Every rocket engine burns its chamber in RocketEngineBase.CombustEngine, which hands
    /// Atmosphere.TryCombust a fixed rate: the share of the limiting reactant that burns in a tick.
    /// The game ships it at 0.96, so 4 % of whichever propellant runs out first leaves in the exhaust
    /// unburnt. With an oxygen and methane premix that is oxygen in the planet's air beside the pad,
    /// and the exhaust cells are sparked, so on a world whose air is fuel it starts fires outdoors.
    /// This swaps that one constant for <see cref="CombustionRate"/>: 1 on a world with the setting
    /// on, the engine's own rate otherwise.
    ///
    /// CombustEngine is private and not virtual, so the one rewrite covers every engine the game has.
    /// It is also what the game runs once per engine type at load to work out the rated thrust
    /// (CalculateMaxThrust, from OnPrefabLoad). No world is running then, so that figure stays the
    /// game's own. Other burners pass their own rates and are not touched.
    ///
    /// Only the host burns: the engine runs in the atmosphere tick, and a joining player's game is
    /// sent the force, exhaust speed and temperature the host worked out. <see cref="Gate.Enabled"/>
    /// is false on a joining player's game as well, so it would burn as shipped if it ever did.
    /// </summary>
    public static class Rockets
    {
        /// <summary>The rate at which every unit of the limiting reactant burns in the tick.</summary>
        public const double CompleteBurn = 1.0;

        // The engine's own rate, read out of its instructions before they are rewritten, so a game
        // update that changes it changes what "as shipped" means here too.
        private static double _shippedRate = double.NaN;

        private static volatile bool _installed;
        private static volatile string _refusal = "not installed yet";
        private static int _rewrites;

        private static readonly MethodInfo CombustionRateMethod = AccessTools.DeclaredMethod(typeof(Rockets), nameof(CombustionRate));

        /// <summary>Whether the engine's burn has been rewritten. Without it engines burn as shipped.</summary>
        public static bool Installed => _installed;

        /// <summary>Why it is not installed, for readouts, or null while it is.</summary>
        public static string Refusal => _installed ? null : _refusal;

        /// <summary>The share an engine burns as the game ships it; not a number until installed.</summary>
        public static double ShippedRate => _shippedRate;

        /// <summary>
        /// Installs the rule. Refused, with the reason, unless the engine's burn still passes one
        /// constant rate to one TryCombust call, so a game update that changes how an engine burns
        /// leaves engines as shipped rather than rewriting something else.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            try
            {
                _shippedRate = CheckShape();
                harmony.Patch(Target(), transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Rockets), nameof(Transpiler))));
                if (_rewrites != 1)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "the engine's burn rewrote {0} rates, expected 1", _rewrites));
                }
                _installed = true;
            }
            catch (Exception e)
            {
                _installed = false;
                _refusal = e.Message;
                throw;
            }
        }

        /// <summary>
        /// Stands in for the constant in RocketEngineBase.CombustEngine. Once per running engine per
        /// atmosphere tick, on the atmosphere thread, so field reads only.
        /// </summary>
        public static double CombustionRate()
        {
            return Effective.RocketsBurnCompletely && Gate.Enabled() ? CompleteBurn : _shippedRate;
        }

        /// <summary>
        /// Reads the engine's instructions and returns the rate it ships with, or throws saying what
        /// no longer matches. The default reader is Harmony's; tools/PatchCheck hands in one that
        /// reads the assembly file with Mono.Cecil, so the shape is checked without the game.
        /// </summary>
        public static double CheckShape(Func<MethodBase, IList<KeyValuePair<OpCode, object>>> read = null)
        {
            MethodInfo combust = Target();
            IList<KeyValuePair<OpCode, object>> body = (read ?? ReadWithHarmony)(combust);
            int site = RateSite(body, TryCombust());
            double rate = (double)body[site].Value;
            if (!(rate > 0.0 && rate <= CompleteBurn))
            {
                // 0 means "work the rate out from the temperature" to TryCombust, so 1 in its place
                // would be a different rule, not a fuller burn.
                throw new InvalidOperationException("the engine burns at " + rate.ToString("R", CultureInfo.InvariantCulture) + ", not a share between 0 and 1");
            }
            return rate;
        }

        /// <summary>
        /// Where the one rate is: a double constant, then true, then the call to TryCombust, and that
        /// call made once in the whole method. Anything else throws, so a second burn, or a rate the
        /// engine works out instead of a constant, is a refusal and not a partial rewrite.
        /// </summary>
        public static int RateSite(IList<KeyValuePair<OpCode, object>> body, MethodBase tryCombust)
        {
            int calls = 0;
            int sites = 0;
            int site = -1;
            for (int i = 0; i < body.Count; i++)
            {
                if (!Calls(body[i], tryCombust))
                {
                    continue;
                }
                calls++;
                if (i >= 2 && body[i - 2].Key == OpCodes.Ldc_R8 && body[i - 2].Value is double && body[i - 1].Key == OpCodes.Ldc_I4_1)
                {
                    sites++;
                    site = i - 2;
                }
            }
            if (calls != 1 || sites != 1)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "the engine's burn calls TryCombust {0} times, {1} of them with a constant rate; expected 1 and 1", calls, sites));
            }
            return site;
        }

        /// <summary>Rewrites the engine's one constant rate to a call to <see cref="CombustionRate"/>.</summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> body = new List<CodeInstruction>(instructions);
            _rewrites = 0;
            int site = RateSite(View(body), TryCombust());
            // In place, so a label on the constant survives.
            body[site].opcode = OpCodes.Call;
            body[site].operand = CombustionRateMethod;
            _rewrites = 1;
            return body;
        }

        /// <summary>One line for the status readout.</summary>
        public static string Describe(CultureInfo c)
        {
            if (Assets.Scripts.Networking.NetworkManager.IsClient)
            {
                return "rocket engines: burn on the host, by its world's setting";
            }
            string shipped = double.IsNaN(_shippedRate) ? "96 %" : string.Format(c, "{0:0.##} %", _shippedRate * 100.0);
            if (!Effective.RocketsBurnCompletely)
            {
                return "rocket engines burn " + shipped + " of their propellant, as shipped; the rest leaves in the exhaust (terraform set RocketsBurnCompletely on)";
            }
            if (!_installed)
            {
                return "rocket engines are set to burn all their propellant, but this game build's engines no longer burn the way the mod expects, so they burn as shipped"
                    + (_refusal != null ? " (" + _refusal + ")" : "");
            }
            if (!Gate.Enabled())
            {
                return "rocket engines are set to burn all their propellant, but the mod is not running on this world (" + Gate.Describe() + "), so they burn as shipped";
            }
            return "rocket engines burn all their propellant (as shipped " + shipped + "), so no unburnt oxidiser or fuel leaves in the exhaust beyond what the mix had too much of";
        }

        // ---- reading the game's instructions ------------------------------------------------------

        private static MethodInfo Target()
        {
            return AccessTools.DeclaredMethod(typeof(RocketEngineBase), "CombustEngine", new[] { typeof(Atmosphere) })
                ?? throw new MissingMethodException("RocketEngineBase.CombustEngine(Atmosphere) is gone");
        }

        private static MethodInfo TryCombust()
        {
            return AccessTools.DeclaredMethod(typeof(Atmosphere), "TryCombust", new[] { typeof(double), typeof(bool) })
                ?? throw new MissingMethodException("Atmosphere.TryCombust(double, bool) is gone");
        }

        private static bool Calls(KeyValuePair<OpCode, object> instruction, MethodBase target)
        {
            return (instruction.Key == OpCodes.Call || instruction.Key == OpCodes.Callvirt)
                && instruction.Value is MethodBase called
                && called.Module == target.Module
                && called.MetadataToken == target.MetadataToken;
        }

        private static IList<KeyValuePair<OpCode, object>> ReadWithHarmony(MethodBase method)
        {
            return View(PatchProcessor.GetOriginalInstructions(method));
        }

        private static IList<KeyValuePair<OpCode, object>> View(IEnumerable<CodeInstruction> instructions)
        {
            List<KeyValuePair<OpCode, object>> view = new List<KeyValuePair<OpCode, object>>();
            foreach (CodeInstruction instruction in instructions)
            {
                view.Add(new KeyValuePair<OpCode, object>(instruction.opcode, instruction.operand));
            }
            return view;
        }
    }
}
