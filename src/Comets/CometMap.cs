using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Serialization;
using Objects.Rockets;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The comets on the space map (docs/COMET-ROCKETS.md, "Fly"): the mod's own map objects, not
    /// rockets. A comet waits at the site it was made at; sent, it flies the whole route to Entry by the
    /// game's rocket travel rule (<see cref="CelestialRules.PositionAt"/>), along the route the game's
    /// pathfinder gives, and parks there, or stops for good where its fuel runs out.
    ///
    /// The host decides every comet. A flight is sent to clients once, as the state it set off with and
    /// its start on the clock every machine shares, so each machine places it by the same rule with
    /// nothing sent while it flies. The list rides the instant message (<see cref="CometNet"/>) and the
    /// planet sync, for a player who joins later.
    ///
    /// Saved as an entry of its own inside the .save, so it rolls back with the save it belongs to; the
    /// unmodded game unpacks the entry and ignores it. Everything here runs on the main thread except
    /// the one write into the save's zip, which only copies bytes made on the main thread.
    /// </summary>
    public static class CometMap
    {
        /// <summary>The name of the comets entry inside a .save. No name the game uses (SaveLoadConstants).</summary>
        public const string SaveEntryName = "tr-comets.xml";

        /// <summary>The comets entry this build writes; it reads this and every earlier one (MapComet.FromRecord).</summary>
        public const int SaveVersion = 3;

        /// <summary>The least real time between two sends of the list while an action keeps changing a comet.</summary>
        private const double ChangeSendSeconds = 2.0;

        internal const double MaxMoles = 1e12;
        internal const int MaxEngines = 64;
        internal const double MaxFuel = 1e10;
        private const int MaxComets = 1000;

        private static readonly object Changes = new object();
        private static volatile MapComet[] _comets = new MapComet[0];
        private static volatile CometLanding[] _landings = new CometLanding[0];
        private static volatile bool _saveInstalled;
        private static volatile byte[] _saveBytes;
        private static int _faults;

        /// <summary>
        /// The sites the load being run has rebuilt, by the id their save gave them
        /// (SpaceMapNode.Create(SiteNodeReference)). That id is not theirs again: see <see cref="LoadFromSave"/>.
        /// </summary>
        private static readonly Dictionary<long, SpaceMapNode> RebuiltSites = new Dictionary<long, SpaceMapNode>();

        /// <summary>
        /// After a load, until every node the loaded comets refer to has the id the game finally gives it:
        /// each node by the id the save knew it by. Null when no comet waits for that.
        /// </summary>
        private static Dictionary<long, SpaceMapNode> _savedNodes;

        /// <summary>A comet changed by an action since the list was last sent.</summary>
        private static volatile bool _changed;
        private static DateTime _lastSent = DateTime.MinValue;

        /// <summary>Rockets a loaded save had running a comet action, until each is back in it.</summary>
        private static List<CometRocketRecord> _pendingModes = new List<CometRocketRecord>();
        private static int _pendingFrames;

        private static readonly Lazy<XmlSerializer> Serializer = new Lazy<XmlSerializer>(() => new XmlSerializer(typeof(CometSaveFile)));

        /// <summary>Every comet now on the map, one consistent list.</summary>
        internal static MapComet[] All => _comets;

        /// <summary>Every comet brought down and still falling (CometLand), one consistent list. The host's, or what it last sent.</summary>
        internal static CometLanding[] Landings => _landings;

        /// <summary>The save and load of comets is installed (Patcher's "comet map save"). Without it no comet is made.</summary>
        public static bool SaveInstalled => _saveInstalled;

        private static double Clock => CometEvent.Clock;

        // ---- the world ------------------------------------------------------------------------------

        /// <summary>Every world start, host and client, new, loaded and joined: the comets of the last world are gone.</summary>
        public static void WorldStart()
        {
            lock (Changes)
            {
                _comets = new MapComet[0];
                _landings = new CometLanding[0];
                _highestId = 0;
                _saveBytes = null;
                _savedNodes = null;
                RebuiltSites.Clear();
                _pendingModes = new List<CometRocketRecord>();
                _changed = false;
            }
            CometAim.WorldStart();
            CometLand.WorldStart();
        }

        /// <summary>SpaceMapNode.Create(SiteNodeReference), during a load: the site the save knew by this id.</summary>
        internal static void SiteRebuilt(long savedId, SpaceMapNode site)
        {
            if (site != null && savedId != 0L)
            {
                RebuiltSites[savedId] = site;
            }
        }

        /// <summary>
        /// A node by an identity that survives a load, for logs and tools/LiveCheck: Entry and the low-orbit
        /// hub by their role, a static node by its template, a site by its template and its parent's identity.
        /// The game's ReferenceId does not survive a load (<see cref="LoadFromSave"/>).
        /// </summary>
        public static string Identity(SpaceMapNode node)
        {
            SpaceMap map = SpaceMap.Current;
            if (node == null)
            {
                return "?";
            }
            if (map != null && node == map.EntryNode)
            {
                return "Entry";
            }
            if (map != null && node == map.LowOrbitHubNode)
            {
                return "LowOrbitHub";
            }
            switch (node.NodeType)
            {
                case NodeType.Static:
                    return "Static " + node.Data?.Id;
                case NodeType.Generated:
                    return "Site " + node.Data?.Id + " under " + Identity(node.ParentConnection?.Parent);
                default:
                    return node.NodeType.ToString();
            }
        }

        /// <summary>The identities of a comet's site and route, for tools/LiveCheck: "site X | route A -> B -> Entry".</summary>
        public static string RouteIdentities(int id)
        {
            MapComet comet = Find(id);
            if (comet == null)
            {
                return "none";
            }
            StringBuilder text = new StringBuilder("site ").Append(Identity(SpaceMapNode.Get(comet.SiteId))).Append(" | route ");
            for (int i = 0; i < comet.Route.Length; i++)
            {
                text.Append(i == 0 ? "" : " -> ").Append(Identity(RouteNode(comet, i)));
            }
            return text.ToString();
        }

        /// <summary>The planet being played as a share of the one the world ships, or 0 when there is none.</summary>
        public static double PlanetSize()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            double shipped = WorldSetting.Current?.Data?.GlobalAtmosphereData?.Volume?.Value ?? 0.0;
            return tank != null && shipped > 0.0 ? tank.Volume.ToDouble() / shipped : 0.0;
        }

        /// <summary>K in force on the planet being played (CelestialRules.SizeMultiplier); Standard's when no planet is there.</summary>
        internal static double SizeMultiplierNow(CelestialTuning tuning)
        {
            double size = PlanetSize();
            return tuning.SizeMultiplierAt(size > 0.0 ? size : CelestialRules.StandardPlanetSize);
        }

        /// <summary>A comet's weight in flight from what it holds, by the settings in force.</summary>
        internal static double FlightKg(CometKind kind, Chemistry.GasType[] gases, double[] moles, double oreUnits, CelestialTuning tuning)
        {
            double contents;
            if (kind == CometKind.Mineral)
            {
                contents = Math.Max(0.0, oreUnits) * tuning.MineralKgPerUnit;
            }
            else
            {
                double[] grams = Array.ConvertAll(gases, g => Mole.MolarMass(g));
                contents = CelestialRules.Shipped.MassKg(moles, grams);
            }
            return CelestialRules.Shipped.FlightKg(contents, tuning.CometMassScale, SizeMultiplierNow(tuning));
        }

        /// <summary>
        /// Whether a comet belongs to this site. The one place the comet actions ask it, so the comet's
        /// site is always named the way CometMap keeps it across a save and a load.
        /// </summary>
        internal static bool IsSite(MapComet comet, SpaceMapNode site) => comet != null && site != null && comet.SiteId == site.ReferenceId;

        /// <summary>The comet waiting at a site, the first made there; null for none.</summary>
        internal static MapComet WaitingAt(SpaceMapNode site)
        {
            MapComet found = null;
            if (site == null)
            {
                return null;
            }
            foreach (MapComet comet in _comets)
            {
                if (comet.State == MapCometState.Waiting && IsSite(comet, site) && (found == null || comet.Id < found.Id))
                {
                    found = comet;
                }
            }
            return found;
        }

        /// <summary>The comet waiting at this site as this machine sees it, for the rocket actions, the console and tools/LiveCheck.</summary>
        public static CometView? WaitingView(SpaceMapNode site)
        {
            MapComet comet = WaitingAt(site);
            return comet == null ? (CometView?)null : View(comet, Clock);
        }

        /// <summary>The sites a comet can be made at, in the order the console lists them: every discovered site on the map.</summary>
        public static List<SpaceMapNode> Sites()
        {
            List<SpaceMapNode> sites = new List<SpaceMapNode>();
            SpaceMap map = SpaceMap.Current;
            if (map == null)
            {
                return sites;
            }
            foreach (SpaceMapNode node in map.Nodes)
            {
                if (node != null && node.NodeType == NodeType.Generated)
                {
                    sites.Add(node);
                }
            }
            return sites;
        }

        /// <summary>The node a route's step stands for: the last is Entry by its role, the rest by id.</summary>
        internal static SpaceMapNode RouteNode(MapComet comet, int index)
        {
            if (index >= comet.Route.Length - 1)
            {
                return SpaceMap.Current?.EntryNode;
            }
            return SpaceMapNode.Get(comet.Route[index]);
        }

        /// <summary>
        /// Whether a comet holds this node, so it must not be removed from the map: the site a comet waits
        /// at, and every node on the route of a comet that has not reached Entry.
        /// </summary>
        public static bool Holds(SpaceMapNode node)
        {
            return node != null && HoldsNode(node.ReferenceId);
        }

        public static bool HoldsNode(long referenceId)
        {
            foreach (MapComet comet in _comets)
            {
                if (comet.State == MapCometState.Waiting)
                {
                    if (comet.SiteId == referenceId)
                    {
                        return true;
                    }
                    continue;
                }
                if (comet.State == MapCometState.Arrived)
                {
                    continue;
                }
                for (int i = 0; i < comet.Route.Length - 1; i++)
                {
                    if (comet.Route[i] == referenceId)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // ---- what the host does --------------------------------------------------------------------

        /// <summary>The engines' premix, from the game's molar masses for methane and oxygen.</summary>
        internal static double PremixKgPerMole() =>
            CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));

        /// <summary>Host: a comet at a site, of ice, or of this much ore when <paramref name="oreUnits"/> is more than 0. Null on success, else why not.</summary>
        internal static string Make(SpaceMapNode site, Chemistry.GasType[] gases, double[] moles, int engines, double fuel, out MapComet made, double oreUnits = 0.0)
        {
            made = null;
            if (site == null || site.NodeType != NodeType.Generated)
            {
                return "That is not a site on the space map.";
            }
            if (_comets.Length >= MaxComets)
            {
                return "There are " + MaxComets + " comets on the map already.";
            }
            CometKind kind = oreUnits > 0.0 ? CometKind.Mineral : CometKind.Ice;
            if (kind == CometKind.Mineral)
            {
                gases = new Chemistry.GasType[0];
                moles = new double[0];
            }
            double kg = FlightKg(kind, gases, moles, oreUnits, Effective.Celestial);
            lock (Changes)
            {
                made = new MapComet(NextId(), MapCometState.Waiting, kind, gases, moles, Math.Max(0.0, oreUnits), 0, kg, engines, fuel, PremixKgPerMole(), site.ReferenceId,
                    null, null, double.NaN, 0.0);
                string wrong = made.Refusal();
                if (wrong != null)
                {
                    made = null;
                    return "That comet has " + wrong + ".";
                }
                Publish(Append(_comets, made));
            }
            CometDirector.Install();
            Log.Info(string.Format(CultureInfo.InvariantCulture, "[TR comet map] comet {0} made at {1} (site {2}): {3:R} mol, {4:R} units of ore, {5:R} kg, {6} engines, {7:R} mol of fuel",
                made.Id, site.DisplayName, site.ReferenceId, made.TotalMoles, made.OreUnits, made.CometKg, engines, fuel));
            CometNet.BroadcastMap();
            return null;
        }

        /// <summary>
        /// Host: sends a waiting comet to Entry along the route the game's pathfinder gives, each hop's
        /// Δv as the game works it out. Null on success, else why not.
        /// </summary>
        internal static string Send(int id, out MapComet sent)
        {
            sent = null;
            MapComet comet = Find(id);
            if (comet == null)
            {
                return "There is no comet " + id + ". terraform comet list shows them.";
            }
            if (comet.State != MapCometState.Waiting)
            {
                return "Comet " + id + " has already been sent.";
            }
            if (comet.Engines <= 0 || !(comet.Fuel > 0.0))
            {
                return "Comet " + id + " has " + (comet.Engines <= 0 ? "no engines" : "no fuel") + ": it would not move.";
            }
            SpaceMapNode site = SpaceMapNode.Get(comet.SiteId);
            SpaceMapNode entry = SpaceMap.Current?.EntryNode;
            if (site == null || entry == null)
            {
                return "Comet " + id + "'s site is no longer on the map.";
            }
            // The path comes back reversed: its last entry is the first hop.
            if (!SpaceMapPathFinder.GetPath(site, entry, out List<NodeTransit> path) || path == null || path.Count == 0)
            {
                return "There is no route from comet " + id + "'s site to " + entry.DisplayName + ".";
            }
            long[] route = new long[path.Count + 1];
            double[] hops = new double[path.Count];
            route[0] = site.ReferenceId;
            SpaceMapNode at = site;
            for (int i = 0; i < path.Count; i++)
            {
                NodeTransit transit = path[path.Count - 1 - i];
                if (transit?.From != at || transit.Destination == null || !(transit.GetDistance() > 0f))
                {
                    return "The game's route from comet " + id + "'s site does not join up; it was not sent.";
                }
                hops[i] = transit.GetDistance();
                route[i + 1] = transit.Destination.ReferenceId;
                at = transit.Destination;
            }
            if (at != entry)
            {
                return "The game's route from comet " + id + "'s site does not end at " + entry.DisplayName + "; it was not sent.";
            }
            lock (Changes)
            {
                comet = Find(id);
                if (comet == null || comet.State != MapCometState.Waiting)
                {
                    return "Comet " + id + " changed while it was being sent; try again.";
                }
                sent = comet.Departed(route, hops, Clock);
                Publish(Replace(_comets, sent));
            }
            Flight flight = sent.WholeFlight;
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "[TR comet map] comet {0} sent at clock {1:R}: {2} hops, {3:R} delta-v, {4}; the rule says {5} in {6:R} s on {7:R} mol",
                sent.Id, sent.StartClock, hops.Length, sent.RouteDeltaV, RouteText(sent),
                flight.Arrives ? "it arrives" : string.Format(CultureInfo.InvariantCulture, "it stops at {0:R} of the route", flight.Share),
                flight.Seconds, flight.FuelMoles));
            CometNet.BroadcastMap();
            return null;
        }

        /// <summary>
        /// Host, main thread (terraform comet arrive): a flight on the clock is moved to the end the rule gives
        /// it, as if it had set off the whole flight's seconds ago, and settled at once by <see cref="Step"/>,
        /// as every flight is: parked at Entry with the fuel it did not burn, or stopped where its fuel runs
        /// out. Every player gets the moved flight and settles it by the same rule. Null on success, else why not.
        /// </summary>
        internal static string FlyToEnd(int id, out MapComet flown)
        {
            flown = null;
            double now = Clock;
            lock (Changes)
            {
                MapComet comet = Find(id);
                if (comet == null || comet.State != MapCometState.Flying || !comet.OnClock)
                {
                    return "Comet " + id + " is not in flight on the clock.";
                }
                flown = comet.FlownFor(comet.WholeFlight.Seconds, now);
                Publish(Replace(_comets, flown));
            }
            Log.Info(string.Format(CultureInfo.InvariantCulture, "[TR comet map] comet {0} flown to the end of its flight by the console at clock {1:R}: {2:R} s, as if sent at clock {3:R}",
                id, now, flown.MarkFlown, flown.StartClock));
            CometNet.BroadcastMap();
            Step();
            return null;
        }

        internal static MapComet Find(int id)
        {
            foreach (MapComet comet in _comets)
            {
                if (comet.Id == id)
                {
                    return comet;
                }
            }
            return null;
        }

        /// <summary>The next comet's id. Under the changes lock.</summary>
        private static int NextId()
        {
            int id = Math.Max(1, _highestId + 1);
            foreach (MapComet comet in _comets)
            {
                id = Math.Max(id, comet.Id + 1);
            }
            foreach (CometLanding landing in _landings)
            {
                id = Math.Max(id, landing.CometId + 1);
            }
            _highestId = id;
            return id;
        }

        /// <summary>The highest id given this session, so a comet that has landed does not lend its id to the next one made.</summary>
        private static int _highestId;

        // ---- the rocket actions at a site (docs/COMET-ROCKETS.md, stage 4) ----------------------------

        /// <summary>
        /// What a site can make a comet of: its kind, and for ice the gases a unit of it holds (the ice the
        /// game's miners cut there, FrozenGasses[0]), and the units the site holds as the game counts them
        /// (TotalOreAtLocation). False for anything that is not a site with ice or ore.
        /// </summary>
        internal static bool SiteStock(SpaceMapNode site, out CometKind kind, out Chemistry.GasType[] gases, out double[] perUnit, out double units)
        {
            kind = CometKind.Ice;
            gases = new Chemistry.GasType[0];
            perUnit = new double[0];
            units = 0.0;
            Objects.Rockets.Mining.MineableDeposit deposit = site?.Deposit;
            if (site == null || site.NodeType != NodeType.Generated || deposit?.DepositComposition == null)
            {
                return false;
            }
            units = deposit.TotalOreAtLocation;
            Objects.Rockets.Mining.MineableDepositType type = deposit.DepositType;
            // The game's mining picks among ores and reagent mixes first and never reaches ice beside them.
            if ((type & (Objects.Rockets.Mining.MineableDepositType.Ore | Objects.Rockets.Mining.MineableDepositType.ReagentMix)) != 0)
            {
                kind = CometKind.Mineral;
                return units > 0.0;
            }
            if ((type & Objects.Rockets.Mining.MineableDepositType.Ice) == 0 || deposit.DepositComposition.FrozenGasses.Count == 0)
            {
                return false;
            }
            List<Chemistry.GasType> types = new List<Chemistry.GasType>();
            List<double> amounts = new List<double>();
            foreach (Assets.Scripts.Objects.SpawnGas spawn in deposit.DepositComposition.FrozenGasses[0].SpawnGasses)
            {
                if (spawn == null || !spawn.IsValid)
                {
                    continue;
                }
                int at = types.IndexOf(spawn.Type);
                if (at < 0)
                {
                    types.Add(spawn.Type);
                    amounts.Add(spawn.Quantity);
                }
                else
                {
                    amounts[at] += spawn.Quantity;
                }
            }
            gases = types.ToArray();
            perUnit = amounts.ToArray();
            return units > 0.0 && gases.Length > 0;
        }

        /// <summary>What a site holds in its own measure: moles of ice, or units of ore.</summary>
        internal static double SiteAmount(CometKind kind, double[] perUnit, double units)
        {
            if (kind == CometKind.Mineral)
            {
                return units;
            }
            double total = 0.0;
            foreach (double moles in perUnit)
            {
                total += moles;
            }
            return units * total;
        }

        /// <summary>
        /// Host, main thread, one Cut cycle at a site (the miner's own cycle, CometActionPatches.MineDepositPrefix):
        /// the site's comet grows by the rule, made on the first cycle. Null with why not when nothing was cut;
        /// the site's own depletion is the game's and has already happened.
        /// </summary>
        internal static string CutCycle(SpaceMapNode site, double headYield, out MapComet comet)
        {
            comet = null;
            if (!SiteStock(site, out CometKind kind, out Chemistry.GasType[] gases, out double[] perUnit, out double units))
            {
                return "no ice or ore to cut";
            }
            CelestialTuning tuning = Effective.Celestial;
            double k = SizeMultiplierNow(tuning);
            double siteAmount = SiteAmount(kind, perUnit, units);
            double perUnitTotal = kind == CometKind.Ice ? siteAmount / units : 1.0;
            bool made = false;
            lock (Changes)
            {
                MapComet before = WaitingAt(site);
                if (before != null && before.Kind != kind)
                {
                    comet = before;
                    return "the comet here is not of this site's kind";
                }
                if (before == null && _comets.Length >= MaxComets)
                {
                    return "there are " + MaxComets + " comets on the map already";
                }
                double has = before == null ? 0.0 : kind == CometKind.Ice ? before.TotalMoles : before.OreUnits;
                double added = CelestialRules.Shipped.CutAdds(siteAmount, has, tuning.CutSharePerCycle, k, headYield);
                if (!(added > 0.0))
                {
                    comet = before;
                    return headYield > 0.0 ? "the comet holds all this site can give" : "the drill head cuts nothing of this site's kind";
                }
                List<Chemistry.GasType> types = new List<Chemistry.GasType>(before?.Gases ?? new Chemistry.GasType[0]);
                List<double> moles = new List<double>(before?.Moles ?? new double[0]);
                double ore = before?.OreUnits ?? 0.0;
                if (kind == CometKind.Ice)
                {
                    for (int i = 0; i < gases.Length; i++)
                    {
                        int at = types.IndexOf(gases[i]);
                        double share = added * perUnit[i] / perUnitTotal;
                        if (at < 0)
                        {
                            types.Add(gases[i]);
                            moles.Add(share);
                        }
                        else
                        {
                            moles[at] += share;
                        }
                    }
                }
                else
                {
                    ore += added;
                }
                Chemistry.GasType[] newTypes = types.ToArray();
                double[] newMoles = moles.ToArray();
                double kg = FlightKg(kind, newTypes, newMoles, ore, tuning);
                comet = before == null
                    ? new MapComet(NextId(), MapCometState.Waiting, kind, newTypes, newMoles, ore, 1, kg, 0, 0.0, PremixKgPerMole(), site.ReferenceId, null, null, double.NaN, 0.0)
                    : before.Contents(newTypes, newMoles, ore, before.Cycles + 1, kg, before.Fuel);
                string wrong = comet.Refusal();
                if (wrong != null)
                {
                    comet = before;
                    return "the comet would have " + wrong;
                }
                Publish(before == null ? Append(_comets, comet) : Replace(_comets, comet));
                made = before == null;
            }
            Changed();
            if (made)
            {
                CometDirector.Install();
            }
            if (made || comet.Cycles % 100 == 0 || CelestialRules.Shipped.CutAdds(siteAmount, kind == CometKind.Ice ? comet.TotalMoles : comet.OreUnits, tuning.CutSharePerCycle, k, 1.0) <= 0.0)
            {
                Log.Info(string.Format(CultureInfo.InvariantCulture, "[TR comet actions] Cut at {0}: comet {1} {2}, {3} cycles, {4:R} {5} of the {6:R} this site can give (K {7:R})",
                    site.DisplayName, comet.Id, made ? "made" : "grown", comet.Cycles, kind == CometKind.Ice ? comet.TotalMoles : comet.OreUnits,
                    kind == CometKind.Ice ? "mol" : "units of ore", k * siteAmount, k));
            }
            return null;
        }

        /// <summary>
        /// Host, main thread, one Burn cycle: the site's ice comet turns its own methane and oxygen into fuel
        /// by the rule. Null with why not when nothing was burnt.
        /// </summary>
        internal static string BurnCycle(SpaceMapNode site, double headYield, out double fuelGained)
        {
            fuelGained = 0.0;
            CelestialTuning tuning = Effective.Celestial;
            MapComet after;
            lock (Changes)
            {
                MapComet comet = WaitingAt(site);
                if (comet == null || comet.Kind != CometKind.Ice)
                {
                    return "there is no ice comet here";
                }
                int methane = Array.IndexOf(comet.Gases, Chemistry.GasType.Methane);
                int oxygen = Array.IndexOf(comet.Gases, Chemistry.GasType.Oxygen);
                double wanted = CelestialRules.Shipped.BurnWanted(comet.TotalMoles, tuning.CutSharePerCycle, headYield);
                double[] moles = CelestialRules.Shipped.AfterBurn(comet.Moles, methane, oxygen, wanted, out fuelGained);
                if (!(fuelGained > 0.0))
                {
                    return "the comet has no methane and oxygen left to burn";
                }
                after = comet.Contents(comet.Gases, moles, 0.0, comet.Cycles, FlightKg(CometKind.Ice, comet.Gases, moles, 0.0, tuning), comet.Fuel + fuelGained);
                string wrong = after.Refusal();
                if (wrong != null)
                {
                    fuelGained = 0.0;
                    return "the comet would have " + wrong;
                }
                Publish(Replace(_comets, after));
            }
            Changed();
            return null;
        }

        /// <summary>Host, main thread (Rig): engines added to a comet waiting at its site, up to the most a comet takes.</summary>
        internal static string AddEngines(int id, int engines, out MapComet rigged)
        {
            lock (Changes)
            {
                rigged = Find(id);
                if (rigged == null || rigged.State != MapCometState.Waiting)
                {
                    return "the comet is no longer waiting here";
                }
                if (rigged.Engines + engines > MaxEngines)
                {
                    return "a comet takes " + MaxEngines + " engines at most";
                }
                rigged = rigged.WithEngines(rigged.Engines + engines);
                Publish(Replace(_comets, rigged));
            }
            Changed();
            return null;
        }

        /// <summary>Host, any thread (Fuel, from the rocket's atmospherics step): premix added to a waiting comet.</summary>
        internal static bool AddFuel(int id, double moles)
        {
            if (!(moles > 0.0))
            {
                return false;
            }
            lock (Changes)
            {
                MapComet comet = Find(id);
                if (comet == null || comet.State != MapCometState.Waiting || comet.Fuel + moles > MaxFuel)
                {
                    return false;
                }
                Publish(Replace(_comets, comet.WithFuel(comet.Fuel + moles)));
            }
            Changed();
            return true;
        }

        /// <summary>
        /// Host, main thread: a parked comet's burn has fired. It leaves the map and becomes a fall, in one
        /// change; everyone is told at once. Null on success, else why not.
        /// </summary>
        internal static string Deorbited(int id, CometLanding landing)
        {
            lock (Changes)
            {
                MapComet comet = Find(id);
                if (comet == null || comet.State != MapCometState.Arrived)
                {
                    return "the comet is no longer in orbit";
                }
                foreach (CometLanding falling in _landings)
                {
                    if (falling.CometId == id)
                    {
                        return "that comet is already coming down";
                    }
                }
                CometLanding[] landings = new CometLanding[_landings.Length + 1];
                Array.Copy(_landings, landings, _landings.Length);
                landings[_landings.Length] = landing;
                _landings = landings;
                Publish(Array.FindAll(_comets, c => c.Id != id));
            }
            CometNet.BroadcastMap();
            return null;
        }

        /// <summary>Host, main thread: the fall is over and the comet gone; everyone is told at once.</summary>
        internal static void Landed(int id)
        {
            lock (Changes)
            {
                _landings = Array.FindAll(_landings, l => l.CometId != id);
            }
            CometNet.BroadcastMap();
        }

        private static void Changed() => _changed = true;

        /// <summary>Something sent with the list changed (a payload bay's target, CometAim): the list goes out with the next send.</summary>
        internal static void MarkChanged() => _changed = true;

        /// <summary>
        /// Main thread, every frame: every rocket a loaded save had running a comet action goes back into it
        /// once the game is running and the rocket is there, which the game then checks as any mode it is
        /// given (Rocket.OnRocketModeUpdated). One the world never brings back is let go of, by name.
        /// </summary>
        private static void RestoreRocketModes()
        {
            List<CometRocketRecord> pending = _pendingModes;
            if (pending.Count == 0 || NetworkManager.IsClient)
            {
                return;
            }
            List<CometRocketRecord> left = new List<CometRocketRecord>();
            foreach (CometRocketRecord record in pending)
            {
                Rocket rocket = Referencable.Find<Rocket>(record.Id);
                if (rocket == null || !CometModes.TryParse(record.Action, out RocketMode mode))
                {
                    left.Add(record);
                    continue;
                }
                rocket.RocketMode = mode;
                Log.Info("[TR comet actions] rocket " + record.Id + " is back to " + record.Action + " after the load" + (rocket.RocketMode == mode ? "" : ", which the game refused (" + rocket.RocketMode + ")"));
            }
            if (left.Count > 0 && ++_pendingFrames > 600)
            {
                foreach (CometRocketRecord record in left)
                {
                    Log.Warn("[TR comet actions] Rocket " + record.Id + " was running " + record.Action + " at the save and is not in this world; it was let go of.");
                }
                left.Clear();
            }
            _pendingModes = left;
        }

        // ---- every frame -----------------------------------------------------------------------------

        /// <summary>
        /// Main thread, every frame, on every machine: a flight read from a save goes back on the clock
        /// once the game is running again, and a flight whose rule says it is over is settled where the
        /// rule put it. Every machine settles by the same rule at the same clock; the host also tells
        /// everyone, so a client that missed the moment is put right.
        /// </summary>
        internal static void Step()
        {
            if (SpaceMap.Current == null || GameManager.GameState != GameState.Running)
            {
                return;
            }
            // The comets' sites first, so a rocket put back to its comet action finds its comet there.
            Relink();
            RestoreRocketModes();
            if (_changed && !NetworkManager.IsClient && (DateTime.UtcNow - _lastSent).TotalSeconds >= ChangeSendSeconds)
            {
                _changed = false;
                _lastSent = DateTime.UtcNow;
                CometNet.BroadcastMap();
            }
            MapComet[] comets = _comets;
            if (comets.Length == 0)
            {
                return;
            }
            Relink();
            ClockRestored();
            double now = Clock;
            bool settled = false;
            lock (Changes)
            {
                MapComet[] list = _comets;
                for (int i = 0; i < list.Length; i++)
                {
                    MapComet comet = list[i];
                    if (comet.State != MapCometState.Flying || !comet.OnClock)
                    {
                        continue;
                    }
                    RoutePosition position = comet.At(now);
                    if (!position.Settled)
                    {
                        continue;
                    }
                    MapComet done = comet.Settled(position.Arrived);
                    list = Replace(list, done);
                    settled = true;
                    if (NetworkManager.IsClient)
                    {
                        continue;
                    }
                    SpaceMapNode from = RouteNode(done, position.Stop.Hop);
                    SpaceMapNode to = RouteNode(done, position.Stop.Hop + 1);
                    Log.Info(string.Format(CultureInfo.InvariantCulture,
                        position.Arrived
                            ? "[TR comet map] comet {0} arrived at {1} at clock {2:R}: burnt {3:R} s, {4:R} mol of fuel left"
                            : "[TR comet map] comet {0} ran out of fuel at clock {2:R} after {3:R} s, at {5:R} of the hop from {6} to {1}, {7:R} of the route; it stays there",
                        done.Id, to?.DisplayName ?? "Entry", now, position.BurnSeconds, position.FuelLeft, position.Stop.Progress, from?.DisplayName ?? "?",
                        position.DeltaV / done.RouteDeltaV));
                }
                if (settled)
                {
                    Publish(list);
                }
            }
            if (settled && !NetworkManager.IsClient)
            {
                CometNet.BroadcastMap();
            }
        }

        /// <summary>
        /// The clock a save was made against is back (OrbitalSimulation.DeserializeSave, which a load
        /// runs once the world is up): every flight read from that save goes back on it, as long before
        /// now as it had flown. Safe to call again; also called from <see cref="Step"/>, which runs no
        /// earlier than the same frame.
        /// </summary>
        internal static void ClockRestored()
        {
            Relink();
            MapComet[] comets = _comets;
            bool waiting = false;
            foreach (MapComet comet in comets)
            {
                waiting |= comet.State == MapCometState.Flying && !comet.OnClock;
            }
            foreach (CometLanding landing in _landings)
            {
                waiting |= !landing.OnClock;
            }
            if (!waiting || NetworkManager.IsClient || GameManager.GameState != GameState.Running)
            {
                return;
            }
            double now = Clock;
            List<CometLanding> falling = new List<CometLanding>();
            lock (Changes)
            {
                MapComet[] list = _comets;
                foreach (MapComet comet in list)
                {
                    if (comet.State == MapCometState.Flying && !comet.OnClock)
                    {
                        MapComet back = comet.BackOnClock(now);
                        list = Replace(list, back);
                        Log.Info(string.Format(CultureInfo.InvariantCulture, "[TR comet map] comet {0} flies on from clock {1:R}, {2:R} s into its flight", back.Id, now, back.MarkFlown));
                    }
                }
                Publish(list);
                CometLanding[] landings = (CometLanding[])_landings.Clone();
                for (int i = 0; i < landings.Length; i++)
                {
                    if (!landings[i].OnClock)
                    {
                        landings[i] = landings[i].BackOnClock(now);
                        falling.Add(landings[i]);
                        Log.Info(string.Format(CultureInfo.InvariantCulture, "[TR comet land] comet {0} falls on from clock {1:R}, {2:R} s into its fall: it lands at clock {3:R}",
                            landings[i].CometId, now, landings[i].MarkFallen, landings[i].ImpactClock));
                    }
                }
                _landings = landings;
            }
            CometNet.BroadcastMap();
            // A save keeps no impact event: each fall gets its own again, timed to the same clock time.
            foreach (CometLanding landing in falling)
            {
                CometLand.BackOnClock(landing);
            }
        }

        // ---- what a console, an icon and tools/LiveCheck read ---------------------------------------

        /// <summary>Every comet as it stands now.</summary>
        public static CometView[] Views()
        {
            MapComet[] comets = _comets;
            CometView[] views = new CometView[comets.Length];
            double now = Clock;
            for (int i = 0; i < comets.Length; i++)
            {
                views[i] = View(comets[i], now);
            }
            return views;
        }

        internal static CometView View(MapComet comet, double now)
        {
            if (comet.State == MapCometState.Waiting)
            {
                return new CometView(comet.Id, comet.State, comet.SiteId, 0, 0, comet.SiteId, comet.SiteId, 0.0, 0.0, 0.0, 0.0, double.NaN, false,
                    comet.Fuel, comet.TotalMoles, comet.CometKg, comet.Engines, double.NaN, comet);
            }
            RoutePosition position = comet.At(now);
            int hop = Math.Max(0, Math.Min(comet.Hops.Length - 1, position.Stop.Hop));
            long from = RouteNode(comet, hop)?.ReferenceId ?? comet.Route[hop];
            long to = RouteNode(comet, hop + 1)?.ReferenceId ?? comet.Route[hop + 1];
            long atNode = position.Stop.Progress >= 1.0 ? to : position.Stop.Progress <= 0.0 ? from : 0L;
            double route = comet.RouteDeltaV;
            Flight whole = comet.WholeFlight;
            return new CometView(comet.Id, comet.State, atNode, hop, comet.Hops.Length, from, to, position.Stop.Progress,
                route > 0.0 ? position.DeltaV / route : 0.0, route, position.BurnSeconds, whole.Seconds, whole.Arrives, position.FuelLeft,
                comet.TotalMoles, comet.CometKg, comet.Engines, comet.StartClock, comet);
        }

        internal static string RouteText(MapComet comet)
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < comet.Route.Length; i++)
            {
                text.Append(i == 0 ? "" : " -> ").Append(RouteNode(comet, i)?.DisplayName ?? "?");
            }
            return text.ToString();
        }

        // ---- the network --------------------------------------------------------------------------------

        /// <summary>
        /// 1: 0.18.0. 2: each comet's kind, ore and Cut cycles after its state (0.19.0). 3: then the target of
        /// every payload bay at Entry, which the game neither saves nor sends (0.20.0). 4: a bay's target may be
        /// NaN, a bay that aims untracked, and after the targets every comet brought down and still falling
        /// (0.21.0 on).
        /// </summary>
        private const byte WireLayout = 4;

        private const int MaxTargets = 4096;

        /// <summary>The whole list, the payload bays' targets and the falling comets, for the instant message and the planet sync.</summary>
        internal static void Write(RocketBinaryWriter writer, MapComet[] comets)
        {
            writer.WriteByte(WireLayout);
            writer.WriteInt32(comets.Length);
            foreach (MapComet comet in comets)
            {
                comet.Write(writer);
            }
            CometAim.Target[] targets = CometAim.Targets;
            writer.WriteInt32(targets.Length);
            foreach (CometAim.Target target in targets)
            {
                writer.WriteInt64(target.BayId);
                writer.WriteSingle(target.X);
                writer.WriteSingle(target.Z);
            }
            CometLanding[] landings = _landings;
            writer.WriteInt32(landings.Length);
            foreach (CometLanding landing in landings)
            {
                landing.Write(writer);
            }
        }

        /// <summary>The host's list, its payload bays' targets and its falling comets; null when it is not one this build can read, which the caller ignores.</summary>
        internal static MapComet[] Read(RocketBinaryReader reader, out CometAim.Target[] targets, out CometLanding[] landings)
        {
            targets = null;
            landings = null;
            if (reader.ReadByte() != WireLayout)
            {
                return null;
            }
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxComets)
            {
                throw new InvalidOperationException("the host's comet map claims " + count + " comets");
            }
            MapComet[] comets = new MapComet[count];
            for (int i = 0; i < count; i++)
            {
                comets[i] = MapComet.Read(reader);
            }
            int aimed = reader.ReadInt32();
            if (aimed < 0 || aimed > MaxTargets)
            {
                throw new InvalidOperationException("the host's comet map claims " + aimed + " payload bays aiming");
            }
            CometAim.Target[] read = new CometAim.Target[aimed];
            for (int i = 0; i < aimed; i++)
            {
                long bay = reader.ReadInt64();
                float x = reader.ReadSingle();
                float z = reader.ReadSingle();
                // NaN is a bay aiming untracked; nothing else that is not a point on the ground is sent.
                if (float.IsInfinity(x) || float.IsInfinity(z) || float.IsNaN(x) != float.IsNaN(z))
                {
                    throw new InvalidOperationException("the host sent payload bay " + bay + " aiming at a point that is not on the ground");
                }
                read[i] = new CometAim.Target(bay, x, z);
            }
            int falling = reader.ReadInt32();
            if (falling < 0 || falling > MaxComets)
            {
                throw new InvalidOperationException("the host's comet map claims " + falling + " comets coming down");
            }
            CometLanding[] down = new CometLanding[falling];
            for (int i = 0; i < falling; i++)
            {
                down[i] = CometLanding.Read(reader);
            }
            targets = read;
            landings = down;
            return comets;
        }

        /// <summary>Client: the host's comets, its payload bays' targets and its falling comets, from either route. The host's list is the list.</summary>
        internal static void FromHost(MapComet[] comets, CometAim.Target[] targets, CometLanding[] landings)
        {
            if (comets == null || !NetworkManager.IsClient)
            {
                return;
            }
            lock (Changes)
            {
                Publish(comets);
                _landings = landings ?? new CometLanding[0];
            }
            CometAim.FromHost(targets);
        }

        // ---- the save -------------------------------------------------------------------------------------

        /// <summary>
        /// XmlSaveLoad.GetWorldData, main thread, as the game gathers what it saves and records its clock:
        /// the comets as they stand at that moment, made into the bytes the save's zip gets. One line per
        /// comet says what was saved.
        /// </summary>
        internal static void SnapshotForSave()
        {
            _saveBytes = null;
            MapComet[] comets = _comets;
            if (NetworkManager.IsClient)
            {
                return;
            }
            CometSaveFile file = new CometSaveFile();
            // The game's own entry for each of these rockets was written None (CometActionPatches.RocketSaveDataPostfix).
            // Deorbit is a burn of one moment, aimed by a target the game does not keep: a save between the order
            // and the burn keeps neither, and the rocket loads idle.
            foreach (Rocket rocket in Rocket.AllRockets)
            {
                string action = rocket == null || rocket.RocketMode == CometModes.Deorbit ? null : CometModes.Name(rocket.RocketMode);
                if (action != null)
                {
                    file.Rockets.Add(new CometRocketRecord { Id = rocket.ReferenceId, Action = action });
                    Log.Info("[TR comet actions] saved rocket " + rocket.ReferenceId + " running " + action);
                }
            }
            CometLanding[] landings = _landings;
            if (comets.Length == 0 && file.Rockets.Count == 0 && landings.Length == 0)
            {
                return;
            }
            double now = Clock;
            foreach (MapComet comet in comets)
            {
                file.Comets.Add(comet.ToRecord(now));
                Log.Info("[TR comet map] saved " + Describe(comet, now));
            }
            foreach (CometLanding landing in landings)
            {
                file.Landings.Add(landing.ToRecord(now));
                Log.Info("[TR comet land] saved " + landing.Describe(now));
            }
            using (MemoryStream bytes = new MemoryStream())
            {
                Serializer.Value.Serialize(bytes, file);
                _saveBytes = bytes.ToArray();
            }
        }

        /// <summary>
        /// SaveHelper.AddZipFile, on the save thread: after the terrain, the comets get an entry of their
        /// own. Never throws: a save is worth more than its comets.
        /// </summary>
        internal static void WriteZipEntry(ICSharpCode.SharpZipLib.Zip.ZipOutputStream zip, string fileName)
        {
            byte[] bytes = _saveBytes;
            if (bytes == null || zip == null || fileName != SaveLoadConstants.TerrainFileName)
            {
                return;
            }
            try
            {
                zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry(SaveEntryName));
                zip.Write(bytes, 0, bytes.Length);
                zip.CloseEntry();
            }
            catch (Exception e)
            {
                Log.Error("[TR comet map] The comets could not be written into this save; the save itself goes on. " + e.Message);
            }
        }

        /// <summary>
        /// SpaceMap.LoadGameSave, once the map and its sites are back with the ids they were saved with:
        /// the comets that save holds. A flight comes back where it was, off the clock until
        /// <see cref="ClockRestored"/>. A comet whose site or route is gone is dropped, by name.
        /// </summary>
        internal static void LoadFromSave(SpaceMapSaveData map)
        {
            List<MapComet> loaded = new List<MapComet>();
            List<CometLanding> falling = new List<CometLanding>();
            Dictionary<long, SpaceMapNode> saved = SavedNodes(map);
            RebuiltSites.Clear();
            List<CometRocketRecord> modes = new List<CometRocketRecord>();
            try
            {
                string folder = XmlSaveLoad.Instance?.CurrentWorldSave?.RootDir?.FullName;
                string path = folder == null ? null : Path.Combine(folder, SaveEntryName);
                if (path == null || !File.Exists(path))
                {
                    return;
                }
                CometSaveFile file;
                using (FileStream stream = File.OpenRead(path))
                {
                    file = Serializer.Value.Deserialize(stream) as CometSaveFile;
                }
                if (file == null || file.Version < 1 || file.Version > SaveVersion)
                {
                    Log.Warn("[TR comet map] This save's comets are in a form this build does not read; none were loaded.");
                    return;
                }
                foreach (CometSaveRecord record in file.Comets ?? new List<CometSaveRecord>())
                {
                    MapComet comet = MapComet.FromRecord(record, out string refused);
                    if (comet == null)
                    {
                        Log.Warn("[TR comet map] Comet " + record?.Id + " in this save was not loaded: it has " + refused + ".");
                        continue;
                    }
                    string missing = MissingNode(comet, saved);
                    if (missing != null)
                    {
                        Log.Warn("[TR comet map] Comet " + comet.Id + " in this save was not loaded: " + missing + ".");
                        continue;
                    }
                    if (loaded.Exists(c => c.Id == comet.Id) || loaded.Count >= MaxComets)
                    {
                        Log.Warn("[TR comet map] Comet " + comet.Id + " in this save was not loaded: the save holds it twice, or too many comets.");
                        continue;
                    }
                    loaded.Add(comet);
                }
                foreach (CometRocketRecord record in file.Rockets ?? new List<CometRocketRecord>())
                {
                    if (record != null && CometModes.TryParse(record.Action, out _))
                    {
                        modes.Add(record);
                        Log.Info("[TR comet actions] loaded rocket " + record.Id + " running " + record.Action);
                    }
                }
                foreach (CometLandingRecord record in file.Landings ?? new List<CometLandingRecord>())
                {
                    CometLanding landing = CometLanding.FromRecord(record, out string refused);
                    if (landing == null || falling.Exists(l => l.CometId == landing.CometId) || loaded.Exists(c => c.Id == landing.CometId) || falling.Count >= MaxComets)
                    {
                        Log.Warn("[TR comet land] The fall of comet " + record?.Comet + " in this save was not loaded: it has " + (refused ?? "a comet of the same id") + ".");
                        continue;
                    }
                    falling.Add(landing);
                    Log.Info("[TR comet land] loaded " + landing.Describe(0.0));
                }
            }
            catch (Exception e)
            {
                Log.Warn("[TR comet map] This save's comets could not be read; none were loaded. " + e.Message);
                loaded.Clear();
                modes.Clear();
                falling.Clear();
            }
            finally
            {
                lock (Changes)
                {
                    Publish(loaded.ToArray());
                    _landings = falling.ToArray();
                    _savedNodes = loaded.Count > 0 ? saved : null;
                    _pendingModes = modes;
                    _pendingFrames = 0;
                }
            }
            if (loaded.Count > 0 || falling.Count > 0)
            {
                CometDirector.Install();
            }
        }

        /// <summary>
        /// Every node a save knew, by the id it knew it by, found again on the rebuilt map without that id:
        /// Entry and the low-orbit hub by their role, a static node by its template, a site as the load
        /// rebuilt it from its record (<see cref="SiteRebuilt"/>).
        /// </summary>
        private static Dictionary<long, SpaceMapNode> SavedNodes(SpaceMapSaveData data)
        {
            Dictionary<long, SpaceMapNode> saved = new Dictionary<long, SpaceMapNode>();
            SpaceMap map = SpaceMap.Current;
            if (data?.Nodes == null || map?.EntryNode == null)
            {
                return saved;
            }
            foreach (MapNodeReference reference in data.Nodes)
            {
                if (reference == null || reference.Id == 0L)
                {
                    continue;
                }
                SpaceMapNode node = null;
                if (reference is SiteNodeReference)
                {
                    RebuiltSites.TryGetValue(reference.Id, out node);
                }
                else if (reference is TemplateNodeReference template)
                {
                    node = template.TemplateId == map.EntryNode.Data?.Id ? map.EntryNode
                        : map.LowOrbitHubNode != null && template.TemplateId == map.LowOrbitHubNode.Data?.Id ? map.LowOrbitHubNode
                        : StaticNamed(map.EntryNode, template.TemplateId);
                }
                if (node != null)
                {
                    saved[reference.Id] = node;
                }
            }
            return saved;
        }

        /// <summary>The static node of this template, found by walking the map from Entry, whatever id it has.</summary>
        private static SpaceMapNode StaticNamed(SpaceMapNode from, string templateId)
        {
            if (from == null || string.IsNullOrEmpty(templateId))
            {
                return null;
            }
            if (from.NodeType == NodeType.Static && from.Data?.Id == templateId)
            {
                return from;
            }
            foreach (NodeConnection connection in from.ChildConnections)
            {
                SpaceMapNode found = connection?.Child?.NodeType == NodeType.Static ? StaticNamed(connection.Child, templateId) : null;
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static string MissingNode(MapComet comet, Dictionary<long, SpaceMapNode> saved)
        {
            if (comet.State == MapCometState.Waiting)
            {
                return saved.ContainsKey(comet.SiteId) ? null : "the site it waits at is not on the map";
            }
            if (SpaceMap.Current?.EntryNode == null)
            {
                return "the map has no Entry";
            }
            if (comet.State == MapCometState.Arrived)
            {
                return null;
            }
            for (int i = 0; i < comet.Route.Length - 1; i++)
            {
                if (!saved.ContainsKey(comet.Route[i]))
                {
                    return "a node on its route is not on the map";
                }
            }
            return null;
        }

        /// <summary>
        /// After a load, once the game has given every node the loaded comets refer to its id for this
        /// session (a site whose saved id the low-orbit hub took is given a new one in
        /// Referencable.AssignNewIdToDuplicates, near the end of the load): every comet's site and route
        /// renumbered from the ids the save knew to those. Then each comet's line, to compare with its line
        /// at the save. Main thread; safe to call again.
        /// </summary>
        internal static void Relink()
        {
            Dictionary<long, SpaceMapNode> saved = _savedNodes;
            if (saved == null || NetworkManager.IsClient || GameManager.GameState != GameState.Running)
            {
                return;
            }
            Dictionary<long, long> ids = new Dictionary<long, long>();
            foreach (KeyValuePair<long, SpaceMapNode> node in saved)
            {
                if (node.Value.ReferenceId == 0L || SpaceMapNode.Get(node.Value.ReferenceId) != node.Value)
                {
                    continue;
                }
                ids[node.Key] = node.Value.ReferenceId;
            }
            MapComet[] relinked;
            lock (Changes)
            {
                List<MapComet> list = new List<MapComet>();
                foreach (MapComet comet in _comets)
                {
                    MapComet renumbered = Renumbered(comet, ids, out string missing);
                    if (renumbered == null)
                    {
                        Log.Warn("[TR comet map] Comet " + comet.Id + " in this save was not loaded: " + missing + ".");
                        continue;
                    }
                    list.Add(renumbered);
                }
                relinked = list.ToArray();
                Publish(relinked);
                _savedNodes = null;
            }
            double now = Clock;
            foreach (MapComet comet in relinked)
            {
                Log.Info("[TR comet map] loaded " + Describe(comet, now));
            }
            CometNet.BroadcastMap();
        }

        /// <summary>
        /// A comet with its site and route renumbered from one set of node ids to another, or null with why
        /// not. A comet that is waiting needs its site, one that has set off and not arrived every node of
        /// its route but Entry, which is always found by its role; a node a parked comet no longer needs
        /// becomes 0.
        /// </summary>
        internal static MapComet Renumbered(MapComet comet, IDictionary<long, long> ids, out string missing)
        {
            missing = null;
            bool needsSite = comet.State == MapCometState.Waiting;
            bool needsRoute = comet.State == MapCometState.Flying || comet.State == MapCometState.Stranded;
            if (!ids.TryGetValue(comet.SiteId, out long site))
            {
                if (needsSite)
                {
                    missing = "the site it waits at is not on the map";
                    return null;
                }
                site = 0L;
            }
            long[] route = new long[comet.Route.Length];
            for (int i = 0; i < route.Length; i++)
            {
                if (ids.TryGetValue(comet.Route[i], out long node))
                {
                    route[i] = node;
                }
                else if (needsRoute && i < route.Length - 1)
                {
                    missing = "a node on its route is not on the map";
                    return null;
                }
            }
            return comet.WithNodes(site, route);
        }

        /// <summary>One comet, its state and where it stands, with every number round-trip exact: how a save and its load are compared.</summary>
        internal static string Describe(MapComet comet, double now)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (comet.State == MapCometState.Waiting)
            {
                return string.Format(c, "comet {0} {1} | site {2} | fuel {3:R} | mol {4:R} | kg {5:R} | engines {6} | kind {7} | ore {8:R} | cycles {9}",
                    comet.Id, comet.State, Identity(SpaceMapNode.Get(comet.SiteId)), comet.Fuel, comet.TotalMoles, comet.CometKg, comet.Engines, comet.Kind, comet.OreUnits, comet.Cycles);
            }
            RoutePosition position = comet.At(now);
            return string.Format(c, "comet {0} {1} | flown {2:R} | hop {3} | progress {4:R} | fuel {5:R} | mol {6:R} | kg {7:R} | engines {8} | node {9} | kind {10} | ore {11:R} | cycles {12}",
                comet.Id, comet.State, comet.State == MapCometState.Flying ? comet.FlownAt(now) : position.BurnSeconds, position.Stop.Hop, position.Stop.Progress,
                position.FuelLeft, comet.TotalMoles, comet.CometKg, comet.Engines,
                Identity(position.Arrived ? SpaceMap.Current?.EntryNode : RouteNode(comet, position.Stop.Hop)),
                comet.Kind, comet.OreUnits, comet.Cycles);
        }

        /// <summary>
        /// The save's own check, outside the game (tools/PatchCheck): comets waiting, in flight and stranded,
        /// with awkward fractions, written as the save writes them and read back as the load reads them,
        /// must come back the same to the last bit, and the flight must stand at the same place once it is
        /// back on the clock, whenever that is. Null when it holds, else what did not.
        /// </summary>
        public static string CheckSave()
        {
            Chemistry.GasType[] gases = { Chemistry.GasType.CarbonDioxide, Chemistry.GasType.Water };
            double[] moles = { 123456.789, 0.1 + 0.2 };
            long[] route = { 42L, 17L, 5L, 3L };
            double[] hops = { 200.00000298023224, 80.599998474121094, 302.70001220703125 };
            const double start = 1000.123;
            const double saved = start + 57.891;
            MapComet waiting = new MapComet(1, MapCometState.Waiting, CometKind.Ice, gases, moles, 0.0, 17, 5433.3332, 2, 8790.1234567, 0.016, 42L, null, null, double.NaN, 0.0);
            MapComet flying = new MapComet(2, MapCometState.Flying, CometKind.Ice, gases, moles, 0.0, 0, 18765.4321, 2, 30000.0 / 7.0, 0.016, 42L, route, hops, start, 0.0);
            MapComet stranded = new MapComet(3, MapCometState.Flying, CometKind.Ice, gases, moles, 0.0, 3, 5433.3332, 2, 1234.5678, 0.016, 42L, route, hops, start, 0.0);
            stranded = stranded.Settled(stranded.At(1e9).Arrived);
            MapComet mineral = new MapComet(4, MapCometState.Waiting, CometKind.Mineral, null, null, 1234.5 / 7.0, 999, 2.592 / 3.0, 1, 0.1 + 0.7, 0.016, 42L, null, null, double.NaN, 0.0);
            MapComet[] before = { waiting, flying, stranded, mineral };
            CometSaveFile file = new CometSaveFile();
            foreach (MapComet comet in before)
            {
                file.Comets.Add(comet.ToRecord(saved));
            }
            // Two falls, an ice comet tracked and a mineral comet untracked, each part way down at the save.
            LandingAim aimed = new LandingAim(77L, 88L, true, 1234.5678, -987.654321, 1300.1, -1000.0 / 3.0, 10.0, 20.0, 5100.0, 2124.25, 0.1 + 0.2, 1801.3441234, 98.7654321, 5, start, 1900.0);
            CometLanding ice = new CometLanding(6, CometKind.Ice, gases, new[] { 0.7 * moles[0], 0.7 * moles[1] }, 0.0, 0.0, 0.7 * (moles[0] + moles[1]),
                1300.1, 312.25 / 3.0, -1000.0 / 3.0, 0.6, -0.4, Math.Sqrt(1.0 - 0.52), 123456, 50.0, saved - 10.0 / 3.0, 0.0, aimed);
            CometLanding rock = new CometLanding(7, CometKind.Mineral, new[] { Chemistry.GasType.CarbonDioxide }, new[] { 1234.5 / 7.0 }, 2.592 * 1000.0 / 3.0, 0.7, 1234.5 / 7.0,
                -55.5, 200.0 / 7.0, 4000.25, 0.0, -1.0, 0.0, 9, 50.0, saved - 20.0, 3.25, null);
            CometLanding[] falls = { ice, rock };
            foreach (CometLanding fall in falls)
            {
                file.Landings.Add(fall.ToRecord(saved));
            }
            file.Rockets.Add(new CometRocketRecord { Id = 9876543210L, Action = CometModes.Name(CometModes.Fuel) });
            CometSaveFile read;
            using (MemoryStream bytes = new MemoryStream())
            {
                Serializer.Value.Serialize(bytes, file);
                bytes.Position = 0;
                read = Serializer.Value.Deserialize(bytes) as CometSaveFile;
            }
            if (read?.Comets == null || read.Comets.Count != before.Length)
            {
                return "the comets entry does not read back as the comets written";
            }
            if (read.Version != SaveVersion || read.Rockets == null || read.Rockets.Count != 1 || read.Rockets[0].Id != 9876543210L
                || !CometModes.TryParse(read.Rockets[0].Action, out RocketMode mode) || mode != CometModes.Fuel)
            {
                return "a rocket running Fuel does not read back from the save as it was written";
            }
            if (read.Landings == null || read.Landings.Count != falls.Length)
            {
                return "the falling comets do not read back from the save";
            }
            for (int i = 0; i < falls.Length; i++)
            {
                CometLanding was = falls[i];
                CometLanding back = CometLanding.FromRecord(read.Landings[i], out string refused);
                if (back == null)
                {
                    return "the fall of comet " + was.CometId + " written by the save is refused by the load: it has " + refused;
                }
                bool same = back.CometId == was.CometId && back.Kind == was.Kind && Same(back.Moles, was.Moles) && back.Gases.Length == was.Gases.Length
                    && Same(new[] { back.MineralKg, back.AirKgPerKg, back.ShowMoles, back.ImpactX, back.ImpactY, back.ImpactZ, back.TravelX, back.TravelY, back.TravelZ, back.FallSeconds },
                        new[] { was.MineralKg, was.AirKgPerKg, was.ShowMoles, was.ImpactX, was.ImpactY, was.ImpactZ, was.TravelX, was.TravelY, was.TravelZ, was.FallSeconds })
                    && back.Seed == was.Seed && !back.OnClock && back.Aim.Tracked == was.Aim.Tracked && back.Aim.RocketId == was.Aim.RocketId
                    && Same(new[] { back.Aim.SkyX, back.Aim.SkyZ, back.Aim.TargetX, back.Aim.TargetZ, back.Aim.Steered, back.Aim.FuelSpent, back.Aim.FuelLeft },
                        new[] { was.Aim.SkyX, was.Aim.SkyZ, was.Aim.TargetX, was.Aim.TargetZ, was.Aim.Steered, was.Aim.FuelSpent, was.Aim.FuelLeft });
                for (int j = 0; same && j < was.Gases.Length; j++)
                {
                    same = back.Gases[j] == was.Gases[j];
                }
                if (!same)
                {
                    return "the fall of comet " + was.CometId + " does not read back from the save as it was written";
                }
                // Back on the clock when the load restores the save's own clock, it lands when it would have.
                CometLanding onClock = back.BackOnClock(saved);
                if (BitConverter.DoubleToInt64Bits(onClock.FallenAt(saved)) != BitConverter.DoubleToInt64Bits(was.FallenAt(saved))
                    || Math.Abs(onClock.ImpactClock - was.ImpactClock) > 1e-9 * Math.Abs(was.ImpactClock))
                {
                    return string.Format(CultureInfo.InvariantCulture, "the fall of comet {0} saved to land at clock {1:R} lands at {2:R} after its load", was.CometId, was.ImpactClock, onClock.ImpactClock);
                }
            }
            for (int i = 0; i < before.Length; i++)
            {
                MapComet was = before[i];
                MapComet back = MapComet.FromRecord(read.Comets[i], out string refused);
                if (back == null)
                {
                    return "comet " + was.Id + " written by the save is refused by the load: it has " + refused;
                }
                bool same = back.Id == was.Id && back.State == was.State && back.Kind == was.Kind && back.Cycles == was.Cycles
                    && BitConverter.DoubleToInt64Bits(back.OreUnits) == BitConverter.DoubleToInt64Bits(was.OreUnits)
                    && back.SiteId == was.SiteId && back.Engines == was.Engines
                    && back.CometKg == was.CometKg && back.Fuel == was.Fuel && back.FuelKgPerMole == was.FuelKgPerMole
                    && Same(back.Moles, was.Moles) && Same(back.Hops, was.Hops) && back.Route.Length == was.Route.Length
                    && Array.TrueForAll(back.Gases, g => Array.IndexOf(was.Gases, g) == Array.IndexOf(back.Gases, g));
                for (int j = 0; same && j < was.Route.Length; j++)
                {
                    same = back.Route[j] == was.Route[j];
                }
                if (!same)
                {
                    return "comet " + was.Id + " does not read back from the save as it was written";
                }
                if (was.State == MapCometState.Waiting)
                {
                    continue;
                }
                RoutePosition atSave = was.At(saved);
                foreach (double later in new[] { 0.0, saved, 98765.4321 })
                {
                    RoutePosition atLoad = (back.OnClock ? back : back.BackOnClock(later)).At(later);
                    if (atLoad.Stop.Hop != atSave.Stop.Hop || atLoad.Stop.Progress != atSave.Stop.Progress || atLoad.DeltaV != atSave.DeltaV
                        || atLoad.FuelLeft != atSave.FuelLeft || atLoad.Settled != atSave.Settled)
                    {
                        return string.Format(CultureInfo.InvariantCulture, "comet {0} saved at hop {1} at {2:R} is back at hop {3} at {4:R} when its load puts it on the clock at {5}",
                            was.Id, atSave.Stop.Hop, atSave.Stop.Progress, atLoad.Stop.Hop, atLoad.Stop.Progress, later);
                    }
                }
            }

            // A load gives nodes new ids: the low-orbit hub takes a fresh one first, so a site saved as 42 comes
            // back as 43 while the hub is now 42. Every node a comet refers to must move to its new id, none may
            // keep the old one, and a flight must stand where it stood.
            Dictionary<long, long> moved = new Dictionary<long, long> { { 42L, 43L }, { 17L, 17L }, { 5L, 6L }, { 3L, 1L } };
            foreach (MapComet was in before)
            {
                MapComet renumbered = Renumbered(was, moved, out string missing);
                long[] expected = Array.ConvertAll(was.Route, id => moved[id]);
                if (renumbered == null || renumbered.SiteId != 43L || renumbered.Route.Length != expected.Length
                    || !Array.TrueForAll(expected, id => Array.IndexOf(renumbered.Route, id) >= 0) || Array.IndexOf(renumbered.Route, 42L) >= 0)
                {
                    return "comet " + was.Id + " is not moved to the ids its nodes have after a load" + (missing != null ? ": " + missing : "");
                }
                if (was.State != MapCometState.Waiting)
                {
                    RoutePosition a = was.At(saved);
                    RoutePosition b = renumbered.At(saved);
                    if (a.Stop.Hop != b.Stop.Hop || a.Stop.Progress != b.Stop.Progress || a.FuelLeft != b.FuelLeft)
                    {
                        return "comet " + was.Id + " moves along its route when its nodes are renumbered";
                    }
                }
            }
            Dictionary<long, long> gone = new Dictionary<long, long> { { 42L, 43L }, { 5L, 6L }, { 3L, 1L } };
            if (Renumbered(flying, gone, out _) != null || Renumbered(new MapComet(5, MapCometState.Waiting, CometKind.Ice, gases, moles, 0.0, 0, 1.0, 1, 1.0, 0.016, 99L, null, null, double.NaN, 0.0), moved, out _) != null)
            {
                return "a comet whose route or site is no longer on the map after a load is kept";
            }
            if (Renumbered(flying.Settled(true), gone, out _) == null)
            {
                return "a comet parked at Entry is dropped because a node it no longer needs is gone";
            }

            // A save made by 0.18.0, before comets had a kind, ore or cycles, still loads its ice comet.
            const string firstVersion = "<?xml version=\"1.0\"?><TerraformingReloadedComets Version=\"1\"><Comet Id=\"5\" State=\"Waiting\"><Site>42</Site>"
                + "<CometKg>1000</CometKg><Engines>1</Engines><Fuel>10</Fuel><FuelKgPerMole>0.016</FuelKgPerMole><Flown>0</Flown>"
                + "<Gases><Gas Type=\"Water\" Moles=\"100\" /></Gases><Route /><Hops /></Comet></TerraformingReloadedComets>";
            CometSaveFile old;
            using (StringReader text = new StringReader(firstVersion))
            {
                old = Serializer.Value.Deserialize(text) as CometSaveFile;
            }
            MapComet oldComet = old?.Comets?.Count == 1 ? MapComet.FromRecord(old.Comets[0], out _) : null;
            if (old?.Version != 1 || oldComet == null || oldComet.Kind != CometKind.Ice || oldComet.OreUnits != 0.0 || oldComet.Cycles != 0 || oldComet.TotalMoles != 100.0)
            {
                return "a comet saved by 0.18.0 does not load as the ice comet it was";
            }
            return null;
        }

        private static bool Same(double[] a, double[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (BitConverter.DoubleToInt64Bits(a[i]) != BitConverter.DoubleToInt64Bits(b[i]))
                {
                    return false;
                }
            }
            return true;
        }

        // ---- the list -----------------------------------------------------------------------------------

        private static void Publish(MapComet[] comets) => _comets = comets;

        private static MapComet[] Append(MapComet[] list, MapComet comet)
        {
            MapComet[] longer = new MapComet[list.Length + 1];
            Array.Copy(list, longer, list.Length);
            longer[list.Length] = comet;
            return longer;
        }

        private static MapComet[] Replace(MapComet[] list, MapComet comet)
        {
            MapComet[] copy = (MapComet[])list.Clone();
            for (int i = 0; i < copy.Length; i++)
            {
                if (copy[i].Id == comet.Id)
                {
                    copy[i] = comet;
                }
            }
            return copy;
        }

        internal static void MarkSaveInstalled() => _saveInstalled = true;

        internal static void Fault(string message)
        {
            if (++_faults <= 3)
            {
                Log.Warn("[TR comet map] " + message);
            }
        }
    }
}
