using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Localization2;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Serialization;
using Objects.Rockets;
using Objects.Rockets.Scanning;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The rocket modes the comet actions add (docs/COMET-ROCKETS.md, stage 4). Byte values the game's
    /// RocketMode does not use, so the network and a logic write carry them as they carry the game's own;
    /// only the save cannot, and is kept from ever seeing one (CometActionPatches.RocketSaveDataPostfix).
    /// Rig is the game's own Deploy, at the comet's site.
    /// </summary>
    public static class CometModes
    {
        public const RocketMode Cut = (RocketMode)9;
        public const RocketMode Fuel = (RocketMode)10;
        public const RocketMode Burn = (RocketMode)11;
        public const RocketMode Launch = (RocketMode)12;

        public static readonly RocketMode[] All = { Cut, Fuel, Burn, Launch };

        public static bool IsComet(RocketMode mode) => mode >= Cut && mode <= Launch;

        /// <summary>The name a save keeps the mode by, or null for one that is not a comet action.</summary>
        public static string Name(RocketMode mode)
        {
            switch (mode)
            {
                case Cut:
                    return "Cut";
                case Fuel:
                    return "Fuel";
                case Burn:
                    return "Burn";
                case Launch:
                    return "Launch";
                default:
                    return null;
            }
        }

        public static bool TryParse(string name, out RocketMode mode)
        {
            foreach (RocketMode one in All)
            {
                if (string.Equals(Name(one), name, StringComparison.Ordinal))
                {
                    mode = one;
                    return true;
                }
            }
            mode = RocketMode.None;
            return false;
        }

        /// <summary>What the location panel calls the action a rocket is running.</summary>
        public static string Doing(RocketMode mode)
        {
            switch (mode)
            {
                case Cut:
                    return "Cutting a comet";
                case Fuel:
                    return "Fuelling the comet";
                case Burn:
                    return "Burning the comet's ice into fuel";
                case Launch:
                    return "Launching the comet";
                default:
                    return null;
            }
        }
    }

    /// <summary>Why a comet action cannot run at a node, as the game reports an action's refusal.</summary>
    public enum CometRefusal
    {
        None,
        Off,
        NotASite,
        NoIceOrOre,
        Depleted,
        WrongKind,
        Full,
        NoComet,
        NothingToBurn,
        NoEngines,
        NoFuel,
        NotSent,
    }

    /// <summary>
    /// What decides whether a comet action can run, shared by the game's questions (HasAction,
    /// IsActionAvailable), the actions themselves and the panel: one answer everywhere. Runs on the host
    /// and on every client, each from its own copy of the comets, which the host sends.
    /// </summary>
    public static class CometActions
    {
        private static volatile bool _installed;
        private static readonly Dictionary<CometRefusal, GameString> Texts = new Dictionary<CometRefusal, GameString>();

        /// <summary>The comet actions are patched in (Patcher's "comet rocket actions").</summary>
        public static bool Installed => _installed;

        internal static void MarkInstalled() => _installed = true;

        /// <summary>Why <paramref name="mode"/> cannot run at <paramref name="node"/>, or None when it can.</summary>
        public static CometRefusal Refusal(SpaceMapNode node, RocketMode mode)
        {
            if (!_installed || CelestialRules.Verdict != null || (!NetworkManager.IsClient && !CometMap.SaveInstalled))
            {
                return CometRefusal.Off;
            }
            if (node == null || node.NodeType != NodeType.Generated)
            {
                return CometRefusal.NotASite;
            }
            MapComet comet = CometMap.WaitingAt(node);
            switch (mode)
            {
                case CometModes.Cut:
                    return CutRefusal(node, comet);
                case CometModes.Fuel:
                case RocketMode.Deploy:
                    return comet == null ? CometRefusal.NoComet : CometRefusal.None;
                case CometModes.Burn:
                    if (comet == null)
                    {
                        return CometRefusal.NoComet;
                    }
                    return node.Deposit == null || comet.Kind != CometKind.Ice || !(comet.MolesOf(Chemistry.GasType.Methane) > 0.0) || !(comet.MolesOf(Chemistry.GasType.Oxygen) > 0.0)
                        ? CometRefusal.NothingToBurn
                        : CometRefusal.None;
                case CometModes.Launch:
                    if (comet == null)
                    {
                        return CometRefusal.NoComet;
                    }
                    return comet.Engines <= 0 ? CometRefusal.NoEngines : !(comet.Fuel > 0.0) ? CometRefusal.NoFuel : CometRefusal.None;
                default:
                    return CometRefusal.Off;
            }
        }

        private static CometRefusal CutRefusal(SpaceMapNode node, MapComet comet)
        {
            if (!CometMap.SiteStock(node, out CometKind kind, out _, out double[] perUnit, out double units))
            {
                return CometRefusal.NoIceOrOre;
            }
            if (node.Deposit.IsDepleted)
            {
                return CometRefusal.Depleted;
            }
            if (comet == null)
            {
                return CometRefusal.None;
            }
            if (comet.Kind != kind)
            {
                return CometRefusal.WrongKind;
            }
            CelestialTuning tuning = Effective.Celestial;
            double has = kind == CometKind.Ice ? comet.TotalMoles : comet.OreUnits;
            double room = CelestialRules.Shipped.CutAdds(CometMap.SiteAmount(kind, perUnit, units), has, tuning.CutSharePerCycle, CometMap.SizeMultiplierNow(tuning), 1.0);
            return room > 0.0 ? CometRefusal.None : CometRefusal.Full;
        }

        /// <summary>The game's own way to say an action was refused, so the rocket's log shows why.</summary>
        internal static RocketActionResult Failure(CometRefusal why)
        {
            GameString text;
            lock (Texts)
            {
                if (!Texts.TryGetValue(why, out text))
                {
                    text = GameString.Create("TRComet" + why, Explain(why));
                    Texts[why] = text;
                }
            }
            return RocketActionResult.Failure(text);
        }

        public static string Explain(CometRefusal why)
        {
            switch (why)
            {
                case CometRefusal.Off:
                    return "Comet actions are off: see the Terraforming Reloaded log";
                case CometRefusal.NotASite:
                    return "Comets are made at sites on the space map";
                case CometRefusal.NoIceOrOre:
                    return "This site has no ice or ore to cut a comet from";
                case CometRefusal.Depleted:
                    return "This site is mined out";
                case CometRefusal.WrongKind:
                    return "The comet here is not of this site's kind";
                case CometRefusal.Full:
                    return "The comet holds all this site can give";
                case CometRefusal.NoComet:
                    return "There is no comet waiting here";
                case CometRefusal.NothingToBurn:
                    return "The comet has no methane and oxygen to burn";
                case CometRefusal.NoEngines:
                    return "The comet has no engines: deliver engine kits with Deploy first";
                case CometRefusal.NoFuel:
                    return "The comet has no fuel: Fuel it first";
                case CometRefusal.NotSent:
                    return "The comet could not be sent: see the Terraforming Reloaded log";
                default:
                    return "Comet action refused";
            }
        }

        /// <summary>
        /// The route's delta-v from a site to Entry as the game's pathfinder gives it, or 0 when there is
        /// none. Main thread only: the pathfinder keeps static state.
        /// </summary>
        internal static double RouteDeltaV(SpaceMapNode site)
        {
            SpaceMapNode entry = SpaceMap.Current?.EntryNode;
            if (site == null || entry == null || !SpaceMapPathFinder.GetPath(site, entry, out List<NodeTransit> path) || path == null)
            {
                return 0.0;
            }
            double total = 0.0;
            foreach (NodeTransit transit in path)
            {
                total += transit?.GetDistance() ?? 0f;
            }
            return total;
        }

        /// <summary>
        /// Host, the rocket's server tick (Launch): sends the site's comet to Entry. Null on success, else why
        /// not. The game's own route and the rule's time are logged by CometMap.Send.
        /// </summary>
        internal static string Launch(Rocket rocket, SpaceMapNode site)
        {
            MapComet comet = CometMap.WaitingAt(site);
            if (comet == null)
            {
                return "there is no comet waiting here";
            }
            string refused = CometMap.Send(comet.Id, out MapComet sent);
            if (refused != null)
            {
                return refused;
            }
            Flight flight = sent.WholeFlight;
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "[TR comet actions] rocket {0} launched comet {1} from {2}: {3}",
                rocket?.ReferenceId ?? 0L, sent.Id, site.DisplayName,
                flight.Arrives
                    ? string.Format(CultureInfo.InvariantCulture, "the rule has it reach orbit in {0:R} s on {1:R} of its {2:R} mol of fuel", flight.Seconds, flight.FuelMoles, sent.Fuel)
                    : string.Format(CultureInfo.InvariantCulture, "its fuel does not cover the route: the rule has it run dry {0:R} of the way, after {1:R} s", flight.Share, flight.Seconds)));
            return null;
        }
    }

    /// <summary>Cut: the game's mining, whose cycles grow the site's comet instead of making ice (CometActionPatches.MineDepositPrefix).</summary>
    internal sealed class CutAction : RocketMine
    {
        public CutAction(SpaceMapNode node)
            : base(node.MineData, node)
        {
        }

        public override bool IsRocketMode(RocketMode rocketMode) => rocketMode == CometModes.Cut;

        public override bool Evaluate(Rocket rocket, out RocketActionResult result)
        {
            CometRefusal why = CometActions.Refusal(SpaceMapNode, CometModes.Cut);
            return result = why == CometRefusal.None ? RocketActionResult.Success : CometActions.Failure(why);
        }
    }

    /// <summary>
    /// Burn: the drill heads work the comet's own ice into propellant, a cycle at a time, with the game's
    /// timing and head wear; the site itself is not mined (CometActionPatches.MineDepositPrefix).
    /// </summary>
    internal sealed class BurnAction : RocketMine
    {
        public BurnAction(SpaceMapNode node)
            : base(node.MineData, node)
        {
        }

        public override bool IsRocketMode(RocketMode rocketMode) => rocketMode == CometModes.Burn;

        public override bool Evaluate(Rocket rocket, out RocketActionResult result)
        {
            CometRefusal why = CometActions.Refusal(SpaceMapNode, CometModes.Burn);
            return result = why == CometRefusal.None ? RocketActionResult.Success : CometActions.Failure(why);
        }

        /// <summary>The game's mining loop without its mined-out check: Burn works the comet, not the site.</summary>
        public override bool ProgressAction(float deltaTime, Rocket rocket, out RocketActionResult result)
        {
            List<IRocketMiner> miners = rocket.GetMiners();
            if (miners == null || miners.Count == 0)
            {
                return result = RocketActionResult.Failure(GameStrings.NoRocketMiner);
            }
            foreach (IRocketMiner miner in miners)
            {
                if (miner.CanProgressAction(out RocketActionResult refused))
                {
                    miner.ProgressMineAction(deltaTime, this);
                }
                else
                {
                    rocket.Report(refused);
                }
            }
            return result = RocketActionResult.Success;
        }
    }

    /// <summary>Fuel: premix moves from the rocket's tanks and pipes into the comet in the rocket's atmospherics step (CometActionPatches.RocketAtmosphericsPostfix).</summary>
    internal sealed class FuelAction : RocketAction
    {
        public FuelAction(SpaceMapNode node)
            : base(null, node)
        {
        }

        public override bool IsRocketMode(RocketMode rocketMode) => rocketMode == CometModes.Fuel;

        public override bool Evaluate(Rocket rocket, out RocketActionResult result)
        {
            CometRefusal why = CometActions.Refusal(SpaceMapNode, CometModes.Fuel);
            return result = why == CometRefusal.None ? RocketActionResult.Success : CometActions.Failure(why);
        }

        public override void Start(Rocket rocket)
        {
        }

        public override bool ProgressAction(float deltaTime, Rocket rocket, out RocketActionResult result) => result = RocketActionResult.Success;
    }

    /// <summary>
    /// Launch: sends the site's comet to Entry once, then puts the rocket back to idle, which closes the
    /// action on its next tick as the game closes any action whose mode changed.
    /// </summary>
    internal sealed class LaunchAction : RocketAction
    {
        private bool _done;

        public LaunchAction(SpaceMapNode node)
            : base(null, node)
        {
        }

        public override bool IsRocketMode(RocketMode rocketMode) => rocketMode == CometModes.Launch;

        public override bool Evaluate(Rocket rocket, out RocketActionResult result)
        {
            if (_done)
            {
                return result = RocketActionResult.Success;
            }
            CometRefusal why = CometActions.Refusal(SpaceMapNode, CometModes.Launch);
            return result = why == CometRefusal.None ? RocketActionResult.Success : CometActions.Failure(why);
        }

        public override void Start(Rocket rocket)
        {
        }

        public override bool ProgressAction(float deltaTime, Rocket rocket, out RocketActionResult result)
        {
            result = RocketActionResult.Success;
            if (!_done)
            {
                _done = true;
                string refused = CometActions.Launch(rocket, SpaceMapNode);
                if (refused != null)
                {
                    Log.Warn("[TR comet actions] rocket " + rocket.ReferenceId + " could not launch the comet at " + SpaceMapNode?.DisplayName + ": " + refused);
                    result = CometActions.Failure(CometRefusal.NotSent);
                }
                rocket.RocketMode = RocketMode.None;
            }
            return result;
        }
    }

    /// <summary>
    /// The comet's line in the location panel while an action works on it (Rocket.ActionProgressables):
    /// how much of the site it holds, its engines, its fuel against what its route to orbit takes. Built
    /// from this machine's own copy of the comets, so a client shows what the host last sent.
    /// </summary>
    internal sealed class CometProgress : IRocketActionProgressable
    {
        private static readonly Dictionary<long, CometProgress> Made = new Dictionary<long, CometProgress>();

        private readonly long _site;
        private RocketMode _mode;

        private CometProgress(long site)
        {
            _site = site;
            ReferenceId = -(1L << 40) - site;
        }

        /// <summary>One per site, kept: the panel compares lines by ReferenceId.</summary>
        public static CometProgress For(SpaceMapNode site, RocketMode mode)
        {
            lock (Made)
            {
                if (!Made.TryGetValue(site.ReferenceId, out CometProgress progress))
                {
                    progress = new CometProgress(site.ReferenceId);
                    Made[site.ReferenceId] = progress;
                }
                progress._mode = mode;
                return progress;
            }
        }

        private MapComet Comet => CometMap.WaitingAt(SpaceMapNode.Get(_site));

        public string DisplayName => Comet == null ? "Comet" : "Comet " + Comet.Id.ToString(CultureInfo.InvariantCulture);

        public ushort NetworkUpdateFlags { get; set; }

        public long ReferenceId { get; set; }

        public bool BeingDestroyed { get; set; }

        public float GetActionProgress
        {
            get
            {
                MapComet comet = Comet;
                if (comet == null)
                {
                    return 0f;
                }
                if (_mode == CometModes.Cut)
                {
                    double cap = Cap(comet);
                    return cap > 0.0 ? (float)Math.Min(1.0, Have(comet) / cap) : 0f;
                }
                double needed = Needed(comet);
                return needed > 0.0 ? (float)Math.Min(1.0, comet.Fuel / needed) : 0f;
            }
        }

        public string GetActionInfoText()
        {
            MapComet comet = Comet;
            CultureInfo c = CultureInfo.InvariantCulture;
            if (comet == null)
            {
                return CometActions.Explain(CometRefusal.NoComet);
            }
            string unit = comet.Kind == CometKind.Ice ? "mol" : "units of ore";
            string held = string.Format(c, "{0:N0} {1} of the {2:N0} this site can give, {3} cycles; {4:N1} t, {5} engine{6}",
                Have(comet), unit, Cap(comet), comet.Cycles, comet.CometKg / 1000.0, comet.Engines, comet.Engines == 1 ? "" : "s");
            if (_mode == CometModes.Cut || _mode == RocketMode.Deploy)
            {
                return held;
            }
            double needed = Needed(comet);
            string fuel = comet.Engines <= 0
                ? string.Format(c, "{0:N0} mol of fuel; with no engines it cannot fly", comet.Fuel)
                : string.Format(c, "{0:N0} mol of fuel of the {1:N0} its route to orbit takes", comet.Fuel, needed);
            if (_mode == CometModes.Burn)
            {
                fuel += string.Format(c, "; methane {0:N0}, oxygen {1:N0} mol left to burn", comet.MolesOf(Chemistry.GasType.Methane), comet.MolesOf(Chemistry.GasType.Oxygen));
            }
            return held + "\n" + fuel;
        }

        private static double Have(MapComet comet) => comet.Kind == CometKind.Ice ? comet.TotalMoles : comet.OreUnits;

        private double Cap(MapComet comet)
        {
            SpaceMapNode site = SpaceMapNode.Get(_site);
            if (!CometMap.SiteStock(site, out CometKind kind, out _, out double[] perUnit, out double units) || kind != comet.Kind)
            {
                return Have(comet);
            }
            return CometMap.SizeMultiplierNow(Effective.Celestial) * CometMap.SiteAmount(kind, perUnit, units);
        }

        private double Needed(MapComet comet)
        {
            double route = CometActions.RouteDeltaV(SpaceMapNode.Get(_site));
            return comet.Engines > 0 && route > 0.0 ? CelestialRules.Shipped.FuelNeeded(comet.CometKg, comet.Engines, route, comet.FuelKgPerMole) : 0.0;
        }

        public bool CanProgressAction(out RocketActionResult result)
        {
            result = RocketActionResult.Success;
            return true;
        }

        public void ClearAction()
        {
        }

        public void PrintDebugInfo(bool verbose = false)
        {
        }

        public void OnAssignedReference()
        {
        }
    }
}
