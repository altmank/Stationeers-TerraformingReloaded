using System;
using Assets.Scripts.Atmospherics;

namespace TerraformingReloaded.Comets
{
    /// <summary>What one comet is: how much it brings, of what, and where it comes down.</summary>
    internal sealed class CometPlan
    {
        public double Moles;

        public Chemistry.GasType[] Types;

        /// <summary>Each gas's share of <see cref="Moles"/>, summing to 1.</summary>
        public double[] Shares;

        /// <summary>The temperature the gas arrives at; null for the planet's own.</summary>
        public double? Kelvin;

        /// <summary>The show only: nothing reaches the planet.</summary>
        public bool Preview;

        /// <summary>Degrees to the right of where the player is looking at launch.</summary>
        public float TurnDegrees;

        /// <summary>Metres from the player to the impact; null to size it from the comet.</summary>
        public float? Range;

        /// <summary>
        /// How big the show is drawn, from how much the comet brings, on a log scale: 0.5 at
        /// 10,000 mol, 1 at 100,000, 1.5 at a million, 2 at ten million, capped at 3.
        /// </summary>
        public float Scale => (float)Math.Max(0.4, Math.Min(3.0, (Math.Log10(Math.Max(1.0, Moles)) - 3.0) / 2.0));
    }
}
