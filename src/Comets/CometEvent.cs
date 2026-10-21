using System;
using Assets.Scripts.Networking;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// One comet as the host decided it, sent once and played the same on every machine from the
    /// same numbers against the clock they share (docs/COMET.md section 1). Nothing in a show is
    /// decided on a client: where it lands, how it travels, which reactions it makes and every
    /// random choice come from here.
    /// </summary>
    internal sealed class CometEvent
    {
        /// <summary>Seconds between the command and the comet first showing, so clients have it in time.</summary>
        public const double LeadSeconds = 1.0;

        /// <summary>Seconds from first sight to the impact.</summary>
        public const double ApproachSeconds = 15.0;

        /// <summary>Seconds from first sight until the show is gone. The darker sky outlasts it.</summary>
        public const double EndSeconds = 115.0;

        public int Sequence;

        /// <summary>When the comet first shows, on <see cref="Clock"/>.</summary>
        public double StartClock;

        public CometPlan Plan;

        public Vector3 Impact;
        public Vector3 Start;

        /// <summary>Unit vector the comet travels along, from <see cref="Start"/> to <see cref="Impact"/>.</summary>
        public Vector3 Travel;

        /// <summary>Every random choice in the show is drawn from this.</summary>
        public int Seed;

        public CometReactions.Reactions Reactions;

        /// <summary>The host called it off: stop showing it.</summary>
        public bool CalledOff;

        /// <summary>
        /// Host only, never sent: the comet brought down by rocket this is the impact of, 0 for a console comet. Its
        /// gas is CometLand's at the end of the fall, not the show's.
        /// </summary>
        public int RocketComet;

        /// <summary>
        /// The clock every machine shares: the host's real-time seconds, sent to joining players and in
        /// every state update, and advanced each frame in between.
        /// </summary>
        public static double Clock => OrbitalSimulation.System?.TotalRealTimeSeconds ?? 0.0;

        public double ImpactClock => StartClock + ApproachSeconds;

        /// <summary>When nothing of it is left to see, the sky included.</summary>
        public double OverClock => Math.Max(StartClock + EndSeconds, ImpactClock + CometRules.SkyClearSeconds);

        public bool SameAs(CometEvent other) => other != null && other.Sequence == Sequence && other.StartClock == StartClock;

        public void Write(RocketBinaryWriter writer)
        {
            writer.WriteInt32(Sequence);
            writer.WriteDouble(StartClock);
            Plan.Write(writer);
            writer.WriteVector3(Impact);
            writer.WriteVector3(Start);
            writer.WriteVector3(Travel);
            writer.WriteInt32(Seed);
            writer.WriteDouble(Reactions.Steam);
            writer.WriteDouble(Reactions.Burning);
            writer.WriteDouble(Reactions.DryIce);
            writer.WriteDouble(Reactions.Toxic);
            writer.WriteDouble(Reactions.Ionised);
            writer.WriteBoolean(CalledOff);
        }

        /// <summary>Reads one whole event; null when it is not one this game can show.</summary>
        public static CometEvent Read(RocketBinaryReader reader)
        {
            CometEvent read = new CometEvent
            {
                Sequence = reader.ReadInt32(),
                StartClock = reader.ReadDouble(),
                Plan = CometPlan.Read(reader),
            };
            read.Impact = reader.ReadVector3();
            read.Start = reader.ReadVector3();
            read.Travel = reader.ReadVector3();
            read.Seed = reader.ReadInt32();
            double steam = reader.ReadDouble();
            double burning = reader.ReadDouble();
            double dryIce = reader.ReadDouble();
            double toxic = reader.ReadDouble();
            double ionised = reader.ReadDouble();
            read.Reactions = new CometReactions.Reactions(Unit(steam), Unit(burning), Unit(dryIce), Unit(toxic), Unit(ionised));
            read.CalledOff = reader.ReadBoolean();
            bool valid = read.Plan != null && !double.IsNaN(read.StartClock) && !double.IsInfinity(read.StartClock)
                && Finite(read.Impact) && Finite(read.Start) && Finite(read.Travel) && read.Travel.sqrMagnitude > 0.5f;
            return valid ? read : null;
        }

        private static double Unit(double value) => double.IsNaN(value) ? 0.0 : Math.Max(0.0, Math.Min(1.0, value));

        private static bool Finite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
                && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }
    }
}
