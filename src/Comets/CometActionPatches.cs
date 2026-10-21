using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.UI;
using HarmonyLib;
using Networks;
using Objects.Items;
using Objects.Rockets;
using Objects.Rockets.Mining;
using Objects.Rockets.Scanning;
using Objects.Rockets.UI;
using TerraformingReloaded.Patching;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Where the comet actions meet the game (docs/COMET-ROCKETS.md, stage 4): the modes a site offers,
    /// the action each mode runs, the miner's cycle, the payload's deploy, the rocket's atmospherics
    /// step, the panel's progress lines and its buttons, and the save, which must never see a mode the
    /// game does not define. Two parts in Patcher: the actions, and the buttons on the map screen.
    /// </summary>
    public static class CometActionPatches
    {
        private static int _faults;

        /// <summary>
        /// The actions. Refused whole unless every place is there, and offered only once every patch, the
        /// save's own guard included, is in: so no comet mode can ever reach a save.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            ConstructorInfo saveData = AccessTools.Constructor(typeof(RocketSaveData), new[] { typeof(Rocket) });
            MethodInfo hasAction = AccessTools.DeclaredMethod(typeof(SpaceMapNode), nameof(SpaceMapNode.HasAction), new[] { typeof(RocketMode) });
            MethodInfo available = AccessTools.DeclaredMethod(typeof(SpaceMapNode), nameof(SpaceMapNode.IsActionAvailable), new[] { typeof(RocketMode) });
            MethodInfo getAction = AccessTools.DeclaredMethod(typeof(SpaceMapNode), nameof(SpaceMapNode.GetAction), new[] { typeof(RocketMode) });
            MethodInfo mine = AccessTools.DeclaredMethod(typeof(MineableDeposit), nameof(MineableDeposit.MineDeposit), new[] { typeof(Thing).MakeByRefType(), typeof(RocketMiningDrillHead) });
            MethodInfo deploy = AccessTools.DeclaredMethod(typeof(DeliveryPayload), nameof(DeliveryPayload.OnDeploy));
            MethodInfo atmospherics = AccessTools.DeclaredMethod(typeof(Rocket), nameof(Rocket.RocketAtmospherics));
            MethodInfo progressables = AccessTools.DeclaredMethod(typeof(Rocket), nameof(Rocket.ActionProgressables));
            if (saveData == null || hasAction == null || available == null || getAction == null || mine == null || deploy == null || atmospherics == null || progressables == null)
            {
                throw new MissingMethodException("a rocket action, the miner's cycle, the payload's deploy, the rocket's atmospherics step or a rocket's save is gone");
            }
            // The cycle and the deploy are reached the way the patches expect: the miner's own tick calls
            // the deposit's mining, and the bay's own tick calls its payload's deploy.
            MethodInfo minerTick = AccessTools.DeclaredMethod(typeof(RocketMiner), nameof(RocketMiner.OnServerTick), new[] { typeof(float) });
            MethodInfo bayTick = AccessTools.DeclaredMethod(typeof(RocketPayloadBay), nameof(RocketPayloadBay.OnServerTick), new[] { typeof(float) });
            MethodInfo payloadDeploy = AccessTools.DeclaredMethod(typeof(IRocketPayload), nameof(IRocketPayload.OnDeploy));
            if (minerTick == null || bayTick == null || !SelfTest.StillCalls(minerTick, new[] { mine }, out _) || !SelfTest.StillCalls(bayTick, new[] { payloadDeploy }, out _))
            {
                throw new InvalidOperationException("the miner no longer mines through the deposit, or the payload bay no longer deploys its payload, where the comet actions expect");
            }
            harmony.Patch(hasAction, postfix: Body(nameof(HasActionPostfix)));
            harmony.Patch(available, prefix: Body(nameof(IsActionAvailablePrefix)));
            harmony.Patch(getAction, prefix: Body(nameof(GetActionPrefix)));
            harmony.Patch(mine, prefix: Body(nameof(MineDepositPrefix)));
            harmony.Patch(deploy, prefix: Body(nameof(OnDeployPrefix)));
            harmony.Patch(atmospherics, postfix: Body(nameof(RocketAtmosphericsPostfix)));
            harmony.Patch(progressables, postfix: Body(nameof(ActionProgressablesPostfix)));
            // Last, as it reaches Unity's own code: tools/PatchCheck binds it but cannot compile it, and the
            // seven before it are then still compiled there. Every one of them asks CometActions.Installed,
            // so until this is in too no site offers a comet action and no comet mode can reach a save.
            harmony.Patch(saveData, postfix: Body(nameof(RocketSaveDataPostfix)));
            CometActions.MarkInstalled();
        }

        /// <summary>The buttons for the five comet actions on the rocket map screen, and the panel's name for the action running.</summary>
        public static void ApplyButtons(Harmony harmony)
        {
            MethodInfo initialize = AccessTools.DeclaredMethod(typeof(LocationActionsPanel), nameof(LocationActionsPanel.Initialize));
            MethodInfo refresh = AccessTools.DeclaredMethod(typeof(LocationActionsPanel), nameof(LocationActionsPanel.Refresh));
            MethodInfo modeString = AccessTools.DeclaredMethod(typeof(LocationProgressPanel), "GetModeString");
            if (initialize == null || refresh == null || modeString == null)
            {
                throw new MissingMethodException("the rocket map's actions panel no longer starts, refreshes or names its action where the buttons expect");
            }
            _mineButton = AccessTools.FieldRefAccess<LocationActionsPanel, RocketActionButton>("_mineButton");
            _actionsLocation = AccessTools.FieldRefAccess<LocationActionsPanel, LocationPanel>("_locationPanel");
            _progressLocation = AccessTools.FieldRefAccess<LocationProgressPanel, LocationPanel>("_locationPanel");
            harmony.Patch(initialize, postfix: Body(nameof(PanelInitializePostfix)));
            harmony.Patch(refresh, postfix: Body(nameof(PanelRefreshPostfix)));
            harmony.Patch(modeString, postfix: Body(nameof(ModeStringPostfix)));
        }

        private static HarmonyMethod Body(string name) =>
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometActionPatches), name) ?? throw new MissingMethodException(nameof(CometActionPatches), name));

        private static void Fault(string message)
        {
            if (++_faults <= 5)
            {
                Log.Warn("[TR comet actions] " + message);
            }
        }

        // ---- the save: never a mode the game does not define ------------------------------------------------

        /// <summary>
        /// RocketSaveData(Rocket): a mode the game does not define (a comet action) is written as None, so the
        /// save never throws and the unmodded game loads it; the comets entry keeps the real one.
        /// </summary>
        private static void RocketSaveDataPostfix(RocketSaveData __instance)
        {
            if (__instance != null && !Enum.IsDefined(typeof(RocketMode), __instance.RocketMode))
            {
                __instance.RocketMode = RocketMode.None;
            }
        }

        // ---- what a site offers -----------------------------------------------------------------------------

        private static void HasActionPostfix(SpaceMapNode __instance, RocketMode mode, ref bool __result)
        {
            try
            {
                if (CometModes.IsComet(mode))
                {
                    __result = CometActions.Refusal(__instance, mode) == CometRefusal.None;
                }
                else if (mode == RocketMode.Deploy && !__result)
                {
                    // Rig: Deploy exists at no site in the game, so here it can only be the comet's.
                    __result = CometActions.Refusal(__instance, RocketMode.Deploy) == CometRefusal.None;
                }
            }
            catch (Exception e)
            {
                Fault("Whether a site offers a comet action could not be worked out: " + e.Message);
            }
        }

        /// <summary>The game's own question throws on a mode it does not know, so a comet mode is answered here first.</summary>
        private static bool IsActionAvailablePrefix(SpaceMapNode __instance, RocketMode actionMode, ref bool __result)
        {
            if (!CometModes.IsComet(actionMode))
            {
                return true;
            }
            try
            {
                __result = CometActions.Refusal(__instance, actionMode) == CometRefusal.None;
            }
            catch (Exception e)
            {
                __result = false;
                Fault("Whether a comet action is available could not be worked out: " + e.Message);
            }
            return false;
        }

        /// <summary>
        /// The action each comet mode runs, and Rig's Deploy at a site, where the game has no deploy data to
        /// make one from. Everything else is the game's own.
        /// </summary>
        private static bool GetActionPrefix(SpaceMapNode __instance, RocketMode mode, ref RocketAction __result)
        {
            try
            {
                if (mode == RocketMode.Deploy)
                {
                    if (__instance.DeployData != null || CometActions.Refusal(__instance, mode) != CometRefusal.None)
                    {
                        return true;
                    }
                    __result = new RocketDeploy(null, __instance);
                    return false;
                }
                if (!CometModes.IsComet(mode))
                {
                    return true;
                }
                __result = null;
                if (CometActions.Refusal(__instance, mode) == CometRefusal.None)
                {
                    switch (mode)
                    {
                        case CometModes.Cut:
                            __result = new CutAction(__instance);
                            break;
                        case CometModes.Burn:
                            __result = new BurnAction(__instance);
                            break;
                        case CometModes.Fuel:
                            __result = new FuelAction(__instance);
                            break;
                        case CometModes.Launch:
                            __result = new LaunchAction(__instance);
                            break;
                        case CometModes.Deorbit:
                            __result = new DeorbitAction(__instance);
                            break;
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                Fault("A comet action could not be started: " + e.Message);
                __result = null;
                return !CometModes.IsComet(mode);
            }
        }

        // ---- Cut and Burn: the miner's own cycle ---------------------------------------------------------------

        /// <summary>
        /// MineableDeposit.MineDeposit, from the miner's own tick, when its rocket runs Cut or Burn. Cut
        /// depletes the site as the game does (OnDepositMined, first, as the game calls it) and grows the
        /// site's comet by the rule; Burn leaves the site alone and turns the comet's methane and oxygen into
        /// fuel. Either way no ice or ore is made: 0 units, and the miner's own last prefab handed back, so a
        /// stack it was part way through is kept. The miner then wears its head as the game does.
        /// </summary>
        private static bool MineDepositPrefix(MineableDeposit __instance, ref Thing prefab, RocketMiningDrillHead drillHead, ref int __result)
        {
            RocketMiner miner = drillHead?.ParentSlot?.Parent as RocketMiner;
            RocketAction action = miner?.Rocket?.CurrentAction;
            bool cut = action is CutAction;
            if (NetworkManager.IsClient || (!cut && !(action is BurnAction)))
            {
                return true;
            }
            prefab = miner.LastMinedPrefab;
            __result = 0;
            try
            {
                SpaceMapNode site = __instance.Parent;
                if (cut)
                {
                    __instance.OnDepositMined();
                    string refused = CometMap.CutCycle(site, HeadYield(__instance, drillHead), out _);
                    if (refused != null)
                    {
                        Fault("A Cut cycle at " + site?.DisplayName + " cut nothing: " + refused + ".");
                    }
                }
                else
                {
                    string refused = CometMap.BurnCycle(site, drillHead.IceYieldMultiplier, out _);
                    if (refused != null)
                    {
                        Fault("A Burn cycle at " + site?.DisplayName + " burnt nothing: " + refused + ".");
                    }
                }
            }
            catch (Exception e)
            {
                Fault("A comet drill cycle failed; it made nothing: " + e.Message);
            }
            return false;
        }

        /// <summary>The head's yield the game's mining would use at this deposit: ice for ice, reagent for a reagent mix, none for plain ore.</summary>
        internal static double HeadYield(MineableDeposit deposit, RocketMiningDrillHead head)
        {
            if ((deposit.DepositType & (MineableDepositType.Ore | MineableDepositType.ReagentMix)) == 0)
            {
                return head.IceYieldMultiplier;
            }
            return deposit.DepositComposition.ReagentMixes.Count > 0 ? head.ReagentYieldMultiplier : 1.0;
        }

        // ---- Rig: the payload's deploy at the comet's site -----------------------------------------------------

        /// <summary>
        /// DeliveryPayload.OnDeploy at a site with a comet waiting (the game deploys a container only at the
        /// planet's orbit, and at a site does nothing): every Pumped Gas engine kit in the container becomes
        /// an engine on the comet, as many as the kits would build, and the kits are used up. The container
        /// stays in the bay.
        /// </summary>
        private static bool OnDeployPrefix(DeliveryPayload __instance)
        {
            if (NetworkManager.IsClient || !CometActions.Installed)
            {
                return true;
            }
            try
            {
                RocketPayloadBay bay = __instance.ParentSlot?.Parent as RocketPayloadBay;
                Rocket rocket = bay?.RocketNetwork?.Rocket;
                SpaceMapNode site = rocket?.CurrentNode;
                if (site == null || site.NodeType != NodeType.Generated)
                {
                    return true;
                }
                MapComet comet = CometMap.WaitingAt(site);
                if (comet == null)
                {
                    return true;
                }
                Rig(__instance, comet, site, rocket);
            }
            catch (Exception e)
            {
                Fault("Engines could not be delivered to a comet: " + e.Message);
            }
            return false;
        }

        private static void Rig(DeliveryPayload container, MapComet comet, SpaceMapNode site, Rocket rocket)
        {
            int room = CometMap.MaxEngines - comet.Engines;
            int engines = 0;
            int kitsUsed = 0;
            foreach (Slot slot in container.Slots)
            {
                // A kit already on its way out (destroyed this frame, still in its slot until Unity removes it) counts for nothing.
                if (!(slot?.Get() is MultiConstructor kit) || kit.BeingDestroyed || room - engines <= 0)
                {
                    continue;
                }
                int perEngine = KitsPerEngine(kit);
                if (perEngine <= 0)
                {
                    continue;
                }
                int built = Math.Min(kit.Quantity / perEngine, room - engines);
                if (built <= 0)
                {
                    continue;
                }
                int used = built * perEngine;
                engines += built;
                kitsUsed += used;
                if (used >= kit.Quantity)
                {
                    UseUp(kit);
                }
                else
                {
                    kit.Quantity -= used;
                }
            }
            if (engines == 0)
            {
                Log.Info("[TR comet actions] rocket " + rocket.ReferenceId + " deployed at " + site.DisplayName + " with no Pumped Gas engine kit in its container: comet " + comet.Id + " keeps " + comet.Engines + " engines.");
                return;
            }
            string refused = CometMap.AddEngines(comet.Id, engines, out MapComet rigged);
            Log.Info(refused == null
                ? string.Format(CultureInfo.InvariantCulture, "[TR comet actions] Rig at {0}: rocket {1} delivered {2} engine kits, comet {3} now has {4} engines ({5:R} kg with its engines)",
                    site.DisplayName, rocket.ReferenceId, kitsUsed, rigged.Id, rigged.Engines, rigged.CometKg + CelestialRules.EngineMassKg * rigged.Engines)
                : "[TR comet actions] Rig at " + site.DisplayName + " used kits but added no engines: " + refused);
        }

        /// <summary>
        /// A kit used up whole: out of its slot now, then destroyed. OnServer.Destroy alone leaves it in the
        /// slot until the end of the frame, because Unity defers a GameObject's destruction and only then does
        /// DynamicThing.OnDestroy empty the slot; this does the same emptying first, the way OnDestroy does it,
        /// so nothing that looks at the container in the meantime finds the kit there.
        /// </summary>
        private static void UseUp(MultiConstructor kit)
        {
            Slot slot = kit.ParentSlot;
            Thing parent = slot?.Parent;
            if (slot != null)
            {
                slot.Empty();
                kit.ParentSlot = null;
                if (parent != null)
                {
                    parent.OnChildExitInventory(kit);
                }
            }
            OnServer.Destroy(kit);
        }

        /// <summary>Kits of this stack one Pumped Gas engine takes to build, or 0 when the kit builds none.</summary>
        internal static int KitsPerEngine(MultiConstructor kit)
        {
            foreach (Structure structure in kit.Constructables)
            {
                if (structure is GovernedGasEngine)
                {
                    int entry = structure.BuildStates != null && structure.BuildStates.Count > 0 ? structure.BuildStates[0].Tool.EntryQuantity : 1;
                    return Math.Max(1, entry);
                }
            }
            return 0;
        }

        // ---- Fuel: the rocket's atmospherics step ---------------------------------------------------------------

        /// <summary>Fuel ticks run, for tools/LiveCheck: each rocket's count, and the moles of the last one.</summary>
        public static int FuelTicks;
        public static double LastFuelMoles;

        /// <summary>
        /// After Rocket.RocketAtmospherics, on the game tick and before the engines draw (ThingPreAtmosphere):
        /// every rocket running Fuel at a comet's site moves premix from its tanks and pipe networks, never an
        /// engine's chamber, into the comet by the rule, for one atmospherics tick.
        /// </summary>
        private static void RocketAtmosphericsPostfix()
        {
            if (!CometActions.Installed || NetworkManager.IsClient)
            {
                return;
            }
            try
            {
                for (int i = Rocket.AllRockets.Count - 1; i >= 0; i--)
                {
                    Rocket rocket = Rocket.AllRockets[i];
                    if (!(rocket?.CurrentAction is FuelAction) || rocket.RocketState != RocketState.InSpace || rocket.Progress != 0f)
                    {
                        continue;
                    }
                    MapComet comet = CometMap.WaitingAt(rocket.CurrentNode);
                    if (comet != null)
                    {
                        FuelTick(rocket, comet);
                    }
                }
            }
            catch (Exception e)
            {
                Fault("A Fuel tick failed: " + e.Message);
            }
        }

        /// <summary>The rocket's fuel stores: each tank's own atmosphere and each pipe network's, once each.</summary>
        public static List<Atmosphere> FuelStores(RocketNetwork network)
        {
            List<Atmosphere> stores = new List<Atmosphere>();
            if (network == null)
            {
                return stores;
            }
            foreach (IRocketInternals part in network.Internals)
            {
                Atmosphere store = part is Tank tank ? tank.InternalAtmosphere : part is Pipe pipe ? pipe.PipeNetwork?.Atmosphere : null;
                if (store != null && !stores.Contains(store))
                {
                    stores.Add(store);
                }
            }
            return stores;
        }

        /// <summary>Methane and oxygen in a store, gas and liquid together, as an engine burns both.</summary>
        public static void Premix(Atmosphere store, out double methane, out double oxygen)
        {
            methane = Moles(store, Chemistry.GasType.Methane) + Moles(store, Chemistry.GasType.LiquidMethane);
            oxygen = Moles(store, Chemistry.GasType.Oxygen) + Moles(store, Chemistry.GasType.LiquidOxygen);
        }

        private static double Moles(Atmosphere store, Chemistry.GasType gas) => store.GasMixture.GetMoleValue(gas).Quantity.ToDouble();

        private static void FuelTick(Rocket rocket, MapComet comet)
        {
            List<Atmosphere> stores = FuelStores(rocket.RocketNetwork);
            double litres = 0.0;
            foreach (Atmosphere store in stores)
            {
                litres += store.Volume.ToDouble();
            }
            double rate = Effective.Celestial.FuelLitresPerSecond;
            double moved = 0.0;
            foreach (Atmosphere store in stores)
            {
                Premix(store, out double methane, out double oxygen);
                OwnFuel take = CelestialRules.Shipped.PremixTransfer(methane, oxygen, litres, rate, CelestialRules.FuelTickSeconds);
                if (comet.Fuel + moved + take.Methane + take.Oxygen > CometMap.MaxFuel)
                {
                    break;
                }
                moved += Take(store, Chemistry.GasType.Methane, Chemistry.GasType.LiquidMethane, take.Methane, methane)
                    + Take(store, Chemistry.GasType.Oxygen, Chemistry.GasType.LiquidOxygen, take.Oxygen, oxygen);
            }
            FuelTicks++;
            LastFuelMoles = moved;
            if (moved > 0.0 && !CometMap.AddFuel(comet.Id, moved))
            {
                Fault("Premix taken for comet " + comet.Id + " found it gone; " + moved.ToString("R", CultureInfo.InvariantCulture) + " mol were lost.");
            }
        }

        /// <summary>Takes this many moles of one fuel from a store, from its gas and its liquid in their shares; what came out.</summary>
        private static double Take(Atmosphere store, Chemistry.GasType gas, Chemistry.GasType liquid, double amount, double total)
        {
            if (!(amount > 0.0) || !(total > 0.0))
            {
                return 0.0;
            }
            double ofGas = amount * Moles(store, gas) / total;
            double ofLiquid = amount - ofGas;
            double taken = 0.0;
            if (ofGas > 0.0)
            {
                taken += store.Remove(new MoleQuantity(ofGas), gas).GetMoleValue(gas).Quantity.ToDouble();
            }
            if (ofLiquid > 0.0)
            {
                taken += store.Remove(new MoleQuantity(ofLiquid), liquid).GetMoleValue(liquid).Quantity.ToDouble();
            }
            return taken;
        }

        // ---- the location panel's progress lines ------------------------------------------------------------------

        /// <summary>
        /// Rocket.ActionProgressables, on every machine: Cut and Burn show the miners as Mine does, and the
        /// comet's line under them; Fuel and Launch the comet's line; Rig, the game's Deploy, adds it below the
        /// bays.
        /// </summary>
        private static void ActionProgressablesPostfix(Rocket __instance, List<IRocketActionProgressable> __result)
        {
            try
            {
                RocketMode mode = __instance.RocketMode;
                SpaceMapNode site = __instance.CurrentNode;
                // Deorbit fires once and has no comet waiting at a site to show a line for.
                if (__result == null || site == null || mode == CometModes.Deorbit || (!CometModes.IsComet(mode) && mode != RocketMode.Deploy))
                {
                    return;
                }
                if (mode == RocketMode.Deploy && (site.NodeType != NodeType.Generated || CometMap.WaitingAt(site) == null))
                {
                    return;
                }
                if (mode == CometModes.Cut || mode == CometModes.Burn)
                {
                    foreach (IRocketMiner miner in __instance.GetMiners() ?? new List<IRocketMiner>())
                    {
                        __result.Add(miner);
                    }
                }
                __result.Add(CometProgress.For(site, mode));
            }
            catch (Exception e)
            {
                Fault("The comet's progress could not be listed: " + e.Message);
            }
        }

        // ---- the buttons ----------------------------------------------------------------------------------------

        private static AccessTools.FieldRef<LocationActionsPanel, RocketActionButton> _mineButton;
        private static AccessTools.FieldRef<LocationActionsPanel, LocationPanel> _actionsLocation;
        private static AccessTools.FieldRef<LocationProgressPanel, LocationPanel> _progressLocation;
        private static readonly Dictionary<LocationActionsPanel, RocketActionButton[]> Buttons = new Dictionary<LocationActionsPanel, RocketActionButton[]>();

        private static readonly string[] ButtonNames = { "Cut", "Fuel", "Burn", "Launch", "Deorbit" };

        /// <summary>The panel's own Mine button, copied once for each comet action, beside it.</summary>
        private static void PanelInitializePostfix(LocationActionsPanel __instance)
        {
            try
            {
                if (Buttons.ContainsKey(__instance))
                {
                    return;
                }
                RocketActionButton mine = _mineButton(__instance);
                if (mine == null)
                {
                    return;
                }
                RocketActionButton[] made = new RocketActionButton[CometModes.All.Length];
                for (int i = 0; i < made.Length; i++)
                {
                    GameObject copy = UnityEngine.Object.Instantiate(mine.gameObject, mine.transform.parent);
                    copy.name = "TR comet " + ButtonNames[i];
                    RocketActionButton button = copy.GetComponent<RocketActionButton>();
                    foreach (TMPro.TMP_Text label in copy.GetComponentsInChildren<TMPro.TMP_Text>(true))
                    {
                        label.text = ButtonNames[i];
                    }
                    RocketMode mode = CometModes.All[i];
                    LocationActionsPanel panel = __instance;
                    button.Button.OnClick = () => _actionsLocation(panel)?.Motherboard?.SelectedRocket()?.Avionics?.SetRocketMode(mode);
                    made[i] = button;
                }
                Buttons[__instance] = made;
            }
            catch (Exception e)
            {
                Fault("The comet action buttons could not be added to the map screen: " + e.Message);
            }
        }

        private static void PanelRefreshPostfix(LocationActionsPanel __instance)
        {
            try
            {
                if (!Buttons.TryGetValue(__instance, out RocketActionButton[] made))
                {
                    return;
                }
                RocketAvionicsDevice avionics = _actionsLocation(__instance)?.Motherboard?.SelectedRocket()?.Avionics;
                Rocket rocket = avionics != null ? avionics.Rocket : null;
                SpaceMapNode node = avionics != null ? avionics.GetCurrentNode() : null;
                bool still = rocket != null && node != null && rocket.Progress == 0f && rocket.RocketState == RocketState.InSpace;
                for (int i = 0; i < made.Length; i++)
                {
                    if (made[i] == null)
                    {
                        continue;
                    }
                    made[i].Button.SetInteractable(still && node.IsActionAvailable(CometModes.All[i]));
                    made[i].ShowSelectionOutline(rocket != null && rocket.RocketMode == CometModes.All[i]);
                }
            }
            catch (Exception e)
            {
                Fault("The comet action buttons could not be refreshed: " + e.Message);
            }
        }

        private static void ModeStringPostfix(LocationProgressPanel __instance, ref string __result)
        {
            try
            {
                RocketAvionicsDevice avionics = _progressLocation(__instance)?.Motherboard?.SelectedRocket()?.Avionics;
                string doing = avionics != null && avionics.Rocket != null ? CometModes.Doing(avionics.Rocket.RocketMode) : null;
                if (doing != null)
                {
                    __result = doing;
                }
            }
            catch (Exception e)
            {
                Fault("The comet action's name could not be shown: " + e.Message);
            }
        }
    }
}
