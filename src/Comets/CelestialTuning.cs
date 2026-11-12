using System;
using System.Collections.Generic;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// One tuning number of the Celestial terraforming section (docs/COMET-ROCKETS.md, "Configuration:
    /// Celestial terraforming"). The one declaration of the setting: the config entry, a world's settings
    /// file, the console's terraform set and the planet sync all take its key, its default and its range
    /// from here, so none of them can hold a value another would refuse. Plain BCL, because
    /// <see cref="Patching.Sidecar"/> reads these and nothing under Patching/ may reference BepInEx.
    /// </summary>
    public sealed class CelestialSetting
    {
        /// <summary>Its place in <see cref="CelestialSettings.All"/> and in a <see cref="CelestialTuning"/>.</summary>
        public readonly int Index;

        /// <summary>The config key, the settings file's key and the console's key.</summary>
        public readonly string Key;

        public readonly string Label;

        /// <summary>For the console's readout; empty for a plain number.</summary>
        public readonly string Unit;

        public readonly string Description;
        public readonly double Default;
        public readonly Range Range;

        /// <summary>The config editor's number format.</summary>
        public readonly string Format;

        internal CelestialSetting(int index, string key, string label, string unit, double value, Range range, string format, string description)
        {
            Index = index;
            Key = key;
            Label = label;
            Unit = unit;
            Default = value;
            Range = range;
            Format = format;
            Description = description;
        }
    }

    /// <summary>
    /// Every tuning number of comets made and brought down by rocket, with its default. Each default is
    /// derived in docs/COMET-ROCKETS.md beside it, and <see cref="CelestialRules.Check"/> holds the
    /// derivable ones to their derivation.
    /// </summary>
    public static class CelestialSettings
    {
        public const string Section = "Celestial terraforming";

        /// <summary>Where the section starts in the config editor's order, after every other section.</summary>
        public const int FirstOrder = 100;

        public static readonly CelestialSetting CutSharePerCycle = new CelestialSetting(0,
            "CutSharePerCycle", "Cut per drill cycle (share of the site)", "", 0.001, new Range(0.00001, 1.0), "%.5f",
            "Share of its site a comet grows by with each drill cycle of Cut. At 0.001 a whole comet is 1,000 drill cycles: about two and a half hours at a typical site with one drill, and on average the working life of one basic drill head or half that of an ice drill head.");

        public static readonly CelestialSetting CometSizeMultiplier = new CelestialSetting(1,
            "CometSizeMultiplier", "Comet size at Standard planet size (x its site)", "x", 5.0, new Range(0.1, 100.0), "%.1f",
            "How many times the gas of the site it is cut from a comet holds once the whole site is cut, on a Standard-size planet. It follows the planet's size: five times larger on a Long planet, a fifth on a Short one, but never under 1. At 5, one comet from the biggest ice site brings less than a third of the air a Mars or Moon needs before you can breathe outside without a suit, on every planet size.");

        public static readonly CelestialSetting OrbitSeconds = new CelestialSetting(2,
            "OrbitSeconds", "Comet orbit (s)", "s", 372.0, new Range(30.0, 7200.0), "%.0f",
            "Real seconds a comet waiting in orbit takes to go once round the planet, the same on every world. It passes over once an orbit, a little further round each time as the planet turns under it. 372 is how long Phobos takes on Mars.");

        public static readonly CelestialSetting OrbitInclinationDegrees = new CelestialSetting(3,
            "OrbitInclinationDegrees", "Comet orbit tilt (degrees)", "degrees", 60.0, new Range(0.0, 180.0), "%.1f",
            "Tilt of a waiting comet's orbit to the planet's equator. The more it is tilted, the further its path across the sky moves from one orbit to the next. At 60, some of its passes go straight overhead on every world, Mimas included.");

        public static readonly CelestialSetting DiscRadiusMetres = new CelestialSetting(4,
            "DiscRadiusMetres", "Landing area radius (m)", "m", 5100.0, new Range(500.0, 20000.0), "%.0f",
            "How far from its launch pad a comet can come down. A comet straight overhead when it is brought down lands on the pad, one on the horizon half this far out, one on the far side of the planet this far out. This is for Mars and Venus; on a world whose distant ground curves away more steeply, the area is smaller to match, so a landing is always in sight.");

        public static readonly CelestialSetting CorrectionMetresPerDeltaV = new CelestialSetting(5,
            "CorrectionMetresPerDeltaV", "Steering per m/s of spare fuel (m)", "m per m/s", 25.0, new Range(0.0, 1000.0), "%.1f",
            "How far a comet's spare fuel can move where it lands, in metres for each metre a second of speed that fuel can still give it. 25 is how far a burn spread over the whole fall moves it sideways.");

        public static readonly CelestialSetting EntryWindowSeconds = new CelestialSetting(6,
            "EntryWindowSeconds", "Fall to the ground (s)", "s", 50.0, new Range(1.0, 600.0), "%.0f",
            "Seconds a comet takes from orbit to the ground, which is as long as its engines can steer it, so few engines on a heavy comet steer it little however much fuel it has. 50 is how long a rocket takes to fall to Mars from the game's usual re-entry height.");

        public static readonly CelestialSetting GroundZeroMetresAt1e6 = new CelestialSetting(7,
            "GroundZeroMetresAt1e6", "Ground zero at 1,000,000 mol (m)", "m", 186.0, new Range(0.0, 5000.0), "%.0f",
            "Radius in which a comet of a million moles destroys every structure and kills anyone there. A bigger comet reaches further by the cube root of its size: twice as far for eight times the gas. 186 is where the pressure wave of such a comet is past what the strongest wall can take.");

        public static readonly CelestialSetting FireballHeatFactor = new CelestialSetting(8,
            "FireballHeatFactor", "Fireball heat (x the fireball)", "x", 1.0, new Range(0.1, 5.0), "%.2f",
            "How far the fireball's heat reaches, as a multiple of the fireball the comet's size draws: structures facing the outside and players outdoors inside it are burned. At 1 the heat reaches exactly as far as the fireball is drawn. Above 1 the fireball is drawn as far as its heat reaches.");

        public static readonly CelestialSetting DebrisRadiusFactor = new CelestialSetting(9,
            "DebrisRadiusFactor", "Falling rocks (x ground zero)", "x", 7.07, new Range(1.0, 50.0), "%.2f",
            "How far falling rocks can hit structures and players, as a multiple of ground zero. 7.07 is where the pressure wave breaks ordinary windows.");

        public static readonly CelestialSetting MaxDamagingRocks = new CelestialSetting(10,
            "MaxDamagingRocks", "Most rocks that hit, per comet", "", 40.0, new Range(0.0, 500.0), "%.0f",
            "Most falling rocks per comet that hit a structure or a player, each a small explosion. Every rock that hits is one of the burning pieces the show draws falling, about 80 of them; the rest, and any over open ground, are only seen.");

        public static readonly CelestialSetting GasArrivingShare = new CelestialSetting(11,
            "GasArrivingShare", "Gas that reaches the planet (share)", "", 0.7, new Range(0.0, 1.0), "%.2f",
            "Share of an ice comet's gas, what is left of it after its flight, that reaches the planet's air when it lands. The rest burns up on the way down.");

        public static readonly CelestialSetting MineralRemovalPerKg = new CelestialSetting(12,
            "MineralRemovalPerKg", "Air a mineral comet removes (kg per kg)", "kg per kg", 0.7, new Range(0.0, 100.0), "%.2f",
            "Kilograms of the planet's air a mineral comet blasts away for every kilogram it weighs. At 0.7 it takes away as much air as an ice comet of the same weight brings.");

        public static readonly CelestialSetting CometMassScale = new CelestialSetting(13,
            "CometMassScale", "Comet weight (share of what was cut)", "", 0.39, new Range(0.0, 10.0), "%.2f",
            "How much a comet weighs in flight, as a share of what the ice or ore cut from its site weighs. The extra gas the comet size setting gives weighs nothing, so a comet cut the same way weighs the same on every planet size. At 0.39 the heaviest comet a Mars map can make needs no more time spent flying its fuel up to it in a big fuel rocket than cutting it took with one drill.");

        public static readonly CelestialSetting MineralKgPerUnit = new CelestialSetting(14,
            "MineralKgPerUnit", "Mineral comet weight per unit of ore (kg)", "kg", 2.592, new Range(0.001, 100.0), "%.3f",
            "What one unit of ore cut into a mineral comet weighs before the comet weight share above. 2.592 kg is a unit of water ice by the game's own figures, 24 mol at 108 g/mol, so a mineral comet weighs what a water ice comet cut the same way does.");

        public static readonly CelestialSetting FuelLitresPerSecond = new CelestialSetting(15,
            "FuelLitresPerSecond", "Fuel into a comet (litres a second)", "L/s", 200.0, new Range(1.0, 100000.0), "%.0f",
            "How fast the Fuel action moves fuel from the rocket's tanks and pipes into a comet: each second, the 2:1 methane and oxygen held in this many litres of those stores. 200 is what a Turbo Volume Pump moves, 100 litres every half-second tick.");

        /// <summary>Every setting, in the order the config editor shows them; each one's place is its <see cref="CelestialSetting.Index"/>.</summary>
        public static readonly CelestialSetting[] All =
        {
            CutSharePerCycle, CometSizeMultiplier, OrbitSeconds, OrbitInclinationDegrees, DiscRadiusMetres,
            CorrectionMetresPerDeltaV, EntryWindowSeconds, GroundZeroMetresAt1e6, FireballHeatFactor,
            DebrisRadiusFactor, MaxDamagingRocks, GasArrivingShare, MineralRemovalPerKg,
            CometMassScale, MineralKgPerUnit, FuelLitresPerSecond,
        };

        /// <summary>The setting with this key, ignoring case, or null.</summary>
        public static CelestialSetting Find(string key)
        {
            foreach (CelestialSetting setting in All)
            {
                if (string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return setting;
                }
            }
            return null;
        }

        /// <summary>
        /// The table's own shape: each setting at its index, every key once, every default inside its
        /// own range. Null when it holds, else the first thing that does not.
        /// </summary>
        public static string CheckTable()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < All.Length; i++)
            {
                CelestialSetting setting = All[i];
                if (setting == null || setting.Index != i)
                {
                    return "the Celestial terraforming setting in place " + i + " does not carry that place";
                }
                if (!keys.Add(setting.Key))
                {
                    return "the Celestial terraforming key " + setting.Key + " is declared twice";
                }
                if (!setting.Range.Holds(setting.Default))
                {
                    return "the default of " + setting.Key + " is outside its own range";
                }
            }
            return null;
        }
    }

    /// <summary>
    /// One whole set of the Celestial terraforming values, never changed once made: a change is a new set,
    /// so code on any thread that takes the set in force reads one consistent set of numbers.
    /// </summary>
    public sealed class CelestialTuning
    {
        private readonly double[] _values;

        public static readonly CelestialTuning Defaults = new CelestialTuning(Array.ConvertAll(CelestialSettings.All, s => s.Default));

        private CelestialTuning(double[] values)
        {
            _values = values;
        }

        public double this[CelestialSetting setting] => _values[setting.Index];

        /// <summary>The same set with one value changed. The caller has checked it against the setting's range.</summary>
        public CelestialTuning With(CelestialSetting setting, double value)
        {
            double[] values = (double[])_values.Clone();
            values[setting.Index] = value;
            return new CelestialTuning(values);
        }

        /// <summary>The values in the order of <see cref="CelestialSettings.All"/>, for the planet sync.</summary>
        public double[] ToArray() => (double[])_values.Clone();

        public double CutSharePerCycle => this[CelestialSettings.CutSharePerCycle];
        /// <summary>The size multiplier setting: K on a Standard-size planet. See <see cref="SizeMultiplierAt"/>.</summary>
        public double CometSizeMultiplier => this[CelestialSettings.CometSizeMultiplier];

        /// <summary>K in force on a planet of this size, a share of the shipped planet (CelestialRules.SizeMultiplier).</summary>
        public double SizeMultiplierAt(double planetSize) => CelestialRules.Shipped.SizeMultiplier(CometSizeMultiplier, planetSize);
        public double OrbitSeconds => this[CelestialSettings.OrbitSeconds];
        public double OrbitInclinationDegrees => this[CelestialSettings.OrbitInclinationDegrees];
        public double DiscRadiusMetres => this[CelestialSettings.DiscRadiusMetres];
        public double CorrectionMetresPerDeltaV => this[CelestialSettings.CorrectionMetresPerDeltaV];
        public double EntryWindowSeconds => this[CelestialSettings.EntryWindowSeconds];
        public double GroundZeroMetresAt1e6 => this[CelestialSettings.GroundZeroMetresAt1e6];
        public double FireballHeatFactor => this[CelestialSettings.FireballHeatFactor];
        public double DebrisRadiusFactor => this[CelestialSettings.DebrisRadiusFactor];
        public double MaxDamagingRocks => this[CelestialSettings.MaxDamagingRocks];
        public double GasArrivingShare => this[CelestialSettings.GasArrivingShare];
        public double MineralRemovalPerKg => this[CelestialSettings.MineralRemovalPerKg];
        public double CometMassScale => this[CelestialSettings.CometMassScale];
        public double MineralKgPerUnit => this[CelestialSettings.MineralKgPerUnit];
        public double FuelLitresPerSecond => this[CelestialSettings.FuelLitresPerSecond];

        /// <summary>The landing disc in force on a world of this curvature and planet size (CelestialRules.DiscRadiusAt).</summary>
        public double DiscRadiusAt(double curvature, double planetSize) =>
            CelestialRules.Shipped.DiscRadiusAt(DiscRadiusMetres, curvature, CometSizeMultiplier, planetSize, this);
    }
}
