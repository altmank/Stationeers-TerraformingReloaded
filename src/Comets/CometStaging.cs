using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using Networks;
using Objects.Items;
using Objects.Rockets;
using Objects.Rockets.Mining;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Debug verbs for staging a comet situation by hand (docs/COMET-ROCKETS.md, "Staging a situation in a
    /// game"), host only, behind CometMapVerb's checks. Each skips a slow part of play by doing what the game,
    /// or the comet rule, does at the end of it:
    ///   terraform comet discover [ice|mineral]: a discovery completing (SpaceMapNode.OnDiscoverPointsUpdated);
    ///   terraform comet rocket [&lt;n&gt; [&lt;site&gt;|entry] [fill]]: a trip ending, by the game's own travel
    ///     step, and the game's own refuel with the premix the Fuel action takes;
    ///   terraform comet arrive &lt;id&gt; [strand]: a comet's flight flown to the end the rule gives it.
    /// </summary>
    internal static class CometStaging
    {
        public const string Usage = "For staging, host only: "
            + "terraform comet discover [ice|mineral] discovers a site of that kind, as a Discover scan completing does, at the node nearest orbit that can have one. "
            + "terraform comet rocket lists the rockets; terraform comet rocket <n> <site>|entry [fill] ends a rocket's trip there now, for a rocket already in space, "
            + "and fill tops up its fuel tanks with 2:1 methane and oxygen as the game's rocket refuel does. "
            + "terraform comet arrive <id> brings a comet in flight to orbit now, as if it had set off long enough ago; "
            + "terraform comet arrive <id> strand stops one that runs out of fuel where it would.";

        /// <summary>Most hops a staged trip is ended through; a route on a shipped map has a few.</summary>
        private const int MaxHops = 64;

        private static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // ---- discover ---------------------------------------------------------------------------------------

        /// <summary>
        /// A new site of the kind asked for, at the node nearest the orbit that can still discover one. The
        /// game's own completion of a discovery (SpaceMapNode.OnDiscoverPointsUpdated): the node's points
        /// back to 0 and SpaceMapNode.CreateSite, which registers the site, sends it to every player and saves
        /// it with the map, with the game's random pick narrowed to the templates of that kind. A rocket can
        /// only scan at a charted node, so the way to the node is charted first, as Chart scans would.
        /// </summary>
        public static string Discover(string[] args)
        {
            CometKind? wanted = null;
            if (args.Length > 2)
            {
                switch (args[2].ToLowerInvariant())
                {
                    case "ice":
                        wanted = CometKind.Ice;
                        break;
                    case "mineral":
                    case "ore":
                        wanted = CometKind.Mineral;
                        break;
                    default:
                        return "terraform comet discover [ice|mineral]: '" + args[2] + "' is not a kind of site a comet is cut from.";
                }
            }
            SpaceMapNode parent = null;
            List<SpaceMapNodeData> templates = null;
            foreach (SpaceMapNode node in SpaceMap.Current.Nodes.Where(n => n?.DiscoverData != null && n.IsActionAvailable(RocketMode.Discover)).OrderBy(DeltaVToEntry))
            {
                List<SpaceMapNodeData> fit = node.DiscoverData.SiteGenerations.Where(t => KindOf(t) is CometKind kind && (wanted == null || kind == wanted)).ToList();
                if (fit.Count > 0)
                {
                    parent = node;
                    templates = fit;
                    break;
                }
            }
            string what = wanted == null ? "ice or mineral" : wanted == CometKind.Ice ? "ice" : "mineral";
            if (parent == null)
            {
                return "No node on the space map can discover another " + what + " site: none has one to find, or each that has already holds as many sites as the game allows.";
            }
            string charted = ChartWay(parent);
            parent.DiscoverPoints = 0;
            SpaceMapNode site = SpaceMapNode.CreateSite(templates.Pick(), parent);
            List<SpaceMapNode> sites = CometMap.Sites();
            int number = sites.IndexOf(site) + 1;
            Log.Info(string.Format(C, "[TR comet staging] discovered site {0} ({1}, id {2}) under {3} by the console{4}", number, site.DisplayName, site.ReferenceId, parent.DisplayName,
                charted.Length > 0 ? "; charted " + charted : ""));
            StringBuilder text = new StringBuilder(string.Format(C, "Discovered site {0}, {1}: ", number, CometMapVerb.Name(site)));
            if (!CometMap.SiteStock(site, out CometKind kindMade, out Chemistry.GasType[] gases, out double[] perUnit, out double units))
            {
                text.Append("no comet can be cut from it");
            }
            else if (kindMade == CometKind.Mineral)
            {
                text.Append(string.Format(C, "ore, {0:N0} units by the game's count", units));
            }
            else
            {
                text.Append("ice of ").Append(string.Join(", ", gases.Select((g, i) => string.Format(C, "{0} {1:0.##}", g, perUnit[i])))).Append(" mol a unit, ")
                    .Append(string.Format(C, "{0:N0} units, {1:N0} mol by the game's count", units, CometMap.SiteAmount(kindMade, perUnit, units)));
            }
            text.Append('.');
            if (charted.Length > 0)
            {
                text.Append(" Charted on the way, as Chart scans would: ").Append(charted).Append('.');
            }
            return text.Append(string.Format(C, " terraform comet rocket <n> {0} takes a rocket there; terraform comet make {0} ... puts a comet there.", number)).ToString();
        }

        /// <summary>The kind of comet a site template's deposit gives, as CometMap.SiteStock reads the site it makes; null for neither.</summary>
        private static CometKind? KindOf(SpaceMapNodeData template)
        {
            DepositCompositionData deposit = template?.DepositData?.DepositCompositionData;
            if (deposit == null)
            {
                return null;
            }
            if (deposit.Ores.Count > 0 || deposit.ReagentMixes.Count > 0)
            {
                return CometKind.Mineral;
            }
            return deposit.FrozenGasMixes.Count > 0 ? CometKind.Ice : (CometKind?)null;
        }

        private static double DeltaVToEntry(SpaceMapNode node) =>
            node == SpaceMap.Current.EntryNode ? 0.0 : CometMapVerb.RouteDeltaV(node, out double deltaV) ? deltaV : double.MaxValue;

        /// <summary>
        /// Every uncharted node from this one up to the map's root, charted as the game charts one: its parent's
        /// chart points raised to the connection's difficulty (SpaceMapNode.OnChartPointsUpdated), or set
        /// charted where the parent keeps no chart points. The names charted, or empty.
        /// </summary>
        private static string ChartWay(SpaceMapNode node)
        {
            List<string> charted = new List<string>();
            for (SpaceMapNode n = node; n != null; n = n.ParentConnection?.Parent)
            {
                if (n.IsCharted)
                {
                    continue;
                }
                NodeConnection up = n.ParentConnection;
                if (up?.Parent?.ChartData != null)
                {
                    up.Parent.ChartPoints = Math.Max(up.Parent.ChartPoints, up.Difficulty);
                }
                if (!n.IsCharted)
                {
                    n.IsCharted = true;
                }
                charted.Add(n.DisplayName);
            }
            return string.Join(", ", charted);
        }

        // ---- rocket -----------------------------------------------------------------------------------------

        /// <summary>The rockets in the order the verb numbers them: by the game's id, so the numbers hold while nothing is built.</summary>
        private static List<Rocket> Rockets() => Rocket.AllRockets.Where(r => r != null && !r.BeingDestroyed).OrderBy(r => r.ReferenceId).ToList();

        public static string RocketVerb(string[] args)
        {
            List<Rocket> rockets = Rockets();
            if (args.Length < 3)
            {
                return RocketList(rockets);
            }
            if (!int.TryParse(args[2], NumberStyles.Integer, C, out int number) || number < 1 || number > rockets.Count)
            {
                return "There is no rocket " + args[2] + ". " + RocketList(rockets);
            }
            Rocket rocket = rockets[number - 1];
            bool fill = false;
            string to = null;
            for (int i = 3; i < args.Length; i++)
            {
                if (string.Equals(args[i], "fill", StringComparison.OrdinalIgnoreCase))
                {
                    fill = true;
                }
                else if (to == null)
                {
                    to = args[i];
                }
                else
                {
                    return "terraform comet rocket <n> <site>|entry [fill]: one place at a time.";
                }
            }
            if (to == null && !fill)
            {
                return RocketLine(number, rocket) + " terraform comet rocket " + number + " <site>|entry [fill] ends its trip there, or fill alone fills its tanks.";
            }
            StringBuilder text = new StringBuilder();
            if (to != null)
            {
                string notFound = null;
                SpaceMapNode node = string.Equals(to, "entry", StringComparison.OrdinalIgnoreCase)
                    ? SpaceMap.Current.EntryNode
                    : CometMapVerb.SiteNamed(CometMap.Sites(), to, out notFound);
                if (node == null)
                {
                    return notFound ?? "The space map has no Entry.";
                }
                string refused = MoveTo(rocket, number, node, out string moved);
                if (refused != null)
                {
                    return refused;
                }
                text.Append(moved);
            }
            if (fill)
            {
                text.Append(text.Length > 0 ? " " : "").Append(Fill(rocket, number));
            }
            return text.ToString();
        }

        /// <summary>
        /// Ends a trip for a rocket in space: the game's own destination (Rocket.ChangeTarget) and then, hop by
        /// hop, the game's `rocket instantmove` (Progress to 1) and its own travel step (Rocket.PhysicsUpdate),
        /// which at each node moves the rocket on (SetCurrentNode, no transit, Progress 0) and at the last runs
        /// the arrival (TargetReached: the rocket log's arrival, idle, the avionics' auto shut-off) and clears
        /// the trip (Reset). Every field it sets flags itself to be sent to every player, and the save keeps
        /// the node. Null on success, else why not.
        /// </summary>
        private static string MoveTo(Rocket rocket, int number, SpaceMapNode node, out string moved)
        {
            moved = null;
            string name = "Rocket " + number + " (" + rocket.DisplayName + ")";
            switch (rocket.RocketState)
            {
                case RocketState.InSpace:
                    break;
                case RocketState.OnLaunchMount:
                    return name + " is on its launch pad. Launch it first: only a rocket already in space can have its trip ended, since its parts stand on the pad until it flies.";
                case RocketState.Launching:
                    return name + " is still climbing to orbit; once it is in space its trip can be ended.";
                case RocketState.Landing:
                    return name + " is coming down to a launch pad; only a rocket in space can have its trip ended.";
                default:
                    return name + " is not in space. Launch it first: only a rocket already in space can have its trip ended.";
            }
            if (rocket.IsManned)
            {
                return name + " is crewed, and the game flies a crewed rocket only to a launch pad.";
            }
            SpaceMapNode from = rocket.CurrentNode;
            if (from == null)
            {
                return name + " has no place on the space map.";
            }
            if (from == node && rocket.Progress == 0f)
            {
                return name + " is already at " + node.DisplayName + ".";
            }
            if (!SpaceMapPathFinder.GetPath(from, node, out List<Assets.Scripts.Serialization.NodeTransit> path) || path == null || path.Count == 0)
            {
                return "The game has no route for " + name + " from " + from.DisplayName + " to " + node.DisplayName + ".";
            }
            rocket.ChangeTarget(node);
            for (int hop = 0; hop < MaxHops && rocket.TargetNode == node; hop++)
            {
                rocket.Progress = 1f;
                rocket.PhysicsUpdate();
            }
            bool there = rocket.CurrentNode == node && rocket.TargetNode == null && rocket.CurrentTransit == null && rocket.Progress == 0f;
            Log.Info(string.Format(C, "[TR comet staging] rocket {0} trip from {1} to {2} ended by the console: {3} at {4}, target {5}, mode {6}",
                rocket.ReferenceId, from.DisplayName, node.DisplayName, there ? "arrived" : "NOT arrived", rocket.CurrentNode?.DisplayName ?? "none",
                rocket.TargetNode?.DisplayName ?? "none", (byte)rocket.RocketMode));
            if (!there)
            {
                return string.Format(C, "The game's travel step did not bring {0} to {1}: it stands at {2} with {3} as its destination, {4:0.##} through a hop. See the log.",
                    name, node.DisplayName, rocket.CurrentNode?.DisplayName ?? "no node", rocket.TargetNode?.DisplayName ?? "none", rocket.Progress);
            }
            moved = string.Format(C, "{0} arrived at {1} from {2}, as the game ends a trip: idle there, with no destination.", name, node.DisplayName, from.DisplayName);
            return null;
        }

        /// <summary>
        /// The game's own rocket refuel (RocketCommand.Refuel: each store up to 30 MPa counted at 215 K, the
        /// moles added at 215 K), into each fuel tank, of the premix the Fuel action takes: two of methane to
        /// one of oxygen.
        /// </summary>
        private static string Fill(Rocket rocket, int number)
        {
            List<Tank> tanks = rocket.RocketNetwork?.Internals.OfType<Tank>().Where(t => t.InternalAtmosphere != null).ToList() ?? new List<Tank>();
            if (tanks.Count == 0)
            {
                return "Rocket " + number + " has no fuel tank to fill.";
            }
            double added = 0.0;
            foreach (Tank tank in tanks)
            {
                Atmosphere store = tank.InternalAtmosphere;
                double moles = IdealGas.Quantity(new PressurekPa(CelestialRules.RefuelKPa), store.Volume, new TemperatureKelvin(CelestialRules.RefuelKelvin)).ToDouble()
                    - store.TotalMoles.ToDouble();
                if (!(moles > 0.0))
                {
                    continue;
                }
                double oxygen = moles / (CelestialRules.MethanePerOxygen + 1.0);
                GasMixture premix = GasMixtureHelper.Create();
                premix.Add(new Mole(Chemistry.GasType.Methane, new MoleQuantity(moles - oxygen), MoleEnergy.Zero));
                premix.Add(new Mole(Chemistry.GasType.Oxygen, new MoleQuantity(oxygen), MoleEnergy.Zero));
                premix.TotalEnergy = IdealGas.Energy(premix.HeatCapacity, new TemperatureKelvin(CelestialRules.RefuelKelvin));
                store.Add(premix);
                added += moles;
            }
            Log.Info(string.Format(C, "[TR comet staging] rocket {0} filled by the console: {1:R} mol of premix into {2} tanks", rocket.ReferenceId, added, tanks.Count));
            return string.Format(C, "Filled rocket {0}'s {1} fuel tank{2} with {3:N0} mol of 2:1 methane and oxygen at {4:0} K, up to {5:N0} kPa, as the game's own rocket refuel does: {6}.",
                number, tanks.Count, tanks.Count == 1 ? "" : "s", added, CelestialRules.RefuelKelvin, CelestialRules.RefuelKPa, string.Join("; ", tanks.Select(TankText)));
        }

        private static string RocketList(List<Rocket> rockets)
        {
            if (rockets.Count == 0)
            {
                return "There are no rockets.";
            }
            StringBuilder text = new StringBuilder("Rockets:");
            for (int i = 0; i < rockets.Count; i++)
            {
                text.AppendLine().Append("  ").Append(RocketLine(i + 1, rockets[i]));
            }
            return text.ToString();
        }

        /// <summary>One rocket: its number, name, where it is, what it is doing, and what it carries for a comet.</summary>
        private static string RocketLine(int number, Rocket rocket)
        {
            StringBuilder text = new StringBuilder(string.Format(C, "{0}: {1}, ", number, rocket.DisplayName));
            switch (rocket.RocketState)
            {
                case RocketState.InSpace:
                    text.Append(rocket.CurrentTransit != null && rocket.Progress != 0f
                        ? string.Format(C, "in space, flying from {0} to {1}, {2:0} % of the hop", rocket.CurrentTransit.From?.DisplayName, rocket.CurrentTransit.Destination?.DisplayName, rocket.Progress * 100f)
                        : "in space at " + (rocket.CurrentNode?.DisplayName ?? "no node"));
                    break;
                case RocketState.OnLaunchMount:
                    text.Append("on its launch pad");
                    break;
                case RocketState.Launching:
                    text.Append("launching");
                    break;
                case RocketState.Landing:
                    text.Append("landing");
                    break;
                default:
                    text.Append("on the ground, not on a launch pad");
                    break;
            }
            text.Append(", ").Append(rocket.RocketMode == RocketMode.None ? "idle" : "running " + (CometModes.Name(rocket.RocketMode) ?? rocket.RocketMode.ToString()));
            RocketNetwork network = rocket.RocketNetwork;
            if (network == null)
            {
                return text.Append('.').ToString();
            }
            List<string> parts = new List<string>();
            foreach (IRocketMiner miner in network.RocketMiners)
            {
                RocketMiningDrillHead head = (miner as Thing)?.Slots.Select(s => s?.Get()).OfType<RocketMiningDrillHead>().FirstOrDefault();
                parts.Add(head == null ? "a miner with no drill head" : string.Format(C, "a miner with a {0} ({1:0} % left)", head.DisplayName, 100f * head.Quantity / Math.Max(1e-6f, head.MaxQuantity)));
            }
            foreach (RocketPayloadBay bay in network.RocketPayloadBays)
            {
                Thing payload = bay.Payload as Thing;
                if (payload == null)
                {
                    parts.Add("an empty payload bay");
                    continue;
                }
                List<string> inside = payload.Slots.Select(s => s?.Get()).Where(t => t != null)
                    .Select(t => t is Stackable stack && stack.Quantity > 1 ? stack.Quantity + " x " + t.DisplayName : t.DisplayName).ToList();
                parts.Add("a payload bay holding " + payload.DisplayName + (inside.Count == 0 ? ", empty" : " (" + string.Join(", ", inside) + ")"));
            }
            List<Tank> tanks = network.Internals.OfType<Tank>().Where(t => t.InternalAtmosphere != null).ToList();
            parts.Add(tanks.Count == 0 ? "no fuel tank" : string.Join("; ", tanks.Select(TankText)));
            return text.Append("; ").Append(string.Join("; ", parts)).Append('.').ToString();
        }

        private static string TankText(Tank tank)
        {
            CometActionPatches.Premix(tank.InternalAtmosphere, out double methane, out double oxygen);
            double premix = (CelestialRules.MethanePerOxygen + 1.0) * Math.Min(methane / CelestialRules.MethanePerOxygen, oxygen);
            return string.Format(C, "{0} with {1:N0} mol of methane and {2:N0} of oxygen, {3:N0} mol of 2:1 premix, at {4:N0} kPa", tank.DisplayName, methane, oxygen, premix,
                tank.InternalAtmosphere.PressureGassesAndLiquids.ToDouble());
        }

        // ---- arrive -----------------------------------------------------------------------------------------

        /// <summary>
        /// A comet in flight brought to the end of its flight now (CometMap.FlyToEnd): parked at Entry as a
        /// natural arrival is, with the fuel the rule says it did not burn; a comet the rule runs dry is never
        /// brought to Entry, only, with strand, stopped where its fuel runs out.
        /// </summary>
        public static string Arrive(string[] args)
        {
            if (args.Length < 3 || !int.TryParse(args[2], NumberStyles.Integer, C, out int id))
            {
                return "terraform comet arrive <id> [strand]: the comet's number from terraform comet list.";
            }
            bool strand = args.Length > 3 && string.Equals(args[3], "strand", StringComparison.OrdinalIgnoreCase);
            if (args.Length > 4 || (args.Length > 3 && !strand))
            {
                return "terraform comet arrive <id> [strand]: '" + string.Join(" ", args.Skip(3)) + "' is not something it takes.";
            }
            MapComet comet = CometMap.Find(id);
            if (comet == null)
            {
                return "There is no comet " + id + ". terraform comet list shows them.";
            }
            switch (comet.State)
            {
                case MapCometState.Waiting:
                    return "Comet " + id + " is still waiting at its site: terraform comet send " + id + " sends it first.";
                case MapCometState.Arrived:
                    return "Comet " + id + " is already parked at Entry.";
                case MapCometState.Stranded:
                    return "Comet " + id + " has already run out of fuel for good.";
            }
            if (!comet.OnClock)
            {
                return "Comet " + id + " was loaded in flight and is waiting for the game's clock; try again in a moment.";
            }
            Flight whole = comet.WholeFlight;
            if (!whole.Arrives && !strand)
            {
                Stop stop = CelestialRules.StopAt(comet.Hops, whole.DeltaV);
                return string.Format(C, "Comet {0} never reaches Entry: by the rule its fuel runs out {1:0.#} % of the way, {2:0.#} % from {3} to {4}, {5:N1} s after it set off. "
                    + "terraform comet arrive {0} strand stops it there now, for good.",
                    id, whole.Share * 100.0, stop.Progress * 100.0, CometMap.RouteNode(comet, stop.Hop)?.DisplayName ?? "?", CometMap.RouteNode(comet, stop.Hop + 1)?.DisplayName ?? "?", whole.Seconds);
            }
            if (whole.Arrives && strand)
            {
                return "Comet " + id + " has the fuel to reach Entry: terraform comet arrive " + id + " parks it there.";
            }
            string refused = CometMap.FlyToEnd(id, out MapComet flown);
            if (refused != null)
            {
                return refused;
            }
            return whole.Arrives
                ? string.Format(C, "Comet {0} has reached Entry, {1}, as if it had set off {2:N1} s ago: {3:N0} of its {4:N0} mol of fuel burnt, {5:N0} left. It parks in the sky.",
                    id, SpaceMap.Current.EntryNode?.DisplayName, whole.Seconds, whole.FuelMoles, comet.Fuel, Math.Max(0.0, comet.Fuel - whole.FuelMoles))
                : string.Format(C, "Comet {0} has run out of fuel {1:0.#} % of the way, as if it had set off {2:N1} s ago, and stays there for good.", id, whole.Share * 100.0, whole.Seconds);
        }
    }
}
