using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Serialization;
using BepInEx.Logging;
using Objects.Rockets;
using Objects.Rockets.UI;
using TerraformingReloaded.Comets;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// -CometFlight: a comet as a map object, flying by the rocket rule (docs/COMET-ROCKETS.md, stage 3).
    ///
    /// Phase "make", in a new world: the driver discovers a real site with the game's own discovery (the
    /// node nearest the orbit that can discover one), and makes three comets there with the mod's debug
    /// verb: A with a quarter more fuel than its route needs, B with half of what A needs, C four times A's
    /// gas with a quarter more than it needs. Before they go, the site guard: the site holding A is kept
    /// when the game's own removal is called on it, and a second site with no comet is removed. Then all
    /// three are sent at once. A must reach Entry when the rule says, and when a flight stepped as the game
    /// steps a rocket says (every fixed update, mass recounted at each 0.5 s fuel draw); B must stop at the
    /// rule's share of the route and still be there 30 s later. With C partway, the world is saved.
    ///
    /// Phase "load", in a fresh instance: the save is loaded. A must be parked at Entry with its fuel, B
    /// stranded where it was, and C must fly on and reach Entry when the rule says. run.ps1 compares the
    /// mod's own "saved" and "loaded" lines across the two instances, comet by comet, and the fuel and
    /// place each phase reports.
    ///
    /// Every expectation is worked out here from the game's own map: the route from the game's pathfinder,
    /// each hop's Δv from the game's NodeTransit, the masses from the game's molar masses, and the clock
    /// from OrbitalSimulation. Only the rule's arithmetic is the mod's, and that is checked against the
    /// game's ticks in tools/PatchCheck.
    /// </summary>
    internal sealed class CometFlightCheck
    {
        private const double TimeTolerance = 0.5;
        private const double TimeShare = 0.001;
        private const double SteppedShare = 0.005;
        private const double HoldSeconds = 30.0;
        private const double SaveFrom = 0.15;
        private const double SaveTo = 0.85;
        private const double SlackSeconds = 60.0;
        private const uint StartTick = 20;

        private readonly ManualLogSource _log;
        private readonly Action<string[]> _command;
        private readonly bool _load;

        private enum Stage { Setup, Flying, Saving, Loaded, Done }

        private Stage _stage = Stage.Setup;
        private double _route;
        private double _premix;
        private SpaceMapNode _site;
        private int _a, _b, _c;
        private double _sentClock;
        private bool _aJudged, _bJudged;
        private double _bStoppedClock = double.NaN;
        private CometView _bStopped;
        private double _savedClock;
        private bool _failed;

        // The flight stepped as the game steps a rocket, for comet A, on FixedUpdate.
        private bool _stepping;
        private double _stepDry, _stepFuel, _stepGained, _stepSeconds, _stepNextDraw;
        private int _stepEngines;
        private double _steppedArrival = double.NaN;

        private CometFlightCheck(ManualLogSource log, Action<string[]> command, bool load)
        {
            _log = log;
            _command = command;
            _load = load;
        }

        /// <summary>The check run.ps1 asked for, or null when this is another scenario.</summary>
        public static CometFlightCheck FromEnvironment(ManualLogSource log, Action<string[]> command)
        {
            string phase = Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETFLIGHT");
            return phase == "make" || phase == "load" ? new CometFlightCheck(log, command, phase == "load") : null;
        }

        private static double Clock => OrbitalSimulation.System?.TotalRealTimeSeconds ?? 0.0;

        private void Info(string line) => _log.LogInfo("LiveCheck: comet " + line);

        private void Fail(string why)
        {
            if (!_failed)
            {
                _failed = true;
                Info("FAIL " + why);
                _stage = Stage.Done;
            }
        }

        // ---- main thread ---------------------------------------------------------------------------

        public void Step()
        {
            if (_stage == Stage.Done || _failed)
            {
                return;
            }
            try
            {
                if (_load)
                {
                    StepLoad();
                }
                else
                {
                    StepMake();
                }
            }
            catch (Exception e)
            {
                Fail("the driver threw: " + e);
            }
        }

        private void StepMake()
        {
            switch (_stage)
            {
                case Stage.Setup when GameManager.GameTickCount >= StartTick:
                    SetUp();
                    break;
                case Stage.Flying:
                    Watch();
                    break;
                case Stage.Saving when Clock - _savedClock > 10.0:
                    Info("phase make done");
                    _stage = Stage.Done;
                    break;
            }
        }

        private void SetUp()
        {
            SpaceMap map = SpaceMap.Current;
            if (map?.EntryNode == null)
            {
                Fail("the headless world has no space map, or no Entry on it");
                return;
            }
            Info(string.Format(CultureInfo.InvariantCulture, "map | {0} | entry {1} (id {2}) | low orbit hub {3} | nodes {4} | rocket canvas {5} | simulation {6} | save part {7}",
                map.Id, map.EntryNode.DisplayName, map.EntryNode.ReferenceId, map.LowOrbitHubNode?.ReferenceId.ToString(CultureInfo.InvariantCulture) ?? "none",
                map.Nodes.Count, RocketCanvas.Instance != null ? "present" : "absent", GameManager.RunSimulation ? "host" : "client", CometMap.SaveInstalled ? "installed" : "MISSING"));
            if (!CometMap.SaveInstalled)
            {
                Fail("the mod's comet map save part is not installed");
                return;
            }
            // The node nearest the orbit that can discover a site, by the game's own route to it.
            SpaceMapNode parent = null;
            double best = double.MaxValue;
            foreach (SpaceMapNode node in map.Nodes)
            {
                if (node?.DiscoverData != null && node.DiscoverData.SiteGenerations.Count > 0 && Route(node, map.EntryNode, out double deltaV, out _) && deltaV < best)
                {
                    best = deltaV;
                    parent = node;
                }
            }
            if (parent == null)
            {
                Fail("no node on the map can discover a site");
                return;
            }
            _site = Discover(parent);
            if (_site == null)
            {
                return;
            }
            if (!Route(_site, map.EntryNode, out _route, out string hops))
            {
                Fail("the game's pathfinder finds no route from the site to Entry");
                return;
            }
            _premix = CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));
            int index = CometMap.Sites().IndexOf(_site) + 1;
            Info(string.Format(CultureInfo.InvariantCulture, "site | {0} ({1}, id {2}) under {3} | listed as {4} | route {5} | {6:R} delta-v | premix {7:R} kg/mol | difficulty {8}",
                _site.DisplayName, _site.Data?.Id, _site.ReferenceId, parent.DisplayName, index, hops, _route, _premix, DifficultySetting.Current?.Id));

            double aKg = Kg(100000.0);
            double cKg = Kg(400000.0);
            double aFuel = FuelFor(aKg, 2, 1.25);
            double bFuel = 0.5 * aFuel / 1.25;
            double cFuel = FuelFor(cKg, 2, 1.25);
            _a = Make(index, "100000", 2, aFuel);
            _b = Make(index, "100000", 2, bFuel);
            _c = Make(index, "400000", 2, cFuel);
            if (_a == 0 || _b == 0 || _c == 0)
            {
                Fail("the mod's make verb did not make all three comets");
                return;
            }
            foreach (int id in new[] { _a, _b, _c })
            {
                CometView made = Find(id);
                double kg = id == _c ? cKg : aKg;
                if (made.State != MapCometState.Waiting || made.AtNode != _site.ReferenceId || made.CometKg != kg || made.Engines != 2)
                {
                    Fail(string.Format(CultureInfo.InvariantCulture, "comet {0} was made {1} at node {2} weighing {3:R} kg with {4} engines, expected waiting at the site, {5:R} kg by the game's molar masses, 2 engines",
                        id, made.State, made.AtNode, made.CometKg, made.Engines, kg));
                    return;
                }
                Flight rule = Rule(made.CometKg, made.Engines, Departure(id));
                Info(string.Format(CultureInfo.InvariantCulture, "made | comet {0} | fuel {1:R} | kg {2:R} | engines {3} | the rule: {4} in {5:0.000} s, {6:R} of the route",
                    id, Departure(id), made.CometKg, made.Engines, rule.Arrives ? "arrives" : "runs dry", rule.Seconds, rule.Share));
            }
            Guard(parent);
            if (_failed)
            {
                return;
            }
            _sentClock = Clock;
            foreach (int id in new[] { _a, _b, _c })
            {
                _command(new[] { "comet", "send", id.ToString(CultureInfo.InvariantCulture) });
            }
            foreach (int id in new[] { _a, _b, _c })
            {
                CometView view = Find(id);
                if (view.State != MapCometState.Flying || view.StartClock != _sentClock)
                {
                    Fail(string.Format(CultureInfo.InvariantCulture, "comet {0} is {1} after send, set off at clock {2:R}, expected Flying from {3:R}", id, view.State, view.StartClock, _sentClock));
                    return;
                }
            }
            _stepDry = aKg + CelestialRules.EngineMassKg * 2;
            _stepFuel = aFuel;
            _stepEngines = 2;
            _stepGained = 0.0;
            _stepSeconds = 0.0;
            _stepNextDraw = 0.0;
            _stepping = true;
            _stage = Stage.Flying;
            Info(string.Format(CultureInfo.InvariantCulture, "sent | comets {0}, {1}, {2} at clock {3:R}", _a, _b, _c, _sentClock));
        }

        /// <summary>A site under this node, made by the game's own discovery: enough points and the node spawns one.</summary>
        private SpaceMapNode Discover(SpaceMapNode parent)
        {
            HashSet<SpaceMapNode> before = new HashSet<SpaceMapNode>(CometMap.Sites());
            parent.DiscoverPoints = parent.DiscoverData.Difficulty;
            SpaceMapNode made = CometMap.Sites().FirstOrDefault(s => !before.Contains(s));
            if (made == null)
            {
                Fail("discovery at " + parent.DisplayName + " made no site");
            }
            return made;
        }

        /// <summary>
        /// The site guard, by the game's own removal: the host branch of RocketMotherboard.RemoveSpaceMapNode
        /// uses nothing of the motherboard, so an uninitialised one is enough to reach it headless.
        /// </summary>
        private void Guard(SpaceMapNode parent)
        {
            RocketMotherboard board = (RocketMotherboard)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RocketMotherboard));
            board.RemoveSpaceMapNode(_site);
            bool kept = SpaceMapNode.Get(_site.ReferenceId) == _site && CometMap.Sites().Contains(_site);
            SpaceMapNode empty = Discover(parent);
            if (empty == null)
            {
                return;
            }
            long emptyId = empty.ReferenceId;
            board.RemoveSpaceMapNode(empty);
            bool removed = SpaceMapNode.Get(emptyId) == null && !CometMap.Sites().Contains(empty);
            Info(string.Format(CultureInfo.InvariantCulture, "guard {0} | the site comet {1} waits at {2} on the map | a site with no comet {3}",
                kept && removed ? "PASS" : "FAIL", _a, kept ? "stayed" : "was REMOVED from", removed ? "was removed" : "was NOT removed"));
            if (!kept || !removed)
            {
                Fail("the site guard did not keep the comet's site, or kept a site with no comet");
            }
        }

        private void Watch()
        {
            double now = Clock;
            CometView a = Find(_a);
            CometView b = Find(_b);
            CometView c = Find(_c);
            Flight aRule = Rule(a.CometKg, a.Engines, Departure(_a));
            if (!_aJudged && a.State == MapCometState.Arrived)
            {
                _aJudged = true;
                JudgeArrival("arrive", a, now - _sentClock, Rule(a.CometKg, a.Engines, Departure(_a)), _steppedArrival);
            }
            else if (!_aJudged && now - _sentClock > aRule.Seconds + SlackSeconds)
            {
                Fail(string.Format(CultureInfo.InvariantCulture, "comet {0} has not arrived {1:0.0} s after it was sent; the rule says {2:0.0} s", _a, now - _sentClock, aRule.Seconds));
                return;
            }
            if (!_bJudged && b.State == MapCometState.Stranded && double.IsNaN(_bStoppedClock))
            {
                _bStoppedClock = now;
                _bStopped = b;
            }
            if (!_bJudged && !double.IsNaN(_bStoppedClock) && now - _bStoppedClock >= HoldSeconds)
            {
                _bJudged = true;
                JudgeStrand(b);
            }
            else if (!_bJudged && double.IsNaN(_bStoppedClock) && now - _sentClock > Rule(b.CometKg, b.Engines, Departure(_b)).Seconds + SlackSeconds)
            {
                Fail(string.Format(CultureInfo.InvariantCulture, "comet {0} has not run out of fuel {1:0.0} s after it was sent", _b, now - _sentClock));
                return;
            }
            if (_failed || !_aJudged || !_bJudged)
            {
                return;
            }
            if (c.State != MapCometState.Flying)
            {
                Fail("comet " + _c + " finished its flight before the save; it is " + c.State);
                return;
            }
            if (c.RouteShare >= SaveFrom && c.RouteShare <= SaveTo)
            {
                _savedClock = now;
                _stage = Stage.Saving;
                Info(string.Format(CultureInfo.InvariantCulture, "saving | at clock {0:R} | comet {1} at {2:0.000} of its route, hop {3} at {4:0.0000}", now, _c, c.RouteShare, c.Hop, c.Progress));
                Util.Commands.CommandLine.Process("file save");
            }
            else if (c.RouteShare > SaveTo)
            {
                Fail("comet " + _c + " was past " + SaveTo + " of its route before A and B were judged; it is too short to save in the middle of");
            }
        }

        private void JudgeArrival(string what, CometView view, double flown, Flight rule, double stepped)
        {
            bool atEntry = view.AtNode == SpaceMap.Current.EntryNode.ReferenceId;
            bool onTime = Math.Abs(flown - rule.Seconds) <= TimeTolerance + TimeShare * rule.Seconds;
            bool steppedOnTime = double.IsNaN(stepped) || Math.Abs(stepped - rule.Seconds) <= TimeTolerance + SteppedShare * rule.Seconds;
            bool fuel = Math.Abs(view.FuelLeft - (Departure(view.Id) - rule.FuelMoles)) <= 1e-6 * Math.Max(1.0, Departure(view.Id));
            bool pass = rule.Arrives && atEntry && onTime && steppedOnTime && fuel;
            Info(string.Format(CultureInfo.InvariantCulture,
                "{0} {1} | comet {2} reached {3} after {4:0.000} s by the clock | rule {5:0.000} s | {6} | fuel left {7:R} (rule {8:R})",
                what, pass ? "PASS" : "FAIL", view.Id, atEntry ? "Entry " + SpaceMap.Current.EntryNode.DisplayName : "a node that is NOT Entry", flown, rule.Seconds,
                double.IsNaN(stepped) ? "stepped as the game steps a rocket: not measured" : string.Format(CultureInfo.InvariantCulture, "stepped as the game steps a rocket {0:0.000} s", stepped),
                view.FuelLeft, Departure(view.Id) - rule.FuelMoles));
            if (!pass)
            {
                Fail("comet " + view.Id + " did not arrive at Entry when the rule says");
            }
        }

        private void JudgeStrand(CometView now)
        {
            Flight rule = Rule(now.CometKg, now.Engines, Departure(now.Id));
            Stop expected = CelestialRules.StopAt(Hops(), rule.DeltaV);
            double stoppedAfter = _bStoppedClock - _sentClock;
            bool where = !rule.Arrives && Math.Abs(now.RouteShare - rule.Share) <= 1e-9 && now.Hop == expected.Hop && Math.Abs(now.Progress - expected.Progress) <= 1e-9;
            bool when = Math.Abs(stoppedAfter - rule.Seconds) <= TimeTolerance + TimeShare * rule.Seconds;
            bool stayed = now.Hop == _bStopped.Hop && now.Progress == _bStopped.Progress && now.State == MapCometState.Stranded && now.FuelLeft <= 1e-9;
            bool pass = where && when && stayed;
            Info(string.Format(CultureInfo.InvariantCulture,
                "strand {0} | comet {1} stopped {2:0.000} s after it was sent (rule {3:0.000} s) at {4:R} of the route (rule {5:R}), hop {6} at {7:R} (rule hop {8} at {9:R}) | {10} {11:0} s later | fuel left {12:R}",
                pass ? "PASS" : "FAIL", now.Id, stoppedAfter, rule.Seconds, now.RouteShare, rule.Share, now.Hop, now.Progress, expected.Hop, expected.Progress,
                stayed ? "still there" : "MOVED", HoldSeconds, now.FuelLeft));
            if (!pass)
            {
                Fail("comet " + now.Id + " did not stop for good where the rule says");
            }
        }

        // ---- phase load ----------------------------------------------------------------------------

        private bool _loadedReported;
        private bool _cJudged;

        private void StepLoad()
        {
            if (GameManager.GameTickCount < 3)
            {
                return;
            }
            CometView[] views = CometMap.Views();
            if (!_loadedReported)
            {
                _loadedReported = true;
                if (!ReadMade())
                {
                    return;
                }
                if (views.Length != 3)
                {
                    Fail("the load brought back " + views.Length + " comets, expected 3");
                    return;
                }
                SpaceMap map = SpaceMap.Current;
                List<SpaceMapNode> sites = CometMap.Sites();
                if (sites.Count != 1 || !Route(sites[0], map.EntryNode, out _route, out string hops))
                {
                    Fail("the load brought back " + sites.Count + " sites, expected the one the comets were made at, with a route to Entry");
                    return;
                }
                _site = sites[0];
                _premix = CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));
                Info(string.Format(CultureInfo.InvariantCulture, "site | {0} (id {1}) | route {2} | {3:R} delta-v", _site.DisplayName, _site.ReferenceId, hops, _route));
                Info(string.Format(CultureInfo.InvariantCulture, "loaded | at clock {0:R} | entry {1} (id {2}) | low orbit hub {3}", Clock, map.EntryNode.DisplayName, map.EntryNode.ReferenceId,
                    map.LowOrbitHubNode?.ReferenceId.ToString(CultureInfo.InvariantCulture) ?? "none"));
                CometView parked = Find(_a);
                CometView stranded = Find(_b);
                bool parkedOk = parked.State == MapCometState.Arrived && parked.AtNode == map.EntryNode.ReferenceId;
                Info(string.Format(CultureInfo.InvariantCulture, "parked {0} | comet {1} {2} at {3} | fuel left {4:R}",
                    parkedOk ? "PASS" : "FAIL", parked.Id, parked.State, parked.AtNode == map.EntryNode.ReferenceId ? "Entry " + map.EntryNode.DisplayName : "a node that is NOT Entry", parked.FuelLeft));
                bool strandedOk = stranded.State == MapCometState.Stranded && stranded.AtNode == 0L;
                Info(string.Format(CultureInfo.InvariantCulture, "stranded {0} | comet {1} {2} | hop {3} at {4:R} | {5:R} of the route | fuel left {6:R}",
                    strandedOk ? "PASS" : "FAIL", stranded.Id, stranded.State, stranded.Hop, stranded.Progress, stranded.RouteShare, stranded.FuelLeft));
                CometView flying = Find(_c);
                Info(string.Format(CultureInfo.InvariantCulture, "resumed | comet {0} {1} | flown {2:0.000} s | hop {3} at {4:0.0000} | {5:0.000} of the route | set off at clock {6:R}",
                    flying.Id, flying.State, flying.FlownSeconds, flying.Hop, flying.Progress, flying.RouteShare, flying.StartClock));
                if (!parkedOk || !strandedOk || flying.State != MapCometState.Flying || double.IsNaN(flying.StartClock))
                {
                    Fail("the comets did not come back parked, stranded and flying on the clock");
                }
                return;
            }
            CometView c = Find(_c);
            double flown = Clock - c.StartClock;
            if (!_cJudged && c.State == MapCometState.Arrived)
            {
                _cJudged = true;
                JudgeArrival("resume arrive", c, flown, Rule(c.CometKg, c.Engines, Departure(c.Id)), double.NaN);
                if (!_failed)
                {
                    Info("done");
                    _stage = Stage.Done;
                }
            }
            else if (!_cJudged && flown > c.FlightSeconds + SlackSeconds)
            {
                Fail(string.Format(CultureInfo.InvariantCulture, "comet {0} has not arrived {1:0.0} s into its flight; the rule says {2:0.0} s", _c, flown, c.FlightSeconds));
            }
        }

        /// <summary>
        /// Phase load: the three comets and the fuel each was made with, from phase make's "made" lines,
        /// which run.ps1 hands over as TR_LIVECHECK_COMETFLIGHT_MADE, "id:fuel" for A, B and C.
        /// </summary>
        private bool ReadMade()
        {
            string[] made = (Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETFLIGHT_MADE") ?? "").Split(',');
            int[] ids = new int[3];
            for (int i = 0; i < made.Length && i < 3; i++)
            {
                string[] parts = made[i].Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ids[i])
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double fuel))
                {
                    break;
                }
                _departureFuel[ids[i]] = fuel;
            }
            if (_departureFuel.Count != 3)
            {
                Fail("run.ps1 did not hand over the three comets phase make made (TR_LIVECHECK_COMETFLIGHT_MADE)");
                return false;
            }
            _a = ids[0];
            _b = ids[1];
            _c = ids[2];
            return true;
        }

        // ---- the flight stepped as the game steps a rocket, FixedUpdate ------------------------------

        /// <summary>
        /// Rocket.PhysicsUpdate's step for comet A, from the moment it was sent: every fixed update, Δv
        /// grows by thrust over the whole mass times the step; every 0.5 s the engines draw their fuel and
        /// the mass is recounted. Independent of the mod's rule.
        /// </summary>
        public void FixedStep()
        {
            if (!_stepping)
            {
                return;
            }
            double dt = Time.fixedDeltaTime;
            if (_stepSeconds >= _stepNextDraw - 1e-9)
            {
                double draw = Math.Min(_stepFuel, _stepEngines * CelestialRules.EngineMolesPerSecond * CelestialRules.FuelTickSeconds);
                if (!(draw > 0.0))
                {
                    _stepping = false;
                    return;
                }
                _stepFuel -= draw;
                _stepNextDraw += CelestialRules.FuelTickSeconds * draw / (_stepEngines * CelestialRules.EngineMolesPerSecond * CelestialRules.FuelTickSeconds);
            }
            _stepGained += _stepEngines * CelestialRules.EngineThrustNewtons / (_stepDry + _stepFuel * _premix) * dt;
            _stepSeconds += dt;
            if (_stepGained >= _route)
            {
                _steppedArrival = _stepSeconds;
                _stepping = false;
            }
        }

        // ---- arithmetic from the game's own figures --------------------------------------------------

        private readonly Dictionary<int, double> _departureFuel = new Dictionary<int, double>();

        /// <summary>The fuel a comet set off with: what make gave it, read back before it was sent.</summary>
        private double Departure(int id) => _departureFuel.TryGetValue(id, out double fuel) ? fuel : double.NaN;

        private Flight Rule(double kg, int engines, double fuel) => CelestialRules.Shipped.Fly(kg, engines, fuel, _route, _premix);

        private double Kg(double carbonDioxide) => carbonDioxide * Mole.MolarMass(Chemistry.GasType.CarbonDioxide) / 1000.0;

        /// <summary>The fuel that is <paramref name="margin"/> times what the route needs, the fuel's own mass included.</summary>
        private double FuelFor(double kg, int engines, double margin)
        {
            double fuel = 1000.0;
            for (int i = 0; i < 50; i++)
            {
                fuel = margin * CelestialRules.Shipped.Fly(kg, engines, fuel, _route, _premix).FuelMoles;
            }
            return fuel;
        }

        private int Make(int site, string moles, int engines, double fuel)
        {
            HashSet<int> before = new HashSet<int>(CometMap.Views().Select(v => v.Id));
            _command(new[] { "comet", "make", site.ToString(CultureInfo.InvariantCulture), moles, "CarbonDioxide",
                "engines=" + engines.ToString(CultureInfo.InvariantCulture), "fuel=" + fuel.ToString("R", CultureInfo.InvariantCulture) });
            foreach (CometView view in CometMap.Views())
            {
                if (!before.Contains(view.Id))
                {
                    _departureFuel[view.Id] = view.FuelLeft;
                    return view.Id;
                }
            }
            return 0;
        }

        private static CometView Find(int id)
        {
            foreach (CometView view in CometMap.Views())
            {
                if (view.Id == id)
                {
                    return view;
                }
            }
            throw new InvalidOperationException("comet " + id + " is gone from the map");
        }

        private double[] Hops()
        {
            Route(_site, SpaceMap.Current.EntryNode, out _, out _, out double[] hops);
            return hops;
        }

        private static bool Route(SpaceMapNode from, SpaceMapNode to, out double deltaV, out string text) => Route(from, to, out deltaV, out text, out _);

        /// <summary>The game's route and each hop's Δv, as the game's pathfinder returns them: reversed, the first hop last.</summary>
        private static bool Route(SpaceMapNode from, SpaceMapNode to, out double deltaV, out string text, out double[] hops)
        {
            deltaV = 0.0;
            text = null;
            hops = null;
            if (!SpaceMapPathFinder.GetPath(from, to, out List<NodeTransit> path) || path == null || path.Count == 0)
            {
                return false;
            }
            hops = new double[path.Count];
            List<string> names = new List<string> { from.DisplayName };
            for (int i = 0; i < path.Count; i++)
            {
                NodeTransit transit = path[path.Count - 1 - i];
                hops[i] = transit.GetDistance();
                deltaV += hops[i];
                names.Add(string.Format(CultureInfo.InvariantCulture, "{0} ({1:R})", transit.Destination.DisplayName, hops[i]));
            }
            text = string.Join(" -> ", names);
            return true;
        }
    }
}
