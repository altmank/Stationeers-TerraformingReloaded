using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Atmospherics;
using TerraformingReloaded.Patching;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Which reactions a comet's material makes as it lands, each as strong as its share
    /// (docs/COMET.md section 6). Worked out once, by the host, and sent with the event, so every
    /// machine draws the same explosion. A liquid counts as its gas. Fuels and oxidisers are
    /// <see cref="FireGases"/>, the mod's own lists.
    /// </summary>
    public static class CometReactions
    {
        /// <summary>
        /// A fireball draws in several times its own volume of the air around it, so a fuel burns in
        /// a planet's air with this many times the oxidiser that air holds per mole.
        /// </summary>
        public const double AirDrawnIn = 4.0;

        private static readonly Chemistry.GasType[] Steamy = { Chemistry.GasType.Water, Chemistry.GasType.Steam, Chemistry.GasType.PollutedWater };
        private static readonly Chemistry.GasType[] DryIce = { Chemistry.GasType.CarbonDioxide };
        private static readonly Chemistry.GasType[] Toxic = { Chemistry.GasType.Pollutant, Chemistry.GasType.HydrochloricAcid };
        private static readonly Chemistry.GasType[] Ionised = { Chemistry.GasType.Nitrogen, Chemistry.GasType.Helium };

        /// <summary>
        /// The strength of each reaction, 0 to 1. <paramref name="planetOxidiserShare"/> is the share
        /// of the planet's air, by moles, that is an oxidiser.
        /// </summary>
        public static Reactions Of(IList<Chemistry.GasType> types, IList<double> shares, double planetOxidiserShare)
        {
            double steam = 0.0, fuel = 0.0, oxidiser = 0.0, hypergolic = 0.0, dryIce = 0.0, toxic = 0.0, ionised = 0.0;
            for (int i = 0; i < types.Count; i++)
            {
                double share = Math.Max(0.0, shares[i]);
                Chemistry.GasType gas = GasOf(types[i]);
                if (Array.IndexOf(Steamy, gas) >= 0)
                {
                    steam += share;
                }
                if (Array.IndexOf(DryIce, gas) >= 0)
                {
                    dryIce += share;
                }
                if (Array.IndexOf(Toxic, gas) >= 0)
                {
                    toxic += share;
                }
                if (Array.IndexOf(Ionised, gas) >= 0)
                {
                    ionised += share;
                }
                if (Array.IndexOf(FireGases.Hypergolics, gas) >= 0)
                {
                    hypergolic += share;
                }
                else if (Array.IndexOf(FireGases.Fuels, gas) >= 0)
                {
                    fuel += share;
                }
                else if (Array.IndexOf(FireGases.Oxidisers, gas) >= 0)
                {
                    oxidiser += share;
                }
            }
            double air = double.IsNaN(planetOxidiserShare) ? 0.0 : Math.Max(0.0, Math.Min(1.0, planetOxidiserShare));
            double burning = hypergolic + Math.Min(fuel, oxidiser + AirDrawnIn * air);
            return new Reactions(Clamp01(steam), Clamp01(burning), Clamp01(dryIce), Clamp01(toxic), Clamp01(ionised));
        }

        /// <summary>The gas a liquid is, by the game's own naming: LiquidCarbonDioxide is CarbonDioxide.</summary>
        public static Chemistry.GasType GasOf(Chemistry.GasType type)
        {
            string name = type.ToString();
            if (name.StartsWith("Liquid", StringComparison.Ordinal)
                && Enum.TryParse(name.Substring(6), out Chemistry.GasType gas)
                && Enum.IsDefined(typeof(Chemistry.GasType), gas))
            {
                return gas;
            }
            return type;
        }

        private static double Clamp01(double t) => t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;

        /// <summary>Worked cases. Returns the first that does not hold, or null.</summary>
        public static string Check()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            Chemistry.GasType co2 = Chemistry.GasType.CarbonDioxide;
            (Chemistry.GasType[] Types, double[] Shares, double Air, Reactions Expected, string What)[] cases =
            {
                (new[] { Chemistry.GasType.Water }, new[] { 1.0 }, 0.0, new Reactions(1, 0, 0, 0, 0), "an ice comet makes steam"),
                (new[] { Chemistry.GasType.Methane }, new[] { 1.0 }, 0.0, new Reactions(0, 0, 0, 0, 0), "volatiles on an airless world do not burn"),
                (new[] { Chemistry.GasType.Methane }, new[] { 1.0 }, 0.21, new Reactions(0, 0.84, 0, 0, 0), "volatiles burn in breathable air, as far as the air drawn in allows"),
                (new[] { Chemistry.GasType.Methane, Chemistry.GasType.Oxygen }, new[] { 0.5, 0.5 }, 0.0, new Reactions(0, 0.5, 0, 0, 0), "a comet that brings its own oxygen burns"),
                (new[] { Chemistry.GasType.LiquidHydrazine }, new[] { 1.0 }, 0.0, new Reactions(0, 1, 0, 0, 0), "hydrazine burns on its own"),
                (new[] { Chemistry.GasType.LiquidCarbonDioxide }, new[] { 1.0 }, 0.0, new Reactions(0, 0, 1, 0, 0), "liquid carbon dioxide is dry ice"),
                (new[] { Chemistry.GasType.Pollutant, Chemistry.GasType.HydrochloricAcid }, new[] { 0.5, 0.5 }, 0.0, new Reactions(0, 0, 0, 1, 0), "pollutant and acid are toxic"),
                (new[] { Chemistry.GasType.Nitrogen, Chemistry.GasType.Helium }, new[] { 0.7, 0.3 }, 0.0, new Reactions(0, 0, 0, 0, 1), "nitrogen and helium are ionised"),
                (new[] { co2, Chemistry.GasType.Water, Chemistry.GasType.NitrousOxide }, new[] { 0.6, 0.25, 0.15 }, 0.0, new Reactions(0.25, 0, 0.6, 0, 0), "an oxidiser with no fuel does not burn"),
            };
            foreach (var (types, shares, air, expected, what) in cases)
            {
                Reactions got = Of(types, shares, air);
                if (!got.Near(expected))
                {
                    return string.Format(c, "{0}: got {1}, expected {2}", what, got, expected);
                }
            }
            if (GasOf(Chemistry.GasType.LiquidMethane) != Chemistry.GasType.Methane || GasOf(co2) != co2)
            {
                return "a liquid is not read as its gas";
            }
            return null;
        }

        /// <summary>The strength of each reaction, 0 to 1. A plain struct: net472 has no IsExternalInit.</summary>
        public readonly struct Reactions
        {
            public readonly double Steam;
            public readonly double Burning;
            public readonly double DryIce;
            public readonly double Toxic;
            public readonly double Ionised;

            public Reactions(double steam, double burning, double dryIce, double toxic, double ionised)
            {
                Steam = steam;
                Burning = burning;
                DryIce = dryIce;
                Toxic = toxic;
                Ionised = ionised;
            }

            public bool Near(Reactions other)
            {
                return Math.Abs(Steam - other.Steam) < 1e-9 && Math.Abs(Burning - other.Burning) < 1e-9 && Math.Abs(DryIce - other.DryIce) < 1e-9
                    && Math.Abs(Toxic - other.Toxic) < 1e-9 && Math.Abs(Ionised - other.Ionised) < 1e-9;
            }

            public override string ToString()
            {
                return string.Format(CultureInfo.InvariantCulture, "steam {0:0.###}, burning {1:0.###}, dry ice {2:0.###}, toxic {3:0.###}, ionised {4:0.###}",
                    Steam, Burning, DryIce, Toxic, Ionised);
            }
        }
    }
}
