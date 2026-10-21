using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Serialization;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// A comet brought down from orbit, falling until it lands (docs/COMET-ROCKETS.md, "Bring it down"). Gone
    /// from the space map and the sky the moment its burn fires; it lands where the burn put it once its fall,
    /// <see cref="FallSeconds"/>, has passed on the clock every machine shares. Never changed once made: a
    /// change is a new one. Like a flight, a fall is held as the moment it went on the clock and the seconds it
    /// had fallen then, so a fall read from a save lands at the clock time it would have.
    ///
    /// The host holds every number here. A joining player's game gets only what it shows and warns with: the
    /// comet, its kind, where and when it lands, and its size (<see cref="Write"/>).
    /// </summary>
    internal sealed class CometLanding
    {
        public readonly int CometId;
        public readonly CometKind Kind;

        /// <summary>
        /// What the show draws. An ice comet: each gas that reaches the planet and its moles, which is also what
        /// it delivers. A mineral comet: the air it would have thrown up at the burn, for the show's colours
        /// only; what it takes is worked out from the air as it is at the impact.
        /// </summary>
        public readonly Chemistry.GasType[] Gases;
        public readonly double[] Moles;

        /// <summary>A mineral comet: its mass before CometMassScale, and the kilograms of air it takes per kilogram, both as at the burn.</summary>
        public readonly double MineralKg;
        public readonly double AirKgPerKg;

        /// <summary>The moles the show and its wave are sized by.</summary>
        public readonly double ShowMoles;

        /// <summary>The impact, a world position on the ground.</summary>
        public readonly double ImpactX;
        public readonly double ImpactY;
        public readonly double ImpactZ;

        /// <summary>Unit vector the comet comes in along.</summary>
        public readonly double TravelX;
        public readonly double TravelY;
        public readonly double TravelZ;

        /// <summary>Every random choice in the show.</summary>
        public readonly int Seed;

        /// <summary>Seconds from the burn to the impact (CelestialTuning.EntryWindowSeconds at the burn).</summary>
        public readonly double FallSeconds;

        /// <summary>When the fall went on the clock: the burn, or the load that brought it back. NaN off the clock.</summary>
        public readonly double MarkClock;

        /// <summary>Seconds it had fallen at <see cref="MarkClock"/>.</summary>
        public readonly double MarkFallen;

        /// <summary>How it was brought down, for the log and tools/LiveCheck. The host's own; empty on a joining player's game.</summary>
        public readonly LandingAim Aim;

        public CometLanding(int cometId, CometKind kind, Chemistry.GasType[] gases, double[] moles, double mineralKg, double airKgPerKg, double showMoles,
            double impactX, double impactY, double impactZ, double travelX, double travelY, double travelZ, int seed, double fallSeconds, double markClock, double markFallen,
            LandingAim aim)
        {
            CometId = cometId;
            Kind = kind;
            Gases = gases ?? new Chemistry.GasType[0];
            Moles = moles ?? new double[0];
            MineralKg = mineralKg;
            AirKgPerKg = airKgPerKg;
            ShowMoles = showMoles;
            ImpactX = impactX;
            ImpactY = impactY;
            ImpactZ = impactZ;
            TravelX = travelX;
            TravelY = travelY;
            TravelZ = travelZ;
            Seed = seed;
            FallSeconds = fallSeconds;
            MarkClock = markClock;
            MarkFallen = markFallen;
            Aim = aim ?? LandingAim.None;
        }

        public bool OnClock => !double.IsNaN(MarkClock);

        /// <summary>When it lands, on the clock; NaN off the clock.</summary>
        public double ImpactClock => MarkClock + (FallSeconds - MarkFallen);

        /// <summary>Seconds it has fallen at <paramref name="clock"/>; off the clock, what it had fallen then.</summary>
        public double FallenAt(double clock) => OnClock ? Math.Max(0.0, MarkFallen + (clock - MarkClock)) : MarkFallen;

        public double TotalMoles
        {
            get
            {
                double total = 0.0;
                foreach (double moles in Moles)
                {
                    total += moles;
                }
                return total;
            }
        }

        /// <summary>The same fall taken off the clock where it stands at <paramref name="clock"/>, as a save takes one off.</summary>
        public CometLanding OffClock(double clock) =>
            new CometLanding(CometId, Kind, Gases, Moles, MineralKg, AirKgPerKg, ShowMoles, ImpactX, ImpactY, ImpactZ, TravelX, TravelY, TravelZ, Seed, FallSeconds, double.NaN, FallenAt(clock), Aim);

        /// <summary>A fall read from a save put back on the clock: from now it falls on from what it had fallen then.</summary>
        public CometLanding BackOnClock(double clock) =>
            new CometLanding(CometId, Kind, Gases, Moles, MineralKg, AirKgPerKg, ShowMoles, ImpactX, ImpactY, ImpactZ, TravelX, TravelY, TravelZ, Seed, FallSeconds, clock, MarkFallen, Aim);

        /// <summary>What is wrong with it as read from a save or the wire, or null. Nothing read is trusted.</summary>
        public string Refusal()
        {
            if (!Enum.IsDefined(typeof(CometKind), Kind))
            {
                return "a kind this build does not know";
            }
            if (Gases.Length != Moles.Length || Gases.Length > 64)
            {
                return "gases that do not match their moles";
            }
            foreach (double moles in Moles)
            {
                if (!Finite(moles) || moles < 0.0 || moles > CometMap.MaxMoles)
                {
                    return "an amount of gas out of range";
                }
            }
            if (!Finite(MineralKg) || MineralKg < 0.0 || MineralKg > 1e15 || !Finite(AirKgPerKg) || AirKgPerKg < 0.0 || !Finite(ShowMoles) || !(ShowMoles > 0.0) || ShowMoles > CometMap.MaxMoles)
            {
                return "a mass or a size out of range";
            }
            if (!Finite(ImpactX) || !Finite(ImpactY) || !Finite(ImpactZ) || Math.Abs(ImpactX) > 1e7 || Math.Abs(ImpactY) > 1e7 || Math.Abs(ImpactZ) > 1e7)
            {
                return "an impact that is not a point in the world";
            }
            double travel = TravelX * TravelX + TravelY * TravelY + TravelZ * TravelZ;
            if (!Finite(travel) || travel < 0.5 || travel > 2.0)
            {
                return "a way in that is not a direction";
            }
            if (!Finite(FallSeconds) || !(FallSeconds > 0.0) || FallSeconds > 1e6 || !Finite(MarkFallen) || MarkFallen < 0.0 || double.IsInfinity(MarkClock))
            {
                return "a fall out of range";
            }
            return null;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        // ---- the wire ---------------------------------------------------------------------------------------

        /// <summary>What a joining player's game shows and warns with.</summary>
        public void Write(RocketBinaryWriter writer)
        {
            writer.WriteInt32(CometId);
            writer.WriteByte((byte)Kind);
            writer.WriteDouble(ShowMoles);
            writer.WriteDouble(ImpactX);
            writer.WriteDouble(ImpactY);
            writer.WriteDouble(ImpactZ);
            writer.WriteDouble(FallSeconds);
            writer.WriteDouble(MarkClock);
            writer.WriteDouble(MarkFallen);
        }

        /// <summary>One whole landing from the host; throws on a shape no host of this build writes, so the caller drops the lot.</summary>
        public static CometLanding Read(RocketBinaryReader reader)
        {
            int id = reader.ReadInt32();
            CometKind kind = (CometKind)reader.ReadByte();
            double show = reader.ReadDouble();
            double x = reader.ReadDouble();
            double y = reader.ReadDouble();
            double z = reader.ReadDouble();
            double fall = reader.ReadDouble();
            double mark = reader.ReadDouble();
            double fallen = reader.ReadDouble();
            CometLanding landing = new CometLanding(id, kind, null, null, 0.0, 0.0, show, x, y, z, 0.0, -1.0, 0.0, 0, fall, mark, fallen, null);
            string wrong = landing.Refusal();
            if (wrong != null)
            {
                throw new InvalidOperationException("the host sent the landing of comet " + id + " with " + wrong);
            }
            return landing;
        }

        // ---- the save ---------------------------------------------------------------------------------------

        public CometLandingRecord ToRecord(double clock)
        {
            CometLandingRecord record = new CometLandingRecord
            {
                Comet = CometId,
                Kind = Kind.ToString(),
                MineralKg = Exact(MineralKg),
                AirKgPerKg = Exact(AirKgPerKg),
                ShowMoles = Exact(ShowMoles),
                Impact = Exact(ImpactX) + " " + Exact(ImpactY) + " " + Exact(ImpactZ),
                Travel = Exact(TravelX) + " " + Exact(TravelY) + " " + Exact(TravelZ),
                Seed = Seed,
                FallSeconds = Exact(FallSeconds),
                Fallen = Exact(FallenAt(clock)),
                Gases = new List<CometSaveGas>(),
                Rocket = Aim.RocketId,
                Bay = Aim.BayId,
                Tracked = Aim.Tracked,
                Sky = Exact(Aim.SkyX) + " " + Exact(Aim.SkyZ),
                Target = Exact(Aim.TargetX) + " " + Exact(Aim.TargetZ),
                Steered = Exact(Aim.Steered),
                FuelSpent = Exact(Aim.FuelSpent),
                FuelLeft = Exact(Aim.FuelLeft),
            };
            for (int i = 0; i < Gases.Length; i++)
            {
                record.Gases.Add(new CometSaveGas { Type = Gases[i].ToString(), Moles = Exact(Moles[i]) });
            }
            return record;
        }

        /// <summary>A landing as a save recorded it, or null with why not. It comes back off the clock, at what it had fallen.</summary>
        public static CometLanding FromRecord(CometLandingRecord record, out string refused)
        {
            refused = null;
            if (record == null || !Enum.TryParse(record.Kind, false, out CometKind kind) || !Enum.IsDefined(typeof(CometKind), kind))
            {
                refused = "a kind this build does not know";
                return null;
            }
            List<Chemistry.GasType> types = new List<Chemistry.GasType>();
            List<double> moles = new List<double>();
            foreach (CometSaveGas gas in record.Gases ?? new List<CometSaveGas>())
            {
                if (gas == null || !Enum.TryParse(gas.Type, false, out Chemistry.GasType type) || !Enum.IsDefined(typeof(Chemistry.GasType), type))
                {
                    refused = "a gas this game does not have (" + gas?.Type + ")";
                    return null;
                }
                types.Add(type);
                moles.Add(Number(gas.Moles));
            }
            double[] impact = Numbers(record.Impact, 3);
            double[] travel = Numbers(record.Travel, 3);
            double[] sky = Numbers(record.Sky, 2);
            double[] target = Numbers(record.Target, 2);
            LandingAim aim = new LandingAim(record.Rocket, record.Bay, record.Tracked, sky[0], sky[1], target[0], target[1], double.NaN, double.NaN, double.NaN, double.NaN,
                Number(record.Steered), Number(record.FuelSpent), Number(record.FuelLeft), 0, double.NaN, double.NaN);
            CometLanding landing = new CometLanding(record.Comet, kind, types.ToArray(), moles.ToArray(), Number(record.MineralKg), Number(record.AirKgPerKg), Number(record.ShowMoles),
                impact[0], impact[1], impact[2], travel[0], travel[1], travel[2], record.Seed, Number(record.FallSeconds), double.NaN, Number(record.Fallen), aim);
            refused = landing.Refusal();
            return refused == null ? landing : null;
        }

        private static string Exact(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

        private static double Number(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

        private static double[] Numbers(string text, int count)
        {
            string[] parts = (text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double[] numbers = new double[count];
            for (int i = 0; i < count; i++)
            {
                numbers[i] = i < parts.Length ? Number(parts[i]) : double.NaN;
            }
            return numbers;
        }

        /// <summary>The landing in one line, every number round-trip exact: how a save and its load are compared.</summary>
        public string Describe(double now)
        {
            return string.Format(CultureInfo.InvariantCulture, "landing of comet {0} | kind {1} | impact ({2:R}, {3:R}, {4:R}) | at clock {5:R} | fallen {6:R} of {7:R} s | show {8:R} mol | mineral {9:R} kg | gases {10}",
                CometId, Kind, ImpactX, ImpactY, ImpactZ, ImpactClock, FallenAt(now), FallSeconds, ShowMoles, MineralKg, Gases.Length);
        }
    }

    /// <summary>How a comet was brought down: the rocket and bay, the sky point, the target and the burn, as the bay's aim had them.</summary>
    internal sealed class LandingAim
    {
        public static readonly LandingAim None = new LandingAim(0L, 0L, false, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN, 0, double.NaN, double.NaN);

        public readonly long RocketId;
        public readonly long BayId;
        public readonly bool Tracked;
        public readonly double SkyX;
        public readonly double SkyZ;
        public readonly double TargetX;
        public readonly double TargetZ;
        public readonly double PadX;
        public readonly double PadZ;
        public readonly double Radius;
        public readonly double Reach;
        public readonly double Steered;
        public readonly double FuelSpent;
        public readonly double FuelLeft;
        public readonly int Frame;
        public readonly double Clock;
        public readonly double Fuel;

        public LandingAim(long rocketId, long bayId, bool tracked, double skyX, double skyZ, double targetX, double targetZ, double padX, double padZ, double radius, double reach,
            double steered, double fuelSpent, double fuelLeft, int frame, double clock, double fuel)
        {
            RocketId = rocketId;
            BayId = bayId;
            Tracked = tracked;
            SkyX = skyX;
            SkyZ = skyZ;
            TargetX = targetX;
            TargetZ = targetZ;
            PadX = padX;
            PadZ = padZ;
            Radius = radius;
            Reach = reach;
            Steered = steered;
            FuelSpent = fuelSpent;
            FuelLeft = fuelLeft;
            Frame = frame;
            Clock = clock;
            Fuel = fuel;
        }
    }

    /// <summary>
    /// A falling comet as a save records it (CometSaveFile, version 3). Every fraction is text of seventeen
    /// significant digits, so the fall read back lands at the same clock time to the last bit.
    /// </summary>
    public class CometLandingRecord
    {
        [XmlAttribute]
        public int Comet;

        [XmlAttribute]
        public string Kind;

        public string MineralKg;
        public string AirKgPerKg;
        public string ShowMoles;

        /// <summary>x y z.</summary>
        public string Impact;

        /// <summary>x y z, a unit vector.</summary>
        public string Travel;

        public int Seed;
        public string FallSeconds;

        /// <summary>Seconds it had fallen at the save.</summary>
        public string Fallen;

        [XmlArray("Gases"), XmlArrayItem("Gas")]
        public List<CometSaveGas> Gases;

        public long Rocket;
        public long Bay;
        public bool Tracked;

        /// <summary>x z of the sky point at the burn.</summary>
        public string Sky;

        /// <summary>x z of the target; NaN NaN untracked.</summary>
        public string Target;

        public string Steered;
        public string FuelSpent;
        public string FuelLeft;
    }

    /// <summary>
    /// A falling comet as this machine sees it, for the console and tools/LiveCheck. A plain struct: net472 has
    /// no IsExternalInit.
    /// </summary>
    public readonly struct LandingView
    {
        public readonly int CometId;
        public readonly CometKind Kind;
        public readonly double ImpactX;
        public readonly double ImpactY;
        public readonly double ImpactZ;

        /// <summary>When it lands on the shared clock; NaN while a fall read from a save waits for its clock.</summary>
        public readonly double ImpactClock;

        public readonly double FallSeconds;
        public readonly double ShowMoles;

        /// <summary>The host's: how the burn was aimed and what it spent. NaN on a joining player's game.</summary>
        public readonly bool Tracked;
        public readonly long RocketId;
        public readonly long BayId;
        public readonly double SkyX;
        public readonly double SkyZ;
        public readonly double TargetX;
        public readonly double TargetZ;
        public readonly double PadX;
        public readonly double PadZ;
        public readonly double Radius;
        public readonly double Reach;
        public readonly double Steered;
        public readonly double Fuel;
        public readonly double FuelSpent;
        public readonly double FuelLeft;
        public readonly int AimFrame;

        /// <summary>An ice comet's gases and the moles of each that reach the planet; a mineral comet's mass and the air it takes per kilogram.</summary>
        public readonly Chemistry.GasType[] Gases;
        public readonly double[] Moles;
        public readonly double MineralKg;
        public readonly double AirKgPerKg;

        internal LandingView(CometLanding landing)
        {
            CometId = landing.CometId;
            Kind = landing.Kind;
            ImpactX = landing.ImpactX;
            ImpactY = landing.ImpactY;
            ImpactZ = landing.ImpactZ;
            ImpactClock = landing.ImpactClock;
            FallSeconds = landing.FallSeconds;
            ShowMoles = landing.ShowMoles;
            LandingAim aim = landing.Aim;
            Tracked = aim.Tracked;
            RocketId = aim.RocketId;
            BayId = aim.BayId;
            SkyX = aim.SkyX;
            SkyZ = aim.SkyZ;
            TargetX = aim.TargetX;
            TargetZ = aim.TargetZ;
            PadX = aim.PadX;
            PadZ = aim.PadZ;
            Radius = aim.Radius;
            Reach = aim.Reach;
            Steered = aim.Steered;
            Fuel = aim.Fuel;
            FuelSpent = aim.FuelSpent;
            FuelLeft = aim.FuelLeft;
            AimFrame = aim.Frame;
            Gases = (Chemistry.GasType[])landing.Gases.Clone();
            Moles = (double[])landing.Moles.Clone();
            MineralKg = landing.MineralKg;
            AirKgPerKg = landing.AirKgPerKg;
        }
    }

    /// <summary>
    /// What one landing did to the planet, read under the tank lock either side of it, for tools/LiveCheck:
    /// every gas the tank holds, before and after, the moles the rule gave for each, and both stored heats in
    /// kelvin (energy over the heat capacity of the planet and its reservoirs) before and after.
    /// </summary>
    public sealed class LandingDelivery
    {
        public int CometId;
        public CometKind Kind;
        public double Clock;
        public Chemistry.GasType[] Types;
        public double[] Before;
        public double[] After;

        /// <summary>The moles the rule gave each gas: added for an ice comet, taken for a mineral comet.</summary>
        public double[] Rule;

        public double LatentKelvinBefore;
        public double LatentKelvinAfter;
        public double ExternalKelvinBefore;
        public double ExternalKelvinAfter;
    }
}
