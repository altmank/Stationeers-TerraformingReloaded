using System;
using System.Globalization;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// A floor and a ceiling on what a running weather event may do to the planet temperature. Only
    /// the event's own share is limited: a storm can never take the planet below the floor nor above
    /// the ceiling, a planet already below the floor (or above the ceiling) without the storm stays
    /// where it is, and nothing here ever moves the planet further than the storm itself would. With
    /// neither bound set the temperature is the game's, bit for bit. docs/STORMS.md has the rule.
    ///
    /// Immutable and published whole (<see cref="Effective.StormBounds"/>), because the temperature
    /// formula reads it on atmosphere worker threads while a console command can change it: both
    /// bounds come from one setting, never half of an old one.
    /// </summary>
    public sealed class StormBounds
    {
        public static readonly StormBounds Off = new StormBounds(null, null);

        public StormBounds(double? floorKelvin, double? ceilingKelvin)
        {
            FloorKelvin = floorKelvin;
            CeilingKelvin = ceilingKelvin;
        }

        /// <summary>Coldest a weather event may take the planet, in kelvin. Null is no floor.</summary>
        public double? FloorKelvin { get; }

        /// <summary>Hottest a weather event may take the planet, in kelvin. Null is no ceiling.</summary>
        public double? CeilingKelvin { get; }

        public bool IsOff => !FloorKelvin.HasValue && !CeilingKelvin.HasValue;

        public StormBounds WithFloor(double? kelvin) => new StormBounds(kelvin, CeilingKelvin);

        public StormBounds WithCeiling(double? kelvin) => new StormBounds(FloorKelvin, kelvin);

        /// <summary>
        /// The planet temperature with the event's share limited. <paramref name="withEvent"/> is the
        /// temperature including the event, <paramref name="eventKelvin"/> what the event added to it
        /// (negative for a cold snap). The result always lies between the temperature without the
        /// event and the temperature with it.
        /// </summary>
        public double Limit(double withEvent, double eventKelvin)
        {
            if (IsOff || eventKelvin == 0.0 || double.IsNaN(eventKelvin) || double.IsInfinity(eventKelvin)
                || double.IsNaN(withEvent) || double.IsInfinity(withEvent))
            {
                return withEvent;
            }
            double without = withEvent - eventKelvin;
            double kelvin = withEvent;
            if (FloorKelvin is double floor)
            {
                kelvin = Math.Max(kelvin, Math.Min(without, floor));
            }
            if (CeilingKelvin is double ceiling)
            {
                kelvin = Math.Min(kelvin, Math.Max(without, ceiling));
            }
            return kelvin;
        }

        /// <summary>
        /// The rule worked through by hand and swept across a grid. Returns the first case that does
        /// not hold, or null. <paramref name="limit"/> stands in for <see cref="Limit"/> so
        /// tools/PatchCheck can hand it wrong rules and see each one caught.
        /// </summary>
        public static string Check(Func<StormBounds, double, double, double> limit = null)
        {
            limit = limit ?? ((bounds, withEvent, eventKelvin) => bounds.Limit(withEvent, eventKelvin));
            CultureInfo c = CultureInfo.InvariantCulture;
            StormBounds floor = new StormBounds(228.0, null);
            StormBounds ceiling = new StormBounds(null, 320.0);

            (StormBounds Bounds, double Without, double Event, double Expected, string What)[] cases =
            {
                (Off, 300.0, -700.0, -400.0, "with no bounds a storm acts in full"),
                (floor, 300.0, -700.0, 228.0, "a cold snap stops at the floor"),
                (floor, 300.0, -50.0, 250.0, "a cold snap that stays above the floor is left alone"),
                (floor, 200.0, -700.0, 200.0, "a planet already below the floor is not cooled further by the storm"),
                (floor, 200.0, 50.0, 250.0, "a warm storm on a planet below the floor is left alone"),
                (floor, 300.0, 0.0, 300.0, "no storm offset, nothing to limit"),
                (ceiling, 300.0, 150.0, 320.0, "a heat storm stops at the ceiling"),
                (ceiling, 350.0, 100.0, 350.0, "a planet already above the ceiling is not warmed further by the storm"),
                (ceiling, 300.0, -100.0, 200.0, "a cold storm under a ceiling is left alone"),
                (new StormBounds(228.0, 320.0), 300.0, -700.0, 228.0, "both bounds: a cold snap stops at the floor"),
                (new StormBounds(228.0, 320.0), 300.0, 150.0, 320.0, "both bounds: a heat storm stops at the ceiling"),
                (new StormBounds(400.0, 300.0), 350.0, -100.0, 350.0, "a floor above the ceiling still never moves the planet past where it is without the storm"),
            };
            foreach (var (bounds, without, eventKelvin, expected, what) in cases)
            {
                double got = limit(bounds, without + eventKelvin, eventKelvin);
                if (Math.Abs(got - expected) > 1e-9)
                {
                    return string.Format(c, "{0}: expected {1} K, got {2} K", what, expected, got);
                }
            }
            if (!double.IsNaN(limit(floor, double.NaN, -700.0)) || limit(floor, 100.0, double.NaN) != 100.0)
            {
                return "a temperature or offset that is not a number is not passed through untouched";
            }

            // The sweep: every combination of planet, storm and bound, against the rule's invariants.
            double?[] floors = { null, 0.0, 150.0, 228.0, 300.0, 1000.0 };
            double?[] ceilings = { null, 100.0, 228.0, 320.0, 600.0, 1500.0 };
            foreach (double? f in floors)
            {
                foreach (double? ce in ceilings)
                {
                    StormBounds bounds = new StormBounds(f, ce);
                    for (double without = 0.0; without <= 1500.0; without += 37.5)
                    {
                        for (double eventKelvin = -800.0; eventKelvin <= 800.0; eventKelvin += 25.0)
                        {
                            double withEvent = without + eventKelvin;
                            double got = limit(bounds, withEvent, eventKelvin);
                            string at = string.Format(c, " (floor {0}, ceiling {1}, planet {2} K, storm {3:+0;-0;0} K, got {4} K)",
                                f.HasValue ? f.Value.ToString(c) : "none", ce.HasValue ? ce.Value.ToString(c) : "none", without, eventKelvin, got);
                            if (got < Math.Min(without, withEvent) - 1e-9 || got > Math.Max(without, withEvent) + 1e-9)
                            {
                                return "the result is outside the span between the planet without the storm and with it" + at;
                            }
                            if (f.HasValue && got < Math.Min(without, f.Value) - 1e-9)
                            {
                                return "the storm took the planet below the floor" + at;
                            }
                            if (ce.HasValue && got > Math.Max(without, ce.Value) + 1e-9)
                            {
                                return "the storm took the planet above the ceiling" + at;
                            }
                            bool inside = (!f.HasValue || withEvent >= f.Value) && (!ce.HasValue || withEvent <= ce.Value);
                            if (inside && got != withEvent)
                            {
                                return "a storm that stays within the bounds was changed" + at;
                            }
                            if (!f.HasValue && !ce.HasValue && got != withEvent)
                            {
                                return "with no bounds the temperature is not the game's own" + at;
                            }
                        }
                    }
                }
            }
            return null;
        }
    }
}
