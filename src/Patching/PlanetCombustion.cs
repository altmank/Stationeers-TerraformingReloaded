using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;
using HarmonyLib;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// The planet's air burns (docs/PLANET-COMBUSTION.md), on a world with
    /// <see cref="Effective.PlanetAirBurns"/> on.
    ///
    /// The game's planet tank never burns: only outdoor cells do, and the planet hands every outdoor
    /// cell beside open ground its share of what it holds every tick, so oxidiser that drained from a
    /// leak into a fuel planet comes back to the base and keeps the cells there burning for hours.
    /// Here one cell's worth of the planet's air goes through the game's own rules every planet tick
    /// (<see cref="FireRule"/>), and while the planet burns the side it uses up is not handed to the
    /// outdoor air: a postfix on GasMixtureHelper.Create(GlobalGasMix, MatterState), through which
    /// every way the game copies the planet's air for a cell goes, takes it out of the copy, and the
    /// take that follows removes from the tank exactly the copy it returns.
    ///
    /// The same postfix carries two more holds, each behind its own world setting
    /// (<see cref="PlanetHold"/>): the armed hold-back, which holds that side whenever the planet's air
    /// would light itself, burning or not (<see cref="Effective.PlanetHoldsBackWhileIgnitable"/>), and
    /// the trace hold, which keeps any gas too thin for an exchanging cell to keep
    /// (<see cref="TraceHoldRule"/>, <see cref="Effective.PlanetKeepsTraceGas"/>).
    ///
    /// Host only: the upkeep that runs the rule and the postfix both need <see cref="Gate.Enabled"/>,
    /// which is false on a joining player's game; clients are sent the tank and both stored heats.
    /// </summary>
    public static class PlanetCombustion
    {
        /// <summary>The tank the held gases belong to, the hold, and its gases. One reference, so it cannot tear.</summary>
        private sealed class HoldBack
        {
            internal readonly GlobalGasMix Tank;
            internal readonly PlanetHold Hold;
            internal readonly HeldGases Gases;

            internal HoldBack(GlobalGasMix tank, PlanetHold hold)
            {
                Tank = tank;
                Hold = hold;
                Gases = hold.All;
            }
        }

        private const int FaultsBeforeStandingDown = 3;

        private static volatile HoldBack _hold;

        // What the fire decided to hold this tick, before the trace hold joins it. Planet tick only.
        private static PlanetHold _decided = PlanetHold.Nothing;

        private static int _traceFaults;

        /// <summary>The mod's own copies of the planet's air, which must see the tank as it is.</summary>
        [ThreadStatic] private static bool _unheld;

        // Set by any atmosphere worker, taken by the planet tick.
        private static int _spark;

        // The planet holds something it could burn, so a burning outdoor cell is worth recording.
        private static volatile bool _listening;

        private static volatile bool _installed;
        private static volatile string _refusal = "not installed yet";
        private static volatile string _stoodDown;
        private static int _faults;

        private static AccessTools.FieldRef<Atmosphere, int> _globalNeighbours;

        // The combustion heat the planet holds, in joules: a part of the external counter. Written
        // under the tank lock, read by the readout and the save.
        private static double _heat;
        private static bool _reconcilePending;

        // Planet tick only, under the tank lock.
        private static FireTick _last = NothingToBurn.Instance;
        private static FireTotals _thisFire;

        private static volatile FireReport _report;

        public static bool Installed => _installed;

        /// <summary>Why the rule is not installed, or null while it is.</summary>
        public static string Refusal => _installed ? null : _refusal;

        /// <summary>The combustion heat the planet holds, in joules: a part of the external counter.</summary>
        public static double Heat => Volatile.Read(ref _heat);

        /// <summary>The fire as of the last planet tick, or null before the first one of a world.</summary>
        public static FireReport Now => _report;

        /// <summary>What the outdoor air is not handed from <paramref name="tank"/> right now.</summary>
        public static HeldGases HeldBackFrom(GlobalGasMix tank) => HoldFrom(tank).All;

        /// <summary>What the outdoor air is not handed from <paramref name="tank"/> right now, and why.</summary>
        public static PlanetHold HoldFrom(GlobalGasMix tank)
        {
            HoldBack hold = _hold;
            return hold != null && ReferenceEquals(hold.Tank, tank) ? hold.Hold : PlanetHold.Nothing;
        }

        // ---- installing ----------------------------------------------------------------------------

        /// <summary>
        /// Installs the rule. Refused, with the reason, unless every copy of the planet's air the game
        /// hands a cell still goes through the one method the hold-back watches, the spark's mixing
        /// method and its field are where they were, and the rule passes its own check on the game's types.
        /// </summary>
        public static void Apply(Harmony harmony, Func<MethodBase, MethodInfo, int> calls = null)
        {
            try
            {
                MethodInfo create = CheckShape(calls);
                MethodInfo mix = AccessTools.DeclaredMethod(typeof(Atmosphere), "MixInWorld", Type.EmptyTypes)
                    ?? throw new MissingMethodException("Atmosphere.MixInWorld is gone");
                FieldInfo neighbours = AccessTools.Field(typeof(Atmosphere), "_previousGlobalNeighboursCount")
                    ?? throw new MissingFieldException("Atmosphere._previousGlobalNeighboursCount is gone");
                _globalNeighbours = AccessTools.FieldRefAccess<Atmosphere, int>(neighbours);
                string wrong = PlanetCombustionCheck.CheckArithmetic(FireRule.Shipped);
                if (wrong != null)
                {
                    throw new InvalidOperationException("the planet fire failed its own check: " + wrong);
                }
                harmony.Patch(create, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PlanetCombustion), nameof(HoldBackPostfix))));
                harmony.Patch(mix, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PlanetCombustion), nameof(SparkPrefix))));
                _installed = true;
            }
            catch (Exception e)
            {
                // A postfix that went in before the spark failed holds nothing back: the hold is only
                // ever set by the upkeep, which does nothing while the rule is not installed.
                _installed = false;
                _refusal = e.Message;
                throw;
            }
        }

        /// <summary>
        /// The method the hold-back watches, after checking that the planet's copies of its air all go
        /// through it: the exchange take, a new outdoor cell, the mixing take and the per-tick read-only
        /// copy each build their mixture with GlobalGasMix.ToInstancedGasMixture, which is that method.
        /// tools/PatchCheck hands in a counter that reads the assembly file, as for gas lost in space.
        /// </summary>
        public static MethodInfo CheckShape(Func<MethodBase, MethodInfo, int> calls = null)
        {
            Func<MethodBase, MethodInfo, int> count = calls ?? Space.CountCalls;
            Type simulation = typeof(PlanetaryAtmosphereSimulation);
            MethodInfo create = AccessTools.DeclaredMethod(typeof(GasMixtureHelper), "Create", new[] { typeof(GlobalGasMix), typeof(AtmosphereHelper.MatterState) })
                ?? throw new MissingMethodException("GasMixtureHelper.Create(GlobalGasMix, MatterState) is gone");
            MethodInfo instanced = AccessTools.DeclaredMethod(typeof(GlobalGasMix), "ToInstancedGasMixture", Type.EmptyTypes)
                ?? throw new MissingMethodException("GlobalGasMix.ToInstancedGasMixture is gone");
            Expect(count(instanced, create), 1, "a copy of the planet's air is built with GasMixtureHelper.Create");
            (string Name, Type[] Parameters, Type Owner, string What)[] copies =
            {
                ("TakeGlobalGasMix", new[] { typeof(VolumeLitres) }, simulation, "the exchange take copies the planet's air"),
                ("CloneGlobalGasMix", new[] { typeof(Atmosphere) }, simulation, "a new outdoor cell copies the planet's air"),
                ("TickPlanetarySimulation", Type.EmptyTypes, simulation, "the per-tick read-only copy copies the planet's air"),
                ("Remove", new[] { typeof(MoleQuantity), typeof(AtmosphereHelper.MatterState) }, typeof(GlobalGasMix), "the mixing take copies the planet's air"),
            };
            foreach ((string name, Type[] parameters, Type owner, string what) in copies)
            {
                MethodInfo method = AccessTools.DeclaredMethod(owner, name, parameters)
                    ?? throw new MissingMethodException(owner.Name + "." + name + " is gone");
                Expect(count(method, instanced), 1, what);
            }
            return create;
        }

        private static void Expect(int found, int expected, string what)
        {
            if (found != expected)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "{0} {1} times, expected {2}", what, found, expected));
            }
        }

        // ---- the hot paths -------------------------------------------------------------------------

        /// <summary>
        /// Postfix on GasMixtureHelper.Create(GlobalGasMix, MatterState), on every atmosphere worker
        /// for every copy of the planet's air. One volatile read while nothing is held.
        /// </summary>
        public static void HoldBackPostfix(GlobalGasMix globalGasMix, ref GasMixture __result)
        {
            HoldBack hold = _hold;
            if (hold == null || !Gate.Enabled())
            {
                return;
            }
            Withhold(hold.Tank, hold.Gases, globalGasMix, ref __result);
        }

        /// <summary>
        /// Takes the held gases, quantity and heat together, out of a copy of <paramref name="tank"/>'s
        /// air, so what is left keeps its temperature. Any other mix, and the mod's own copies, are left alone.
        /// </summary>
        public static void Withhold(GlobalGasMix tank, HeldGases held, GlobalGasMix source, ref GasMixture mixture)
        {
            if (held.IsEmpty || _unheld || !ReferenceEquals(tank, source))
            {
                return;
            }
            foreach (Chemistry.GasType type in FireGases.Gases)
            {
                if (held.Contains(type))
                {
                    mixture.SetMoleValue(type, MoleQuantity.Zero, MoleEnergy.Zero);
                }
            }
        }

        /// <summary>A scope in which this thread's copies of the planet's air are not held back.</summary>
        public readonly struct Unheld : IDisposable
        {
            private readonly bool _was;

            private Unheld(bool was)
            {
                _was = was;
            }

            public static Unheld Begin()
            {
                Unheld scope = new Unheld(_unheld);
                _unheld = true;
                return scope;
            }

            public void Dispose() => _unheld = _was;
        }

        /// <summary>
        /// Prefix on Atmosphere.MixInWorld, per outdoor cell per tick. A cell that burnt last tick and
        /// is about to trade air with the planet is the spark: the game's mixing sparks every cell a
        /// burning cell gives to except the planet. One field read while the planet holds nothing it
        /// could burn. The conditions are the ones under which the cell relaxes toward the planet.
        /// </summary>
        public static void SparkPrefix(Atmosphere __instance)
        {
            if (!_listening || !__instance.Inflamed)
            {
                return;
            }
            if (__instance.Mode != AtmosphereHelper.AtmosphereMode.World || __instance.Room != null || __instance.Cell != null
                || _globalNeighbours(__instance) <= 0 || PlanetaryAtmosphereSimulation.IsInSpaceAtmosphere(__instance.WorldGrid))
            {
                return;
            }
            Volatile.Write(ref _spark, 1);
        }

        // ---- the planet tick -----------------------------------------------------------------------

        /// <summary>
        /// Guards.Upkeep, under the tank lock, after the phase change has been put in proportion and
        /// before the pressure ceiling. Catches its own exceptions; three of them stand the rule down
        /// for the session, with nothing held back.
        /// </summary>
        internal static void Upkeep()
        {
            if (!_installed)
            {
                return;
            }
            if (!Effective.PlanetAirBurns || _stoodDown != null)
            {
                GoOut("the planet's fire is switched off for this world");
                return;
            }
            try
            {
                Run();
            }
            catch (Exception e)
            {
                _hold = null;
                _decided = PlanetHold.Nothing;
                _listening = false;
                _last = NothingToBurn.Instance;
                _report = null;
                int faults = Interlocked.Increment(ref _faults);
                if (faults <= FaultsBeforeStandingDown)
                {
                    Log.Error("The planet's fire could not be worked out this tick and nothing is held back: " + e);
                }
                if (faults >= FaultsBeforeStandingDown)
                {
                    _stoodDown = "it failed " + FaultsBeforeStandingDown + " times this session (the log has why)";
                    Log.Error("The planet's fire is off for the rest of this session: " + _stoodDown + ".");
                }
            }
        }

        private static void Run()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            GlobalAtmosphereData data = WorldSetting.Current?.Data?.GlobalAtmosphereData;
            double volume = tank?.Volume.ToDouble() ?? 0.0;
            bool sparked = Interlocked.Exchange(ref _spark, 0) != 0;
            if (tank == null || data == null || !(volume > 0.0) || double.IsInfinity(volume) || !Storms.DayPeak(tank, data, out double peak))
            {
                GoOut("the day's hottest hour could not be worked out");
                return;
            }
            double capacity = PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble();
            FireConditions now = new FireConditions(peak, tank.GetGlobalGasMixTemperature(data).ToDouble(), capacity, sparked);
            StoredHeats heats = new StoredHeats(PlanetaryAtmosphereSimulation.LatentEnergyOffset.ToDouble(),
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset.ToDouble(), Heat);

            GasMixture before = FireRule.Copy(tank, 1.0, now.NowKelvin);
            FireTick was = _last;
            FireTick tick = FireRule.Shipped.Step(tank, now, was, heats);
            if (tick is Burning burning)
            {
                PlanetaryAtmosphereSimulation.LatentEnergyOffset = new MoleEnergy(burning.Heats.Latent);
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset = new MoleEnergy(burning.Heats.External);
                Volatile.Write(ref _heat, burning.Heats.Combustion);
            }
            // On the tank after this tick's burn: the air the cells are about to be handed.
            bool armedHoldBack = Effective.PlanetHoldsBackWhileIgnitable;
            HeldGases armed = armedHoldBack ? FireRule.Shipped.Armed(tank, peak) : HeldGases.None;
            _decided = PlanetHold.Of(tick, FireRule.Shipped.HeldBack(tick), armed, armedHoldBack, HeldGases.None, false);
            _listening = !(tick is NothingToBurn);
            _last = tick;
            Record(was, tick, before, peak, PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble(), FireRule.Shipped.Sample(tank, peak));
        }

        /// <summary>
        /// Guards.Upkeep, under the tank lock, after the pressure ceiling: publishes the tick's hold, the
        /// fire's (decided by <see cref="Upkeep"/>) and the trace hold's, worked out here on the tank as
        /// the cells will draw from it and at the exchange rate they will use. A fault in the trace hold
        /// leaves it out for the tick and is logged, three times at most.
        /// </summary>
        internal static void PublishHold(GlobalGasMix tank)
        {
            if (!_installed || tank == null)
            {
                _hold = null;
                return;
            }
            PlanetHold hold = _decided;
            if (Effective.PlanetKeepsTraceGas)
            {
                try
                {
                    hold = hold.WithTrace(TraceHoldRule.Shipped.Thin(tank, AtmosphereHelper.LerpRate()));
                }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref _traceFaults) <= FaultsBeforeStandingDown)
                    {
                        Log.Error("The trace hold could not be worked out this tick and keeps nothing back: " + e);
                    }
                }
            }
            _hold = hold.IsEmpty ? null : new HoldBack(tank, hold);
        }

        /// <summary>The log lines on a change, the totals of this fire, and the readout's view.</summary>
        private static void Record(FireTick was, FireTick tick, GasMixture before, double peak, double capacity, GasMixture sample)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            bool burning = tick is Burning;
            bool wasBurning = was is Burning;
            if (tick is Burning started && !wasBurning)
            {
                _thisFire = FireTotals.Since(GameManager.GameTickCount);
                Log.Info(string.Format(c, "Planet fire started ({0}): {1}, hottest hour {2:0.0} K, {3:0.0##} % a tick.",
                    started.Cause == FireCause.Heat ? "heat" : "spark", FireReport.Burnable(before, c), peak, started.Rate * 100.0));
            }
            if (tick is Burning now)
            {
                _thisFire = _thisFire.Plus(now);
            }
            if (wasBurning && !burning)
            {
                Log.Info("Planet fire out: " + _thisFire.Describe(c, capacity) + ".");
            }
            _report = new FireReport(tick, before, peak, FireRule.IgnitionKelvin(sample), _thisFire, capacity);
        }

        private static void GoOut(string why)
        {
            _decided = PlanetHold.Nothing;
            _listening = false;
            if (_last is Burning)
            {
                Log.Info("Planet fire out (" + why + "): " + _thisFire.Describe(CultureInfo.InvariantCulture, PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble()) + ".");
            }
            _last = NothingToBurn.Instance;
            _report = null;
        }

        // ---- the combustion heat counter --------------------------------------------------------------

        /// <summary>
        /// The tick's settling of the stored heats (Guards.Upkeep), with the combustion heat this world
        /// holds. The first one of a world first squares the recorded combustion heat with the external
        /// heat the save carries (<see cref="Reconcile"/>), and logs any heat the limit then cuts.
        /// Returns the settled heats; the caller writes the game's two counters.
        /// </summary>
        internal static StoredHeats Settle(double latent, double external, double capacity)
        {
            double limit = Effective.MaxExternalOffsetKelvin;
            bool first = _reconcilePending;
            _reconcilePending = false;
            double combustion = first ? Reconcile(Heat, external, capacity, limit) : Heat;
            StoredHeats settled = FireRule.Shipped.Settle(new StoredHeats(latent, external, combustion), capacity,
                Effective.ExternalHeatHalfLifeMinutes, limit, GameManager.GameTickSpeedSeconds);
            Volatile.Write(ref _heat, settled.Combustion);
            double cut = Math.Abs(external) - Math.Abs(settled.External);
            if (first && capacity > 0.0 && cut / capacity > 0.01)
            {
                Log.Warn(string.Format(CultureInfo.InvariantCulture,
                    "This world's added heat was {0:0.##} K and the added heat limit allows {1:0.##} K beside {2:0.##} K of recorded fire heat, so {3:0.##} K was cut on load.",
                    external / capacity, limit, settled.Combustion / capacity, cut / capacity));
            }
            return settled;
        }

        /// <summary>
        /// The combustion heat a world's file recorded, squared with the external heat its save carries.
        /// The file sits beside every save of a world, so an older save can be loaded beside a newer
        /// figure. Combustion heat is a part of external heat, so a figure the save cannot hold within
        /// the limit is cut toward the save's own external heat, which never creates heat.
        /// </summary>
        public static double Reconcile(double combustion, double external, double capacity, double limitKelvin)
        {
            double limit = Math.Max(0.0, limitKelvin) * Math.Max(0.0, capacity);
            if (Math.Abs(external - combustion) <= limit * (1.0 + 1e-9) + 1e-6)
            {
                return combustion;
            }
            return Math.Max(Math.Min(0.0, external), Math.Min(Math.Max(0.0, external), combustion));
        }

        /// <summary>World start and the world's settings file: the combustion heat this world recorded.</summary>
        internal static void SetHeat(double joules)
        {
            Volatile.Write(ref _heat, double.IsNaN(joules) || double.IsInfinity(joules) ? 0.0 : joules);
            _reconcilePending = true;
        }

        /// <summary>Planet.Rescale, AddGas and RemoveGas move every stored heat with the heat capacity.</summary>
        internal static void ScaleHeat(double factor)
        {
            if (factor >= 0.0 && !double.IsInfinity(factor))
            {
                Volatile.Write(ref _heat, Heat * factor);
            }
        }

        /// <summary>The planet is back as the world ships: no fire and no heat of one.</summary>
        internal static void ForgetFire()
        {
            Volatile.Write(ref _heat, 0.0);
            _hold = null;
            _decided = PlanetHold.Nothing;
            _last = NothingToBurn.Instance;
            _report = null;
        }

        /// <summary>World start: nothing of the last world's fire carries over. The heat comes from the world's file.</summary>
        internal static void WorldStart()
        {
            _hold = null;
            _decided = PlanetHold.Nothing;
            _listening = false;
            Interlocked.Exchange(ref _spark, 0);
            _last = NothingToBurn.Instance;
            _thisFire = default;
            _report = null;
        }

        // ---- the readout --------------------------------------------------------------------------

        /// <summary>The fire line and the heat line for the status readout.</summary>
        public static void Describe(StringBuilder text, CultureInfo c)
        {
            if (NetworkManager.IsClient)
            {
                text.AppendLine("  planet fire: run by the host");
                return;
            }
            text.AppendLine("  " + FireLine(c));
            text.AppendLine("  " + HoldLine(c));
            double capacity = PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble();
            double heat = Heat;
            if (heat != 0.0 && capacity > 0.0)
            {
                text.AppendLine(string.Format(c, "  planet fire heat: {0:0.##} K now, {1}, not limited", heat / capacity,
                    Effective.ExternalHeatHalfLifeMinutes.HasValue
                        ? string.Format(c, "fading by half every {0:0.##} min", Effective.ExternalHeatHalfLifeMinutes.Value)
                        : "never fading"));
            }
        }

        /// <summary>What the planet keeps back from the outdoor air now, gas by gas, with what it holds of each and why.</summary>
        private static string HoldLine(CultureInfo c)
        {
            if (!_installed)
            {
                return "planet holds back from the outdoor air: nothing; this game build no longer hands out the planet's air the way the mod expects";
            }
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            PlanetHold hold = HoldFrom(tank);
            List<string> parts = new List<string>(4);
            foreach (Chemistry.GasType type in FireGases.Gases)
            {
                string why = hold.Reasons(type);
                if (why != null)
                {
                    parts.Add(string.Format(c, "{0} {1:0.######} mol ({2})", type, tank.Get(type).ToDouble(), why));
                }
            }
            return "planet holds back from the outdoor air: " + (parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray()));
        }

        private static string FireLine(CultureInfo c)
        {
            if (!Effective.PlanetAirBurns)
            {
                return "planet fire: off for this world (terraform set PlanetAirBurns on)";
            }
            if (!_installed)
            {
                return "planet fire: on for this world, but this game build no longer hands out the planet's air the way the mod expects, so the planet's air does not burn"
                    + (_refusal != null ? " (" + _refusal + ")" : "");
            }
            if (_stoodDown != null)
            {
                return "planet fire: off for this session because " + _stoodDown;
            }
            if (!Gate.Enabled())
            {
                return "planet fire: on for this world, but the mod is not running this planet (" + Gate.Describe() + ")";
            }
            FireReport report = _report;
            return report == null ? "planet fire: not worked out yet" : "planet fire: " + report.Describe(c);
        }
    }

    /// <summary>What one fire has burnt so far. Immutable: each tick makes the next.</summary>
    public readonly struct FireTotals
    {
        public readonly uint StartTick;
        public readonly double Oxidiser;
        public readonly double Fuel;
        public readonly double Hypergolic;
        public readonly double Energy;
        public readonly double Added;

        private FireTotals(uint startTick, double oxidiser, double fuel, double hypergolic, double energy, double added)
        {
            StartTick = startTick;
            Oxidiser = oxidiser;
            Fuel = fuel;
            Hypergolic = hypergolic;
            Energy = energy;
            Added = added;
        }

        public static FireTotals Since(uint tick) => new FireTotals(tick, 0.0, 0.0, 0.0, 0.0, 0.0);

        public FireTotals Plus(Burning tick) => new FireTotals(StartTick, Oxidiser + tick.Burnt.Oxidiser, Fuel + tick.Burnt.Fuel,
            Hypergolic + tick.Burnt.Hypergolic, Energy + tick.Burnt.Energy, Added + tick.Added);

        public string Describe(CultureInfo c, double capacity)
        {
            return string.Format(c, "{0:N3} mol of oxidiser and {1:N3} mol of fuel{2} burnt, {3:0.###} GJ of heat, {4:0.##} K added",
                Oxidiser, Fuel, Hypergolic > 0.0 ? string.Format(c, " and {0:N3} mol of hydrazine", Hypergolic) : "",
                Energy / 1e9, capacity > 0.0 ? Added / capacity : 0.0);
        }
    }

    /// <summary>The planet's fire as the readout shows it, built whole on the planet tick and published as one reference.</summary>
    public sealed class FireReport
    {
        public readonly FireTick Tick;

        /// <summary>The planet's gas before this tick's burn.</summary>
        public readonly GasMixture Air;

        public readonly double PeakKelvin;

        /// <summary>The point at which one cell's worth lights itself, or null when it never does.</summary>
        public readonly double? IgnitionKelvin;

        public readonly FireTotals ThisFire;
        public readonly double Capacity;

        public FireReport(FireTick tick, GasMixture air, double peakKelvin, double? ignitionKelvin, FireTotals thisFire, double capacity)
        {
            Tick = tick;
            Air = air;
            PeakKelvin = peakKelvin;
            IgnitionKelvin = ignitionKelvin;
            ThisFire = thisFire;
            Capacity = capacity;
        }

        public string Describe(CultureInfo c)
        {
            switch (Tick)
            {
                case NothingToBurn _:
                    return "none, the planet's air holds no fuel beside an oxidiser";
                case Unlit unlit:
                    return string.Format(c, "not burning; {0}{1}, hottest hour {2:N1} K{3}; a fire outdoors would light it",
                        Moles(unlit.Scarce, c), Beside(unlit.Scarce), PeakKelvin,
                        IgnitionKelvin.HasValue
                            ? string.Format(c, " against the {0:N2} K at which it lights itself", IgnitionKelvin.Value)
                            : ", and it never lights itself: no fuel reaches the game's one mole per outdoor cell");
                case Burning burning:
                    return string.Format(c, "burning since tick {0:N0} ({1}), {2:0.0##} % a tick; burning {3}{4}; holding back {5} from the outdoor air; this fire: {6}",
                        ThisFire.StartTick, Because(burning, c), burning.Rate * 100.0, Moles(burning.Scarce, c), Against(burning.Scarce),
                        Names(burning.Scarce), ThisFire.Describe(c, Capacity));
                default:
                    throw new InvalidOperationException("a fire tick of an unknown kind");
            }
        }

        private string Because(Burning burning, CultureInfo c)
        {
            string lights = IgnitionKelvin.HasValue ? string.Format(c, "lights at {0:N2} K", IgnitionKelvin.Value) : "never lights itself";
            return burning.Cause == FireCause.Heat
                ? string.Format(c, "heat: hottest hour {0:N1} K, {1}", PeakKelvin, lights)
                : string.Format(c, "spark: a fire outdoors touches it; hottest hour {0:N1} K, {1}", PeakKelvin, lights);
        }

        private string Beside(HeldGases scarce)
        {
            HeldGases partner = Partner(scarce);
            return partner.IsEmpty ? ", which burns on its own" : " beside " + Names(partner);
        }

        private string Against(HeldGases scarce)
        {
            HeldGases partner = Partner(scarce);
            return partner.IsEmpty ? ", which burns on its own" : " against " + Names(partner);
        }

        /// <summary>The other side of the reaction from <paramref name="scarce"/>.</summary>
        private static HeldGases Partner(HeldGases scarce)
        {
            HeldGases partner = HeldGases.None;
            if (scarce.Contains(Chemistry.GasType.Oxygen))
            {
                partner = partner.With(FireGases.FuelSide);
            }
            if (scarce.Contains(Chemistry.GasType.Methane))
            {
                partner = partner.With(FireGases.OxidiserSide);
            }
            return partner;
        }

        /// <summary>"1,000.000 mol of Methane", "12.000 mol of Oxygen and NitrousOxide": the gases of the set the planet holds, and their total.</summary>
        private string Moles(HeldGases gases, CultureInfo c)
        {
            double moles = 0.0;
            foreach (Chemistry.GasType type in FireGases.Burnable)
            {
                if (gases.Contains(type))
                {
                    moles += Air.GetMoleValue(type).Quantity.ToDouble();
                }
            }
            return string.Format(c, "{0:N3} mol of {1}", moles, Names(gases));
        }

        private string Names(HeldGases gases)
        {
            List<string> names = new List<string>(4);
            foreach (Chemistry.GasType type in FireGases.Burnable)
            {
                if (gases.Contains(type) && Air.GetMoleValue(type).Quantity.ToDouble() > 0.0)
                {
                    names.Add(type.ToString());
                }
            }
            return names.Count == 0 ? "nothing" : Join(names);
        }

        /// <summary>The burnable gases a mixture holds, with their moles, for the log.</summary>
        public static string Burnable(GasMixture air, CultureInfo c)
        {
            List<string> parts = new List<string>(6);
            foreach (Chemistry.GasType type in FireGases.Burnable)
            {
                double moles = air.GetMoleValue(type).Quantity.ToDouble();
                if (moles > 0.0)
                {
                    parts.Add(string.Format(c, "{0} {1:N3} mol", type, moles));
                }
            }
            return string.Join(", ", parts.ToArray());
        }

        private static string Join(List<string> names)
        {
            return names.Count == 1
                ? names[0]
                : string.Join(", ", names.GetRange(0, names.Count - 1).ToArray()) + " and " + names[names.Count - 1];
        }
    }
}
