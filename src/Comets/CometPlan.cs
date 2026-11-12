using System;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;

namespace TerraformingReloaded.Comets
{
    /// <summary>What one comet is: how much it brings, of what, and where it comes down.</summary>
    internal sealed class CometPlan
    {
        /// <summary>More gases than this in one comet is not a comet anyone typed.</summary>
        private const int MaxGases = 32;

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

        /// <summary>Metres from the player to the impact; null to land as close as is safe.</summary>
        public float? Range;

        /// <summary>How big the show is drawn, from how much the comet brings (<see cref="CometRules.Scale"/>).</summary>
        public float Scale => (float)CometRules.Shipped.Scale(Moles);

        /// <summary>What the joining machines need: the turn and range only decided where it lands.</summary>
        public void Write(RocketBinaryWriter writer)
        {
            writer.WriteDouble(Moles);
            writer.WriteInt32(Types.Length);
            for (int i = 0; i < Types.Length; i++)
            {
                writer.WriteUInt32((uint)Types[i]);
                writer.WriteDouble(Shares[i]);
            }
            writer.WriteBoolean(Kelvin.HasValue);
            writer.WriteDouble(Kelvin ?? 0.0);
            writer.WriteBoolean(Preview);
        }

        /// <summary>Null when what was read is not a comet this game can show.</summary>
        public static CometPlan Read(RocketBinaryReader reader)
        {
            CometPlan plan = new CometPlan { Moles = reader.ReadDouble() };
            int count = reader.ReadInt32();
            if (count < 1 || count > MaxGases)
            {
                return null;
            }
            plan.Types = new Chemistry.GasType[count];
            plan.Shares = new double[count];
            bool valid = Finite(plan.Moles) && plan.Moles > 0.0;
            for (int i = 0; i < count; i++)
            {
                uint type = reader.ReadUInt32();
                double share = reader.ReadDouble();
                plan.Types[i] = (Chemistry.GasType)type;
                plan.Shares[i] = share;
                valid &= Enum.IsDefined(typeof(Chemistry.GasType), plan.Types[i]) && Finite(share) && share >= 0.0 && share <= 1.0;
            }
            bool hasKelvin = reader.ReadBoolean();
            double kelvin = reader.ReadDouble();
            plan.Kelvin = hasKelvin ? kelvin : (double?)null;
            plan.Preview = reader.ReadBoolean();
            return valid && (!hasKelvin || Finite(kelvin)) ? plan : null;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
