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

        internal const double MaxMoles = 1e12;
        internal const int MaxEngines = 64;
        internal const double MaxFuel = 1e10;
        private const int MaxComets = 1000;

        private static readonly object Changes = new object();
        private static volatile MapComet[] _comets = new MapComet[0];
        private static volatile bool _saveInstalled;
        private static volatile byte[] _saveBytes;
        private static int _faults;

        private static readonly Lazy<XmlSerializer> Serializer = new Lazy<XmlSerializer>(() => new XmlSerializer(typeof(CometSaveFile)));

        /// <summary>Every comet now on the map, one consistent list.</summary>
        internal static MapComet[] All => _comets;

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
                _saveBytes = null;
            }
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

        /// <summary>Host: a comet at a site. Null on success, else why not.</summary>
        internal static string Make(SpaceMapNode site, Chemistry.GasType[] gases, double[] moles, int engines, double fuel, out MapComet made)
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
            double kg = 0.0;
            for (int i = 0; i < gases.Length; i++)
            {
                kg += moles[i] * Mole.MolarMass(gases[i]) / 1000.0;
            }
            lock (Changes)
            {
                int id = 1;
                foreach (MapComet comet in _comets)
                {
                    id = Math.Max(id, comet.Id + 1);
                }
                made = new MapComet(id, MapCometState.Waiting, gases, moles, kg, engines, fuel, PremixKgPerMole(), site.ReferenceId, null, null, double.NaN, 0.0);
                string wrong = made.Refusal();
                if (wrong != null)
                {
                    made = null;
                    return "That comet has " + wrong + ".";
                }
                Publish(Append(_comets, made));
            }
            CometDirector.Install();
            Log.Info(string.Format(CultureInfo.InvariantCulture, "[TR comet map] comet {0} made at {1} (site {2}): {3:R} mol, {4:R} kg, {5} engines, {6:R} mol of fuel",
                made.Id, site.DisplayName, site.ReferenceId, made.TotalMoles, made.CometKg, engines, fuel));
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

        // ---- every frame -----------------------------------------------------------------------------

        /// <summary>
        /// Main thread, every frame, on every machine: a flight read from a save goes back on the clock
        /// once the game is running again, and a flight whose rule says it is over is settled where the
        /// rule put it. Every machine settles by the same rule at the same clock; the host also tells
        /// everyone, so a client that missed the moment is put right.
        /// </summary>
        internal static void Step()
        {
            MapComet[] comets = _comets;
            if (comets.Length == 0 || SpaceMap.Current == null || GameManager.GameState != GameState.Running)
            {
                return;
            }
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
            MapComet[] comets = _comets;
            bool waiting = false;
            foreach (MapComet comet in comets)
            {
                waiting |= comet.State == MapCometState.Flying && !comet.OnClock;
            }
            if (!waiting || NetworkManager.IsClient || GameManager.GameState != GameState.Running)
            {
                return;
            }
            double now = Clock;
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
            }
            CometNet.BroadcastMap();
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
                    comet.Fuel, comet.TotalMoles, comet.CometKg, comet.Engines, double.NaN);
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
                comet.TotalMoles, comet.CometKg, comet.Engines, comet.StartClock);
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

        private const byte WireLayout = 1;

        /// <summary>The whole list, for the instant message and the planet sync.</summary>
        internal static void Write(RocketBinaryWriter writer, MapComet[] comets)
        {
            writer.WriteByte(WireLayout);
            writer.WriteInt32(comets.Length);
            foreach (MapComet comet in comets)
            {
                comet.Write(writer);
            }
        }

        /// <summary>The host's list; null when it is not one this build can read, which the caller ignores.</summary>
        internal static MapComet[] Read(RocketBinaryReader reader)
        {
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
            return comets;
        }

        /// <summary>Client: the host's comets, from either route. The host's list is the list.</summary>
        internal static void FromHost(MapComet[] comets)
        {
            if (comets == null || !NetworkManager.IsClient)
            {
                return;
            }
            lock (Changes)
            {
                Publish(comets);
            }
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
            if (comets.Length == 0 || NetworkManager.IsClient)
            {
                return;
            }
            double now = Clock;
            CometSaveFile file = new CometSaveFile();
            foreach (MapComet comet in comets)
            {
                file.Comets.Add(comet.ToRecord(now));
                Log.Info("[TR comet map] saved " + Describe(comet, now));
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
        internal static void LoadFromSave()
        {
            List<MapComet> loaded = new List<MapComet>();
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
                if (file == null || file.Version != 1)
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
                    string missing = MissingNode(comet);
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
                    Log.Info("[TR comet map] loaded " + Describe(comet, 0.0));
                }
            }
            catch (Exception e)
            {
                Log.Warn("[TR comet map] This save's comets could not be read; none were loaded. " + e.Message);
                loaded.Clear();
            }
            finally
            {
                lock (Changes)
                {
                    Publish(loaded.ToArray());
                }
            }
            if (loaded.Count > 0)
            {
                CometDirector.Install();
            }
        }

        private static string MissingNode(MapComet comet)
        {
            if (comet.State == MapCometState.Waiting)
            {
                return SpaceMapNode.Get(comet.SiteId) == null ? "the site it waits at is not on the map" : null;
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
                if (SpaceMapNode.Get(comet.Route[i]) == null)
                {
                    return "a node on its route is not on the map";
                }
            }
            return null;
        }

        /// <summary>One comet, its state and where it stands, with every number round-trip exact: how a save and its load are compared.</summary>
        internal static string Describe(MapComet comet, double now)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (comet.State == MapCometState.Waiting)
            {
                return string.Format(c, "comet {0} {1} | site {2} | fuel {3:R} | mol {4:R} | kg {5:R} | engines {6}",
                    comet.Id, comet.State, comet.SiteId, comet.Fuel, comet.TotalMoles, comet.CometKg, comet.Engines);
            }
            RoutePosition position = comet.At(now);
            return string.Format(c, "comet {0} {1} | flown {2:R} | hop {3} | progress {4:R} | fuel {5:R} | mol {6:R} | kg {7:R} | engines {8} | node {9}",
                comet.Id, comet.State, comet.State == MapCometState.Flying ? comet.FlownAt(now) : position.BurnSeconds, position.Stop.Hop, position.Stop.Progress,
                position.FuelLeft, comet.TotalMoles, comet.CometKg, comet.Engines,
                position.Arrived ? "Entry " + (SpaceMap.Current?.EntryNode?.DisplayName ?? "?") : RouteNode(comet, position.Stop.Hop)?.DisplayName ?? "?");
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
            MapComet waiting = new MapComet(1, MapCometState.Waiting, gases, moles, 5433.3332, 2, 8790.1234567, 0.016, 42L, null, null, double.NaN, 0.0);
            MapComet flying = new MapComet(2, MapCometState.Flying, gases, moles, 18765.4321, 2, 30000.0 / 7.0, 0.016, 42L, route, hops, start, 0.0);
            MapComet stranded = new MapComet(3, MapCometState.Flying, gases, moles, 5433.3332, 2, 1234.5678, 0.016, 42L, route, hops, start, 0.0);
            stranded = stranded.Settled(stranded.At(1e9).Arrived);
            MapComet[] before = { waiting, flying, stranded };
            CometSaveFile file = new CometSaveFile();
            foreach (MapComet comet in before)
            {
                file.Comets.Add(comet.ToRecord(saved));
            }
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
            for (int i = 0; i < before.Length; i++)
            {
                MapComet was = before[i];
                MapComet back = MapComet.FromRecord(read.Comets[i], out string refused);
                if (back == null)
                {
                    return "comet " + was.Id + " written by the save is refused by the load: it has " + refused;
                }
                bool same = back.Id == was.Id && back.State == was.State && back.SiteId == was.SiteId && back.Engines == was.Engines
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
