using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The colour each gas burns in a comet's fire and plume. Loosely after what the gas does in a
    /// flame or an aurora (nitrogen violet, hydrogen red, sodium salts orange), pushed toward what
    /// reads at a distance rather than what a spectrometer would say. A liquid takes its gas's colour.
    /// </summary>
    internal static class CometPalette
    {
        private static readonly Dictionary<string, Color> ByName = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "Oxygen", new Color(0.45f, 0.75f, 1.00f) },
            { "Nitrogen", new Color(0.82f, 0.42f, 1.00f) },
            { "CarbonDioxide", new Color(1.00f, 0.58f, 0.22f) },
            { "Methane", new Color(0.30f, 1.00f, 0.50f) },
            { "Pollutant", new Color(0.90f, 0.86f, 0.18f) },
            { "Water", new Color(0.80f, 0.93f, 1.00f) },
            { "Steam", new Color(0.86f, 0.95f, 1.00f) },
            { "PollutedWater", new Color(0.70f, 0.80f, 0.55f) },
            { "NitrousOxide", new Color(1.00f, 0.32f, 0.66f) },
            { "Hydrogen", new Color(1.00f, 0.22f, 0.28f) },
            { "Helium", new Color(1.00f, 0.84f, 0.38f) },
            { "Hydrazine", new Color(0.58f, 0.30f, 1.00f) },
            { "Alcohol", new Color(0.38f, 0.58f, 1.00f) },
            { "Ozone", new Color(0.28f, 0.52f, 1.00f) },
            { "HydrochloricAcid", new Color(0.70f, 1.00f, 0.20f) },
            { "Silanol", new Color(1.00f, 0.95f, 0.80f) },
            { "SodiumChloride", new Color(1.00f, 0.70f, 0.15f) },
        };

        private static readonly Color Unknown = new Color(0.95f, 0.92f, 0.85f);

        public static Color Of(Chemistry.GasType type)
        {
            string name = type.ToString();
            if (ByName.TryGetValue(name, out Color colour))
            {
                return colour;
            }
            if (name.StartsWith("Liquid", StringComparison.Ordinal) && ByName.TryGetValue(name.Substring(6), out colour))
            {
                return colour;
            }
            return Unknown;
        }

        /// <summary>The mix as one colour: mole-weighted, then brought back to full brightness.</summary>
        public static Color Blend(IList<Chemistry.GasType> types, IList<double> shares)
        {
            Color sum = Color.black;
            for (int i = 0; i < types.Count; i++)
            {
                sum += Of(types[i]) * (float)shares[i];
            }
            float peak = Mathf.Max(sum.r, Mathf.Max(sum.g, sum.b));
            Color blended = peak > 0f ? sum / peak : Unknown;
            blended.a = 1f;
            return blended;
        }

        /// <summary>
        /// A stepped gradient with one band per gas, as wide as its share, for particle systems that
        /// pick a random colour from it: a plume of mostly carbon dioxide with some nitrous oxide is
        /// mostly amber with pink running through it, not one muddy average. Unity holds eight keys.
        /// </summary>
        public static Gradient Bands(IList<Chemistry.GasType> types, IList<double> shares)
        {
            List<int> order = new List<int>();
            for (int i = 0; i < types.Count; i++)
            {
                order.Add(i);
            }
            order.Sort((a, b) => shares[b].CompareTo(shares[a]));
            if (order.Count > 8)
            {
                order.RemoveRange(8, order.Count - 8);
            }
            double total = 0.0;
            foreach (int i in order)
            {
                total += shares[i];
            }
            GradientColorKey[] keys = new GradientColorKey[order.Count];
            double edge = 0.0;
            for (int k = 0; k < order.Count; k++)
            {
                edge += shares[order[k]] / total;
                keys[k] = new GradientColorKey(Of(types[order[k]]), k == order.Count - 1 ? 1f : (float)edge);
            }
            Gradient gradient = new Gradient { mode = GradientMode.Fixed };
            gradient.SetKeys(keys, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    }
}
