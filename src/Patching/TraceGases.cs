using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using HarmonyLib;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Trace gases gather where they are consumed.
    ///
    /// An outdoor cell beside open ground relaxes toward the planet every tick
    /// (Atmosphere.LerpToGlobalAtmosphere): it takes one cell's worth of the planet's air, moves a
    /// share t of the way toward it, and gives back what it did not keep. For a gas the planet holds
    /// only a trace of, that share is so small that the cells which consume it (a fire, a filter, an
    /// intake) take hours to clear it, and while it lasts a fire around the base never goes out.
    ///
    /// For such a gas this draws <see cref="Effective.TraceGasGathering"/> times the normal share.
    /// Every extra mole is taken out of the planet under the tank lock before the cell sees it, and
    /// what the cell does not keep goes back through the game's own give, so nothing is created or
    /// destroyed. A gas above the trace line is untouched.
    ///
    /// Bounds: the extra drawn by one cell never exceeds what the planet holds of that gas at that
    /// moment, and all cells together may draw at most <see cref="MaxShareOfPoolPerTick"/> of it in
    /// one tick, however many cells border open ground.
    /// </summary>
    public static class TraceGases
    {
        /// <summary>
        /// Most of one gas the rule may move in a tick, as a share of what the planet held of it when
        /// the tick began. The planet can then lose at most 1 % a tick to gathering, so a trace takes
        /// at least 69 ticks to halve that way, whatever the settings and however large the base.
        /// </summary>
        public const double MaxShareOfPoolPerTick = 0.01;

        /// <summary>The gases a cell draws from the planet, as GasMixture.LerpGasses lists them.</summary>
        private static readonly Chemistry.GasType[] Gases =
        {
            Chemistry.GasType.Oxygen, Chemistry.GasType.Nitrogen, Chemistry.GasType.CarbonDioxide,
            Chemistry.GasType.Methane, Chemistry.GasType.Pollutant, Chemistry.GasType.NitrousOxide,
            Chemistry.GasType.Steam, Chemistry.GasType.Hydrogen, Chemistry.GasType.Hydrazine,
            Chemistry.GasType.Helium, Chemistry.GasType.Silanol, Chemistry.GasType.HydrochloricAcid,
            Chemistry.GasType.Ozone,
        };

        // Written by the planet tick and read by every cell, both under the tank lock. The flag is
        // read outside it so a planet with no trace gas costs a cell one field read.
        private static readonly double[] Budget = new double[Gases.Length];
        private static double _extraFactor;
        private static volatile bool _active;

        private static int _faults;

        internal static readonly MethodInfo TakeGlobalGasMix =
            AccessTools.DeclaredMethod(typeof(PlanetaryAtmosphereSimulation), nameof(PlanetaryAtmosphereSimulation.TakeGlobalGasMix), new[] { typeof(VolumeLitres) });

        private static readonly MethodInfo TakeForLerpMethod = AccessTools.DeclaredMethod(typeof(TraceGases), nameof(TakeForLerp));

        private static int _rewrites;

        /// <summary>
        /// Installs the rule. Refused, with the reason, unless the outdoor lerp takes from the planet
        /// exactly once and the arithmetic passes its own check on the game's types, so a game update
        /// that changes either cannot turn the rule into a leak.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            MethodInfo lerp = AccessTools.DeclaredMethod(typeof(Atmosphere), "LerpToGlobalAtmosphere", Type.EmptyTypes)
                ?? throw new MissingMethodException(nameof(Atmosphere), "LerpToGlobalAtmosphere");
            if (TakeGlobalGasMix == null)
            {
                throw new MissingMethodException(nameof(PlanetaryAtmosphereSimulation), nameof(PlanetaryAtmosphereSimulation.TakeGlobalGasMix));
            }
            int takes = CountTakes(lerp);
            if (takes != 1)
            {
                throw new InvalidOperationException($"the outdoor lerp takes from the planet {takes} times, expected 1");
            }
            string wrong = CheckArithmetic();
            if (wrong != null)
            {
                throw new InvalidOperationException("the gathering arithmetic failed its own check: " + wrong);
            }
            harmony.Patch(lerp, transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(TraceGases), nameof(Transpiler))));
            if (_rewrites != 1)
            {
                throw new InvalidOperationException($"the outdoor lerp rewrote {_rewrites} takes, expected 1");
            }
        }

        /// <summary>
        /// Every planet tick, under the tank lock, before the cells mix. Decides which gases are
        /// traces this tick and how much of each the rule may move.
        /// </summary>
        internal static void Refresh(GlobalGasMix tank)
        {
            _active = false;
            Array.Clear(Budget, 0, Budget.Length);
            double factor = Effective.TraceGasGathering;
            double line = Effective.TraceGasLine;
            double volume = tank?.Volume.ToDouble() ?? 0.0;
            if (!(factor > 1.0) || !(line > 0.0) || !(volume > 0.0) || double.IsInfinity(volume))
            {
                return;
            }
            double perCell = Chemistry.GridVolume.ToDouble() / volume;
            bool any = false;
            for (int i = 0; i < Gases.Length; i++)
            {
                double held = tank.Get(Gases[i]).ToDouble();
                if (held > 0.0 && held * perCell < line)
                {
                    Budget[i] = held * MaxShareOfPoolPerTick;
                    any = true;
                }
            }
            _extraFactor = factor - 1.0;
            _active = any;
        }

        /// <summary>
        /// One line for the status readout: which gases are traces on this planet now and what the
        /// planet holds of each. Worked out from the tank and the settings in force, not from the
        /// tick's budgets, so it takes no lock; like the rest of the readout it can lag a tick.
        /// </summary>
        public static string Describe(GlobalGasMix tank, System.Globalization.CultureInfo c)
        {
            if (Assets.Scripts.Networking.NetworkManager.IsClient)
            {
                return "trace gases: gathered on the host, by its world's settings";
            }
            double factor = Effective.TraceGasGathering;
            double line = Effective.TraceGasLine;
            string head = string.Format(c, "trace gases (below {0:0.#######} mol per cell) gather {1:0.##}x", line, factor);
            if (!(factor > 1.0) || !(line > 0.0))
            {
                return "trace gases: gathering is off for this world";
            }
            double volume = tank.Volume.ToDouble();
            if (!(volume > 0.0) || double.IsInfinity(volume))
            {
                return head + ": this world's planet has no usable volume";
            }
            double perCell = Chemistry.GridVolume.ToDouble() / volume;
            System.Text.StringBuilder found = new System.Text.StringBuilder();
            foreach (Chemistry.GasType type in Gases)
            {
                double held = tank.Get(type).ToDouble();
                if (held > 0.0 && held * perCell < line)
                {
                    found.Append(found.Length == 0 ? ": " : ", ").AppendFormat(c, "{0} {1:0.###} mol", type, held);
                }
            }
            return head + (found.Length == 0 ? ": none on this planet" : found.ToString());
        }

        /// <summary>World start: nothing carries over from the last world.</summary>
        internal static void Reset()
        {
            _active = false;
        }

        /// <summary>
        /// Stands in for the lerp's call to PlanetaryAtmosphereSimulation.TakeGlobalGasMix. Runs per
        /// outdoor cell per tick on the atmosphere workers, so it allocates nothing and costs one
        /// field read on a planet with no trace gas.
        /// </summary>
        public static GasMixture TakeForLerp(VolumeLitres takeVolume)
        {
            GasMixture drawn = PlanetaryAtmosphereSimulation.TakeGlobalGasMix(takeVolume);
            if (!_active || !Gate.Enabled())
            {
                return drawn;
            }
            object tankLock = Guards.TankLock;
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tankLock == null || tank == null)
            {
                return drawn;
            }
            try
            {
                lock (tankLock)
                {
                    Gather(ref drawn, tank, _extraFactor, Budget);
                }
            }
            catch (Exception e)
            {
                _active = false;
                if (Interlocked.Increment(ref _faults) <= 3)
                {
                    Log.Error("Trace gas gathering failed and is off until the next tick: " + e.Message);
                }
            }
            return drawn;
        }

        /// <summary>
        /// Moves the extra share of each budgeted gas from <paramref name="tank"/> into
        /// <paramref name="drawn"/>: <paramref name="extraFactor"/> times what was drawn, no more
        /// than the budget left and no more than the tank holds. The moles arrive at the temperature
        /// the take gave them. The caller holds the tank lock.
        /// </summary>
        public static void Gather(ref GasMixture drawn, GlobalGasMix tank, double extraFactor, double[] budget)
        {
            for (int i = 0; i < Gases.Length; i++)
            {
                double allowed = budget[i];
                if (!(allowed > 0.0))
                {
                    continue;
                }
                Chemistry.GasType type = Gases[i];
                Mole mole = drawn.GetMoleValue(type);
                double share = mole.Quantity.ToDouble();
                double left = tank.Get(type).ToDouble();
                double extra = Math.Min(share * extraFactor, Math.Min(allowed, left));
                if (!(extra > 0.0))
                {
                    continue;
                }
                tank.Set(new MoleQuantity(left - extra), type);
                budget[i] = allowed - extra;
                drawn.SetMoleValue(type, new MoleQuantity(share + extra), mole.Energy * ((share + extra) / share));
            }
        }

        /// <summary>Rewrites the lerp's one take from the planet to <see cref="TakeForLerp"/>.</summary>
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (IsTake(instruction))
                {
                    // In place, so labels on the instruction survive.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = TakeForLerpMethod;
                    count++;
                }
                yield return instruction;
            }
            _rewrites = count;
        }

        private static int CountTakes(MethodBase method)
        {
            int count = 0;
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
            {
                if (IsTake(instruction))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsTake(CodeInstruction instruction)
        {
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodInfo method
                && method == TakeGlobalGasMix;
        }

        /// <summary>
        /// Runs <see cref="Gather"/> on a planet and a cell built for the purpose and checks that
        /// every mole it moved left the planet, that the three bounds hold, that a gas with no budget
        /// is left alone, and that what arrives keeps its temperature. The game's own types do the
        /// arithmetic, so a game update that changes how a mole is set or read shows up here.
        /// Returns the problem, or null.
        /// </summary>
        public static string CheckArithmetic()
        {
            try
            {
                const double pool = 25.0;
                const double co2 = 1.0e6;
                const double share = 1.0e-4;
                const double energyPerMole = 30000.0;
                GlobalGasMix tank = new GlobalGasMix(new VolumeLitres(2.0e9));
                tank.Set(new MoleQuantity(pool), Chemistry.GasType.NitrousOxide);
                tank.Set(new MoleQuantity(co2), Chemistry.GasType.CarbonDioxide);

                double[] budget = new double[Gases.Length];
                int nitrous = Array.IndexOf(Gases, Chemistry.GasType.NitrousOxide);
                budget[nitrous] = pool * MaxShareOfPoolPerTick;

                GasMixture drawn = Cell(share, energyPerMole);
                Gather(ref drawn, tank, 49.0, budget);
                double got = drawn.GetMoleValue(Chemistry.GasType.NitrousOxide).Quantity.ToDouble();
                double kept = tank.Get(Chemistry.GasType.NitrousOxide).ToDouble();
                if (Math.Abs(got - 50.0 * share) > 1e-12)
                {
                    return $"a cell drew {got:R} mol of a trace gas, expected {50.0 * share:R}";
                }
                if (Math.Abs(got + kept - (pool + share)) > 1e-9)
                {
                    return $"the planet and the cell hold {got + kept:R} mol together, expected {pool + share:R}";
                }
                if (Math.Abs(budget[nitrous] - (pool * MaxShareOfPoolPerTick - 49.0 * share)) > 1e-12)
                {
                    return "the tick's budget did not fall by what was moved";
                }
                double temperatureRatio = drawn.GetMoleValue(Chemistry.GasType.NitrousOxide).Energy.ToDouble() / got / energyPerMole;
                if (Math.Abs(temperatureRatio - 1.0) > 1e-9)
                {
                    return "the moles arrived at a different temperature from the ones drawn with them";
                }
                if (drawn.GetMoleValue(Chemistry.GasType.CarbonDioxide).Quantity.ToDouble() != 4.0
                    || tank.Get(Chemistry.GasType.CarbonDioxide).ToDouble() != co2)
                {
                    return "a gas above the trace line was moved";
                }

                // The budget runs out.
                budget[nitrous] = 1.0e-3;
                drawn = Cell(share, energyPerMole);
                double before = tank.Get(Chemistry.GasType.NitrousOxide).ToDouble();
                Gather(ref drawn, tank, 49.0, budget);
                double moved = before - tank.Get(Chemistry.GasType.NitrousOxide).ToDouble();
                if (Math.Abs(moved - 1.0e-3) > 1e-12 || budget[nitrous] != 0.0)
                {
                    return $"with 0.001 mol left in the tick's budget the rule moved {moved:R}";
                }

                // The planet runs out.
                tank.Set(new MoleQuantity(2.0e-4), Chemistry.GasType.NitrousOxide);
                budget[nitrous] = 1.0;
                drawn = Cell(share, energyPerMole);
                Gather(ref drawn, tank, 49.0, budget);
                if (tank.Get(Chemistry.GasType.NitrousOxide).ToDouble() != 0.0
                    || Math.Abs(drawn.GetMoleValue(Chemistry.GasType.NitrousOxide).Quantity.ToDouble() - 3.0e-4) > 1e-15)
                {
                    return "a cell drew more than the planet held";
                }
                return null;
            }
            catch (Exception e)
            {
                return "it threw: " + (e.InnerException ?? e).Message;
            }
        }

        private static GasMixture Cell(double nitrous, double energyPerMole)
        {
            GasMixture cell = GasMixtureHelper.Create();
            cell.SetMoleValue(Chemistry.GasType.NitrousOxide, new MoleQuantity(nitrous), new MoleEnergy(nitrous * energyPerMole));
            cell.SetMoleValue(Chemistry.GasType.CarbonDioxide, new MoleQuantity(4.0), new MoleEnergy(4.0 * energyPerMole));
            return cell;
        }
    }
}
