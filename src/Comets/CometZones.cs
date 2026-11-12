using System;
using System.Collections.Generic;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// What an impact does near where it lands (docs/COMET-ROCKETS.md, "Near the impact"): which zone a point
    /// is in, how much the fireball's heat burns, and where the falling rocks that hit come down. One distance
    /// per structure or player picks one row of one table; the pass that applies it is
    /// <see cref="CometZonePass"/>. Plain arithmetic with no game or Unity calls, so tools/PatchCheck runs
    /// <see cref="Check(CometZones)"/> outside the game. The parts are virtual so PatchCheck can hand the check
    /// wrong rules and see each caught.
    /// </summary>
    public class CometZones
    {
        public static readonly CometZones Shipped = new CometZones();

        /// <summary>
        /// The game's own heat damage: what a thing standing in lava takes each second
        /// (OcclusionManager.LavaDamage, 25 x the frame's seconds), the one rate the game burns anything at by
        /// where it is.
        /// </summary>
        public const double LavaBurnPerSecond = 25.0;

        /// <summary>
        /// A rock that hits is the game's own smallest explosion that damages anything: a suit bursting
        /// (SuitBase and AdvancedSuit, Explosion.Explode(200, position, 2.3)).
        /// </summary>
        public const double RockForce = 200.0;

        public const double RockRadiusMetres = 2.3;

        /// <summary>The cost rule's cap on a rock's explosion (docs/COMET-ROCKETS.md, "Cost rule").</summary>
        public const double MaxRockRadiusMetres = 3.0;

        /// <summary>
        /// How far from where it lands a rock damages anything: Explosion.Explode damages what lies inside a
        /// sphere of each whole metre below its radius, so 1 m for 2.3 m.
        /// </summary>
        public static double RockReachMetres => Math.Max(0, (int)RockRadiusMetres - 1);

        /// <summary>
        /// The ground is judged for rocks in squares this wide, one large grid cell: a square with a structure
        /// or a player in it is a place a rock can land on something. Twice the rock's reach, so one rock
        /// damages what is in one square and no more.
        /// </summary>
        public static double SpotMetres => 2.0 * RockReachMetres;

        /// <summary>Where the world has no gravity to speak of, rocks are taken to come down this long after the wave, as the show draws them.</summary>
        public const double WeightlessRockSeconds = 30.0;

        /// <summary>
        /// How far each zone reaches for a comet this size: ground zero by the world's setting, the
        /// fireball's heat as far as the fireball is drawn (its yield's law times the world's heat factor),
        /// the falling rocks by the world's setting.
        /// </summary>
        public virtual ZoneReach ReachFor(double moles, CelestialTuning tuning)
        {
            double groundZero = CelestialRules.Shipped.GroundZeroMetres(moles, tuning.GroundZeroMetresAt1e6);
            double heat = CometYield.Shipped.FireballLawMetres(CometYield.Shipped.Kilotons(moles)) * Math.Max(0.0, tuning.FireballHeatFactor);
            return new ZoneReach(groundZero, heat, groundZero * Math.Max(0.0, tuning.DebrisRadiusFactor));
        }

        /// <summary>
        /// The one zone a point this far from the impact is in, innermost first. A zone that a nearer one
        /// covers is empty: past ground zero the heat, past the heat the rocks, then only the wave, by its own
        /// rule (docs/COMET.md section 3).
        /// </summary>
        public virtual CometZone ZoneAt(double metres, ZoneReach reach)
        {
            double d = Math.Max(0.0, metres);
            if (d <= reach.GroundZero)
            {
                return CometZone.GroundZero;
            }
            if (d <= reach.Heat)
            {
                return CometZone.Fireball;
            }
            if (d <= reach.Debris)
            {
                return CometZone.Debris;
            }
            return CometZone.Wave;
        }

        /// <summary>
        /// The Burn damage the fireball's heat does to a structure facing the outside in the fireball, or to
        /// a player outdoors there: the game's lava rate for as long as the fireball takes to grow and its
        /// thermal pulse to end (<see cref="CometSizes.FireballSeconds"/>, ten times the pulse's peak), the
        /// same everywhere in it. Nothing outside the fireball.
        /// </summary>
        public virtual double HeatBurn(double metres, ZoneReach reach, CometSizes sizes)
        {
            return ZoneAt(metres, reach) == CometZone.Fireball ? LavaBurnPerSecond * Math.Max(0.0, sizes.FireballSeconds) : 0.0;
        }

        /// <summary>
        /// The chance a rock comes down on a place this far out: 1 at the inner edge of the falling rocks, and
        /// thinning as an ejecta blanket does, as the inverse cube of the distance (McGetchin, Settle and Head
        /// 1973), to its outer edge; none anywhere else.
        /// </summary>
        public virtual double RockChance(double metres, ZoneReach reach)
        {
            if (ZoneAt(metres, reach) != CometZone.Debris)
            {
                return 0.0;
            }
            double inner = reach.Inner;
            return inner > 0.0 ? Math.Min(1.0, Math.Pow(inner / Math.Max(inner, metres), 3.0)) : 1.0;
        }

        /// <summary>
        /// Seconds after the impact a rock comes down this far out: the flight of a 45 degree throw that
        /// lands there, sqrt(2 d / g), under the world's gravity; never before the wave gets there.
        /// </summary>
        public virtual double RockSeconds(double metres, double gravity)
        {
            double d = Math.Max(0.0, metres);
            double wave = d / CometRules.SpeedOfSound;
            double flight = gravity > 0.05 ? Math.Sqrt(2.0 * d / gravity) : wave + WeightlessRockSeconds;
            return Math.Max(wave, flight);
        }

        /// <summary>
        /// Which of the places the pass found something on get a rock, nearest first: each place in the falling
        /// rocks zone by its chance, drawn from the event's seed in that order, until <paramref name="cap"/>
        /// rocks have come down. Indices into <paramref name="spotMetres"/>.
        /// </summary>
        public virtual int[] Rocks(double[] spotMetres, ZoneReach reach, int seed, int cap)
        {
            List<int> hits = new List<int>();
            if (spotMetres == null || cap <= 0)
            {
                return hits.ToArray();
            }
            int[] order = new int[spotMetres.Length];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }
            Array.Sort(order, (a, b) => spotMetres[a] != spotMetres[b] ? spotMetres[a].CompareTo(spotMetres[b]) : a.CompareTo(b));
            Random random = new Random(seed);
            foreach (int i in order)
            {
                double chance = RockChance(spotMetres[i], reach);
                if (chance <= 0.0)
                {
                    continue;
                }
                if (random.NextDouble() < chance)
                {
                    hits.Add(i);
                    if (hits.Count >= cap)
                    {
                        break;
                    }
                }
            }
            return hits.ToArray();
        }

        private static readonly Lazy<string> ShippedVerdict = new Lazy<string>(() =>
        {
            try
            {
                return Check(Shipped);
            }
            catch (Exception e)
            {
                return "the zones check itself failed: " + e.Message;
            }
        });

        /// <summary>The shipped zones' own check, run once: null when they hold, else the first case that does not.</summary>
        public static string Verdict => ShippedVerdict.Value;

        /// <summary>
        /// The zones against docs/COMET-ROCKETS.md's table, the heat against the drawn fireball and the game's
        /// lava, and the rocks against their chance, their cap and the cost rule. Returns the first case that
        /// does not hold, or null.
        /// </summary>
        public static string Check(CometZones zones)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            CelestialTuning tuning = CelestialTuning.Defaults;
            CometYield yield = CometYield.Shipped;

            // ---- the reach of each zone, a million moles: ground zero past the strongest wall, the heat as far as
            // the fireball is drawn, the rocks to where ordinary windows break.
            ZoneReach million = zones.ReachFor(1e6, tuning);
            double law = yield.FireballLawMetres(yield.Kilotons(1e6));
            if (Math.Abs(million.GroundZero - 186.0) > 1e-9 || Math.Abs(million.Heat - law) > 1e-9 || Math.Abs(law - 625.0) > 1.0
                || Math.Abs(million.Debris - 186.0 * tuning.DebrisRadiusFactor) > 1e-9)
            {
                return string.Format(c, "a comet of a million moles reaches {0:0.#} m (ground zero), {1:0.#} m (the fireball's heat) and {2:0.#} m (the rocks), expected 186, the drawn fireball's {3:0.#} and {4:0.#}",
                    million.GroundZero, million.Heat, million.Debris, law, 186.0 * tuning.DebrisRadiusFactor);
            }
            foreach (var (metres, zone) in new[] { (0.0, CometZone.GroundZero), (186.0, CometZone.GroundZero), (187.0, CometZone.Fireball), (624.0, CometZone.Fireball),
                (626.0, CometZone.Debris), (1315.0, CometZone.Debris), (1316.0, CometZone.Wave), (20000.0, CometZone.Wave) })
            {
                CometZone got = zones.ZoneAt(metres, million);
                if (got != zone)
                {
                    return string.Format(c, "{0} m from a comet of a million moles is {1}, expected {2}", metres, got, zone);
                }
            }

            // ---- for every size and every setting's reach: the heat reaches exactly as far as the fireball is
            // drawn whenever the world's heat factor is 1 or more, and never farther; the zones nest.
            CelestialTuning hot = tuning.With(CelestialSettings.FireballHeatFactor, CelestialSettings.FireballHeatFactor.Range.Max);
            CelestialTuning cool = tuning.With(CelestialSettings.FireballHeatFactor, CelestialSettings.FireballHeatFactor.Range.Min);
            foreach (CelestialTuning t in new[] { tuning, hot, cool })
            {
                for (double moles = 1.0; moles <= 1e12; moles *= 3.0)
                {
                    ZoneReach reach = zones.ReachFor(moles, t);
                    CometSizes sizes = yield.For(moles, t);
                    bool exact = t.FireballHeatFactor >= 1.0 ? Math.Abs(reach.Heat - sizes.FireballMetres) <= 1e-9 * Math.Max(1.0, sizes.FireballMetres)
                        : reach.Heat < sizes.FireballMetres;
                    if (!exact || reach.Heat > sizes.FireballMetres + 1e-6)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol at heat factor {1}: the heat reaches {2:N0} m, the fireball is drawn {3:N0} m", moles, t.FireballHeatFactor, reach.Heat, sizes.FireballMetres);
                    }
                    if (!(reach.GroundZero >= 0.0) || !(reach.Debris >= reach.GroundZero) || Math.Abs(reach.Inner - Math.Max(reach.GroundZero, reach.Heat)) > 1e-9)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol: ground zero {1:N0} m, heat {2:N0} m, rocks {3:N0} m do not nest", moles, reach.GroundZero, reach.Heat, reach.Debris);
                    }
                    // Nowhere is in two zones, and the table is walked inside out.
                    CometZone last = CometZone.GroundZero;
                    for (double d = 0.0; d <= 1.2 * Math.Max(reach.Debris, reach.Heat) + 10.0; d += Math.Max(1.0, reach.Debris / 200.0))
                    {
                        CometZone z = zones.ZoneAt(d, reach);
                        if (z < last)
                        {
                            return string.Format(c, "a comet of {0:0.#E+0} mol: {1:N0} m out is {2}, inside the {3} it had already passed", moles, d, z, last);
                        }
                        last = z;
                    }
                }
            }

            // ---- the heat: the game's lava rate for the fireball's growth and thermal pulse, the same all through
            // the fireball and nothing outside it.
            foreach (double moles in new[] { 1e5, 1e6, 1e7, 1e9 })
            {
                ZoneReach reach = zones.ReachFor(moles, tuning);
                CometSizes sizes = yield.For(moles, tuning);
                double full = LavaBurnPerSecond * 10.0 * yield.ThermalPeakSeconds(sizes.Kilotons);
                foreach (double share in new[] { 0.0, 0.5, 0.999, 1.001, 2.0 })
                {
                    double d = reach.GroundZero + share * (reach.Heat - reach.GroundZero);
                    double burn = zones.HeatBurn(d, reach, sizes);
                    bool inside = zones.ZoneAt(d, reach) == CometZone.Fireball;
                    if (inside ? Math.Abs(burn - full) > 1e-9 * full : burn != 0.0)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol burns {1:0.###} at {2:N0} m ({3}), expected {4:0.###}: the game's lava rate, 25 a second, for the fireball's growth, inside the fireball and nowhere else",
                            moles, burn, d, zones.ZoneAt(d, reach), inside ? full : 0.0);
                    }
                }
            }
            ZoneReach ten = zones.ReachFor(1e7, tuning);
            double tenBurn = zones.HeatBurn(0.5 * (ten.GroundZero + ten.Heat), ten, yield.For(1e7, tuning));
            if (Math.Abs(tenBurn - 307.5) > 0.5)
            {
                return string.Format(c, "a comet of ten million moles burns {0:0.#} in its fireball, expected 307.5 (25 a second for 12.3 s)", tenBurn);
            }

            // ---- the rocks: the cost rule's explosion, one per square, its chance, its time and its cap.
            if (RockRadiusMetres > MaxRockRadiusMetres || RockReachMetres < 1.0 || Math.Abs(SpotMetres - 2.0 * RockReachMetres) > 1e-12)
            {
                return string.Format(c, "a rock's explosion is {0} m across a {1} m reach in {2} m squares: at most {3} m, reaching at least 1 m, one square a rock", RockRadiusMetres, RockReachMetres, SpotMetres, MaxRockRadiusMetres);
            }
            double inner = million.Inner;
            if (Math.Abs(zones.RockChance(inner + 1e-6, million) - 1.0) > 1e-6 || Math.Abs(zones.RockChance(2.0 * inner, million) - 0.125) > 1e-9
                || zones.RockChance(inner - 1.0, million) != 0.0 || zones.RockChance(million.Debris + 1.0, million) != 0.0 || zones.RockChance(million.GroundZero, million) != 0.0)
            {
                return string.Format(c, "the chance of a rock is {0:0.###} at the inner edge of the falling rocks, {1:0.###} twice as far out, {2:0.###} inside it and {3:0.###} past its outer edge: expected 1, an eighth (the inverse cube), 0 and 0",
                    zones.RockChance(inner + 1e-6, million), zones.RockChance(2.0 * inner, million), zones.RockChance(inner - 1.0, million), zones.RockChance(million.Debris + 1.0, million));
            }
            if (Math.Abs(zones.RockSeconds(1000.0, 3.7) - Math.Sqrt(2000.0 / 3.7)) > 1e-9 || zones.RockSeconds(100000.0, 3.7) < 100000.0 / CometRules.SpeedOfSound
                || Math.Abs(zones.RockSeconds(1000.0, 0.0) - (1000.0 / CometRules.SpeedOfSound + WeightlessRockSeconds)) > 1e-9)
            {
                return "a rock does not come down when a 45 degree throw lands there, or comes down before the wave";
            }
            int cap = (int)tuning.MaxDamagingRocks;
            double[] crowd = new double[200];
            for (int i = 0; i < crowd.Length; i++)
            {
                crowd[i] = inner + 1.0 + 0.01 * i;
            }
            int[] capped = zones.Rocks(crowd, million, 12345, cap);
            bool nearest = capped.Length == cap;
            for (int i = 0; i < capped.Length && nearest; i++)
            {
                nearest = capped[i] < cap + 20;
            }
            if (!nearest)
            {
                return string.Format(c, "200 places at the inner edge of the falling rocks get {0} rocks, expected the cap of {1}, nearest first", capped.Length, cap);
            }
            double[] outside = { 0.0, million.GroundZero, 0.5 * (million.GroundZero + million.Heat), million.Debris + 1.0, 5.0 * million.Debris };
            if (zones.Rocks(outside, million, 7, 1000).Length != 0 || zones.Rocks(crowd, million, 7, 0).Length != 0 || zones.Rocks(new double[0], million, 7, cap).Length != 0)
            {
                return "a rock comes down outside the falling rocks zone, past a cap of none, or where nothing is";
            }
            double[] spread = new double[4000];
            for (int i = 0; i < spread.Length; i++)
            {
                spread[i] = 2.0 * inner;
            }
            int many = zones.Rocks(spread, million, 99, spread.Length).Length;
            double expected = spread.Length / 8.0;
            if (Math.Abs(many - expected) > 4.0 * Math.Sqrt(expected * 7.0 / 8.0))
            {
                return string.Format(c, "4,000 places twice as far out as the inner edge get {0} rocks, expected about {1:0} (an eighth)", many, expected);
            }
            int[] again = zones.Rocks(spread, million, 99, spread.Length);
            if (again.Length != many)
            {
                return "the same places and the same seed do not get the same rocks";
            }
            if (!(cap > 0) || cap > 78)
            {
                return "the cap on rocks that hit is not a share of the 78 the show draws falling";
            }
            return null;
        }
    }

    /// <summary>How far each zone around an impact reaches, metres. A plain struct: net472 has no IsExternalInit.</summary>
    public readonly struct ZoneReach
    {
        public readonly double GroundZero;

        /// <summary>The fireball's heat.</summary>
        public readonly double Heat;

        /// <summary>The falling rocks' outer edge.</summary>
        public readonly double Debris;

        public ZoneReach(double groundZero, double heat, double debris)
        {
            GroundZero = groundZero;
            Heat = heat;
            Debris = debris;
        }

        /// <summary>Where the falling rocks start: past ground zero and the fireball.</summary>
        public double Inner => Math.Max(GroundZero, Heat);

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "ground zero {0:0.#} m, fireball {1:0.#} m, falling rocks {2:0.#} m", GroundZero, Heat, Debris);
        }
    }
}
