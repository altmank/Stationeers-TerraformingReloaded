using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using HarmonyLib;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Gas released in space is deleted, when a world has <see cref="Effective.SpaceDeletesGas"/> on.
    ///
    /// Outdoor cells at or above the space line (PlanetaryAtmosphereSimulation.IsInSpaceAtmosphere,
    /// 1,000 m) never neighbour a cell below it and never take from the planet, but everything they
    /// shed is still handed to PlanetaryAtmosphereSimulation.GiveToGlobal, which has no position and
    /// adds it to the planet. Three places in the game do that for a cell:
    ///   Atmosphere.LerpToGlobalAtmosphere      the cell relaxing toward the empty space it borders;
    ///   Atmosphere.GiveAtmospheresMixInWorld   the share of the cell's mixing that goes to a
    ///                                          bordering grid with no cell, which is the planet;
    ///   AtmosphericsManager.Deregister         a cell being removed hands over what it holds.
    /// The first two run only inside Atmosphere.MixInWorld, so a prefix there marks the thread while
    /// a space cell mixes, and Guards.GivePrefix deletes what that thread gives. The third is
    /// Guards.DeregisterPrefix, which empties a space cell before the game hands it over.
    ///
    /// Only the host runs either: the mixing runs where GameManager.RunSimulation holds, and every
    /// guard asks Gate.Enabled(), which is false on a joining player's game. Joining players are sent
    /// the smaller planet with the rest of it, and this world's setting and its running total.
    /// </summary>
    public static class Space
    {
        /// <summary>Set while this thread mixes a cell at or above the space line, with the world's setting on.</summary>
        [ThreadStatic] private static bool _inSpaceMix;

        // Moles deleted in this world so far. Added to from every atmosphere worker, so every write
        // is a compare-and-swap; loaded from the world's settings file at world start.
        private static double _lostMoles;

        private static volatile bool _installed;
        private static volatile string _refusal = "not installed yet";

        // What the host said, on a joining player's game: its world's setting and its running total.
        private static volatile bool _hostKnown;
        private static volatile bool _hostDeletes;
        private static volatile bool _hostInstalled;
        private static double _hostLostMoles;

        /// <summary>Whether the patch that finds a space cell's mixing is in. Without it nothing is deleted.</summary>
        public static bool Installed => _installed;

        /// <summary>Why it is not installed, for readouts, or null while it is.</summary>
        public static string Refusal => _installed ? null : _refusal;

        /// <summary>Moles deleted at or above the space line in this world, as recorded with it.</summary>
        public static double LostMoles => Volatile.Read(ref _lostMoles);

        /// <summary>
        /// Installs the rule. Refused, with the reason, unless the game still hands a space cell's gas
        /// to the planet in exactly the places listed on this class and nowhere else in Atmosphere:
        /// a game update that adds a fourth path would otherwise let space gas back without a word.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo mix = CheckShape();
                string wrong = CheckLedger();
                if (wrong != null)
                {
                    throw new InvalidOperationException("the running total failed its own check: " + wrong);
                }
                harmony.Patch(mix,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Space), nameof(MixPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Space), nameof(MixFinalizer))));
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
        /// Reads the game's own instructions and returns the method to patch, or throws saying what
        /// no longer matches. Also run by tools/PatchCheck against the installed game, which hands
        /// in its own way of counting calls: outside Unity the runtime cannot even open the body of
        /// a method whose locals are a System.Span, and the mixing method has one.
        ///
        /// JIT inlining is the one thing this cannot see. MixInWorld is far too large to be inlined
        /// into its caller, and GiveToGlobal takes a lock, so neither is a candidate; a game build
        /// that restructured them would show up here as a changed count first.
        /// </summary>
        public static MethodInfo CheckShape(Func<MethodBase, MethodInfo, int> calls = null)
        {
            Func<MethodBase, MethodInfo, int> Calls = calls ?? CountCalls;
            Type atmosphere = typeof(Atmosphere);
            Type simulation = typeof(PlanetaryAtmosphereSimulation);
            MethodInfo mix = Need(AccessTools.DeclaredMethod(atmosphere, "MixInWorld", Type.EmptyTypes), "Atmosphere.MixInWorld");
            MethodInfo lerp = Need(AccessTools.DeclaredMethod(atmosphere, "LerpToGlobalAtmosphere", Type.EmptyTypes), "Atmosphere.LerpToGlobalAtmosphere");
            MethodInfo share = Need(AccessTools.DeclaredMethod(atmosphere, "GiveAtmospheresMixInWorld", Type.EmptyTypes), "Atmosphere.GiveAtmospheresMixInWorld");
            MethodInfo deregister = Need(AccessTools.DeclaredMethod(typeof(AtmosphericsManager), "Deregister", new[] { atmosphere }), "AtmosphericsManager.Deregister");
            MethodInfo give = Need(AccessTools.DeclaredMethod(simulation, "GiveToGlobal", new[] { typeof(GasMixture) }), "PlanetaryAtmosphereSimulation.GiveToGlobal");
            MethodInfo inSpace = Need(AccessTools.DeclaredMethod(simulation, "IsInSpaceAtmosphere", new[] { typeof(WorldGrid) }), "PlanetaryAtmosphereSimulation.IsInSpaceAtmosphere");

            Expect(Calls(lerp, give), 1, "the outdoor lerp gives to the planet");
            Expect(Calls(lerp, inSpace), 1, "the outdoor lerp asks whether it is in space");
            Expect(Calls(share, give), 1, "the outdoor mixing gives to the planet");
            Expect(Calls(mix, lerp), 1, "the outdoor mixing runs the lerp");
            Expect(Calls(mix, share), 1, "the outdoor mixing hands out its share");
            Expect(Calls(deregister, give), 1, "removing a cell gives to the planet");

            int everywhere = 0;
            foreach (MethodBase method in BodiesOf(atmosphere))
            {
                everywhere += Calls(method, give);
            }
            Expect(everywhere, 2, "Atmosphere gives to the planet");
            return mix;
        }

        /// <summary>
        /// Atmosphere.MixInWorld, per outdoor cell per tick on the atmosphere workers. One field read
        /// on a world with the setting off. Always assigned, so a flag can never outlive its call.
        /// </summary>
        public static void MixPrefix(Atmosphere __instance)
        {
            _inSpaceMix = Effective.SpaceDeletesGas && PlanetaryAtmosphereSimulation.IsInSpaceAtmosphere(__instance.WorldGrid);
        }

        /// <summary>A finalizer, so a mix that throws part way cannot leave the thread marked.</summary>
        public static void MixFinalizer()
        {
            _inSpaceMix = false;
        }

        /// <summary>
        /// From Guards.GivePrefix, with a mixture it has already vetted: true when the give comes from
        /// a space cell's mixing, and so has been deleted and counted instead of reaching the planet.
        /// </summary>
        internal static bool DeletesGive(double moles)
        {
            if (!_inSpaceMix)
            {
                return false;
            }
            Lose(moles);
            return true;
        }

        /// <summary>
        /// From Guards.DeregisterPrefix: a cell at or above the space line is being removed, and the
        /// game is about to hand what it holds to the planet. Counts it and empties the cell, so the
        /// give that follows is of nothing. True when it did.
        /// </summary>
        internal static bool DeletesRemovedCell(Atmosphere atmosphere)
        {
            if (!_installed || !Effective.SpaceDeletesGas || atmosphere.Mode != AtmosphereHelper.AtmosphereMode.World
                || !PlanetaryAtmosphereSimulation.IsInSpaceAtmosphere(atmosphere.WorldGrid))
            {
                return false;
            }
            double moles = atmosphere.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
            // A mixture the give filter would have refused is emptied all the same, which is what the
            // game does to it straight after the refused give, but it is not counted as gas.
            if (moles > 0.0 && !double.IsInfinity(moles))
            {
                Lose(moles);
            }
            atmosphere.GasMixture.Reset();
            return true;
        }

        private static void Lose(double moles)
        {
            double seen = Volatile.Read(ref _lostMoles);
            while (true)
            {
                double found = Interlocked.CompareExchange(ref _lostMoles, seen + moles, seen);
                // Compared bit for bit: a NaN never equals itself, and the total is never NaN anyway.
                if (BitConverter.DoubleToInt64Bits(found) == BitConverter.DoubleToInt64Bits(seen))
                {
                    return;
                }
                seen = found;
            }
        }

        /// <summary>World start and the world's settings file: the running total this world recorded.</summary>
        internal static void SetLost(double moles)
        {
            Interlocked.Exchange(ref _lostMoles, moles >= 0.0 && !double.IsInfinity(moles) ? moles : 0.0);
        }

        // ---- a joining player's game -------------------------------------------------------------

        /// <summary>Sync, on a joining player's game: what the host's world does with space gas.</summary>
        internal static void FromHost(bool deletes, bool installed, double lostMoles)
        {
            Interlocked.Exchange(ref _hostLostMoles, lostMoles >= 0.0 && !double.IsInfinity(lostMoles) ? lostMoles : 0.0);
            _hostDeletes = deletes;
            _hostInstalled = installed;
            _hostKnown = true;
        }

        /// <summary>World start: nothing the last host said carries over.</summary>
        internal static void ForgetHost()
        {
            _hostKnown = false;
        }

        // ---- readout -----------------------------------------------------------------------------

        /// <summary>One line for the status readout.</summary>
        public static string Describe(CultureInfo c)
        {
            if (Assets.Scripts.Networking.NetworkManager.IsClient)
            {
                if (!_hostKnown)
                {
                    return "gas released in space: decided by the host's world; its setting has not arrived yet";
                }
                return Line(c, _hostDeletes, _hostInstalled, Volatile.Read(ref _hostLostMoles), null) + " (the host's world setting)";
            }
            return Line(c, Effective.SpaceDeletesGas, _installed, LostMoles, _refusal);
        }

        private static string Line(CultureInfo c, bool deletes, bool installed, double lost, string refusal)
        {
            string total = string.Format(c, "{0:N3} mol lost to space", lost);
            if (!deletes)
            {
                return "gas released in space (at or above 1,000 m) returns to the planet"
                    + (lost > 0.0 ? "; " + total + " while deleting it was on" : "");
            }
            if (!installed)
            {
                return "gas released in space is set to be deleted, but this game build no longer mixes outdoor air the way the mod expects, so it returns to the planet"
                    + (refusal != null ? " (" + refusal + ")" : "") + "; " + total;
            }
            return "gas released in space (at or above 1,000 m) is deleted: " + total;
        }

        // ---- self-check --------------------------------------------------------------------------

        /// <summary>
        /// Checks the running total on the runtime it runs on: that a give is only deleted while the
        /// thread is marked, that the mark is per thread, and that adds from many threads at once are
        /// all kept. Leaves the total and the mark as it found them. Returns the problem, or null.
        /// </summary>
        public static string CheckLedger()
        {
            double saved = LostMoles;
            bool mark = _inSpaceMix;
            try
            {
                SetLost(0.0);
                _inSpaceMix = false;
                if (DeletesGive(5.0) || LostMoles != 0.0)
                {
                    return "a give was deleted while no space cell was mixing";
                }
                _inSpaceMix = true;
                if (!DeletesGive(5.0) || !DeletesGive(2.5) || LostMoles != 7.5)
                {
                    return "a give from a space cell was not deleted and counted";
                }
                bool otherThreadMarked = true;
                Thread other = new Thread(() => otherThreadMarked = _inSpaceMix);
                other.Start();
                other.Join();
                if (otherThreadMarked)
                {
                    return "the mark set on one thread was seen on another";
                }
                _inSpaceMix = false;

                const int threads = 8;
                const int each = 20000;
                SetLost(0.0);
                Parallel.For(0, threads, _ =>
                {
                    for (int i = 0; i < each; i++)
                    {
                        Lose(0.5);
                    }
                });
                if (LostMoles != threads * each * 0.5)
                {
                    return string.Format(CultureInfo.InvariantCulture, "adds from {0} threads at once kept {1:R} of {2:R} mol", threads, LostMoles, threads * each * 0.5);
                }
                SetLost(-1.0);
                if (LostMoles != 0.0)
                {
                    return "a negative total was taken from a settings file";
                }
                return null;
            }
            catch (Exception e)
            {
                return "it threw: " + (e.InnerException ?? e).Message;
            }
            finally
            {
                SetLost(saved);
                _inSpaceMix = mark;
            }
        }

        // ---- reading the game's instructions ------------------------------------------------------

        private static MethodInfo Need(MethodInfo method, string name)
        {
            return method ?? throw new MissingMethodException(name + " is gone");
        }

        private static void Expect(int found, int expected, string what)
        {
            if (found != expected)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "{0} {1} times, expected {2}", what, found, expected));
            }
        }

        /// <summary>
        /// How many times <paramref name="method"/> calls <paramref name="target"/>, read straight off
        /// its IL. Not through Harmony's instruction reader, which resolves every operand, so one
        /// operand this runtime cannot resolve would fail the whole check. Here only call operands
        /// are resolved, one at a time, and one that cannot be resolved cannot be the target: the
        /// target's own signature resolves, or the rule would not have found it.
        /// </summary>
        public static int CountCalls(MethodBase method, MethodInfo target)
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                return 0;
            }
            Type[] typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArguments = method is MethodInfo info && info.IsGenericMethod ? info.GetGenericArguments() : null;
            int count = 0;
            int at = 0;
            while (at < il.Length)
            {
                OpCode code;
                if (il[at] == 0xFE && at + 1 < il.Length)
                {
                    code = IlTable.TwoByte[il[at + 1]];
                    at += 2;
                }
                else
                {
                    code = IlTable.OneByte[il[at]];
                    at += 1;
                }
                if ((code == OpCodes.Call || code == OpCodes.Callvirt) && at + 4 <= il.Length)
                {
                    int token = BitConverter.ToInt32(il, at);
                    MethodBase called = null;
                    try
                    {
                        called = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                    }
                    catch (Exception)
                    {
                        // Not resolvable on this runtime, so not the target: see the summary.
                    }
                    if (called != null && called.Module == target.Module && called.MetadataToken == target.MetadataToken)
                    {
                        count++;
                    }
                }
                at += OperandSize(code, il, at);
            }
            return count;
        }

        /// <summary>
        /// The IL opcode tables, in a class of their own so that Space keeps no static constructor:
        /// its members are read per outdoor cell per tick, and a class with one pays an
        /// initialisation check on every read.
        /// </summary>
        private static class IlTable
        {
            internal static readonly OpCode[] OneByte = Build(1);
            internal static readonly OpCode[] TwoByte = Build(2);

            private static OpCode[] Build(int size)
            {
                OpCode[] table = new OpCode[256];
                foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.GetValue(null) is OpCode code && code.Size == size)
                    {
                        table[unchecked((ushort)code.Value) & 0xFF] = code;
                    }
                }
                return table;
            }
        }

        private static int OperandSize(OpCode code, byte[] il, int at)
        {
            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    return 4 + 4 * BitConverter.ToInt32(il, at);
                default:
                    return 4;
            }
        }

        /// <summary>From the method's flags alone: opening the body is the counter's business.</summary>
        private static bool HasBody(MethodBase method)
        {
            return (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0
                && (method.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0;
        }

        /// <summary>Every method of a type with a body, its compiler-made nested types included (lambdas live there).</summary>
        private static IEnumerable<MethodBase> BodiesOf(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodInfo method in type.GetMethods(all))
            {
                if (HasBody(method))
                {
                    yield return method;
                }
            }
            foreach (ConstructorInfo constructor in type.GetConstructors(all))
            {
                if (HasBody(constructor))
                {
                    yield return constructor;
                }
            }
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (MethodBase method in BodiesOf(nested))
                {
                    yield return method;
                }
            }
        }
    }
}
