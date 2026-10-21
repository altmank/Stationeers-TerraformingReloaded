using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Serialization;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;

namespace TerraformingReloaded.Comets
{
    /// <summary>Where a comet on the space map is in its life (docs/COMET-ROCKETS.md, "Fly").</summary>
    public enum MapCometState : byte
    {
        /// <summary>At the site it was made at, until it is sent.</summary>
        Waiting = 0,

        /// <summary>On its way to Entry, the planet's orbit node.</summary>
        Flying = 1,

        /// <summary>Parked at Entry.</summary>
        Arrived = 2,

        /// <summary>Out of fuel short of Entry, where it stays.</summary>
        Stranded = 3,
    }

    /// <summary>What a comet is made of (docs/COMET-ROCKETS.md, "Cut"): the ice of an ice site, or the ore of a mineral site.</summary>
    public enum CometKind : byte
    {
        /// <summary>Gas, cut from an ice site; it adds to the planet's air.</summary>
        Ice = 0,

        /// <summary>Ore, cut from a mineral site; it takes air away at its impact.</summary>
        Mineral = 1,
    }

    /// <summary>
    /// One comet on the space map. Never changed once made: a change is a new one, so code on any thread
    /// that takes the list in force reads one consistent comet. A flight is held as the state it set off
    /// with and the clock it set off at, so where it is at any moment is
    /// <see cref="CelestialRules.PositionAt"/> of the time flown, the same on the host, after a load and
    /// on every client, with nothing sent while it flies.
    /// </summary>
    internal sealed class MapComet
    {
        /// <summary>Seconds that stand for "long after it stopped": a settled comet is placed where its flight ended.</summary>
        private const double Ever = 1e12;

        public readonly int Id;
        public readonly MapCometState State;
        public readonly CometKind Kind;
        public readonly Chemistry.GasType[] Gases;
        public readonly double[] Moles;

        /// <summary>A mineral comet's ore, in the game's units of its site; 0 for an ice comet.</summary>
        public readonly double OreUnits;

        /// <summary>Cut cycles that went into it; 0 for one made any other way.</summary>
        public readonly int Cycles;

        /// <summary>The comet's own mass in flight (CelestialRules.FlightKg), without engines or fuel.</summary>
        public readonly double CometKg;

        public readonly int Engines;

        /// <summary>Waiting: the fuel aboard. After it is sent: the fuel it set off with.</summary>
        public readonly double Fuel;

        /// <summary>The engines' premix, kilograms a mole, by the game's molar masses.</summary>
        public readonly double FuelKgPerMole;

        /// <summary>The site it was made at.</summary>
        public readonly long SiteId;

        /// <summary>
        /// The nodes of its route, the site first and Entry last. Entry is found by its role
        /// (SpaceMap.Current.EntryNode) whenever it is needed, never by the id kept here.
        /// </summary>
        public readonly long[] Route;

        /// <summary>Each hop's Δv as the game worked it out when the comet set off (NodeTransit.GetDistance).</summary>
        public readonly double[] Hops;

        /// <summary>
        /// A moment of a flight on <see cref="CometEvent.Clock"/>: when it set off, or when a flight read from
        /// a save went back on the clock. NaN for a flight read from a save until the clock that save was made
        /// against is back; until then it stands at <see cref="MarkFlown"/>.
        /// </summary>
        public readonly double MarkClock;

        /// <summary>
        /// Seconds the flight had flown at <see cref="MarkClock"/>: 0 when it set off, what it had flown at
        /// the save for one read from a save. Kept apart rather than folded into a start time, so a flight
        /// back on the clock stands exactly where it was saved.
        /// </summary>
        public readonly double MarkFlown;

        public MapComet(int id, MapCometState state, CometKind kind, Chemistry.GasType[] gases, double[] moles, double oreUnits, int cycles, double cometKg,
            int engines, double fuel, double fuelKgPerMole, long siteId, long[] route, double[] hops, double markClock, double markFlown)
        {
            Id = id;
            State = state;
            Kind = kind;
            Gases = gases ?? new Chemistry.GasType[0];
            Moles = moles ?? new double[0];
            OreUnits = oreUnits;
            Cycles = cycles;
            CometKg = cometKg;
            Engines = engines;
            Fuel = fuel;
            FuelKgPerMole = fuelKgPerMole;
            SiteId = siteId;
            Route = route ?? new long[0];
            Hops = hops ?? new double[0];
            MarkClock = markClock;
            MarkFlown = markFlown;
        }

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

        public double RouteDeltaV
        {
            get
            {
                double total = 0.0;
                foreach (double hop in Hops)
                {
                    total += hop;
                }
                return total;
            }
        }

        /// <summary>A flight that is on the clock: sent, not read from a save still waiting for its clock.</summary>
        public bool OnClock => !double.IsNaN(MarkClock);

        /// <summary>When the flight set off, on the clock; NaN off the clock.</summary>
        public double StartClock => MarkClock - MarkFlown;

        /// <summary>Seconds flown at <paramref name="clock"/>: for a flight read from a save and not yet on the clock, what it had flown then.</summary>
        public double FlownAt(double clock)
        {
            switch (State)
            {
                case MapCometState.Flying:
                    return OnClock ? Math.Max(0.0, MarkFlown + (clock - MarkClock)) : MarkFlown;
                case MapCometState.Arrived:
                case MapCometState.Stranded:
                    return Ever;
                default:
                    return 0.0;
            }
        }

        /// <summary>Where it stands on its route at <paramref name="clock"/>. Not meaningful for a comet still waiting at its site.</summary>
        public RoutePosition At(double clock)
        {
            return CelestialRules.Shipped.PositionAt(Hops, CometKg, Engines, Fuel, FlownAt(clock), FuelKgPerMole);
        }

        /// <summary>The flight the whole route takes by the rule, from the state it set off with.</summary>
        public Flight WholeFlight => CelestialRules.Shipped.Fly(CometKg, Engines, Fuel, RouteDeltaV, FuelKgPerMole);

        public MapComet Departed(long[] route, double[] hops, double clock) =>
            new MapComet(Id, MapCometState.Flying, Kind, Gases, Moles, OreUnits, Cycles, CometKg, Engines, Fuel, FuelKgPerMole, SiteId, route, hops, clock, 0.0);

        /// <summary>A flight read from a save put back on the clock: from now it flies on from what it had flown then.</summary>
        public MapComet BackOnClock(double clock) =>
            new MapComet(Id, State, Kind, Gases, Moles, OreUnits, Cycles, CometKg, Engines, Fuel, FuelKgPerMole, SiteId, Route, Hops, clock, MarkFlown);

        /// <summary>The same comet with its site and route given as other node ids, after a load renumbered them.</summary>
        public MapComet WithNodes(long siteId, long[] route) =>
            new MapComet(Id, State, Kind, Gases, Moles, OreUnits, Cycles, CometKg, Engines, Fuel, FuelKgPerMole, siteId, route, Hops, MarkClock, MarkFlown);

        /// <summary>The flight over: parked at Entry, or stranded where its fuel ran out.</summary>
        public MapComet Settled(bool arrived) =>
            new MapComet(Id, arrived ? MapCometState.Arrived : MapCometState.Stranded, Kind, Gases, Moles, OreUnits, Cycles, CometKg, Engines, Fuel,
                FuelKgPerMole, SiteId, Route, Hops, MarkClock, MarkFlown);

        /// <summary>A comet still at its site with new contents: grown by Cut, or less what Burn turned into fuel.</summary>
        public MapComet Contents(Chemistry.GasType[] gases, double[] moles, double oreUnits, int cycles, double cometKg, double fuel) =>
            new MapComet(Id, State, Kind, gases, moles, oreUnits, cycles, cometKg, Engines, fuel, FuelKgPerMole, SiteId, Route, Hops, MarkClock, MarkFlown);

        /// <summary>A comet still at its site with this many engines (Rig).</summary>
        public MapComet WithEngines(int engines) =>
            new MapComet(Id, State, Kind, Gases, Moles, OreUnits, Cycles, CometKg, engines, Fuel, FuelKgPerMole, SiteId, Route, Hops, MarkClock, MarkFlown);

        /// <summary>A comet still at its site with this much fuel aboard (Fuel).</summary>
        public MapComet WithFuel(double fuel) =>
            new MapComet(Id, State, Kind, Gases, Moles, OreUnits, Cycles, CometKg, Engines, fuel, FuelKgPerMole, SiteId, Route, Hops, MarkClock, MarkFlown);

        /// <summary>The moles of one gas it holds, 0 for one it has none of.</summary>
        public double MolesOf(Chemistry.GasType gas)
        {
            int index = Array.IndexOf(Gases, gas);
            return index < 0 ? 0.0 : Moles[index];
        }

        // ---- the wire: the instant message and the planet sync -------------------------------------------

        private const int MaxGases = 64;
        private const int MaxHops = 1024;

        public void Write(RocketBinaryWriter writer)
        {
            writer.WriteInt32(Id);
            writer.WriteByte((byte)State);
            writer.WriteByte((byte)Kind);
            writer.WriteDouble(OreUnits);
            writer.WriteInt32(Cycles);
            writer.WriteDouble(CometKg);
            writer.WriteInt32(Engines);
            writer.WriteDouble(Fuel);
            writer.WriteDouble(FuelKgPerMole);
            writer.WriteInt64(SiteId);
            writer.WriteDouble(MarkClock);
            writer.WriteDouble(MarkFlown);
            writer.WriteInt32(Gases.Length);
            for (int i = 0; i < Gases.Length; i++)
            {
                writer.WriteInt32((int)Gases[i]);
                writer.WriteDouble(Moles[i]);
            }
            writer.WriteInt32(Route.Length);
            foreach (long node in Route)
            {
                writer.WriteInt64(node);
            }
            writer.WriteInt32(Hops.Length);
            foreach (double hop in Hops)
            {
                writer.WriteDouble(hop);
            }
        }

        /// <summary>Reads one whole comet; throws on a shape no host of this build writes, so the caller drops the lot.</summary>
        public static MapComet Read(RocketBinaryReader reader)
        {
            int id = reader.ReadInt32();
            byte state = reader.ReadByte();
            byte kind = reader.ReadByte();
            double oreUnits = reader.ReadDouble();
            int cycles = reader.ReadInt32();
            double cometKg = reader.ReadDouble();
            int engines = reader.ReadInt32();
            double fuel = reader.ReadDouble();
            double fuelKgPerMole = reader.ReadDouble();
            long site = reader.ReadInt64();
            double markClock = reader.ReadDouble();
            double markFlown = reader.ReadDouble();
            int gases = Count(reader.ReadInt32(), MaxGases, "gases");
            Chemistry.GasType[] types = new Chemistry.GasType[gases];
            double[] moles = new double[gases];
            for (int i = 0; i < gases; i++)
            {
                types[i] = (Chemistry.GasType)reader.ReadInt32();
                moles[i] = reader.ReadDouble();
            }
            long[] route = new long[Count(reader.ReadInt32(), MaxHops + 1, "nodes")];
            for (int i = 0; i < route.Length; i++)
            {
                route[i] = reader.ReadInt64();
            }
            double[] hops = new double[Count(reader.ReadInt32(), MaxHops, "hops")];
            for (int i = 0; i < hops.Length; i++)
            {
                hops[i] = reader.ReadDouble();
            }
            MapComet comet = new MapComet(id, (MapCometState)state, (CometKind)kind, types, moles, oreUnits, cycles, cometKg, engines, fuel, fuelKgPerMole,
                site, route, hops, markClock, markFlown);
            string wrong = comet.Refusal();
            if (wrong != null)
            {
                throw new InvalidOperationException("the host sent comet " + id + " with " + wrong);
            }
            return comet;
        }

        private static int Count(int count, int most, string what)
        {
            if (count < 0 || count > most)
            {
                throw new InvalidOperationException("a comet claims " + count + " " + what);
            }
            return count;
        }

        /// <summary>
        /// What is wrong with this comet as read from a save or the wire, or null. Nothing read is trusted:
        /// every number is finite and in range, and a comet that has set off has a route of hops to fly.
        /// </summary>
        public string Refusal()
        {
            if (!Enum.IsDefined(typeof(MapCometState), State))
            {
                return "a state this build does not know";
            }
            if (!Enum.IsDefined(typeof(CometKind), Kind))
            {
                return "a kind this build does not know";
            }
            if (Gases.Length != Moles.Length || (Kind == CometKind.Ice && Gases.Length == 0) || (Kind == CometKind.Mineral && Gases.Length != 0))
            {
                return Kind == CometKind.Ice ? "no gas" : "gas in a mineral comet";
            }
            if (!Finite(OreUnits) || OreUnits < 0.0 || OreUnits > CometMap.MaxMoles || (Kind == CometKind.Mineral) != (OreUnits > 0.0) || Cycles < 0)
            {
                return "an amount of ore, or a count of cycles, out of range";
            }
            foreach (double moles in Moles)
            {
                if (!Finite(moles) || moles < 0.0 || moles > CometMap.MaxMoles)
                {
                    return "an amount of gas out of range";
                }
            }
            if (!Finite(CometKg) || CometKg < 0.0 || Engines < 0 || Engines > CometMap.MaxEngines || !Finite(Fuel) || Fuel < 0.0 || Fuel > CometMap.MaxFuel
                || !Finite(FuelKgPerMole) || !(FuelKgPerMole > 0.0) || !Finite(MarkFlown) || MarkFlown < 0.0 || double.IsInfinity(MarkClock))
            {
                return "its mass, engines or fuel out of range";
            }
            if (State == MapCometState.Waiting)
            {
                return null;
            }
            if (Hops.Length == 0 || Route.Length != Hops.Length + 1)
            {
                return "a route that does not match its hops";
            }
            foreach (double hop in Hops)
            {
                if (!Finite(hop) || !(hop > 0.0))
                {
                    return "a hop with no delta-v";
                }
            }
            return null;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        // ---- the save: an entry of its own inside the .save ------------------------------------------------

        public CometSaveRecord ToRecord(double clock)
        {
            CometSaveRecord record = new CometSaveRecord
            {
                Id = Id,
                State = State.ToString(),
                Kind = Kind.ToString(),
                OreUnits = Exact(OreUnits),
                Cycles = Cycles,
                Site = SiteId,
                CometKg = Exact(CometKg),
                Engines = Engines,
                Fuel = Exact(Fuel),
                FuelKgPerMole = Exact(FuelKgPerMole),
                Flown = Exact(State == MapCometState.Flying ? FlownAt(clock) : 0.0),
                Gases = new List<CometSaveGas>(),
                Route = new List<long>(Route),
                Hops = new List<string>(Array.ConvertAll(Hops, Exact)),
            };
            for (int i = 0; i < Gases.Length; i++)
            {
                record.Gases.Add(new CometSaveGas { Type = Gases[i].ToString(), Moles = Exact(Moles[i]) });
            }
            return record;
        }

        /// <summary>Seventeen significant digits: every double written this way reads back as the same double.</summary>
        private static string Exact(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

        /// <summary>A number as a save wrote it; NaN for anything else, which <see cref="Refusal"/> then refuses.</summary>
        private static double Number(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;

        /// <summary>A comet as a save recorded it, or null with why not. A flight comes back off the clock, at what it had flown.</summary>
        public static MapComet FromRecord(CometSaveRecord record, out string refused)
        {
            refused = null;
            if (record == null || !Enum.TryParse(record.State, false, out MapCometState state) || !Enum.IsDefined(typeof(MapCometState), state))
            {
                refused = "a state this build does not know";
                return null;
            }
            // A save from before comets had a kind holds ice comets only.
            CometKind kind = CometKind.Ice;
            if (record.Kind != null && (!Enum.TryParse(record.Kind, false, out kind) || !Enum.IsDefined(typeof(CometKind), kind)))
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
            double[] hops = (record.Hops ?? new List<string>()).ConvertAll(Number).ToArray();
            MapComet comet = new MapComet(record.Id, state, kind, types.ToArray(), moles.ToArray(), record.OreUnits == null ? 0.0 : Number(record.OreUnits), record.Cycles,
                Number(record.CometKg), record.Engines, Number(record.Fuel),
                Number(record.FuelKgPerMole), record.Site, (record.Route ?? new List<long>()).ToArray(), hops,
                state == MapCometState.Flying ? double.NaN : 0.0, state == MapCometState.Flying ? Number(record.Flown) : 0.0);
            refused = comet.Refusal();
            return refused == null ? comet : null;
        }
    }

    /// <summary>The comets entry of a .save (CometMap.SaveEntryName). Public, with public fields, for XmlSerializer.</summary>
    [XmlRoot("TerraformingReloadedComets")]
    public class CometSaveFile
    {
        /// <summary>1: 0.18.0, ice comets only. 2: a comet's kind, its ore and its Cut cycles, and the rockets running a comet action.</summary>
        [XmlAttribute]
        public int Version = CometMap.SaveVersion;

        [XmlElement("Comet")]
        public List<CometSaveRecord> Comets = new List<CometSaveRecord>();

        /// <summary>
        /// Each rocket running a comet action at the save. The game's own entry for it says None, so the
        /// unmodded game loads the save; the action it was running is kept here.
        /// </summary>
        [XmlElement("Rocket")]
        public List<CometRocketRecord> Rockets = new List<CometRocketRecord>();
    }

    /// <summary>A rocket and the comet action it was running at the save.</summary>
    public class CometRocketRecord
    {
        [XmlAttribute]
        public long Id;

        [XmlAttribute]
        public string Action;
    }

    /// <summary>
    /// One comet as a save records it. Every fraction is text of seventeen significant digits, so a flight
    /// read back is the flight saved to the last bit, whatever the runtime's own formatting of a double.
    /// </summary>
    public class CometSaveRecord
    {
        [XmlAttribute]
        public int Id;

        [XmlAttribute]
        public string State;

        /// <summary>Ice or Mineral; absent in a version 1 save, which holds ice comets only.</summary>
        [XmlAttribute]
        public string Kind;

        public string OreUnits;
        public int Cycles;

        /// <summary>The ReferenceId of the site it was made at, which the game restores with the site.</summary>
        public long Site;

        public string CometKg;
        public int Engines;
        public string Fuel;
        public string FuelKgPerMole;

        /// <summary>A flight: seconds flown at the save. Its place is the rule's at that time, so this is the whole of its progress.</summary>
        public string Flown;

        [XmlArray("Gases"), XmlArrayItem("Gas")]
        public List<CometSaveGas> Gases;

        [XmlArray("Route"), XmlArrayItem("Node")]
        public List<long> Route;

        [XmlArray("Hops"), XmlArrayItem("DeltaV")]
        public List<string> Hops;
    }

    public class CometSaveGas
    {
        [XmlAttribute]
        public string Type;

        [XmlAttribute]
        public string Moles;
    }

    /// <summary>
    /// A comet as it stands at one moment, for the console, the map icon and tools/LiveCheck. Node ids are
    /// the game's ReferenceIds; Entry's is the one SpaceMap.Current has now. A plain struct: net472 has no
    /// IsExternalInit.
    /// </summary>
    public readonly struct CometView
    {
        public readonly int Id;
        public readonly MapCometState State;

        /// <summary>The node it stands at, or 0 while it is between two.</summary>
        public readonly long AtNode;

        /// <summary>The hop it is in, from its start node to its end node, and the game's Progress through it.</summary>
        public readonly int Hop;
        public readonly int HopCount;
        public readonly long FromNode;
        public readonly long ToNode;
        public readonly double Progress;

        /// <summary>The share of its route's Δv flown, 0 before it sets off.</summary>
        public readonly double RouteShare;

        public readonly double RouteDeltaV;

        /// <summary>Seconds its engines have burnt so far.</summary>
        public readonly double FlownSeconds;

        /// <summary>Seconds the whole flight takes by the rule: to Entry, or until the fuel runs out.</summary>
        public readonly double FlightSeconds;

        /// <summary>Whether the rule has it reach Entry.</summary>
        public readonly bool Arrives;

        public readonly double FuelLeft;
        public readonly double TotalMoles;
        public readonly double CometKg;
        public readonly int Engines;

        /// <summary>When it set off, on the shared clock; NaN before, or while a loaded flight waits for its clock.</summary>
        public readonly double StartClock;

        public readonly CometKind Kind;
        public readonly double OreUnits;
        public readonly int Cycles;

        /// <summary>Its gases and their moles, copies of its own.</summary>
        public readonly Chemistry.GasType[] Gases;
        public readonly double[] GasMoles;

        internal CometView(int id, MapCometState state, long atNode, int hop, int hopCount, long fromNode, long toNode, double progress, double routeShare,
            double routeDeltaV, double flownSeconds, double flightSeconds, bool arrives, double fuelLeft, double totalMoles, double cometKg, int engines, double startClock,
            MapComet comet)
        {
            Kind = comet.Kind;
            OreUnits = comet.OreUnits;
            Cycles = comet.Cycles;
            Gases = (Chemistry.GasType[])comet.Gases.Clone();
            GasMoles = (double[])comet.Moles.Clone();
            Id = id;
            State = state;
            AtNode = atNode;
            Hop = hop;
            HopCount = hopCount;
            FromNode = fromNode;
            ToNode = toNode;
            Progress = progress;
            RouteShare = routeShare;
            RouteDeltaV = routeDeltaV;
            FlownSeconds = flownSeconds;
            FlightSeconds = flightSeconds;
            Arrives = arrives;
            FuelLeft = fuelLeft;
            TotalMoles = totalMoles;
            CometKg = cometKg;
            Engines = engines;
            StartClock = startClock;
        }
    }
}
