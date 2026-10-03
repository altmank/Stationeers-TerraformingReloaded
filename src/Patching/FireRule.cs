using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// A set of gases. Chemistry.GasType gives every gas a bit of its own, so the set is that mask.
    /// Immutable, so one tick's set is handed to every atmosphere worker as a single value.
    /// </summary>
    public readonly struct HeldGases
    {
        private readonly uint _mask;

        private HeldGases(uint mask)
        {
            _mask = mask;
        }

        public static HeldGases None => default;

        public static HeldGases Of(IEnumerable<Chemistry.GasType> types)
        {
            uint mask = 0;
            foreach (Chemistry.GasType type in types)
            {
                mask |= (uint)type;
            }
            return new HeldGases(mask);
        }

        public HeldGases With(HeldGases other) => new HeldGases(_mask | other._mask);

        public bool Contains(Chemistry.GasType type) => (_mask & (uint)type) != 0;

        public bool IsEmpty => _mask == 0;
    }

    /// <summary>What lit a burning planet this tick.</summary>
    public enum FireCause
    {
        /// <summary>The day's hottest hour is past the point at which the planet's air lights itself.</summary>
        Heat,

        /// <summary>An outdoor cell trading air with the planet was on fire.</summary>
        Spark,
    }

    /// <summary>
    /// The planet's stored heats, in joules. Latent and external are the game's own counters;
    /// combustion is the part of external heat that the planet's own fire booked, which the added
    /// heat limit does not apply to.
    /// </summary>
    public readonly struct StoredHeats
    {
        public readonly double Latent;
        public readonly double External;
        public readonly double Combustion;

        public StoredHeats(double latent, double external, double combustion)
        {
            Latent = latent;
            External = external;
            Combustion = combustion;
        }

        /// <summary>Every heat times <paramref name="factor"/>, so the kelvin each applies holds when the heat capacity moves by it.</summary>
        public StoredHeats Scaled(double factor) => new StoredHeats(Latent * factor, External * factor, Combustion * factor);

        /// <summary>The same heats with <paramref name="joules"/> more of combustion heat, which is part of external heat.</summary>
        public StoredHeats PlusCombustion(double joules) => new StoredHeats(Latent, External + joules, Combustion + joules);
    }

    /// <summary>What one tick's burn did to the tank.</summary>
    public readonly struct Burnt
    {
        /// <summary>The heat of combustion the game's own burn returned, in joules.</summary>
        public readonly double Energy;

        /// <summary>The tank's heat capacity after the burn less before it, in J/K.</summary>
        public readonly double CapacityChange;

        public readonly double Oxidiser;
        public readonly double Fuel;
        public readonly double Hypergolic;

        public Burnt(double energy, double capacityChange, double oxidiser, double fuel, double hypergolic)
        {
            Energy = energy;
            CapacityChange = capacityChange;
            Oxidiser = oxidiser;
            Fuel = fuel;
            Hypergolic = hypergolic;
        }
    }

    /// <summary>What one planet tick knows before it decides: the day's hottest hour, the temperature now, the planet's heat capacity, and whether a burning outdoor cell touched the planet.</summary>
    public readonly struct FireConditions
    {
        public readonly double PeakKelvin;
        public readonly double NowKelvin;
        public readonly double CapacityJoulesPerKelvin;
        public readonly bool Sparked;

        public FireConditions(double peakKelvin, double nowKelvin, double capacityJoulesPerKelvin, bool sparked)
        {
            PeakKelvin = peakKelvin;
            NowKelvin = nowKelvin;
            CapacityJoulesPerKelvin = capacityJoulesPerKelvin;
            Sparked = sparked;
        }
    }

    /// <summary>One planet tick of the fire, as decided. Exactly one of the three below.</summary>
    public abstract class FireTick
    {
        private protected FireTick()
        {
        }
    }

    /// <summary>The planet's air holds no fuel beside an oxidiser, nor a hypergolic, in burnable amounts.</summary>
    public sealed class NothingToBurn : FireTick
    {
        public static readonly NothingToBurn Instance = new NothingToBurn();

        private NothingToBurn()
        {
        }
    }

    /// <summary>The air could burn but nothing lights it: the hottest hour is short of ignition and no fire outdoors touches it.</summary>
    public sealed class Unlit : FireTick
    {
        /// <summary>The side a complete burn would use up.</summary>
        public readonly HeldGases Scarce;

        public Unlit(HeldGases scarce)
        {
            Scarce = scarce;
        }
    }

    /// <summary>The planet burnt this tick.</summary>
    public sealed class Burning : FireTick
    {
        public readonly FireCause Cause;

        /// <summary>The side the burn uses up, held back from the outdoor air while it burns.</summary>
        public readonly HeldGases Scarce;

        /// <summary>The share of the scarce side burnt in the tick.</summary>
        public readonly double Rate;

        public readonly Burnt Burnt;

        /// <summary>The planet's stored heats after the tick's heat was booked.</summary>
        public readonly StoredHeats Heats;

        /// <summary>The heat the tick added, in joules, as the game books a burnt cell.</summary>
        public readonly double Added;

        public Burning(FireCause cause, HeldGases scarce, double rate, Burnt burnt, StoredHeats heats, double added)
        {
            Cause = cause;
            Scarce = scarce;
            Rate = rate;
            Burnt = burnt;
            Heats = heats;
            Added = added;
        }
    }

    /// <summary>
    /// The gas tables the rule reads, taken from the game rather than written out: every gas the
    /// tank holds as a gas, and the game's own lists of oxidisers, fuels and hypergolics
    /// (Atmospherics.Combustion) cut to their gases, because only gas burns in the planet. In a class
    /// of their own so the classes read per outdoor cell keep no static constructor.
    /// </summary>
    public static class FireGases
    {
        public static readonly Chemistry.GasType[] Gases = OfState(AtmosphereHelper.MatterState.Gas);

        public static readonly Chemistry.GasType[] Oxidisers = GasesOf(Combustion.Oxidisers);
        public static readonly Chemistry.GasType[] Fuels = GasesOf(Combustion.Fuels);
        public static readonly Chemistry.GasType[] Hypergolics = GasesOf(Combustion.Hypergolics);

        /// <summary>Every gas that can burn or feed a burn, as a gas.</summary>
        public static readonly Chemistry.GasType[] Burnable = Join(Oxidisers, Fuels, Hypergolics);

        public static readonly HeldGases OxidiserSide = HeldGases.Of(Oxidisers);
        public static readonly HeldGases FuelSide = HeldGases.Of(Fuels);
        public static readonly HeldGases HypergolicSide = HeldGases.Of(Hypergolics);

        private static Chemistry.GasType[] OfState(AtmosphereHelper.MatterState state)
        {
            List<Chemistry.GasType> found = new List<Chemistry.GasType>(16);
            foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
            {
                // Air and Fuel are mixtures rather than gases and report no state; Undefined is not a gas.
                if (type != Chemistry.GasType.Undefined && Mole.MatterState(type) == state)
                {
                    found.Add(type);
                }
            }
            return found.ToArray();
        }

        private static Chemistry.GasType[] GasesOf(Chemistry.GasType[] types)
        {
            List<Chemistry.GasType> found = new List<Chemistry.GasType>(types.Length);
            foreach (Chemistry.GasType type in types)
            {
                if (Mole.MatterState(type) == AtmosphereHelper.MatterState.Gas)
                {
                    found.Add(type);
                }
            }
            return found.ToArray();
        }

        private static Chemistry.GasType[] Join(params Chemistry.GasType[][] lists)
        {
            List<Chemistry.GasType> all = new List<Chemistry.GasType>(8);
            foreach (Chemistry.GasType[] list in lists)
            {
                all.AddRange(list);
            }
            return all.ToArray();
        }
    }

    /// <summary>
    /// The planet's air as one big cell, put through the game's own rules for a cell
    /// (docs/PLANET-COMBUSTION.md, Appendix B). Every part is a game call on one cell's worth of the
    /// planet's air, or on a copy of the whole tank:
    ///   the gate is GasMixture.IsAutoIgnition at the day's hottest hour, or a spark;
    ///   the rate is Atmosphere.GetCombustionMultiplierCurved;
    ///   the scarce side is what GasMixture.Combust at a share of 1 leaves empty;
    ///   the burn is GasMixture.Combust at the rate on a gas-only copy of the tank, written back;
    ///   the heat is booked as a burnt cell books it.
    ///
    /// The parts a mistake could quietly change are virtual so that tools/PatchCheck can hand
    /// <see cref="PlanetCombustionCheck.CheckArithmetic"/> a rule with one of them broken and see
    /// the check fail. <see cref="Shipped"/> is the only rule the game runs.
    /// </summary>
    public class FireRule
    {
        /// <summary>Kelvin the hottest hour must fall below the ignition point before a fire lit by heat goes out.</summary>
        public const double FlappingMarginKelvin = 5.0;

        /// <summary>A side a complete burn leaves under this share of what it started with is used up.</summary>
        private const double UsedUp = 1e-9;

        public static readonly FireRule Shipped = new FireRule();

        [ThreadStatic] private static Atmosphere _scratch;

        /// <summary>
        /// One planet tick: decides, burns the tank in place, and books the heat. Does not touch the
        /// game's heat counters or anything outside <paramref name="tank"/>; the caller applies
        /// <see cref="Burning.Heats"/>.
        /// </summary>
        public FireTick Step(GlobalGasMix tank, FireConditions now, FireTick previous, StoredHeats heats)
        {
            GasMixture sample = Sample(tank, now.PeakKelvin);
            if (!EnoughToBurn(sample))
            {
                return NothingToBurn.Instance;
            }
            HeldGases scarce = ScarceSide(sample);
            bool wasLitByHeat = previous is Burning { Cause: FireCause.Heat };
            GasMixture gate = wasLitByHeat ? Sample(tank, now.PeakKelvin + FlappingMarginKelvin) : sample;
            FireCause? cause = LitByHeat(gate) ? FireCause.Heat : now.Sparked ? FireCause.Spark : (FireCause?)null;
            if (!cause.HasValue)
            {
                return new Unlit(scarce);
            }
            double rate = Rate(sample);
            Burnt burnt = Burn(tank, rate, now.NowKelvin);
            double added = NetHeat(burnt, now.NowKelvin);
            StoredHeats after = heats.Scaled(CapacityFactor(now.CapacityJoulesPerKelvin, burnt.CapacityChange)).PlusCombustion(added);
            return new Burning(cause.Value, scarce, rate, burnt, after, added);
        }

        /// <summary>
        /// One 8000 L cell's worth of the tank's gas at <paramref name="kelvin"/>: each gas times the
        /// cell's share of the tank's volume, as the game builds its own one-cell copy of the planet
        /// (PlanetaryAtmosphereSimulation.TickPlanetarySimulation). Liquids are the sea, not air.
        /// </summary>
        public virtual GasMixture Sample(GlobalGasMix tank, double kelvin)
        {
            return Copy(tank, (Chemistry.GridVolume / tank.Volume).ToDouble(), kelvin);
        }

        /// <summary>The game's own self-ignition test on the sample, at the sample's temperature.</summary>
        public virtual bool LitByHeat(GasMixture sample) => sample.IsAutoIgnition();

        /// <summary>
        /// The side of the reaction a complete burn uses up, found by asking the game: the sample burnt
        /// to completion by GasMixture.Combust, which shares every oxidiser between the fuels by what
        /// each needs. Every oxidiser if the oxidisers come out empty, every fuel if the fuels do, both
        /// if both; a hypergolic that burns away is a side of its own.
        /// </summary>
        public virtual HeldGases ScarceSide(GasMixture sample)
        {
            GasMixture burnt = GasMixtureHelper.Create(sample);
            double oxidiser = sample.TotalOxidiser.ToDouble();
            double fuel = sample.TotalFuel.ToDouble();
            double hypergolic = sample.TotalHypergolics.ToDouble();
            burnt.Combust(1.0, out _, out _);
            double oxidiserLeft = burnt.TotalOxidiser.ToDouble();
            double fuelLeft = burnt.TotalFuel.ToDouble();
            HeldGases held = HeldGases.None;
            if (oxidiser > 0.0 && fuel > 0.0)
            {
                bool oxidiserGone = oxidiserLeft <= oxidiser * UsedUp;
                bool fuelGone = fuelLeft <= fuel * UsedUp;
                // A complete burn always empties one side; should a game build ever leave both with a
                // little, the side with the smaller share of itself left is the one that runs out.
                if (!oxidiserGone && !fuelGone)
                {
                    oxidiserGone = oxidiserLeft / oxidiser <= fuelLeft / fuel;
                    fuelGone = !oxidiserGone;
                }
                held = held.With(oxidiserGone ? FireGases.OxidiserSide : HeldGases.None)
                    .With(fuelGone ? FireGases.FuelSide : HeldGases.None);
            }
            if (hypergolic > 0.0 && burnt.TotalHypergolics.ToDouble() <= hypergolic * UsedUp)
            {
                held = held.With(FireGases.HypergolicSide);
            }
            return held;
        }

        /// <summary>What the outdoor air is not handed after <paramref name="tick"/>: the scarce side, only while burning.</summary>
        public virtual HeldGases HeldBack(FireTick tick) => tick is Burning burning ? burning.Scarce : HeldGases.None;

        /// <summary>
        /// The armed hold-back: the side the planet's fire would use up, whenever one cell's worth of
        /// the planet's air would light itself at the day's hottest hour plus <see cref="FlappingMarginKelvin"/>,
        /// whether or not anything burns yet; none while it would not. Held from the tick before an
        /// oxidiser arrives, so the tick a leak drains into the planet hands none of it out. The margin
        /// arms it before a fire lit by heat can light and keeps it armed until that fire is out.
        /// </summary>
        public virtual HeldGases Armed(GlobalGasMix tank, double peakKelvin)
        {
            GasMixture gate = Sample(tank, peakKelvin + FlappingMarginKelvin);
            return LitByHeat(gate) ? SideToHold(gate) : HeldGases.None;
        }

        /// <summary>
        /// <see cref="ScarceSide"/>, with a side the planet holds none of counted as used up: a fuel
        /// planet holding no oxidiser yet holds every oxidiser, an oxidiser planet with no fuel every
        /// fuel. Where it holds both, the game's own complete burn decides, as for a fire.
        /// </summary>
        public virtual HeldGases SideToHold(GasMixture sample)
        {
            bool oxidiser = sample.TotalOxidiser.ToDouble() > 0.0;
            bool fuel = sample.TotalFuel.ToDouble() > 0.0;
            HeldGases scarce = ScarceSide(sample);
            return fuel && !oxidiser ? scarce.With(FireGases.OxidiserSide)
                : oxidiser && !fuel ? scarce.With(FireGases.FuelSide)
                : scarce;
        }

        /// <summary>
        /// Burns the tank's gas in place: a copy of all of it at the planet's temperature through the
        /// game's GasMixture.Combust at <paramref name="rate"/>, every gas written back. The game splits
        /// each oxidiser between the fuels by need and adds its own products. Liquids are never read or
        /// written.
        /// </summary>
        public virtual Burnt Burn(GlobalGasMix tank, double rate, double kelvin)
        {
            GasMixture whole = Copy(tank, 1.0, kelvin);
            double capacityBefore = tank.GetHeatCapacity().ToDouble();
            double oxidiser = whole.TotalOxidiser.ToDouble();
            double fuel = whole.TotalFuel.ToDouble();
            double hypergolic = whole.TotalHypergolics.ToDouble();
            double energy = whole.Combust(rate, out _, out _).ToDouble();
            foreach (Chemistry.GasType type in FireGases.Gases)
            {
                tank.Set(new MoleQuantity(Math.Max(0.0, whole.GetMoleValue(type).Quantity.ToDouble())), type);
            }
            return new Burnt(energy, tank.GetHeatCapacity().ToDouble() - capacityBefore,
                oxidiser - whole.TotalOxidiser.ToDouble(), fuel - whole.TotalFuel.ToDouble(), hypergolic - whole.TotalHypergolics.ToDouble());
        }

        /// <summary>
        /// The heat a burn adds, booked as the game books a burnt cell: the heat of combustion goes into
        /// the gas and the reactants' heat is carried into products that hold more heat per kelvin
        /// (CombustionResult.RunCombustion), so the temperature rises by the heat of combustion less the
        /// products' extra heat capacity times the temperature.
        /// </summary>
        public virtual double NetHeat(Burnt burnt, double kelvin)
        {
            double net = burnt.Energy - burnt.CapacityChange * kelvin;
            return double.IsNaN(net) || double.IsInfinity(net) ? 0.0 : net;
        }

        /// <summary>
        /// One tick of fading toward zero, then the added heat limit. Latent heat and the external heat
        /// that is not combustion heat are each held within the limit, in kelvin of the planet's heat
        /// capacity. Combustion heat fades with the rest and is not limited. A counter that is not a
        /// number goes to zero: the game saves both, and a saved NaN would poison the planet.
        /// </summary>
        public virtual StoredHeats Settle(StoredHeats heats, double capacity, double? halfLifeMinutes, double limitKelvin, double tickSeconds)
        {
            double fade = halfLifeMinutes.HasValue && halfLifeMinutes.Value > 0.0
                ? Math.Pow(0.5, tickSeconds / (halfLifeMinutes.Value * 60.0))
                : 1.0;
            double limit = Math.Max(0.0, limitKelvin) * Math.Max(0.0, capacity);
            if (double.IsNaN(limit))
            {
                limit = 0.0;
            }
            double combustion = Finite(heats.Combustion) * fade;
            double rest = Bound(Finite(heats.External) * fade - combustion, limit);
            return new StoredHeats(Bound(Finite(heats.Latent) * fade, limit), combustion + rest, combustion);
        }

        /// <summary>The game's own quantity test in Atmosphere.TryCombust: a hypergolic, or a fuel and an oxidiser.</summary>
        public static bool EnoughToBurn(GasMixture sample)
        {
            MoleQuantity least = Chemistry.MINIMUM_QUANTITY_MOLES;
            return !(sample.TotalHypergolics < least && (sample.TotalOxidiser < least || sample.TotalFuel < least));
        }

        /// <summary>
        /// The game's own share of the scarce side burnt in a tick, Atmosphere.GetCombustionMultiplierCurved
        /// on the sample. Virtual only because that method needs Unity's runtime library (Math.Clamp),
        /// so tools/PatchCheck runs the rest of the check with the formula written out in its place.
        /// </summary>
        public virtual double Rate(GasMixture sample)
        {
            Atmosphere scratch = _scratch ??= new Atmosphere();
            scratch.GasMixture = sample;
            try
            {
                return scratch.GetCombustionMultiplierCurved();
            }
            finally
            {
                scratch.GasMixture = GasMixtureHelper.Create();
            }
        }

        /// <summary>
        /// The tank's gases times <paramref name="scale"/>, at <paramref name="kelvin"/>. Built here
        /// rather than with GasMixtureHelper.Create(GlobalGasMix, ...), which works out the planet's
        /// temperature for itself and is the method the hold-back watches; the temperature is set the
        /// way that method sets it, the heat capacity times the temperature shared out by the setter.
        /// </summary>
        public static GasMixture Copy(GlobalGasMix tank, double scale, double kelvin)
        {
            GasMixture mixture = GasMixtureHelper.Create();
            foreach (Chemistry.GasType type in FireGases.Gases)
            {
                mixture.SetMoleValue(type, new MoleQuantity(Math.Max(0.0, tank.Get(type).ToDouble() * scale)), MoleEnergy.Zero);
            }
            mixture.TotalEnergy = new MoleEnergy(mixture.HeatCapacity, new TemperatureKelvin(Math.Max(0.0, kelvin)));
            return mixture;
        }

        /// <summary>
        /// The temperature above which one cell's worth lights itself, the way IsAutoIgnition decides
        /// it, or null when it never does because no fuel holds more than the game's mole a cell. For
        /// the readout only: the rule asks the game, and the load-time check holds the two together.
        /// </summary>
        public static double? IgnitionKelvin(GasMixture sample)
        {
            MoleQuantity mole = GasMixture.MinCombustionMoles;
            double offset = Math.Min(
                sample.NitrousOxide.Quantity + sample.LiquidNitrousOxide.Quantity > mole ? Chemistry.AutoIgnitionOffsetNitrogenDioxide.ToDouble() : 0.0,
                sample.Ozone.Quantity + sample.LiquidOzone.Quantity > mole ? Chemistry.AutoIgnitionOffsetOzone.ToDouble() : 0.0);
            double? lowest = null;
            if (sample.Methane.Quantity + sample.LiquidMethane.Quantity > mole)
            {
                lowest = Lower(lowest, Chemistry.AutoIgnitionMethane.ToDouble() + offset);
            }
            if (sample.Hydrogen.Quantity + sample.LiquidHydrogen.Quantity > mole)
            {
                lowest = Lower(lowest, Chemistry.AutoIgnitionHydrogen.ToDouble() + offset);
            }
            if (sample.LiquidAlcohol.Quantity > mole)
            {
                lowest = Lower(lowest, Chemistry.AutoIgnitionAlcohol.ToDouble() + offset);
            }
            if (sample.Hydrazine.Quantity + sample.LiquidHydrazine.Quantity > mole)
            {
                lowest = Lower(lowest, Chemistry.AutoIgnitionHydrazine.ToDouble());
            }
            return lowest;
        }

        /// <summary>How much the stored heats grow when the planet's heat capacity moves by <paramref name="change"/>, so their kelvin hold.</summary>
        public static double CapacityFactor(double capacityBefore, double change)
        {
            return capacityBefore > 0.0 && !double.IsInfinity(capacityBefore) && !double.IsNaN(change)
                ? Math.Max(0.0, capacityBefore + change) / capacityBefore
                : 1.0;
        }

        private static double? Lower(double? a, double b) => a.HasValue ? Math.Min(a.Value, b) : b;

        private static double Bound(double energy, double limit) => Math.Max(-limit, Math.Min(limit, energy));

        private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
    }
}
