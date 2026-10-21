using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Serialization;
using BepInEx.Logging;
using HarmonyLib;
using Networks;
using Objects.Items;
using Objects.Rockets;
using Objects.Rockets.Mining;
using TerraformingReloaded.Comets;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// -CometActions: the rocket actions that make and send a comet (docs/COMET-ROCKETS.md, stage 4), run by
    /// a real rocket the game builds and drives.
    ///
    /// The rocket. A headless run has no player to build with, so the driver spawns from the game's own
    /// prefabs what a player would build: an engine mount (the game makes its rocket network and the Rocket
    /// itself as the mount registers, StructureFuselage.OnRegistered), a Rocket Miner with a Basic drill
    /// head, a Medium gas fuel tank, and a Large payload bay holding a Payload Delivery Container with two
    /// Pumped Gas engine kits. The parts are joined to the rocket with the game's own calls its load makes
    /// (Structure.RocketData, RocketNetwork.AdoptFromLoad, then RefreshRocket), not by placing them in the
    /// hull's cells, which needs the hull's exact geometry. The rocket is stood at each site with the game's
    /// SetCurrentNode in space, as a load does; how it got there is not under test. The parts have no cable,
    /// so the driver holds their power on, standing in for a battery. Everything else is the game's own: the
    /// mode is set as the game's mode message sets it, and the game's server tick, the miner's cycle, the
    /// bay's deploy and the atmospherics step run the actions.
    ///
    /// Phase "make", a new world: Cut cycles at a real discovered ice site, judged against the rule from the
    /// game's own site figures, the site's depletion against the game's own; Rig by Deploy; Burn on a comet
    /// holding methane and oxygen at a second site; a Cut at a mineral site; then Fuel, every tick judged
    /// against the rule from the tank's own gas, and the world saved while it runs. Phase "load": the rocket
    /// is back in Fuel where its comet waits, Fuel goes on by the rule, and Launch sends the comet, which must
    /// arrive when the rule says. Phase "vanilla" is CometActionsVanilla, with the mod removed.
    /// </summary>
    internal sealed class CometActionsCheck
    {
        private const uint StartTick = 20;
        private const int CutCycles = 4;
        private const int MineralCycles = 2;
        private const int BurnCycles = 2;
        private const int FuelTicksBeforeSave = 6;
        private const int FuelTicksAfterLoad = 4;
        private const double StageSeconds = 120.0;
        private const double Relative = 1e-9;

        private static CometActionsCheck _instance;

        private readonly ManualLogSource _log;
        private readonly Action<string[]> _command;
        private readonly bool _load;

        private enum Stage { Setup, Build, Cut, Rig, Burn, Mineral, Fuel, Saving, Loaded, LoadFuel, Launch, Done }

        private Stage _stage = Stage.Setup;
        private double _stageClock;
        private bool _failed;

        private SpaceMapNode _ice, _burnSite, _mineral;
        private Rocket _rocket;
        private RocketMiner _miner;
        private Tank _tank;
        private RocketPayloadBay _bay;
        private DeliveryPayload _container;
        private int _cometId;

        // Cut: the site before the first cycle.
        private float _densityBefore, _richnessBefore;
        private uint _minedBefore;
        private float _headBefore;

        // Burn: the comet's last seen state.
        private int _burnId;
        private double _burnFuelSeen;
        private double[] _burnMolesSeen;
        private Chemistry.GasType[] _burnGases;
        private int _burnJudged;

        // Fuel: the tick sampler (on the game tick, after the mod's own step).
        private volatile bool _sampling;
        private int _fuelJudged;
        private double _lastMethane = double.NaN, _lastOxygen, _lastNitrogen, _lastCometFuel;
        private int _lastFuelTicks = -1;

        // Launch.
        private double _launchClock = double.NaN, _launchFuel, _launchKg, _route;
        private int _launchEngines;

        private CometActionsCheck(ManualLogSource log, Action<string[]> command, bool load)
        {
            _log = log;
            _command = command;
            _load = load;
        }

        public static CometActionsCheck FromEnvironment(ManualLogSource log, Action<string[]> command)
        {
            string phase = Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETACTIONS");
            if (phase != "make" && phase != "load")
            {
                return null;
            }
            _instance = new CometActionsCheck(log, command, phase == "load");
            new Harmony("xceled.stationeers.terraformingreloaded.livecheck.cometactions").Patch(
                AccessTools.DeclaredMethod(typeof(Rocket), nameof(Rocket.RocketAtmospherics)),
                postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometActionsCheck), nameof(AtmosphericsPostfix))) { priority = Priority.Last });
            return _instance;
        }

        private static double Clock => OrbitalSimulation.System?.TotalRealTimeSeconds ?? 0.0;

        private void Info(string line) => _log.LogInfo("LiveCheck: actions " + line);

        private void Fail(string why)
        {
            if (!_failed)
            {
                _failed = true;
                _sampling = false;
                Info("FAIL " + why);
                _stage = Stage.Done;
            }
        }

        private void Next(Stage stage)
        {
            _stage = stage;
            _stageClock = Clock;
        }

        private bool Overdue(string what)
        {
            if (Clock - _stageClock > StageSeconds)
            {
                Fail(what + " did not happen within " + StageSeconds + " s" + Diagnose());
                return true;
            }
            return false;
        }

        // ---- main thread ------------------------------------------------------------------------------

        public void Step()
        {
            if (_stage == Stage.Done || _failed)
            {
                return;
            }
            try
            {
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
                    SetUp();
                    break;
                case Stage.Build when Clock - _stageClock > 3.0:
                    StartCut();
                    break;
                case Stage.Cut:
                    WatchCut();
                    break;
                case Stage.Rig:
                    WatchRig();
                    break;
                case Stage.Burn:
                    WatchBurn();
                    break;
                case Stage.Mineral:
                    WatchMineral();
                    break;
                case Stage.Fuel:
                    WatchFuelBeforeSave();
                    break;
                case Stage.Saving when Clock - _stageClock > 10.0:
                    Info("phase make done");
                    _stage = Stage.Done;
                    break;
            }
        }

        private void SetUp()
        {
            SpaceMap map = SpaceMap.Current;
            Info(string.Format(CultureInfo.InvariantCulture, "map | entry {0} | nodes {1} | save part {2} | actions part {3} | planet size {4:R} | K {5:R} | mass scale {6:R}",
                map?.EntryNode?.DisplayName ?? "none", map?.Nodes.Count ?? 0, CometMap.SaveInstalled ? "installed" : "MISSING", TerraformingReloaded.Comets.CometActions.Installed ? "installed" : "MISSING",
                CometMap.PlanetSize(), K(), Tuning.CometMassScale));
            if (map?.EntryNode == null || !CometMap.SaveInstalled || !TerraformingReloaded.Comets.CometActions.Installed)
            {
                Fail("no space map, or the mod's comet save or actions part is not installed");
                return;
            }
            _ice = Discover(MineableDepositType.Ice);
            _burnSite = _ice == null ? null : Discover(MineableDepositType.Ice);
            _mineral = _burnSite == null ? null : Discover(MineableDepositType.ReagentMix | MineableDepositType.Ore);
            if (_mineral == null)
            {
                return;
            }
            foreach (SpaceMapNode site in new[] { _ice, _burnSite, _mineral })
            {
                MineableDeposit d = site.Deposit;
                Info(string.Format(CultureInfo.InvariantCulture, "site | {0} ({1}, id {2}) under {3} | type {4} | density {5:R} richness {6:R} size {7:R} | total ore {8}",
                    site.DisplayName, site.Id, site.ReferenceId, site.ParentConnection?.Parent?.DisplayName, d.DepositType, d.Density, d.Richness, d.Size, d.TotalOreAtLocation));
            }
            if (!Build())
            {
                return;
            }
            Next(Stage.Build);
        }

        /// <summary>A new site of this kind, by the game's own discovery at the nodes nearest the orbit first.</summary>
        private SpaceMapNode Discover(MineableDepositType kind)
        {
            SpaceMap map = SpaceMap.Current;
            List<SpaceMapNode> parents = map.Nodes.Where(n => n?.DiscoverData != null && n.DiscoverData.SiteGenerations.Count > 0)
                .OrderBy(n => RouteDeltaV(n)).ToList();
            foreach (SpaceMapNode parent in parents)
            {
                for (int tries = 0; tries < 6; tries++)
                {
                    HashSet<SpaceMapNode> before = new HashSet<SpaceMapNode>(CometMap.Sites());
                    parent.DiscoverPoints = parent.DiscoverData.Difficulty;
                    SpaceMapNode made = CometMap.Sites().FirstOrDefault(s => !before.Contains(s));
                    if (made == null)
                    {
                        break;
                    }
                    MineableDepositType type = made.Deposit?.DepositType ?? MineableDepositType.None;
                    bool ice = kind == MineableDepositType.Ice;
                    bool fits = ice ? type == MineableDepositType.Ice && made.Deposit.DepositComposition.FrozenGasses.Count > 0
                        : (type & (MineableDepositType.Ore | MineableDepositType.ReagentMix)) != 0;
                    if (fits && made.Deposit.TotalOreAtLocation > 0)
                    {
                        return made;
                    }
                }
            }
            Fail("discovery made no " + kind + " site on the map");
            return null;
        }

        /// <summary>The rocket, from the game's own prefabs (see the class summary).</summary>
        private bool Build()
        {
            Vector3 at = new Vector3(100f, 400f, 100f);
            EngineFuselage mount = Spawn<EngineFuselage>("StructureEngineMountTypeA1", at);
            RocketNetwork network = mount?.RocketNetwork;
            _rocket = network?.Rocket;
            if (_rocket == null)
            {
                Fail("the engine mount made no rocket network and rocket (mount " + (mount != null) + ", network " + (network != null) + ")");
                return false;
            }
            _miner = Spawn<RocketMiner>("StructureRocketMiner", at + new Vector3(8f, 0f, 0f));
            _tank = Spawn<Tank>("StructureMediumRocketGasFuelTank", at + new Vector3(16f, 0f, 0f));
            _bay = Spawn<RocketPayloadBay>("StructureRocketLargePayloadBay", at + new Vector3(24f, 0f, 0f));
            if (_miner == null || _tank == null || _bay == null)
            {
                Fail("a rocket part did not spawn (miner " + (_miner != null) + ", tank " + (_tank != null) + ", bay " + (_bay != null) + ")");
                return false;
            }
            foreach (Structure part in new Structure[] { _miner, _tank, _bay })
            {
                part.RocketData = new RocketData(network, part.ThingTransformPosition - mount.ThingTransformPosition);
                network.AdoptFromLoad((IRocketInternals)part);
            }
            network.RefreshRocket();

            Thing headPrefab = Prefab.Find("ItemRocketMiningDrillHead");
            Slot headSlot = _miner.Slots.FirstOrDefault(s => headPrefab is DynamicThing d && s.Type == d.SlotType) ?? _miner.Slots.LastOrDefault();
            RocketMiningDrillHead head = headPrefab == null || headSlot == null ? null : OnServer.Create<RocketMiningDrillHead>(headPrefab, headSlot);
            _container = OnServer.Create<DeliveryPayload>(Prefab.Find("DynamicPayloadDeliveryContainer"), _bay.PayloadSlot);
            Thing kitPrefab = Prefab.Find("ItemKitGovernedGasRocketEngine");
            int kits = 0;
            for (int i = 0; _container != null && kitPrefab != null && i < 2 && i < _container.Slots.Count; i++)
            {
                MultiConstructor kit = OnServer.Create<MultiConstructor>(kitPrefab, _container.Slots[i]);
                if (kit != null)
                {
                    kit.Quantity = 1;
                    kits++;
                }
            }
            if (head == null || _container == null || kits != 2)
            {
                Fail("the drill head, the container or its two engine kits did not spawn (head " + (head != null) + ", container " + (_container != null) + ", kits " + kits + ")");
                return false;
            }

            // The tank: premix with a little more methane than pairs with its oxygen, and nitrogen, which is
            // not fuel: neither may move.
            GasMixture fill = GasMixtureHelper.Create();
            fill.Add(new Mole(Chemistry.GasType.Methane, new MoleQuantity(18000.0), MoleEnergy.Zero));
            fill.Add(new Mole(Chemistry.GasType.Oxygen, new MoleQuantity(8500.0), MoleEnergy.Zero));
            fill.Add(new Mole(Chemistry.GasType.Nitrogen, new MoleQuantity(500.0), MoleEnergy.Zero));
            fill.TotalEnergy = IdealGas.Energy(fill.HeatCapacity, new TemperatureKelvin(300.0));
            _tank.InternalAtmosphere.Add(fill);

            _rocket.SetCurrentNode(_ice);
            _rocket.RocketState = RocketState.InSpace;
            HoldPower();
            Info(string.Format(CultureInfo.InvariantCulture, "rocket | {0} (id {1}) | network {2} | internals {3} | miners {4} | bays {5} | stores {6} | at {7} | state {8} | head {9} ice yield {10:R} reagent yield {11:R}",
                _rocket.DisplayName, _rocket.ReferenceId, network.ReferenceId, network.Internals.Count, network.RocketMiners.Count, network.RocketPayloadBays.Count,
                CometActionPatches.FuelStores(network).Count, _rocket.CurrentNode?.DisplayName, _rocket.RocketState, head.PrefabName, head.IceYieldMultiplier, head.ReagentYieldMultiplier));
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

        /// <summary>The parts have no cable: the driver keeps them on and powered, in place of a battery.</summary>
        private void HoldPower()
        {
            if (_rocket == null && _load)
            {
                FindRocket();
            }
            foreach (Device device in new Device[] { _miner, _bay })
            {
                if (device == null)
                {
                    continue;
                }
                if (!device.OnOff)
                {
                    OnServer.Interact(device.InteractOnOff, 1);
                }
                if (!device.Powered)
                {
                    OnServer.Interact(device.InteractPowered, 1);
                }
            }
            if (_bay != null && _bay.Payload != null && !_bay.IsOpen)
            {
                OnServer.Interact(_bay.InteractOpen, 1);
            }
        }

        // ---- Cut --------------------------------------------------------------------------------------

        private void StartCut()
        {
            MineableDeposit d = _ice.Deposit;
            _densityBefore = d.Density;
            _richnessBefore = d.Richness;
            _minedBefore = d.MinedQuantityTotal;
            _headBefore = Head()?.Quantity ?? -1f;
            SetMode(CometModes.Cut);
            Next(Stage.Cut);
        }

        private void WatchCut()
        {
            CometView? view = CometMap.WaitingView(_ice);
            if (view == null || view.Value.Cycles < CutCycles)
            {
                Overdue(CutCycles + " Cut cycles");
                return;
            }
            SetMode(RocketMode.None);
            CometView comet = view.Value;
            _cometId = comet.Id;
            MineableDeposit d = _ice.Deposit;
            // The site's ice as the game holds it, and the rule's comet after that many cycles.
            List<Assets.Scripts.Objects.SpawnGas> ice = d.DepositComposition.FrozenGasses[0].SpawnGasses.Where(g => g != null && g.IsValid).ToList();
            double perUnit = ice.Sum(g => (double)g.Quantity);
            double siteMoles = d.TotalOreAtLocation * perUnit;
            double yield = Head().IceYieldMultiplier;
            double expected = CelestialRules.Shipped.CometMoles(siteMoles, comet.Cycles * yield, Tuning.CutSharePerCycle, K());
            bool moles = Math.Abs(comet.TotalMoles - expected) <= Relative * expected;
            bool split = ice.All(g => Math.Abs(GasOf(comet, g.Type) - expected * ice.Where(x => x.Type == g.Type).Sum(x => (double)x.Quantity) / perUnit) <= 1e-6 * expected);
            double contentsKg = comet.Gases.Select((g, i) => comet.GasMoles[i] * Mole.MolarMass(g)).Sum() / 1000.0;
            double kg = contentsKg * Tuning.CometMassScale / K();
            bool weight = Math.Abs(comet.CometKg - kg) <= Relative * kg;
            // The game's own depletion, once a cycle, in the game's own float arithmetic.
            float density = _densityBefore;
            float richness = _richnessBefore;
            for (int i = 0; i < comet.Cycles; i++)
            {
                richness = Mathf.Max(1f, richness - MineableDeposit.RichnessReduction(richness, d.Size));
                density = Mathf.Max(0f, density - MineableDeposit.DensityReduction(d.Size));
            }
            bool depleted = d.Density == density && d.Richness == richness;
            bool noIce = d.MinedQuantityTotal == _minedBefore && _miner.QuantityMined == 0 && _miner.ExportSlot?.Get() == null;
            bool pass = comet.Kind == CometKind.Ice && moles && split && weight && depleted && noIce;
            Info(string.Format(CultureInfo.InvariantCulture,
                "cut {0} | comet {1} at {2} | {3} cycles | mol {4:R} (rule {5:R}: site {6:R} mol, share {7:R}, K {8:R}, head yield {9:R}) | gases {10} | kg {11:R} (rule {12:R}) | site density {13:R} -> {14:R} (game {15:R}) richness {16:R} (game {17:R}) | mined total {18} -> {19} | miner holds {20}, export {21} | head {22:R} -> {23:R}",
                pass ? "PASS" : "FAIL", comet.Id, _ice.DisplayName, comet.Cycles, comet.TotalMoles, expected, siteMoles, Tuning.CutSharePerCycle, K(), yield, Gases(comet),
                comet.CometKg, kg, _densityBefore, d.Density, density, d.Richness, richness, _minedBefore, d.MinedQuantityTotal, _miner.QuantityMined,
                _miner.ExportSlot?.Get() == null ? "empty" : "NOT EMPTY", _headBefore, Head().Quantity));
            if (!pass)
            {
                Fail("Cut did not grow the comet by the rule, deplete the site as the game does, or made ice");
                return;
            }
            SetMode(RocketMode.Deploy);
            Next(Stage.Rig);
        }

        // ---- Rig --------------------------------------------------------------------------------------

        private void WatchRig()
        {
            CometView comet = CometMap.WaitingView(_ice) ?? default;
            if (comet.Engines < 2)
            {
                Overdue("Rig delivering two engines");
                return;
            }
            SetMode(RocketMode.None);
            int kitsLeft = _container.Slots.Count(s => s?.Get() is MultiConstructor);
            bool kept = _bay.Payload == (IRocketPayload)_container;
            bool pass = comet.Id == _cometId && comet.Engines == 2 && kitsLeft == 0 && kept;
            Info(string.Format(CultureInfo.InvariantCulture, "rig {0} | comet {1} | engines {2} | kits left in the container {3} | container {4} | kg {5:R}, with its engines {6:R}",
                pass ? "PASS" : "FAIL", comet.Id, comet.Engines, kitsLeft, kept ? "still in the bay" : "GONE", comet.CometKg, comet.CometKg + CelestialRules.EngineMassKg * comet.Engines));
            if (!pass)
            {
                Fail("Rig did not turn the two engine kits into two engines on the comet");
                return;
            }
            StartBurn();
        }

        // ---- Burn -------------------------------------------------------------------------------------

        private void StartBurn()
        {
            _rocket.SetCurrentNode(_burnSite);
            int index = CometMap.Sites().IndexOf(_burnSite) + 1;
            _command(new[] { "comet", "make", index.ToString(CultureInfo.InvariantCulture), "6000", "Methane:2", "Oxygen:1", "Nitrogen:1", "engines=0", "fuel=0" });
            CometView? made = CometMap.WaitingView(_burnSite);
            if (made == null)
            {
                Fail("the debug verb made no comet at the second site for Burn");
                return;
            }
            _burnId = made.Value.Id;
            _burnGases = made.Value.Gases;
            _burnMolesSeen = made.Value.GasMoles;
            _burnFuelSeen = made.Value.FuelLeft;
            _densityBefore = _burnSite.Deposit.Density;
            SetMode(CometModes.Burn);
            Next(Stage.Burn);
        }

        private void WatchBurn()
        {
            CometView comet = CometMap.WaitingView(_burnSite) ?? default;
            if (comet.Id != _burnId)
            {
                Fail("the Burn comet is gone from its site");
                return;
            }
            if (!(comet.FuelLeft > _burnFuelSeen))
            {
                Overdue("a Burn cycle");
                return;
            }
            // One cycle since the last state seen: the rule from that state.
            double total = _burnMolesSeen.Sum();
            double wanted = total * Tuning.CutSharePerCycle * Head().IceYieldMultiplier;
            int mi = Array.IndexOf(_burnGases, Chemistry.GasType.Methane);
            int oi = Array.IndexOf(_burnGases, Chemistry.GasType.Oxygen);
            double burnt = Math.Min(wanted, Math.Min(_burnMolesSeen[mi] * 1.5, _burnMolesSeen[oi] * 3.0));
            double methane = _burnMolesSeen[mi] - burnt * 2.0 / 3.0;
            double oxygen = _burnMolesSeen[oi] - burnt / 3.0;
            double gained = comet.FuelLeft - _burnFuelSeen;
            double lost = total - comet.GasMoles.Sum();
            bool others = Enumerable.Range(0, _burnGases.Length).Where(i => i != mi && i != oi).All(i => comet.GasMoles[i] == _burnMolesSeen[i]);
            double kg = comet.Gases.Select((g, i) => comet.GasMoles[i] * Mole.MolarMass(g)).Sum() / 1000.0 * Tuning.CometMassScale / K();
            bool pass = Math.Abs(comet.GasMoles[mi] - methane) <= 1e-9 * total && Math.Abs(comet.GasMoles[oi] - oxygen) <= 1e-9 * total
                && Math.Abs(gained - burnt) <= 1e-9 * total && Math.Abs(gained - lost) <= 1e-9 * total && others
                && Math.Abs(comet.CometKg - kg) <= Relative * kg && _burnSite.Deposit.Density == _densityBefore;
            _burnJudged++;
            Info(string.Format(CultureInfo.InvariantCulture,
                "burn {0} | cycle {1} | comet {2} | methane {3:R} (rule {4:R}) oxygen {5:R} (rule {6:R}) | fuel +{7:R} (rule {8:R}), gas -{9:R} | other gases {10} | kg {11:R} (rule {12:R}) | site density {13:R} -> {14:R}",
                pass ? "PASS" : "FAIL", _burnJudged, comet.Id, comet.GasMoles[mi], methane, comet.GasMoles[oi], oxygen, gained, burnt, lost, others ? "unchanged" : "CHANGED",
                comet.CometKg, kg, _densityBefore, _burnSite.Deposit.Density));
            if (!pass)
            {
                Fail("Burn did not turn the comet's methane and oxygen into fuel by the rule, or touched the site");
                return;
            }
            _burnMolesSeen = comet.GasMoles;
            _burnFuelSeen = comet.FuelLeft;
            if (_burnJudged >= BurnCycles)
            {
                SetMode(RocketMode.None);
                _rocket.SetCurrentNode(_mineral);
                SetMode(CometModes.Cut);
                Next(Stage.Mineral);
            }
        }

        // ---- a mineral comet ---------------------------------------------------------------------------

        private void WatchMineral()
        {
            CometView? view = CometMap.WaitingView(_mineral);
            if (view == null || view.Value.Cycles < MineralCycles)
            {
                Overdue(MineralCycles + " Cut cycles at the mineral site");
                return;
            }
            SetMode(RocketMode.None);
            CometView comet = view.Value;
            MineableDeposit d = _mineral.Deposit;
            double yield = d.DepositComposition.ReagentMixes.Count > 0 ? Head().ReagentYieldMultiplier : 1.0;
            double expected = CelestialRules.Shipped.CometMoles(d.TotalOreAtLocation, comet.Cycles * yield, Tuning.CutSharePerCycle, K());
            double kg = expected * Tuning.MineralKgPerUnit * Tuning.CometMassScale / K();
            bool pass = comet.Kind == CometKind.Mineral && comet.Gases.Length == 0 && Math.Abs(comet.OreUnits - expected) <= Relative * expected
                && Math.Abs(comet.CometKg - kg) <= 1e-6 * kg && _miner.QuantityMined == 0;
            Info(string.Format(CultureInfo.InvariantCulture, "mineral {0} | comet {1} at {2} | {3} | {4} cycles | ore {5:R} units (rule {6:R}: site {7} units, K {8:R}, head yield {9:R}) | kg {10:R} (rule {11:R})",
                pass ? "PASS" : "FAIL", comet.Id, _mineral.DisplayName, comet.Kind, comet.Cycles, comet.OreUnits, expected, d.TotalOreAtLocation, K(), yield, comet.CometKg, kg));
            if (!pass)
            {
                Fail("Cut at a mineral site did not make a mineral comet by the rule");
                return;
            }
            _rocket.SetCurrentNode(_ice);
            _lastMethane = double.NaN;
            _sampling = true;
            SetMode(CometModes.Fuel);
            Next(Stage.Fuel);
        }

        // ---- Fuel -------------------------------------------------------------------------------------

        /// <summary>
        /// On the game tick, after the mod's own atmospherics step: one Fuel tick, judged against the rule
        /// from what the tank held after the last one. The comet's gain must be what the tank lost.
        /// </summary>
        private static void AtmosphericsPostfix()
        {
            CometActionsCheck check = _instance;
            if (check == null || !check._sampling || check._tank?.InternalAtmosphere == null || check._rocket == null)
            {
                return;
            }
            try
            {
                check.SampleFuel();
            }
            catch (Exception e)
            {
                check._sampling = false;
                check._log.LogInfo("LiveCheck: actions FAIL the Fuel sampler threw: " + e);
            }
        }

        private void SampleFuel()
        {
            Atmosphere store = _tank.InternalAtmosphere;
            double methane = Get(store, Chemistry.GasType.Methane) + Get(store, Chemistry.GasType.LiquidMethane);
            double oxygen = Get(store, Chemistry.GasType.Oxygen) + Get(store, Chemistry.GasType.LiquidOxygen);
            double nitrogen = Get(store, Chemistry.GasType.Nitrogen) + Get(store, Chemistry.GasType.LiquidNitrogen);
            CometView? view = _rocket.CurrentNode == null ? null : CometMap.WaitingView(_rocket.CurrentNode);
            double fuel = view?.FuelLeft ?? double.NaN;
            // Judged only on a tick the mod's own step ran Fuel on: the action starts and stops on the server
            // tick, which runs beside this one.
            int ticks = CometActionPatches.FuelTicks;
            bool ran = ticks == _lastFuelTicks + 1 && _rocket.RocketMode == CometModes.Fuel;
            _lastFuelTicks = ticks;
            if (!double.IsNaN(_lastMethane) && ran)
            {
                // The rule, independently: the pump's share of the store's volume a tick, of what pairs two to one.
                double share = Math.Min(1.0, Tuning.FuelLitresPerSecond * CelestialRules.FuelTickSeconds / store.Volume.ToDouble());
                double oxygenTaken = share * Math.Min(_lastMethane / 2.0, _lastOxygen);
                double tookMethane = _lastMethane - methane;
                double tookOxygen = _lastOxygen - oxygen;
                double gained = fuel - _lastCometFuel;
                bool pass = Math.Abs(tookOxygen - oxygenTaken) <= 1e-9 * _lastOxygen + 1e-9 && Math.Abs(tookMethane - 2.0 * oxygenTaken) <= 1e-9 * _lastMethane + 1e-9
                    && Math.Abs(gained - (tookMethane + tookOxygen)) <= 1e-9 * (_lastMethane + _lastOxygen) + 1e-9 && nitrogen == _lastNitrogen && view?.Id == _cometId;
                _fuelJudged++;
                _log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: actions fuel tick {0} | tick {1} | {2} | tank methane -{3:R} (rule {4:R}) oxygen -{5:R} (rule {6:R}) nitrogen {7:R} -> {8:R} | comet {9} fuel {10:R} (+{11:R}) | left in the tank: methane {12:R} oxygen {13:R}",
                    pass ? "PASS" : "FAIL", GameManager.GameTickCount, _load ? "after the load" : "before the save", tookMethane, 2.0 * oxygenTaken, tookOxygen, oxygenTaken,
                    _lastNitrogen, nitrogen, view?.Id ?? 0, fuel, gained, methane, oxygen));
                if (!pass)
                {
                    _sampling = false;
                    _log.LogInfo("LiveCheck: actions FAIL a Fuel tick did not move the rule's premix from the tank into the comet");
                }
            }
            _lastMethane = methane;
            _lastOxygen = oxygen;
            _lastNitrogen = nitrogen;
            _lastCometFuel = fuel;
        }

        private static double Get(Atmosphere store, Chemistry.GasType gas) => store.GasMixture.GetMoleValue(gas).Quantity.ToDouble();

        private void WatchFuelBeforeSave()
        {
            if (!_sampling)
            {
                Fail("a Fuel tick failed (see the line above)");
                return;
            }
            if (_fuelJudged < FuelTicksBeforeSave)
            {
                Overdue(FuelTicksBeforeSave + " Fuel ticks");
                return;
            }
            Info(string.Format(CultureInfo.InvariantCulture, "saving | at clock {0:R} | rocket {1} in mode {2} at {3} | comet {4} fuel {5:R}",
                Clock, _rocket.ReferenceId, (byte)_rocket.RocketMode, _rocket.CurrentNode?.DisplayName, _cometId, CometMap.WaitingView(_ice)?.FuelLeft ?? double.NaN));
            Util.Commands.CommandLine.Process("file save");
            Next(Stage.Saving);
        }

        // ---- phase load -------------------------------------------------------------------------------

        private void FindRocket()
        {
            if (!long.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETACTIONS_ROCKET"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                return;
            }
            _rocket = Referencable.Find<Rocket>(id);
            RocketNetwork network = _rocket?.RocketNetwork;
            if (network == null)
            {
                return;
            }
            _miner = network.Internals.OfType<RocketMiner>().FirstOrDefault();
            _tank = network.Internals.OfType<Tank>().FirstOrDefault();
            _bay = network.Internals.OfType<RocketPayloadBay>().FirstOrDefault();
        }

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
                    Loaded();
                    break;
                case Stage.LoadFuel:
                    if (!_sampling)
                    {
                        Fail("a Fuel tick after the load failed (see the line above)");
                    }
                    else if (_fuelJudged >= FuelTicksAfterLoad)
                    {
                        _sampling = false;
                        SetMode(RocketMode.None);
                        Next(Stage.Launch);
                    }
                    else
                    {
                        Overdue(FuelTicksAfterLoad + " Fuel ticks after the load");
                    }
                    break;
                case Stage.Launch:
                    WatchLaunch();
                    break;
            }
        }

        private void Loaded()
        {
            if (_rocket == null)
            {
                FindRocket();
            }
            if (_rocket == null || _tank == null || _miner == null || _bay == null)
            {
                if (Clock - _stageClock > 60.0)
                {
                    Fail("the load brought back no rocket " + Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETACTIONS_ROCKET") + " with its miner, tank and bay");
                }
                return;
            }
            if (_rocket.RocketMode != CometModes.Fuel)
            {
                if (Clock - _stageClock > 30.0)
                {
                    Fail("the rocket did not go back to Fuel after the load; it is in mode " + (byte)_rocket.RocketMode);
                }
                return;
            }
            CometView? view = _rocket.CurrentNode == null ? null : CometMap.WaitingView(_rocket.CurrentNode);
            bool here = view != null;
            Info(string.Format(CultureInfo.InvariantCulture, "loaded {0} | rocket {1} back in mode {2} at {3} | state {4} | the comet waiting there {5} | engines {6} | fuel {7:R} | internals {8}",
                here ? "PASS" : "FAIL", _rocket.ReferenceId, (byte)_rocket.RocketMode, _rocket.CurrentNode?.DisplayName, _rocket.RocketState,
                here ? view.Value.Id.ToString(CultureInfo.InvariantCulture) : "NONE", view?.Engines ?? 0, view?.FuelLeft ?? double.NaN, _rocket.RocketNetwork.Internals.Count));
            if (!here)
            {
                Fail("after the load the rocket does not stand where its comet waits");
                return;
            }
            _cometId = view.Value.Id;
            _lastMethane = double.NaN;
            _fuelJudged = 0;
            _sampling = true;
            Next(Stage.LoadFuel);
        }

        private void WatchLaunch()
        {
            if (double.IsNaN(_launchClock))
            {
                if (_rocket.RocketMode != RocketMode.None || _rocket.CurrentAction != null)
                {
                    return;
                }
                CometView comet = CometMap.WaitingView(_rocket.CurrentNode) ?? default;
                _route = CometActionsRoute(_rocket.CurrentNode);
                _launchFuel = comet.FuelLeft;
                _launchKg = comet.CometKg;
                _launchEngines = comet.Engines;
                _launchClock = Clock;
                Flight rule = CelestialRules.Shipped.Fly(_launchKg, _launchEngines, _launchFuel, _route, Premix());
                Info(string.Format(CultureInfo.InvariantCulture, "launching | comet {0} | kg {1:R} | engines {2} | fuel {3:R} | route {4:R} delta-v | the rule: {5} in {6:R} s on {7:R} mol",
                    comet.Id, _launchKg, _launchEngines, _launchFuel, _route, rule.Arrives ? "arrives" : "runs dry", rule.Seconds, rule.FuelMoles));
                SetMode(CometModes.Launch);
                return;
            }
            CometView view = CometMap.Views().FirstOrDefault(v => v.Id == _cometId);
            Flight flight = CelestialRules.Shipped.Fly(_launchKg, _launchEngines, _launchFuel, _route, Premix());
            if (view.State == MapCometState.Waiting)
            {
                if (Clock - _launchClock > 10.0)
                {
                    Fail("Launch did not send the comet; the rocket is in mode " + (byte)_rocket.RocketMode);
                }
                return;
            }
            if (view.State != MapCometState.Arrived)
            {
                if (Clock - _launchClock > flight.Seconds + 60.0)
                {
                    Fail(string.Format(CultureInfo.InvariantCulture, "comet {0} has not arrived {1:0.0} s after Launch; the rule says {2:0.0} s", _cometId, Clock - _launchClock, flight.Seconds));
                }
                return;
            }
            double flown = Clock - view.StartClock;
            bool atEntry = view.AtNode == SpaceMap.Current.EntryNode.ReferenceId;
            bool onTime = Math.Abs(flown - flight.Seconds) <= 0.5 + 0.001 * flight.Seconds;
            bool fuel = Math.Abs(view.FuelLeft - (_launchFuel - flight.FuelMoles)) <= 1e-6 * Math.Max(1.0, _launchFuel);
            bool idle = _rocket.RocketMode == RocketMode.None && _rocket.CurrentAction == null;
            bool pass = flight.Arrives && atEntry && onTime && fuel && idle && Math.Abs(view.StartClock - _launchClock) < 2.0;
            Info(string.Format(CultureInfo.InvariantCulture, "launch {0} | comet {1} sent at clock {2:R}, reached {3} after {4:0.000} s by the clock | rule {5:0.000} s | fuel left {6:R} (rule {7:R}) | rocket back to {8}",
                pass ? "PASS" : "FAIL", view.Id, view.StartClock, atEntry ? "Entry " + SpaceMap.Current.EntryNode.DisplayName : "a node that is NOT Entry", flown, flight.Seconds,
                view.FuelLeft, _launchFuel - flight.FuelMoles, idle ? "idle" : "mode " + (byte)_rocket.RocketMode));
            if (!pass)
            {
                Fail("Launch did not send the comet to arrive at Entry when the rule says");
                return;
            }
            Info("done");
            _stage = Stage.Done;
        }

        // ---- helpers ----------------------------------------------------------------------------------

        private static TerraformingReloaded.Comets.CelestialTuning Tuning => TerraformingReloaded.Effective.Celestial;

        private static double K()
        {
            double size = CometMap.PlanetSize();
            return Tuning.SizeMultiplierAt(size > 0.0 ? size : CelestialRules.StandardPlanetSize);
        }

        private static double Premix() => CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));

        private RocketMiningDrillHead Head() => _miner?.Slots.Select(s => s?.Get()).OfType<RocketMiningDrillHead>().FirstOrDefault();

        /// <summary>A mode set the way the game's mode message sets one on the host.</summary>
        private void SetMode(RocketMode mode)
        {
            _rocket.RocketMode = mode;
            Info(string.Format(CultureInfo.InvariantCulture, "mode | asked {0} | the rocket is in {1} at {2}", (byte)mode, (byte)_rocket.RocketMode, _rocket.CurrentNode?.DisplayName));
            if (_rocket.RocketMode != mode)
            {
                Fail("the game refused mode " + (byte)mode + " at " + _rocket.CurrentNode?.DisplayName + ": " + TerraformingReloaded.Comets.CometActions.Explain(TerraformingReloaded.Comets.CometActions.Refusal(_rocket.CurrentNode, mode)));
            }
        }

        private static double GasOf(CometView comet, Chemistry.GasType gas)
        {
            int i = Array.IndexOf(comet.Gases, gas);
            return i < 0 ? 0.0 : comet.GasMoles[i];
        }

        private static string Gases(CometView comet) =>
            string.Join(", ", comet.Gases.Select((g, i) => g + " " + comet.GasMoles[i].ToString("R", CultureInfo.InvariantCulture)));

        private string Diagnose()
        {
            if (_rocket == null)
            {
                return "";
            }
            RocketMiningDrillHead head = Head();
            return string.Format(CultureInfo.InvariantCulture, " (rocket mode {0}, action {1}, at {2}; miner on {3} powered {4} progress {5:R} holds {6}; head {7}; bay on {8} powered {9} open {10} progress {11:R} payload {12})",
                (byte)_rocket.RocketMode, _rocket.CurrentAction?.GetType().Name ?? "none", _rocket.CurrentNode?.DisplayName,
                _miner?.OnOff, _miner?.Powered, _miner?.MiningProgress ?? -1f, _miner?.QuantityMined ?? -1, head == null ? "none" : head.Quantity.ToString(CultureInfo.InvariantCulture),
                _bay?.OnOff, _bay?.Powered, _bay?.IsOpen, _bay?.DeployProgress ?? -1f, _bay?.Payload != null);
        }

        /// <summary>The game's route from a site to Entry, each hop's delta-v as the game works it out.</summary>
        private static double CometActionsRoute(SpaceMapNode site)
        {
            if (!SpaceMapPathFinder.GetPath(site, SpaceMap.Current.EntryNode, out List<NodeTransit> path) || path == null)
            {
                return 0.0;
            }
            return path.Sum(t => (double)t.GetDistance());
        }

        private static double RouteDeltaV(SpaceMapNode node) => node == SpaceMap.Current.EntryNode ? 0.0 : CometActionsRoute(node);
    }

    /// <summary>
    /// -CometActions, phase "vanilla": the save phase make wrote, loaded by the game without the mod. Only the
    /// game's own types, so this loads with the mod gone. The rocket must be there, idle (its comet action
    /// was saved as None), at a node, with its parts.
    /// </summary>
    internal sealed class CometActionsVanilla
    {
        private readonly ManualLogSource _log;
        private bool _done;

        private CometActionsVanilla(ManualLogSource log)
        {
            _log = log;
        }

        public static CometActionsVanilla FromEnvironment(ManualLogSource log) =>
            Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETACTIONS") == "vanilla" ? new CometActionsVanilla(log) : null;

        public void Step()
        {
            if (_done || GameManager.GameTickCount < 5)
            {
                return;
            }
            _done = true;
            long.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETACTIONS_ROCKET"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id);
            Rocket rocket = Referencable.Find<Rocket>(id);
            bool pass = rocket != null && rocket.RocketMode == RocketMode.None && rocket.CurrentNode != null && rocket.RocketState == RocketState.InSpace
                && rocket.RocketNetwork != null && rocket.RocketNetwork.Internals.Count >= 3;
            _log.LogInfo(string.Format(CultureInfo.InvariantCulture, "LiveCheck: actions vanilla {0} | rockets {1} | rocket {2} {3} | mode {4} | state {5} | at {6} | parts {7} | tick {8}",
                pass ? "PASS" : "FAIL", Rocket.AllRockets.Count, id, rocket != null ? "loaded" : "MISSING", rocket != null ? (byte)rocket.RocketMode : -1,
                rocket?.RocketState, rocket?.CurrentNode?.DisplayName ?? "none", rocket?.RocketNetwork?.Internals.Count ?? 0, GameManager.GameTickCount));
        }
    }
}
