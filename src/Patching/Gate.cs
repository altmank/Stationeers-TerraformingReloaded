using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using HarmonyLib;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// The game ships a planet-wide gas tank and routes every outdoor take and give through it,
    /// but each of those calls first asks PlanetaryAtmosphereSimulation.IsGlobalInteraction, which
    /// is compiled to return false. This swaps that question, at each call site, for Enabled().
    ///
    /// The call sites are rewritten rather than the getter patched because a two byte static
    /// getter is an inlining candidate, and an inlined copy would never see a getter patch.
    /// </summary>
    public static class Gate
    {
        private static volatile bool _armed;
        private static volatile bool _worldAllowed;
        private static volatile string _worldRefusal;
        private static volatile string _fault;

        /// <summary>Call sites rewritten so far, keyed by method, so a miss can be told from a hit.</summary>
        internal static readonly Dictionary<MethodBase, int> Rewrites = new Dictionary<MethodBase, int>();

        internal static MethodInfo VanillaGetter;
        private static readonly MethodInfo EnabledMethod = AccessTools.Method(typeof(Gate), nameof(Enabled));

        public static bool Armed => _armed;

        internal static void Arm(bool armed) => _armed = armed;

        /// <summary>
        /// Set at world start. Tutorials script their atmosphere, so they stay vanilla, and a world
        /// can also be refused for its own reasons (a planet of no volume). The reason is carried so
        /// that every readout names the one that applies rather than guessing at a tutorial.
        /// </summary>
        internal static void SetWorldAllowed(bool allowed, string refusal = null)
        {
            _worldRefusal = refusal;
            _worldAllowed = allowed;
        }

        public static bool WorldAllowed => _worldAllowed;

        /// <summary>
        /// Why this world is not one the mod runs, or null while it is. For readouts only; the hot
        /// path reads <see cref="WorldAllowed"/>.
        /// </summary>
        public static string WorldRefusal => _worldAllowed ? null : (_worldRefusal ?? "no world started, or a tutorial");

        /// <summary>
        /// Something the mod depends on answered wrongly (SelfTest). The planet goes back to shipped
        /// behaviour until the game restarts; a new world does not clear it, the game build has not changed.
        /// </summary>
        internal static void Fault(string reason) => _fault = reason;

        /// <summary>
        /// Runs on the tick thread and on every atmosphere worker, per outdoor cell per tick, so it
        /// stays a handful of field reads.
        ///
        /// Off while a save loads: the loader builds each saved outdoor cell by cloning the planet's
        /// air, which debits the tank, and then overwrites the cell from the save. Counting those
        /// clones would drain the tank a little on every load.
        ///
        /// Off on clients: a client never runs the mixing that takes from the tank but does run the
        /// cleanup that gives to it, so its copy would only ever grow. Clients are sent the host's.
        /// </summary>
        public static bool Enabled()
        {
            if (!_armed || !_worldAllowed || !Settings.Enabled || _fault != null)
            {
                return false;
            }
            if (NetworkManager.IsClient)
            {
                return false;
            }
            GameState state = GameManager.GameState;
            return state == GameState.Running || state == GameState.Paused;
        }

        /// <summary>Why Enabled() is false right now, for the status readout.</summary>
        public static string Describe()
        {
            if (!_armed) return "off: patches not applied";
            if (_fault != null) return "off: " + _fault;
            if (!Settings.Enabled) return "off: disabled in config";
            if (!_worldAllowed) return "off: " + WorldRefusal;
            if (NetworkManager.IsClient) return "off: client, planet comes from the host";
            GameState state = GameManager.GameState;
            if (state != GameState.Running && state != GameState.Paused) return "off: game state is " + state;
            return "live";
        }

        public static int CountCallSites(MethodBase method)
        {
            int count = 0;
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
            {
                if (IsGetterCall(instruction))
                {
                    count++;
                }
            }
            return count;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int count = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (IsGetterCall(instruction))
                {
                    // Reassign in place so labels and exception blocks on the instruction survive.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = EnabledMethod;
                    count++;
                }
                yield return instruction;
            }
            lock (Rewrites)
            {
                Rewrites[original] = count;
            }
        }

        private static bool IsGetterCall(CodeInstruction instruction)
        {
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodInfo method
                && method == VanillaGetter;
        }
    }
}
