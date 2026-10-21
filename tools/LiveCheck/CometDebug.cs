using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Localization2;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Serialization;
using BepInEx.Logging;
using Networks;
using Objects.Items;
using Objects.Rockets;
using Objects.Rockets.Log;
using Objects.Rockets.Mining;
using TerraformingReloaded.Comets;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// -CometDebug: the console verbs for staging a comet situation by hand (docs/COMET-ROCKETS.md, "Staging a
    /// situation in a game"), each run through the mod's console command as a player types it, and judged by
    /// what the game and the mod then hold.
    ///
    /// Phase "make", a new world:
    ///   discover   terraform comet discover ice, mineral and with no kind each make one new site of that
    ///              kind under a charted node, its discovery points back to 0, and answer with its number;
    ///   rocket     a rocket from the game's prefabs (as -CometActions builds one: engine mount, miner with a
    ///              Basic drill head, an empty Medium gas fuel tank, a payload bay with a Payload Delivery
    ///              Container) stood in space at Entry, and one left on the ground. terraform comet rocket lists
    ///              both; the one in space is moved to the ice site and then to Entry, each time with every
    ///              field the game's arrival sets as the game sets it and the arrival in the rocket log; then
    ///              the game itself flies it back to the ice site (its own destination and, hop by hop, its own
    ///              `rocket instantmove`), and what the game's trip left must equal what the verb's left; the
    ///              rocket on the ground is refused and left as it was; fill puts the game's refuel figure of
    ///              2:1 premix in the tank at 215 K, and a second fill adds nothing;
    ///   arrive     comet V, sent with fuel to spare, arrive: parked at Entry at once with the rule's fuel left,
    ///              as if sent the rule's seconds ago, and in the sky with the rule's orbit; comet N, sent the
    ///              same way, arrives by itself and must stand the same; comet D, short of fuel, is refused
    ///              with where it strands, and with strand stops where the rule says;
    ///   save       the rocket is moved to the mineral site and the world saved.
    /// Phase "load": the rocket at the same node, idle; V and N parked with the same fuel and back in the sky,
    /// D stranded at the same share; then one Cut cycle at the mineral site by the moved rocket, by the rule.
    /// </summary>
    internal sealed class CometDebugCheck
    {
        private const uint StartTick = 20;
        private const double StageSeconds = 180.0;
        private const double Relative = 1e-9;

        private static readonly CultureInfo C = CultureInfo.InvariantCulture;

        private readonly ManualLogSource _log;
        private readonly Func<string[], string> _command;
        private readonly bool _load;

        private enum Stage { Setup, Moved, GameTrip, Filled, Arrived, Natural, Stranded, Offing, Off, Saving, Loaded, Cut, Done }

        private Stage _stage = Stage.Setup;
        private double _stageClock;
        private int _frames;
        private bool _failed;

        private SpaceMapNode _ice, _mineral;
        private Rocket _rocket, _ground;
        private RocketMiner _miner;
        private Tank _tank;
        private string _verbTrip;
        private int _arrivalsBefore;
        private int _gameMoves;

        private int _v, _n, _d;
        private double _vFuel, _nFuel, _nSent;

        private CometDebugCheck(ManualLogSource log, Func<string[], string> command, bool load)
        {
            _log = log;
            _command = command;
            _load = load;
        }

        public static CometDebugCheck FromEnvironment(ManualLogSource log, Func<string[], string> command)
        {
            string phase = Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETDEBUG");
            return phase == "make" || phase == "load" ? new CometDebugCheck(log, command, phase == "load") : null;
        }

        private static double Clock => OrbitalSimulation.System?.TotalRealTimeSeconds ?? 0.0;

        private static SpaceMapNode Entry => SpaceMap.Current.EntryNode;

        private void Info(string line) => _log.LogInfo("LiveCheck: debug " + line);

        private void Fail(string why)
        {
            if (!_failed)
            {
                _failed = true;
                Info("FAIL " + why);
                _stage = Stage.Done;
            }
        }

        private void Next(Stage stage)
        {
            _stage = stage;
            _stageClock = Clock;
            _frames = 0;
        }

        private bool Overdue(string what)
        {
            if (Clock - _stageClock > StageSeconds)
            {
                Fail(what + " did not happen within " + StageSeconds + " s");
                return true;
            }
            return false;
        }

        private string Cmd(params string[] args) => _command(new[] { "comet" }.Concat(args).ToArray());

        private bool Judge(string what, bool pass, string detail)
        {
            Info(what + " " + (pass ? "PASS" : "FAIL") + " | " + detail);
            if (!pass)
            {
                Fail(what + " did not hold (see the line above)");
            }
            return pass;
        }

        // ---- main thread --------------------------------------------------------------------------------

        public void Step()
        {
            if (_stage == Stage.Done || _failed)
            {
                return;
            }
            try
            {
                _frames++;
                HoldPower();
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
                    if (CometsOn() && Discovered() && Build() && Listed() && Move(_ice, SiteNumber(_ice).ToString(C), "move to the ice site") && VerbTrip() && Move(Entry, "entry", "move to Entry"))
                    {
                        // The game's own trip back to the ice site, for the comparison: its destination, then its
                        // own instant move hop by hop.
                        _arrivalsBefore = 0;
                        _rocket.ChangeTarget(_ice);
                        Next(Stage.GameTrip);
                    }
                    break;
                case Stage.GameTrip:
                    GameTrip();
                    break;
                case Stage.Moved when _frames > 3:
                    Ground();
                    Fill();
                    break;
                case Stage.Filled when _frames > 3:
                    FillAgain();
                    break;
                case Stage.Arrived when _frames > 3:
                    JudgeParked("arrive", _v, _vFuel, Clock - _stageClock, double.NaN);
                    if (!_failed)
                    {
                        _n = MakeAndSend("100", "4", "20000", out _nFuel);
                        _nSent = Clock;
                        Next(Stage.Natural);
                    }
                    break;
                case Stage.Natural:
                    Natural();
                    break;
                case Stage.Stranded when _frames > 3:
                    Stranded();
                    break;
                case Stage.Offing when _frames > 3:
                    Offing();
                    break;
                case Stage.Off:
                    Off();
                    break;
                case Stage.Saving when !_saved && _frames > 3:
                    Save();
                    break;
                case Stage.Saving when _saved && Clock - _stageClock > 10.0:
                    Info("phase make done");
                    _stage = Stage.Done;
                    break;
            }
        }

        // ---- discover -----------------------------------------------------------------------------------

        /// <summary>A new world has comets off, the default: first that every verb refuses, then on.</summary>
        private bool CometsOn()
        {
            string refused = Cmd("discover");
            bool off = !TerraformingReloaded.Effective.CometsEnabled && refused == "Comets are experimental and off for this world. Turn them on, at your own risk, with: terraform set CometsEnabled true";
            string answer = Switch(true);
            return Judge("default off", off && TerraformingReloaded.Effective.CometsEnabled && TerraformingReloaded.Patching.Gate.CometsEnabled(),
                "a new world: comets " + (off ? "off" : "NOT OFF") + ", the verb answered '" + refused + "'; terraform set CometsEnabled true answered '" + answer.Split('\n')[0].Trim() + "'");
        }

        private bool Discovered()
        {
            SpaceMap map = SpaceMap.Current;
            Info(string.Format(C, "map | entry {0} | nodes {1} | sites {2} | save part {3} | actions part {4}", map?.EntryNode?.DisplayName ?? "none", map?.Nodes.Count ?? 0,
                CometMap.Sites().Count, CometMap.SaveInstalled ? "installed" : "MISSING", TerraformingReloaded.Comets.CometActions.Installed ? "installed" : "MISSING"));
            if (map?.EntryNode == null || !CometMap.SaveInstalled || !TerraformingReloaded.Comets.CometActions.Installed)
            {
                Fail("no space map, or the mod's comet save or actions part is not installed");
                return false;
            }
            _ice = Discover("ice");
            _mineral = _ice == null ? null : Discover("mineral");
            return _mineral != null && Discover(null) != null;
        }

        private SpaceMapNode Discover(string kind)
        {
            HashSet<SpaceMapNode> before = new HashSet<SpaceMapNode>(CometMap.Sites());
            string answer = kind == null ? Cmd("discover") : Cmd("discover", kind);
            List<SpaceMapNode> made = CometMap.Sites().Where(s => !before.Contains(s)).ToList();
            SpaceMapNode site = made.Count == 1 ? made[0] : null;
            SpaceMapNode parent = site?.ParentConnection?.Parent;
            MineableDepositType type = site?.Deposit?.DepositType ?? MineableDepositType.None;
            bool ice = type == MineableDepositType.Ice && site.Deposit.DepositComposition.FrozenGasses.Count > 0;
            bool mineral = (type & (MineableDepositType.Ore | MineableDepositType.ReagentMix)) != 0;
            bool fits = kind == "ice" ? ice : kind == "mineral" ? mineral : ice || mineral;
            bool pass = site != null && fits && site.Deposit.TotalOreAtLocation > 0 && site.IsCharted && parent != null && parent.IsCharted && parent.DiscoverPoints == 0
                && parent.ChildConnections.Any(c => c.Child == site) && answer.StartsWith("Discovered site " + SiteNumber(site) + ", ", StringComparison.Ordinal);
            Judge("discover", pass, string.Format(C, "asked {0} | new sites {1} | site {2} ({3}, id {4}) under {5}, charted {6}, its points {7} | type {8} | total ore {9}",
                kind ?? "any", made.Count, site == null ? 0 : SiteNumber(site), site?.DisplayName, site?.ReferenceId, parent?.DisplayName, parent?.IsCharted, parent?.DiscoverPoints,
                type, site?.Deposit?.TotalOreAtLocation));
            return pass ? site : null;
        }

        private static int SiteNumber(SpaceMapNode site) => CometMap.Sites().IndexOf(site) + 1;

        // ---- the rockets --------------------------------------------------------------------------------

        private bool Build()
        {
            Vector3 at = new Vector3(100f, 400f, 100f);
            EngineFuselage mount = Spawn<EngineFuselage>("StructureEngineMountTypeA1", at);
            RocketNetwork network = mount?.RocketNetwork;
            _rocket = network?.Rocket;
            EngineFuselage groundMount = Spawn<EngineFuselage>("StructureEngineMountTypeA1", at + new Vector3(0f, 0f, 60f));
            _ground = groundMount?.RocketNetwork?.Rocket;
            if (_rocket == null || _ground == null)
            {
                Fail("an engine mount made no rocket network and rocket");
                return false;
            }
            _miner = Spawn<RocketMiner>("StructureRocketMiner", at + new Vector3(8f, 0f, 0f));
            _tank = Spawn<Tank>("StructureMediumRocketGasFuelTank", at + new Vector3(16f, 0f, 0f));
            RocketPayloadBay bay = Spawn<RocketPayloadBay>("StructureRocketLargePayloadBay", at + new Vector3(24f, 0f, 0f));
            if (_miner == null || _tank == null || bay == null)
            {
                Fail("a rocket part did not spawn");
                return false;
            }
            foreach (Structure part in new Structure[] { _miner, _tank, bay })
            {
                part.RocketData = new RocketData(network, part.ThingTransformPosition - mount.ThingTransformPosition);
                network.AdoptFromLoad((IRocketInternals)part);
            }
            network.RefreshRocket();
            Thing headPrefab = Prefab.Find("ItemRocketMiningDrillHead");
            Slot headSlot = _miner.Slots.FirstOrDefault(s => headPrefab is DynamicThing d && s.Type == d.SlotType) ?? _miner.Slots.LastOrDefault();
            RocketMiningDrillHead head = headPrefab == null || headSlot == null ? null : OnServer.Create<RocketMiningDrillHead>(headPrefab, headSlot);
            DeliveryPayload container = OnServer.Create<DeliveryPayload>(Prefab.Find("DynamicPayloadDeliveryContainer"), bay.PayloadSlot);
            if (head == null || container == null)
            {
                Fail("the drill head or the container did not spawn");
                return false;
            }
            _rocket.SetCurrentNode(Entry);
            _rocket.RocketState = RocketState.InSpace;
            HoldPower();
            Info(string.Format(C, "rocket | {0} (id {1}) | at {2} | state {3} | internals {4} | ground rocket {5} (id {6}) state {7} at {8}",
                _rocket.DisplayName, _rocket.ReferenceId, _rocket.CurrentNode?.DisplayName, _rocket.RocketState, network.Internals.Count,
                _ground.DisplayName, _ground.ReferenceId, _ground.RocketState, _ground.CurrentNode?.DisplayName ?? "none"));
            if (network.RocketMiners.Count != 1 || network.RocketPayloadBays.Count != 1 || !network.Internals.Contains(_tank))
            {
                Fail("the rocket did not take its miner, bay and tank as its own");
                return false;
            }
            return true;
        }

        private static T Spawn<T>(string prefabName, Vector3 position) where T : Structure
        {
            Thing prefab = Prefab.Find(prefabName);
            if (prefab == null)
            {
                return null;
            }
            T made = OnServer.Create<T>(prefab, position, Quaternion.identity);
            if (made != null && made.BuildStates != null && made.BuildStates.Count > 0)
            {
                made.CurrentBuildStateIndex = made.BuildStates.Count - 1;
            }
            return made;
        }

        /// <summary>The parts have no cable: the driver keeps the miner on and powered, in place of a battery.</summary>
        private void HoldPower()
        {
            if (_miner == null)
            {
                return;
            }
            if (!_miner.OnOff)
            {
                OnServer.Interact(_miner.InteractOnOff, 1);
            }
            if (!_miner.Powered)
            {
                OnServer.Interact(_miner.InteractPowered, 1);
            }
        }

        /// <summary>The number the verb gives a rocket: its place among the rockets by the game's id.</summary>
        private static int Number(Rocket rocket) =>
            Rocket.AllRockets.Where(r => r != null && !r.BeingDestroyed).OrderBy(r => r.ReferenceId).ToList().IndexOf(rocket) + 1;

        private bool Listed()
        {
            string answer = Cmd("rocket");
            string space = Number(_rocket) + ": " + _rocket.DisplayName + ", in space at " + Entry.DisplayName + ", idle; a miner with a ";
            string ground = Number(_ground) + ": " + _ground.DisplayName + ", ";
            bool pass = answer.Contains(space) && answer.Contains(ground) && answer.Contains("a payload bay holding ") && answer.Contains(_tank.DisplayName + " with 0 mol of methane");
            return Judge("list", pass, "the rocket in space listed as '" + space + "...', the one on the ground as '" + ground + "', its bay and its empty tank");
        }

        /// <summary>Every field the game's arrival sets, as the game leaves them at the end of a trip.</summary>
        private static string TripEnd(Rocket r) => string.Format(C,
            "its node's rockets hold it {0} | other nodes {1} | target {2} | transit {3} | progress {4:R} | velocity {5:R} | acceleration {6:R} | engine acceleration {7:R} | mode {8} | state {9} | travelling {10} | action {11}",
            r.CurrentNode?.RocketsHere.Count(x => x == r) ?? 0, SpaceMap.Current.Nodes.Count(n => n != null && n != r.CurrentNode && n.RocketsHere.Contains(r)),
            r.TargetNode?.DisplayName ?? "none", r.CurrentTransit == null ? "none" : "SET", r.Progress, r.Velocity, r.Acceleration, r.EngineAcceleration, (byte)r.RocketMode,
            r.RocketState, r.IsTravelling, r.CurrentAction?.GetType().Name ?? "none");

        private const string Ended = "its node's rockets hold it 1 | other nodes 0 | target none | transit none | progress 0 | velocity 0 | acceleration 0 | engine acceleration 0 | mode 0 | state InSpace | travelling False | action none";

        /// <summary>The rocket log's arrivals of the rocket at this node (ReachedDestinationEvent, which the game's arrival writes).</summary>
        private int Arrivals(SpaceMapNode node) =>
            RocketLog.GetLogText().Split('\n').Count(l => l.Contains(GameStrings.RocketLogArrived.AsString(_rocket.DisplayName, node.DisplayName)));

        private bool Move(SpaceMapNode node, string typed, string what)
        {
            SpaceMapNode from = _rocket.CurrentNode;
            string answer = Cmd("rocket", Number(_rocket).ToString(C), typed);
            string end = TripEnd(_rocket);
            bool pass = _rocket.CurrentNode == node && end == Ended && answer.Contains(" arrived at " + node.DisplayName + " from " + from?.DisplayName);
            return Judge("move", pass, string.Format(C, "{0} | from {1} to {2} | now at {3} | {4}", what, CometMap.Identity(from),
                CometMap.Identity(node), CometMap.Identity(_rocket.CurrentNode), end));
        }

        /// <summary>The game's own trip: each frame its `rocket instantmove`, until its own travel step ends the trip.</summary>
        /// <summary>What the verb's trip to the ice site left, for the game's own trip there to be held to.</summary>
        private bool VerbTrip()
        {
            _verbTrip = TripEnd(_rocket);
            return true;
        }

        private int _tripEnded = -1;
        private string _gameTrip;

        private void GameTrip()
        {
            if (_frames == 3)
            {
                // The verb's arrival at the ice site is in the rocket log by now.
                _arrivalsBefore = Arrivals(_ice);
            }
            if (_frames <= 3)
            {
                return;
            }
            if (_rocket.TargetNode != null)
            {
                if (_rocket.CurrentTransit != null && _rocket.Progress < 1f)
                {
                    _gameMoves++;
                    Util.Commands.CommandLine.Process("rocket instantmove " + _rocket.ReferenceId.ToString(C));
                }
                Overdue("the game's own trip to the ice site");
                return;
            }
            if (_tripEnded < 0)
            {
                // What the game's own arrival left, the frame it ended the trip.
                _tripEnded = _frames;
                _gameTrip = TripEnd(_rocket);
                return;
            }
            if (_frames < _tripEnded + 3)
            {
                return;
            }
            int after = Arrivals(_ice);
            bool pass = _rocket.CurrentNode == _ice && _gameTrip == Ended && _verbTrip == Ended && after == _arrivalsBefore + 1;
            if (Judge("game trip", pass, string.Format(C, "the game's own trip from Entry to {0} in {1} instant moves | now at {2} | the game left: {3} | the verb left: {4} | arrivals in the rocket log {5} -> {6}",
                CometMap.Identity(_ice), _gameMoves, CometMap.Identity(_rocket.CurrentNode), _gameTrip, _verbTrip, _arrivalsBefore, after)))
            {
                Next(Stage.Moved);
            }
        }

        private void Ground()
        {
            SpaceMapNode nodeBefore = _ground.CurrentNode;
            RocketState stateBefore = _ground.RocketState;
            string answer = Cmd("rocket", Number(_ground).ToString(C), "entry");
            Judge("ground", answer.Contains(" Launch it first") && _ground.CurrentNode == nodeBefore && _ground.RocketState == stateBefore && _ground.TargetNode == null,
                string.Format(C, "the rocket on the ground, state {0} at {1}, refused: '{2}' | after: state {3} at {4}, target {5}", stateBefore, nodeBefore?.DisplayName ?? "none", answer,
                    _ground.RocketState, _ground.CurrentNode?.DisplayName ?? "none", _ground.TargetNode?.DisplayName ?? "none"));
        }

        // ---- fill ---------------------------------------------------------------------------------------

        private double _filledMoles;

        private void Fill()
        {
            if (_failed)
            {
                return;
            }
            Cmd("rocket", Number(_rocket).ToString(C), "fill");
            Next(Stage.Filled);
        }

        private void FillAgain()
        {
            Atmosphere store = _tank.InternalAtmosphere;
            double methane = Moles(store, Chemistry.GasType.Methane);
            double oxygen = Moles(store, Chemistry.GasType.Oxygen);
            double total = store.TotalMoles.ToDouble();
            double full = IdealGas.Quantity(new PressurekPa(30000.0), store.Volume, new TemperatureKelvin(215.0)).ToDouble();
            double kelvin = store.Temperature.ToDouble();
            bool pass = Math.Abs(methane - 2.0 * oxygen) <= Relative * methane && Math.Abs(total - full) <= 1e-6 * full && Math.Abs(methane + oxygen - total) <= Relative * total
                && Math.Abs(kelvin - 215.0) < 1.0;
            Judge("fill", pass, string.Format(C, "tank {0} of {1:R} L | methane {2:R} oxygen {3:R} (2:1 is {4:R}) | total {5:R}, the game's refuel figure {6:R} (30 MPa at 215 K) | {7:R} K",
                _tank.DisplayName, store.Volume.ToDouble(), methane, oxygen, 2.0 * oxygen, total, full, kelvin));
            if (_failed)
            {
                return;
            }
            _filledMoles = total;
            Cmd("rocket", Number(_rocket).ToString(C), "fill");
            double again = store.TotalMoles.ToDouble();
            if (Judge("fill again", Math.Abs(again - _filledMoles) <= 1e-6 * _filledMoles, string.Format(C, "a full tank filled again holds {0:R} mol, was {1:R}", again, _filledMoles)))
            {
                _v = MakeAndSend("5000", "2", "100000", out _vFuel);
                string answer = Cmd("arrive", _v.ToString(C));
                Info("arrive answered | " + answer);
                Next(Stage.Arrived);
            }
        }

        private static double Moles(Atmosphere store, Chemistry.GasType gas) => store.GasMixture.GetMoleValue(gas).Quantity.ToDouble();

        // ---- arrive -------------------------------------------------------------------------------------

        /// <summary>A comet of water made at the ice site with the debug verb and sent; its id, and its fuel.</summary>
        private int MakeAndSend(string moles, string engines, string fuel, out double fuelMoles)
        {
            HashSet<int> before = new HashSet<int>(CometMap.Views().Select(v => v.Id));
            Cmd("make", SiteNumber(_ice).ToString(C), moles, "Water", "engines=" + engines, "fuel=" + fuel);
            CometView made = CometMap.Views().FirstOrDefault(v => !before.Contains(v.Id));
            fuelMoles = double.Parse(fuel, C);
            if (made.Id == 0)
            {
                Fail("the debug verb made no comet at the ice site");
                return 0;
            }
            Cmd("send", made.Id.ToString(C));
            return made.Id;
        }

        /// <summary>
        /// A comet parked at Entry as the rule ends its flight: Arrived at Entry, the fuel it did not burn, sent
        /// the rule's seconds before it parked (or at <paramref name="sentClock"/>), and in the sky with the orbit the
        /// rule gives it.
        /// </summary>
        private bool JudgeParked(string what, int id, double fuel, double since, double sentClock)
        {
            CometView view = CometMap.Views().FirstOrDefault(v => v.Id == id);
            Flight rule = CelestialRules.Shipped.Fly(view.CometKg, view.Engines, fuel, view.RouteDeltaV, Premix());
            double parkedClock = Clock - since;
            bool sent = double.IsNaN(sentClock)
                ? Math.Abs(view.StartClock - (parkedClock - rule.Seconds)) <= 0.5
                : Math.Abs(view.StartClock - sentClock) <= 2.0;
            CometSkyBodies.Shown shown = CometSkyBodies.For(id);
            SkyFrame? frame = CometSkyBodies.Frame();
            bool sky = shown != null && frame != null && Elements(shown.Body, CometSkyBodies.OrbitOf(frame.Value, id));
            bool pass = view.Id == id && view.State == MapCometState.Arrived && view.AtNode == Entry.ReferenceId && rule.Arrives
                && Math.Abs(view.FuelLeft - (fuel - rule.FuelMoles)) <= Relative * fuel && Math.Abs(view.FlownSeconds - rule.Seconds) <= 1e-9 * rule.Seconds && sent && sky;
            return Judge(what, pass, string.Format(C, "comet {0} {1} at {2} | fuel left {3:R} (rule {4:R} of {5:R}) | burnt {6:R} s (rule {7:R}) | sent at clock {8:R}, parked by clock {9:R} | in the sky {10}",
                id, view.State, CometMap.Identity(SpaceMapNode.Get(view.AtNode)), view.FuelLeft, fuel - rule.FuelMoles, fuel, view.FlownSeconds, rule.Seconds, view.StartClock, parkedClock,
                shown == null ? "NO" : sky ? "with the rule's orbit" : "with ANOTHER orbit"));
        }

        private static bool Elements(CelestialBody body, SkyOrbit rule)
        {
            Orbit orbit = body.Orbit;
            return orbit.Period == rule.PeriodDays && orbit.Inclination == (float)rule.InclinationDegrees && orbit.Eccentricity == 0f && orbit.SemiMajorAxis == rule.SemiMajorAxisAu
                && orbit.LongitudeOfAscendingNode == rule.AscendingNodeRadians && orbit.ArgumentOfPeriapsis == rule.PeriapsisRadians && body.LongitudeAtEpoch == (float)rule.EpochDegrees;
        }

        private static double Premix() => CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));

        private void Natural()
        {
            CometView view = CometMap.Views().FirstOrDefault(v => v.Id == _n);
            if (view.State != MapCometState.Arrived || CometSkyBodies.For(_n) == null)
            {
                Overdue("comet " + _n + " arriving by itself");
                return;
            }
            if (!JudgeParked("natural", _n, _nFuel, 0.0, _nSent))
            {
                return;
            }
            _d = MakeAndSend("1000000", "1", DryFuel.ToString(C), out double _);
            string answer = Cmd("arrive", _d.ToString(C));
            CometView flying = CometMap.Views().FirstOrDefault(v => v.Id == _d);
            if (!Judge("dry refused", flying.State == MapCometState.Flying && !flying.Arrives && answer.Contains("never reaches Entry"),
                string.Format(C, "comet {0}, short of fuel, answered '{1}' | still {2}", _d, answer, flying.State)))
            {
                return;
            }
            Cmd("arrive", _d.ToString(C), "strand");
            Next(Stage.Stranded);
        }

        /// <summary>Comet D's fuel: a 1e6 mol water comet on one engine burns it in 139 s, far short of any route.</summary>
        private const double DryFuel = 5000.0;

        private void Stranded()
        {
            CometView view = CometMap.Views().FirstOrDefault(v => v.Id == _d);
            Flight rule = CelestialRules.Shipped.Fly(view.CometKg, view.Engines, DryFuel, view.RouteDeltaV, Premix());
            if (!Judge("strand", view.State == MapCometState.Stranded && !rule.Arrives && Math.Abs(view.RouteShare - rule.Share) <= Relative && view.FuelLeft <= Relative * DryFuel
                    && Math.Abs(view.FlownSeconds - rule.Seconds) <= 1e-9 * rule.Seconds && CometSkyBodies.For(_d) == null,
                string.Format(C, "comet {0} {1} at {2:R} of its route (rule {3:R}), {4:R} through the hop from {5} to {6} | burnt {7:R} s (rule {8:R}) | fuel left {9:R} | in the sky {10}",
                    _d, view.State, view.RouteShare, rule.Share, view.Progress, CometMap.Identity(SpaceMapNode.Get(view.FromNode)), CometMap.Identity(SpaceMapNode.Get(view.ToNode)),
                    view.FlownSeconds, rule.Seconds, view.FuelLeft, CometSkyBodies.For(_d) == null ? "no" : "YES")))
            {
                return;
            }
            if (!Move(_mineral, SiteNumber(_mineral).ToString(C), "move to the mineral site"))
            {
                return;
            }
            // A rocket running a comet action when comets go off.
            _rocket.RocketMode = CometModes.Cut;
            Info(string.Format(C, "mode | asked Cut | the rocket is in {0} at {1}", (byte)_rocket.RocketMode, _rocket.CurrentNode?.DisplayName));
            if (_rocket.RocketMode != CometModes.Cut)
            {
                Fail("the game refused Cut at " + _rocket.CurrentNode?.DisplayName);
                return;
            }
            // A comet in flight when comets go off: heavy, so its flight lasts minutes.
            _f = MakeAndSend("1000000", "2", "100000", out double _);
            Next(Stage.Offing);
        }

        // ---- comets off ---------------------------------------------------------------------------------

        private int _f;
        private double _fFlown;

        /// <summary>The world's switch, through terraform set, as a host turns it.</summary>
        private string Switch(bool on) => _command(new[] { "set", "CometsEnabled", on ? "true" : "false" });

        private void Offing()
        {
            CometView f = CometMap.Views().FirstOrDefault(v => v.Id == _f);
            if (f.State != MapCometState.Flying || !(f.FlownSeconds > 0.0) || _rocket.RocketMode != CometModes.Cut)
            {
                Overdue("comet " + _f + " under way, the rocket in Cut");
                return;
            }
            Info("comets off answered | " + Switch(false));
            Next(Stage.Off);
        }

        /// <summary>What comets off leaves, judged the same in both phases: nothing offered, shown or answered.</summary>
        private bool JudgeOff(string what)
        {
            bool offered = new[] { CometModes.Cut, CometModes.Fuel, CometModes.Burn, CometModes.Launch }.Any(m => _mineral.HasAction(m) || _mineral.IsActionAvailable(m))
                || Entry.HasAction(CometModes.Deorbit) || Entry.IsActionAvailable(CometModes.Deorbit);
            string list = Cmd("list"), discover = Cmd("discover"), console = Cmd("1000", "CarbonDioxide", "preview"), rocket = Cmd("rocket");
            const string Refused = "Comets are experimental and off for this world. Turn them on, at your own risk, with: terraform set CometsEnabled true";
            bool verbs = new[] { list, discover, console, rocket }.All(a => a == Refused);
            bool sky = CometSkyBodies.All.Length == 0;
            CometView f = CometMap.Views().FirstOrDefault(v => v.Id == _f);
            bool stopped = f.State == MapCometState.Flying && double.IsNaN(f.StartClock);
            bool idle = _rocket.RocketMode == RocketMode.None;
            return Judge(what, !offered && verbs && sky && stopped && idle && !TerraformingReloaded.Patching.Gate.CometsEnabled(), string.Format(C,
                "comet actions offered {0} | the verbs refuse {1} ('{2}') | comets in the sky {3} | comet {4} {5}, {6} | rocket {7} in mode {8}",
                offered ? "YES" : "none", verbs ? "all four" : "NOT all", list, CometSkyBodies.All.Length, _f, f.State,
                double.IsNaN(f.StartClock) ? string.Format(C, "off the clock {0:R} s into its flight", f.FlownSeconds) : "STILL ON THE CLOCK", _rocket.ReferenceId, (byte)_rocket.RocketMode));
        }

        private bool _judgedOff;

        private void Off()
        {
            // Past the game tick's main-thread moment, where a shorter sky list is published.
            if (!_judgedOff && Clock - _stageClock > 1.5)
            {
                _judgedOff = true;
                if (JudgeOff("off"))
                {
                    _fFlown = CometMap.Views().First(v => v.Id == _f).FlownSeconds;
                }
                return;
            }
            if (_judgedOff && Clock - _stageClock > 4.5)
            {
                double flown = CometMap.Views().First(v => v.Id == _f).FlownSeconds;
                if (Judge("off held", flown == _fFlown && _rocket.RocketMode == RocketMode.None,
                    string.Format(C, "3 s later, comet {0} still {1:R} s into its flight (was {2:R}), rocket mode {3}", _f, flown, _fFlown, (byte)_rocket.RocketMode)))
                {
                    Next(Stage.Saving);
                }
            }
        }

        /// <summary>
        /// A few frames after the last move, once every arrival is in the rocket log: one arrival a trip, the
        /// verb's as the game's; then the save, with comets off.
        /// </summary>
        private void Save()
        {
            int ice = Arrivals(_ice), entry = Arrivals(Entry), mineral = Arrivals(_mineral);
            if (!Judge("rocket log", ice == 2 && _arrivalsBefore == 1 && entry == 1 && mineral == 1,
                string.Format(C, "arrivals of {0}: at the ice site {1} (the verb's, {2} of them before the game's own trip, then the game's), at Entry {3} (the verb's), at the mineral site {4} (the verb's)",
                    _rocket.DisplayName, ice, _arrivalsBefore, entry, mineral)))
            {
                return;
            }
            CometView v = CometMap.Views().First(x => x.Id == _v);
            CometView n = CometMap.Views().First(x => x.Id == _n);
            CometView d = CometMap.Views().First(x => x.Id == _d);
            Info(string.Format(C, "saving | rocket {0} at {1} | parked {2} fuel {3:R} | parked {4} fuel {5:R} | stranded {6} share {7:R} | stopped {8} flown {9:R} | comets off | at clock {10:R}",
                _rocket.ReferenceId, CometMap.Identity(_rocket.CurrentNode), _v, v.FuelLeft, _n, n.FuelLeft, _d, d.RouteShare, _f, _fFlown, Clock));
            Util.Commands.CommandLine.Process("file save");
            _saved = true;
            _stageClock = Clock;
        }

        private bool _saved;

        // ---- phase load ---------------------------------------------------------------------------------

        private void StepLoad()
        {
            if (GameManager.GameTickCount < 3)
            {
                return;
            }
            switch (_stage)
            {
                case Stage.Setup:
                    if (_stageClock == 0.0)
                    {
                        _stageClock = Clock;
                    }
                    LoadedOff();
                    break;
                case Stage.Loaded:
                    LoadedOn();
                    break;
                case Stage.Cut:
                    WatchCut();
                    break;
            }
        }

        private static string Env(string key) => Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETDEBUG_" + key) ?? "";

        private int _cyclesBefore;

        /// <summary>The save loaded with comets off, as it was saved: everything kept, nothing offered or shown.</summary>
        private void LoadedOff()
        {
            long.TryParse(Env("ROCKET"), NumberStyles.Integer, C, out long rocketId);
            _rocket = Referencable.Find<Rocket>(rocketId);
            if (_rocket == null || Clock - _stageClock < 3.0)
            {
                if (Clock - _stageClock > 60.0)
                {
                    Fail("the load brought back no rocket " + rocketId);
                }
                return;
            }
            _miner = _rocket.RocketNetwork?.Internals.OfType<RocketMiner>().FirstOrDefault();
            _mineral = _rocket.CurrentNode;
            int.TryParse(Env("STOPPED"), NumberStyles.Integer, C, out _f);
            int.TryParse(Env("STRANDED"), NumberStyles.Integer, C, out _d);
            string end = TripEnd(_rocket);
            if (!Judge("loaded rocket", CometMap.Identity(_rocket.CurrentNode) == Env("AT") && end == Ended && _miner != null && !TerraformingReloaded.Effective.CometsEnabled,
                string.Format(C, "rocket {0} at {1}, saved at {2} | {3} | miner {4} | comets {5} for this world", rocketId, CometMap.Identity(_rocket.CurrentNode), Env("AT"), end,
                    _miner != null ? "aboard" : "MISSING", TerraformingReloaded.Effective.CometsEnabled ? "ON" : "off")))
            {
                return;
            }
            if (!JudgeOff("loaded off") || !JudgeComets("loaded comets", false))
            {
                return;
            }
            CometView? waiting = CometMap.WaitingView(_mineral);
            _cyclesBefore = waiting?.Cycles ?? 0;
            _densityBefore = _mineral.Deposit.Density;
            Info("comets on answered | " + Switch(true));
            Next(Stage.Loaded);
        }

        /// <summary>
        /// Every comet as the save kept it: the parked ones with their fuel (and, once comets are on, in the sky with
        /// the rule's orbit), the stranded one at its share, the stopped one at its seconds (once on, flying on from them).
        /// </summary>
        private bool JudgeComets(string what, bool on)
        {
            List<string> seen = new List<string>();
            bool comets = true;
            SkyFrame? frame = CometSkyBodies.Frame();
            foreach (string entry in Env("PARKED").Split(';'))
            {
                string[] pair = entry.Split('=');
                int id = int.Parse(pair[0], C);
                double fuel = double.Parse(pair[1], C);
                CometView view = CometMap.Views().FirstOrDefault(v => v.Id == id);
                CometSkyBodies.Shown shown = CometSkyBodies.For(id);
                bool sky = on ? shown != null && frame != null && Elements(shown.Body, CometSkyBodies.OrbitOf(frame.Value, id)) : shown == null;
                comets &= view.State == MapCometState.Arrived && view.AtNode == Entry.ReferenceId && view.FuelLeft == fuel && sky;
                seen.Add(string.Format(C, "{0} {1} at {2} fuel {3:R} (saved {4:R}) sky {5}", id, view.State, CometMap.Identity(SpaceMapNode.Get(view.AtNode)), view.FuelLeft, fuel,
                    shown == null ? "none" : sky ? "the rule's orbit" : "ANOTHER orbit"));
            }
            CometView stranded = CometMap.Views().FirstOrDefault(v => v.Id == _d);
            double share = double.Parse(Env("SHARE"), C);
            comets &= stranded.State == MapCometState.Stranded && stranded.RouteShare == share;
            seen.Add(string.Format(C, "{0} {1} at {2:R} of its route (saved {3:R})", _d, stranded.State, stranded.RouteShare, share));
            CometView f = CometMap.Views().FirstOrDefault(v => v.Id == _f);
            double flown = double.Parse(Env("FLOWN"), C);
            bool going = on ? f.State != MapCometState.Waiting && !double.IsNaN(f.StartClock) && f.FlownSeconds >= flown
                : f.State == MapCometState.Flying && double.IsNaN(f.StartClock) && f.FlownSeconds == flown;
            comets &= going;
            seen.Add(string.Format(C, "{0} {1}, {2:R} s into its flight (saved {3:R}), {4}", _f, f.State, f.FlownSeconds, flown, double.IsNaN(f.StartClock) ? "off the clock" : "on the clock"));
            return Judge(what, comets, string.Join(" | ", seen));
        }

        /// <summary>Comets on again: the sky back, the stopped flight flying on, and the rocket back in Cut, by the mod.</summary>
        private void LoadedOn()
        {
            bool skyReady = Env("PARKED").Split(';').All(p => CometSkyBodies.For(int.Parse(p.Split('=')[0], C)) != null);
            if (!skyReady || Clock - _stageClock < 1.5 || _rocket.RocketMode != CometModes.Cut)
            {
                if (Clock - _stageClock > 60.0)
                {
                    Fail(string.Format(C, "comets on again did not bring the parked comets back to the sky, or the rocket back to Cut (mode {0})", (byte)_rocket.RocketMode));
                }
                return;
            }
            if (!JudgeComets("on again", true))
            {
                return;
            }
            Info(string.Format(C, "mode | the rocket is back in {0} at {1} by the mod", (byte)_rocket.RocketMode, _rocket.CurrentNode?.DisplayName));
            Next(Stage.Cut);
        }

        private float _densityBefore;

        private void WatchCut()
        {
            CometView? made = CometMap.WaitingView(_mineral);
            if (made == null || made.Value.Cycles <= _cyclesBefore)
            {
                Overdue("a Cut cycle at the mineral site");
                return;
            }
            _rocket.RocketMode = RocketMode.None;
            CometView comet = made.Value;
            MineableDeposit d = _mineral.Deposit;
            RocketMiningDrillHead head = _miner.Slots.Select(s => s?.Get()).OfType<RocketMiningDrillHead>().FirstOrDefault();
            double yield = d.DepositComposition.ReagentMixes.Count > 0 ? head.ReagentYieldMultiplier : 1.0;
            CelestialTuning tuning = Effective.Celestial;
            double size = CometMap.PlanetSize();
            double k = tuning.SizeMultiplierAt(size > 0.0 ? size : CelestialRules.StandardPlanetSize);
            double expected = CelestialRules.Shipped.CometMoles(d.TotalOreAtLocation, comet.Cycles * yield, tuning.CutSharePerCycle, k);
            bool pass = comet.Kind == CometKind.Mineral && Math.Abs(comet.OreUnits - expected) <= Relative * expected && d.Density < _densityBefore && _miner.QuantityMined == 0;
            if (Judge("cut", pass, string.Format(C, "the moved rocket at {0}: comet {1} {2}, {3} cycles (had {4}) | ore {5:R} units (rule {6:R}: site {7} units, K {8:R}, head yield {9:R}) | site density {10:R} -> {11:R}",
                CometMap.Identity(_mineral), comet.Id, comet.Kind, comet.Cycles, _cyclesBefore, comet.OreUnits, expected, d.TotalOreAtLocation, k, yield, _densityBefore, d.Density)))
            {
                Info("done");
                _stage = Stage.Done;
            }
        }
    }
}
