using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// terraform comet &lt;mol&gt; &lt;gas&gt;[:&lt;share&gt;] ... [turn=&lt;deg&gt;] [range=&lt;m&gt;] [K=&lt;kelvin&gt;] [preview]
    /// and terraform comet stop. A prototype: the comet is called down from the console, not found
    /// and sent by a rocket.
    /// </summary>
    internal static class CometVerb
    {
        public const string Usage = "terraform comet <mol> <gas>[:<share>] [<gas>[:<share>] ...] [turn=<deg>] [range=<m>] [K=<kelvin>] [preview] | terraform comet stop. "
            + "<mol> is everything the comet brings, for the whole planet; each gas takes its share of it (equal shares if none are given). "
            + "It comes down where you are looking, turn=<deg> to the right of it (negative for left), range=<m> away; never nearer to any structure or player than is safe for its size, which is also where it lands if range is left out. "
            + "K=<kelvin> is the temperature its gas arrives at; left out, the planet's own. preview shows it and changes nothing. Host only. "
            + "Example: terraform comet 1e6 CarbonDioxide:60 Water:25 NitrousOxide:15";

        private const double MaxMoles = 1e12;

        public static string Run(string[] args, Func<string, Chemistry.GasType?> gasNamed, Func<string> gasNames)
        {
            if (NetworkManager.IsClient)
            {
                return "Can only be run on the server";
            }
            if (args.Length > 1 && args[1].ToLowerInvariant() == "stop")
            {
                return CometDirector.CallOff() ? "Comet called off." : "No comet is on its way.";
            }
            if (args.Length < 3)
            {
                return Usage + " Gases: " + gasNames() + ".";
            }
            string broken = CometRules.Verdict ?? CometGround.Verdict;
            if (broken != null)
            {
                return "Comets are off: the rules they are judged by did not pass their own check (" + broken + "). Please report this with the log.";
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            if (!double.TryParse(args[1], NumberStyles.Float, c, out double moles) || double.IsNaN(moles) || !(moles > 0.0) || moles > MaxMoles)
            {
                return string.Format(c, "'{0}' is not an amount. Give moles for the whole planet, more than 0 and up to {1:0.#####E+0}.", args[1], MaxMoles);
            }

            CometPlan plan = new CometPlan { Moles = moles };
            List<Chemistry.GasType> types = new List<Chemistry.GasType>();
            List<double> shares = new List<double>();
            for (int i = 2; i < args.Length; i++)
            {
                string arg = args[i];
                string lower = arg.ToLowerInvariant();
                if (lower == "preview")
                {
                    plan.Preview = true;
                    continue;
                }
                int equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    string key = lower.Substring(0, equals);
                    string value = arg.Substring(equals + 1);
                    if (!double.TryParse(value, NumberStyles.Float, c, out double number) || double.IsNaN(number) || double.IsInfinity(number))
                    {
                        return "'" + arg + "': '" + value + "' is not a number.";
                    }
                    switch (key)
                    {
                        case "turn":
                            if (Math.Abs(number) > 180.0)
                            {
                                return "turn is degrees to the right of where you are looking, from -180 to 180.";
                            }
                            plan.TurnDegrees = (float)number;
                            break;
                        case "range":
                            if (number < 300.0 || number > 20000.0)
                            {
                                return "range is metres from you to the impact, from 300 to 20,000.";
                            }
                            plan.Range = (float)number;
                            break;
                        case "k":
                            if (!(number > 0.0) || number > 10000.0)
                            {
                                return "K is the temperature the comet's gas arrives at, more than 0 and up to 10,000 kelvin.";
                            }
                            plan.Kelvin = number;
                            break;
                        default:
                            return "'" + arg + "' is not something a comet takes. " + Usage;
                    }
                    continue;
                }
                string name = arg;
                double share = 1.0;
                int colon = arg.IndexOf(':');
                if (colon >= 0)
                {
                    name = arg.Substring(0, colon);
                    string typed = arg.Substring(colon + 1);
                    if (!double.TryParse(typed, NumberStyles.Float, c, out share) || double.IsNaN(share) || double.IsInfinity(share) || !(share > 0.0))
                    {
                        return "'" + arg + "': the share after the colon has to be a number more than 0.";
                    }
                }
                if (string.Equals(name, "Volatiles", StringComparison.OrdinalIgnoreCase))
                {
                    name = "Methane";
                }
                Chemistry.GasType? type = gasNamed(name);
                if (type == null)
                {
                    return "'" + name + "' is not a gas the planet can hold. Gases: " + gasNames() + ".";
                }
                int already = types.IndexOf(type.Value);
                if (already >= 0)
                {
                    shares[already] += share;
                }
                else
                {
                    types.Add(type.Value);
                    shares.Add(share);
                }
            }
            if (types.Count == 0)
            {
                return "Name at least one gas for the comet to bring. " + Usage;
            }
            double total = 0.0;
            foreach (double share in shares)
            {
                total += share;
            }
            plan.Types = types.ToArray();
            plan.Shares = shares.ConvertAll(s => s / total).ToArray();

            string refused = CometDirector.Launch(plan, out CometSite site);
            if (refused != null)
            {
                return refused;
            }
            StringBuilder text = new StringBuilder();
            text.Append(string.Format(c, "Comet inbound, {0:N0} m away, {1:0}\u00b0 to the {2} of where you are looking, in {3:0} s. ",
                site.Range, Math.Abs(site.Bearing), site.Bearing < 0f ? "left" : "right", CometEvent.LeadSeconds + CometEvent.ApproachSeconds));
            if (site.Moved != null)
            {
                text.Append(site.Moved).Append(' ');
            }
            text.Append(string.Format(c, "It brings {0:N0} mol: ", moles));
            for (int i = 0; i < plan.Types.Length; i++)
            {
                text.Append(string.Format(c, "{0}{1} {2:0.#}%", i > 0 ? ", " : "", plan.Types[i], plan.Shares[i] * 100.0));
            }
            text.Append(plan.Preview
                ? ". Preview: nothing will reach the planet."
                : plan.Kelvin.HasValue
                    ? string.Format(c, ", arriving at {0:0.#} K.", plan.Kelvin.Value)
                    : ", at the planet's own temperature.");
            return text.ToString();
        }
    }
}
