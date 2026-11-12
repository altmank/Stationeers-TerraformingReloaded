using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;
using Objects.Rockets;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Debug verbs for the comets on the space map (docs/COMET-ROCKETS.md, stage 3, and "Staging a
    /// situation in a game"):
    ///   terraform comet make [&lt;site&gt; &lt;mol&gt; &lt;gas&gt;[:&lt;share&gt;] ... engines=&lt;n&gt; fuel=&lt;mol&gt;]
    ///   terraform comet send &lt;id&gt;
    ///   terraform comet list
    ///   terraform comet discover / rocket / arrive (CometStaging)
    /// list answers anywhere, from that machine's own copy of the comets; the rest are host only.
    /// </summary>
    internal static class CometMapVerb
    {
        public const string Usage = "Debug, for comets on the space map: "
            + "terraform comet make <site> <mol> <gas>[:<share>] [<gas>[:<share>] ...] engines=<n> fuel=<mol> puts a comet at a discovered site; "
            + "terraform comet make <site> <units> Ore engines=<n> fuel=<mol> puts a mineral comet of that many units of ore there; "
            + "terraform comet make alone lists the sites, and <site> is a number from that list or a site's name. "
            + "terraform comet send <id> sends it to the planet's orbit by the game's rocket travel rule. "
            + "terraform comet list shows every comet, where it is and when it arrives. make and send are host only. "
            + CometStaging.Usage;

        public static bool Handles(string verb)
        {
            switch (verb.ToLowerInvariant())
            {
                case "make":
                case "send":
                case "list":
                case "discover":
                case "rocket":
                case "arrive":
                    return true;
                default:
                    return false;
            }
        }

        public static string Run(string[] args, Func<string, Chemistry.GasType?> gasNamed, Func<string> gasNames)
        {
            string verb = args[1].ToLowerInvariant();
            if (SpaceMap.Current == null)
            {
                return "There is no space map in this world.";
            }
            if (verb == "list")
            {
                return List();
            }
            if (NetworkManager.IsClient)
            {
                return "Can only be run on the server";
            }
            string broken = CelestialRules.Verdict;
            if (broken != null)
            {
                return "Comets on the space map are off: the rules they fly by did not pass their own check (" + broken + "). Please report this with the log.";
            }
            if (!CometMap.SaveInstalled)
            {
                return "Comets on the space map are off for this game build: they could not be saved with the world, so one would be lost at the next load. See the log.";
            }
            switch (verb)
            {
                case "make":
                    return Make(args, gasNamed, gasNames);
                case "send":
                    return Send(args);
                case "discover":
                    return CometStaging.Discover(args);
                case "rocket":
                    return CometStaging.RocketVerb(args);
                default:
                    return CometStaging.Arrive(args);
            }
        }

        private static string Make(string[] args, Func<string, Chemistry.GasType?> gasNamed, Func<string> gasNames)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            List<SpaceMapNode> sites = CometMap.Sites();
            if (args.Length < 3)
            {
                return SiteList(sites) + " " + Usage;
            }
            SpaceMapNode site = SiteNamed(sites, args[2], out string notFound);
            if (site == null)
            {
                return notFound;
            }
            if (args.Length < 5)
            {
                return Usage + " Gases: " + gasNames() + ".";
            }
            if (!double.TryParse(args[3], NumberStyles.Float, c, out double moles) || double.IsNaN(moles) || !(moles > 0.0) || moles > CometMap.MaxMoles)
            {
                return string.Format(c, "'{0}' is not an amount. Give the comet's moles, more than 0 and up to {1:0.#####E+0}.", args[3], CometMap.MaxMoles);
            }
            int engines = -1;
            double fuel = -1.0;
            bool ore = false;
            List<Chemistry.GasType> types = new List<Chemistry.GasType>();
            List<double> shares = new List<double>();
            for (int i = 4; i < args.Length; i++)
            {
                string arg = args[i];
                int equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    string key = arg.Substring(0, equals).ToLowerInvariant();
                    string value = arg.Substring(equals + 1);
                    switch (key)
                    {
                        case "engines":
                            if (!int.TryParse(value, NumberStyles.Integer, c, out engines) || engines < 0 || engines > CometMap.MaxEngines)
                            {
                                return "engines is how many Pumped Gas engines the comet has, from 0 to " + CometMap.MaxEngines + ".";
                            }
                            break;
                        case "fuel":
                            if (!double.TryParse(value, NumberStyles.Float, c, out fuel) || double.IsNaN(fuel) || fuel < 0.0 || fuel > CometMap.MaxFuel)
                            {
                                return string.Format(c, "fuel is the moles of 2:1 methane and oxygen premix aboard, from 0 to {0:0.#####E+0}.", CometMap.MaxFuel);
                            }
                            break;
                        default:
                            return "'" + arg + "' is not something a comet on the map takes. " + Usage;
                    }
                    continue;
                }
                if (string.Equals(arg, "Ore", StringComparison.OrdinalIgnoreCase))
                {
                    ore = true;
                    continue;
                }
                string wrongGas = CometVerb.AddGas(arg, gasNamed, gasNames, types, shares);
                if (wrongGas != null)
                {
                    return wrongGas;
                }
            }
            if (ore && types.Count > 0)
            {
                return "A comet is of gas or of Ore, not both. " + Usage;
            }
            if (types.Count == 0 && !ore)
            {
                return "Name at least one gas for the comet, or Ore. " + Usage;
            }
            if (engines < 0 || fuel < 0.0)
            {
                return "Give the comet engines=<n> and fuel=<mol>. " + Usage;
            }
            double total = 0.0;
            foreach (double share in shares)
            {
                total += share;
            }
            double[] amounts = shares.ConvertAll(s => moles * s / total).ToArray();
            string refused = CometMap.Make(site, types.ToArray(), amounts, engines, fuel, out MapComet made, ore ? moles : 0.0);
            if (refused != null)
            {
                return refused;
            }
            StringBuilder text = new StringBuilder(ore
                ? string.Format(c, "Comet {0} made at {1}: {2:N0} units of ore", made.Id, Name(site), made.OreUnits)
                : string.Format(c, "Comet {0} made at {1}: {2:N0} mol (", made.Id, Name(site), made.TotalMoles));
            for (int i = 0; i < made.Gases.Length; i++)
            {
                text.Append(string.Format(c, "{0}{1} {2:N0}", i > 0 ? ", " : "", made.Gases[i], made.Moles[i]));
            }
            text.Append(string.Format(c, "{0}, {1:N1} t, {2} engine{3}, {4:N0} mol of fuel.", ore ? "" : ")", made.CometKg / 1000.0, engines, engines == 1 ? "" : "s", fuel));
            if (RouteDeltaV(site, out double toEntry))
            {
                Flight flight = CelestialRules.Shipped.Fly(made.CometKg, made.Engines, made.Fuel, toEntry, made.FuelKgPerMole);
                text.Append(flight.Arrives
                    ? string.Format(c, " Sent now, it would reach orbit ({0:N1} delta-v) in {1:N1} s on {2:N0} mol of fuel.", toEntry, flight.Seconds, flight.FuelMoles)
                    : string.Format(c, " Sent now, it would run out of fuel {0:0.#} % of the way to orbit ({1:N1} delta-v), after {2:N1} s.", flight.Share * 100.0, toEntry, flight.Seconds));
            }
            return text.Append(" terraform comet send ").Append(made.Id.ToString(c)).Append(" sends it.").ToString();
        }

        private static string Send(string[] args)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (args.Length < 3 || !int.TryParse(args[2], NumberStyles.Integer, c, out int id))
            {
                return "terraform comet send <id>: the comet's number from terraform comet list.";
            }
            string refused = CometMap.Send(id, out MapComet sent);
            if (refused != null)
            {
                return refused;
            }
            Flight flight = sent.WholeFlight;
            string route = CometMap.RouteText(sent);
            return flight.Arrives
                ? string.Format(c, "Comet {0} is on its way: {1}, {2:N1} delta-v. It reaches orbit in {3:N1} s on {4:N0} of its {5:N0} mol of fuel.",
                    id, route, sent.RouteDeltaV, flight.Seconds, flight.FuelMoles, sent.Fuel)
                : string.Format(c, "Comet {0} is on its way: {1}, {2:N1} delta-v. Its fuel runs out {3:0.#} % of the way, after {4:N1} s, and it stays there.",
                    id, route, sent.RouteDeltaV, flight.Share * 100.0, flight.Seconds);
        }

        private static string List()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder text = new StringBuilder("Comets on the space map (debug).");
            double size = CometMap.PlanetSize();
            if (size > 0.0)
            {
                CelestialTuning tuning = Effective.Celestial;
                text.Append(string.Format(c, " Size multiplier K in force: {0:0.##} (planet size {1:0.####}, the setting {2:0.##} at Standard size).",
                    tuning.SizeMultiplierAt(size), size, tuning.CometSizeMultiplier));
            }
            CometView[] comets = CometMap.Views();
            LandingView[] falling = CometLand.Falling();
            if (comets.Length == 0 && falling.Length == 0)
            {
                return text.Append(" None. terraform comet make puts one at a site.").ToString();
            }
            double now = CometEvent.Clock;
            foreach (LandingView landing in falling)
            {
                text.AppendLine();
                text.Append(double.IsNaN(landing.ImpactClock)
                    ? string.Format(c, "  {0}: brought down, coming down at ({1:N0}, {2:N0}) (loaded, waiting for the clock)", landing.CometId, landing.ImpactX, landing.ImpactZ)
                    : string.Format(c, "  {0}: brought down, lands at ({1:N0}, {2:N0}) in {3:N1} s", landing.CometId, landing.ImpactX, landing.ImpactZ, Math.Max(0.0, landing.ImpactClock - now)));
            }
            foreach (CometView comet in comets)
            {
                text.AppendLine();
                text.Append(string.Format(c, "  {0}: {1}, {2:N1} t in flight, {3} engine{4}{5}; ", comet.Id,
                    comet.Kind == CometKind.Mineral ? string.Format(c, "{0:N0} units of ore", comet.OreUnits) : string.Format(c, "{0:N0} mol", comet.TotalMoles),
                    comet.CometKg / 1000.0, comet.Engines, comet.Engines == 1 ? "" : "s", comet.Cycles > 0 ? string.Format(c, ", {0} Cut cycles", comet.Cycles) : ""));
                string from = NodeName(comet.FromNode);
                string to = NodeName(comet.ToNode);
                switch (comet.State)
                {
                    case MapCometState.Waiting:
                        text.Append(string.Format(c, "waiting at {0}, {1:N0} mol of fuel", from, comet.FuelLeft));
                        break;
                    case MapCometState.Flying:
                        text.Append(string.Format(c, "flying, hop {0} of {1} from {2} to {3} at {4:0.0} %, {5:0.0} % of the route's {6:N1} delta-v, {7:N0} mol of fuel left; ",
                            comet.Hop + 1, comet.HopCount, from, to, comet.Progress * 100.0, comet.RouteShare * 100.0, comet.RouteDeltaV, comet.FuelLeft));
                        double left = comet.FlightSeconds - comet.FlownSeconds;
                        text.Append(comet.Arrives
                            ? string.Format(c, "reaches orbit in {0:N1} s by the rule", left)
                            : string.Format(c, "runs out of fuel {0:N1} s from now, short of orbit", left));
                        if (double.IsNaN(comet.StartClock))
                        {
                            text.Append(" (loaded, waiting for the clock)");
                        }
                        break;
                    case MapCometState.Arrived:
                        text.Append(string.Format(c, "in orbit at {0}, {1:N0} mol of fuel left", to, comet.FuelLeft));
                        break;
                    default:
                        text.Append(string.Format(c, "out of fuel for good, {0:0.0} % of the way from {1} to {2}, {3:0.0} % of the route", comet.Progress * 100.0, from, to, comet.RouteShare * 100.0));
                        break;
                }
            }
            return text.ToString();
        }

        internal static string SiteList(List<SpaceMapNode> sites)
        {
            if (sites.Count == 0)
            {
                return "No site has been discovered on the space map yet.";
            }
            StringBuilder text = new StringBuilder("Sites:");
            for (int i = 0; i < sites.Count; i++)
            {
                text.Append(string.Format(CultureInfo.InvariantCulture, " {0} {1};", i + 1, Name(sites[i])));
            }
            return text.ToString();
        }

        /// <summary>A site by its number in the list, its name or its template id. Null with why not.</summary>
        internal static SpaceMapNode SiteNamed(List<SpaceMapNode> sites, string typed, out string notFound)
        {
            notFound = null;
            if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                if (number >= 1 && number <= sites.Count)
                {
                    return sites[number - 1];
                }
                notFound = "There is no site " + typed + ". " + SiteList(sites);
                return null;
            }
            SpaceMapNode found = null;
            foreach (SpaceMapNode site in sites)
            {
                if (string.Equals(site.DisplayName, typed, StringComparison.OrdinalIgnoreCase) || string.Equals(site.Data?.Id, typed, StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null)
                    {
                        notFound = "More than one site is called " + typed + "; give its number. " + SiteList(sites);
                        return null;
                    }
                    found = site;
                }
            }
            if (found == null)
            {
                notFound = "No site is called " + typed + ". " + SiteList(sites);
            }
            return found;
        }

        internal static string Name(SpaceMapNode site)
        {
            string parent = site.ParentConnection?.Parent?.DisplayName;
            return site.DisplayName + (string.IsNullOrEmpty(parent) ? "" : " (by " + parent + ")");
        }

        internal static string NodeName(long referenceId) => SpaceMapNode.Get(referenceId)?.DisplayName ?? "?";

        /// <summary>The route's Δv from a site to Entry as the game's pathfinder gives it, for the answer to make.</summary>
        internal static bool RouteDeltaV(SpaceMapNode site, out double deltaV)
        {
            deltaV = 0.0;
            SpaceMapNode entry = SpaceMap.Current?.EntryNode;
            if (site == null || entry == null || !SpaceMapPathFinder.GetPath(site, entry, out List<Assets.Scripts.Serialization.NodeTransit> path) || path == null)
            {
                return false;
            }
            foreach (Assets.Scripts.Serialization.NodeTransit transit in path)
            {
                deltaV += transit.GetDistance();
            }
            return deltaV > 0.0;
        }
    }
}
