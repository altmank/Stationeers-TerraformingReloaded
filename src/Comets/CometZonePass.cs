using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The one pass over the world an impact makes (docs/COMET-ROCKETS.md, "Cost rule"): every structure's
    /// distance once, then every player's, each picking its row of <see cref="CometZones"/>' table, and each
    /// effect timed for when the wave gets there (a rock, for when it comes down). Ground zero destroys
    /// whatever its limit; the fireball burns what faces the outside; the wave breaks what is over its limit
    /// and makes windows near it creak; in the falling rocks, a place the pass found something on may get a
    /// rock, the game's own small explosion. Every machine makes the same list from its own copy of the world
    /// for the sounds; only the host changes anything, and the game sends the damage, the removals, the
    /// deaths and its explosions to everyone. Main thread only.
    /// </summary>
    internal static class CometZonePass
    {
        /// <summary>Something that happens a set time after the impact.</summary>
        internal readonly struct Timed
        {
            public readonly float At;
            public readonly Action Act;

            public Timed(float at, Action act)
            {
                At = at;
                Act = act;
            }
        }

        /// <summary>
        /// Makes the pass for one impact and returns what it schedules. <paramref name="host"/> decides whether
        /// anything is changed; on every machine the sounds of what breaks, creaks or is destroyed are played,
        /// by <paramref name="sound"/>, at the structure, when the wave gets there.
        /// </summary>
        public static List<Timed> Run(CometEvent comet, Vector3 impact, double moles, CometSizes sizes, CelestialTuning tuning, CometRules rules, bool host,
            Action<int, Vector3> sound, out string log)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            Stopwatch watch = Stopwatch.StartNew();
            CometZones zones = CometZones.Shipped;
            ZoneReach reach = zones.ReachFor(moles, tuning);
            double burn = zones.HeatBurn(0.5 * (reach.GroundZero + reach.Heat), reach, sizes);
            if (reach.Heat <= reach.GroundZero)
            {
                burn = 0.0;
            }
            float gravity = Physics.gravity.magnitude;
            List<Timed> timed = new List<Timed>();
            CometZoneReport report = new CometZoneReport
            {
                Sequence = comet.Sequence,
                RocketComet = comet.RocketComet,
                Moles = moles,
                ImpactX = impact.x,
                ImpactY = impact.y,
                ImpactZ = impact.z,
                ImpactClock = comet.ImpactClock,
                Reach = reach,
                Burn = burn,
                RockCap = Math.Max(0, (int)Math.Round(tuning.MaxDamagingRocks)),
                Seed = comet.Seed,
                Host = host,
            };

            // Places a rock can land on something, by square; the first thing the pass finds in a square stands for it.
            Dictionary<long, int> spotOf = new Dictionary<long, int>();
            List<Spot> spots = new List<Spot>();
            double square = CometZones.SpotMetres;

            int seen = 0;
            int destroyed = 0, burned = 0, breaks = 0, creaks = 0;
            GridController.AllStructuresPool.ForEach((Action<Structure>)(structure =>
            {
                if (structure == null || structure.IsBeingDestroyed)
                {
                    return;
                }
                seen++;
                Vector3 at = structure.CenterPosition;
                float distance = Vector3.Distance(impact, at);
                CometZone zone = zones.ZoneAt(distance, reach);
                bool destructible = structure.DamageState != null && !structure.Indestructable;
                double peak = rules.PeakKPa(moles, distance);
                double limit = structure.maxPressureDelta;
                bool? outside = null;
                bool FacesOutside() => outside ?? (outside = CometWave.FacesOutside(structure)).Value;

                if (zone == CometZone.GroundZero)
                {
                    if (!destructible)
                    {
                        return;
                    }
                    destroyed++;
                    report.Add(structure, distance, zone, peak, limit, false, false, 0.0, true);
                    Structure s = structure;
                    int fail = CometWave.FailSound(s);
                    timed.Add(new Timed((float)rules.ArrivalSeconds(distance), () =>
                    {
                        if (host && Destroy(s))
                        {
                            report.Destroyed++;
                        }
                        sound(fail, at);
                    }));
                    return;
                }

                double heat = zone == CometZone.Fireball && destructible && burn > 0.0 && FacesOutside() ? burn : 0.0;
                bool breakable = limit > 0.0 && !structure.Indestructable && !structure.IsBroken && !structure.CanAirPass;
                bool window = CometWave.IsGlass(structure) && structure is Wall;
                bool cracks = breakable && rules.Breaks(peak, limit);
                bool creak = breakable && !cracks && window && rules.Creaks(peak, limit);
                if ((cracks || creak) && !FacesOutside())
                {
                    cracks = creak = false;
                }
                if (zone == CometZone.Debris && host && destructible && FacesOutside())
                {
                    long key = SquareKey(at, square);
                    if (!spotOf.ContainsKey(key))
                    {
                        spotOf[key] = spots.Count;
                        spots.Add(new Spot(structure, null, at, distance));
                    }
                }
                if (heat <= 0.0 && !cracks && !creak)
                {
                    return;
                }
                burned += heat > 0.0 ? 1 : 0;
                breaks += cracks ? 1 : 0;
                creaks += creak ? 1 : 0;
                report.Add(structure, distance, zone, peak, limit, cracks, creak, heat, false);
                Structure hit = structure;
                int noise = cracks ? CometWave.FailSound(hit) : creak ? CometWave.StressSound : 0;
                timed.Add(new Timed((float)rules.ArrivalSeconds(distance), () =>
                {
                    if (host && heat > 0.0 && Burn(hit, heat))
                    {
                        report.Burned++;
                    }
                    if (host && cracks && CometWave.Break(hit))
                    {
                        report.Broken++;
                    }
                    if (noise != 0)
                    {
                        sound(noise, at);
                    }
                }));
            }));

            // The players, on the host: ground zero kills, the fireball burns a player outdoors, and the falling
            // rocks may come down on one. The wave's own push and suit damage are the show's, as before.
            int people = 0;
            if (host)
            {
                foreach (Human human in Human.AllHumans)
                {
                    if (human == null || human.OrganBrain == null || !human.OrganBrain.IsOnline || human.IsDead)
                    {
                        continue;
                    }
                    people++;
                    Vector3 at = human.transform.position;
                    float distance = Vector3.Distance(at, impact);
                    CometZone zone = zones.ZoneAt(distance, reach);
                    Human who = human;
                    if (zone == CometZone.GroundZero)
                    {
                        report.Players.Add(string.Format(c, "{0} at {1:0.#} m: ground zero, dies", who.DisplayName, distance));
                        timed.Add(new Timed((float)rules.ArrivalSeconds(distance), () =>
                        {
                            if (Kill(who))
                            {
                                Debug.Log("[TR comet] " + who.DisplayName + " was at ground zero");
                            }
                        }));
                    }
                    else if (zone == CometZone.Fireball && burn > 0.0 && CometWave.Outdoors(who))
                    {
                        report.Players.Add(string.Format(c, "{0} at {1:0.#} m: in the fireball, burned {2:0.#}", who.DisplayName, distance, burn));
                        timed.Add(new Timed((float)rules.ArrivalSeconds(distance), () =>
                        {
                            if (BurnPlayer(who, burn))
                            {
                                Debug.Log(string.Format(CultureInfo.InvariantCulture, "[TR comet] the fireball burned {0} by {1:0.#}", who.DisplayName, burn));
                            }
                        }));
                    }
                    else if (zone == CometZone.Debris && CometWave.Outdoors(who))
                    {
                        long key = SquareKey(at, square);
                        if (!spotOf.ContainsKey(key))
                        {
                            spotOf[key] = spots.Count;
                            spots.Add(new Spot(null, who, at, distance));
                        }
                    }
                }
            }

            // The rocks: by their chance, nearest first, up to the cap; each the game's own small explosion where it comes down.
            if (host && spots.Count > 0)
            {
                double[] metres = new double[spots.Count];
                for (int i = 0; i < spots.Count; i++)
                {
                    metres[i] = spots[i].Distance;
                    report.SpotMetres.Add(spots[i].Distance);
                }
                foreach (int i in zones.Rocks(metres, reach, comet.Seed, report.RockCap))
                {
                    Spot spot = spots[i];
                    float when = (float)zones.RockSeconds(spot.Distance, gravity);
                    report.Rocks.Add(new CometZoneReport.Rock(spot.Structure != null ? spot.Structure.ReferenceId : 0L, spot.Human != null ? spot.Human.DisplayName : null,
                        spot.At.x, spot.At.y, spot.At.z, spot.Distance, when));
                    timed.Add(new Timed(when, () =>
                    {
                        Vector3 down = spot.Structure != null && spot.Structure ? spot.Structure.Position : spot.Human != null && spot.Human ? spot.Human.transform.position : spot.At;
                        global::Explosion.Explode((float)CometZones.RockForce, down, (float)CometZones.RockRadiusMetres);
                        report.RocksLanded++;
                    }));
                }
            }

            watch.Stop();
            report.Structures = seen;
            report.People = people;
            report.Milliseconds = watch.Elapsed.TotalMilliseconds;
            report.LastSeconds = 0.0;
            foreach (Timed t in timed)
            {
                report.LastSeconds = Math.Max(report.LastSeconds, t.At);
            }
            CometZoneReport.Publish(report);
            log = string.Format(c, "zones in one pass over {0} structures and {1} players in {2:0.###} ms: {3}; {4} destroyed at ground zero, {5} burned {6:0.#} each in the fireball, {7} break and {8} creak in the wave, {9} rocks of at most {10} on {11} places in the falling rocks; the last {12:0.0} s after the impact",
                seen, people, report.Milliseconds, reach, destroyed, burned, burn, breaks, creaks, report.Rocks.Count, report.RockCap, spots.Count, report.LastSeconds);
            return timed;
        }

        private static long SquareKey(Vector3 at, double square)
        {
            long x = (long)Math.Floor(at.x / square);
            long y = (long)Math.Floor(at.y / square);
            long z = (long)Math.Floor(at.z / square);
            return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
        }

        /// <summary>
        /// Gone whatever its limit: the game's own removal of a structure its damage destroys
        /// (Structure.OnDamageDestroyed), which leaves its wreckage where it has any and is sent to every player.
        /// </summary>
        private static bool Destroy(Structure structure)
        {
            if (structure == null || structure.IsBeingDestroyed || structure.DamageState == null || structure.Indestructable)
            {
                return false;
            }
            structure.OnDamageDestroyed();
            return true;
        }

        /// <summary>The fireball's heat: Burn damage through the game's own damage, which syncs and breaks or removes what it takes past its health.</summary>
        private static bool Burn(Structure structure, double amount)
        {
            if (structure == null || structure.IsBeingDestroyed || structure.DamageState == null || structure.Indestructable)
            {
                return false;
            }
            structure.DamageState.Damage(ChangeDamageType.Increment, (float)amount, DamageUpdateType.Burn);
            return true;
        }

        /// <summary>Ground zero: the player's whole health as Burn, the game's own death by damage. A player just respawned is spared.</summary>
        private static bool Kill(Human human)
        {
            if (human == null || human.IsDead || human.Invulnerability > 0f || human.DamageState == null)
            {
                return false;
            }
            human.DamageState.Damage(ChangeDamageType.Set, human.DamageState.MaxDamage, DamageUpdateType.Burn);
            return true;
        }

        /// <summary>
        /// The fireball's heat on a player outdoors, as the game burns one standing in lava (Human.ApplyLavaDamage):
        /// the suit and the helmet, each by as much as it lets lava burn it, or the body when there is no suit.
        /// </summary>
        private static bool BurnPlayer(Human human, double amount)
        {
            if (human == null || human.IsDead || human.Invulnerability > 0f || !CometWave.Outdoors(human))
            {
                return false;
            }
            float lava = OcclusionManager.LavaDamage;
            if (human.Suit != null && human.HeadAsSpaceHelmet != null)
            {
                DynamicThing suit = human.Suit.AsThing as DynamicThing;
                float suitShare = suit != null && lava > 0f ? suit.LavaDamage / lava : 1f;
                float helmetShare = lava > 0f ? human.HeadAsSpaceHelmet.LavaDamage / lava : 1f;
                human.Suit.AsThing.DamageState?.Damage(ChangeDamageType.Increment, (float)amount * suitShare, DamageUpdateType.Burn);
                human.HeadAsSpaceHelmet.DamageState?.Damage(ChangeDamageType.Increment, (float)amount * helmetShare, DamageUpdateType.Burn);
                return true;
            }
            human.DamageState?.Damage(ChangeDamageType.Increment, (float)amount, DamageUpdateType.Burn);
            return true;
        }

        private readonly struct Spot
        {
            public readonly Structure Structure;
            public readonly Human Human;
            public readonly Vector3 At;
            public readonly double Distance;

            public Spot(Structure structure, Human human, Vector3 at, double distance)
            {
                Structure = structure;
                Human = human;
                At = at;
                Distance = distance;
            }
        }
    }

    /// <summary>
    /// What the last impact's pass found and did on this machine, for the log and tools/LiveCheck: the reach of
    /// each zone, every structure it acts on with its zone and what it does to it, the places in the falling
    /// rocks and the rocks that came down, how many it went over and how long it took.
    /// </summary>
    public sealed class CometZoneReport
    {
        private static readonly object Gate = new object();
        private static CometZoneReport _last;

        public int Sequence;

        /// <summary>The rocket comet the impact is of; 0 for a console comet.</summary>
        public int RocketComet;

        public double Moles;
        public double ImpactX;
        public double ImpactY;
        public double ImpactZ;
        public double ImpactClock;
        public ZoneReach Reach;

        /// <summary>The Burn damage the fireball does to each structure and player in it.</summary>
        public double Burn;

        public int RockCap;
        public int Seed;
        public bool Host;
        public int Structures;
        public int People;
        public double Milliseconds;

        /// <summary>Seconds after the impact the last of what the pass scheduled happens.</summary>
        public double LastSeconds;

        // Counted as the scheduled effects happen, on the host.
        public int Destroyed;
        public int Burned;
        public int Broken;
        public int RocksLanded;

        public readonly List<Entry> Entries = new List<Entry>();
        public readonly List<double> SpotMetres = new List<double>();
        public readonly List<Rock> Rocks = new List<Rock>();
        public readonly List<string> Players = new List<string>();

        /// <summary>The last impact's report on this machine, or null.</summary>
        public static CometZoneReport Last
        {
            get
            {
                lock (Gate)
                {
                    return _last;
                }
            }
        }

        internal static void Publish(CometZoneReport report)
        {
            lock (Gate)
            {
                _last = report;
            }
        }

        /// <summary>The impact event in force on this machine: its sequence, where and when it lands and its size; false with none. For tools/LiveCheck.</summary>
        public static bool Current(out int sequence, out Vector3 impact, out double impactClock, out double moles)
        {
            CometEvent current = CometDirector.Current;
            sequence = current?.Sequence ?? 0;
            impact = current?.Impact ?? Vector3.zero;
            impactClock = current?.ImpactClock ?? double.NaN;
            moles = current?.Plan?.Moles ?? double.NaN;
            return current != null;
        }

        internal void Add(Structure structure, double distance, CometZone zone, double peak, double limit, bool breaks, bool creaks, double burn, bool destroyed)
        {
            Entries.Add(new Entry(structure.ReferenceId, structure.PrefabName, distance, zone, peak, limit, breaks, creaks, burn, destroyed));
        }

        /// <summary>One structure the pass acts on. A plain struct: net472 has no IsExternalInit.</summary>
        public readonly struct Entry
        {
            public readonly long ReferenceId;
            public readonly string Prefab;
            public readonly double Distance;
            public readonly CometZone Zone;
            public readonly double PeakKPa;
            public readonly double LimitKPa;
            public readonly bool Breaks;
            public readonly bool Creaks;
            public readonly double Burn;
            public readonly bool Destroyed;

            public Entry(long referenceId, string prefab, double distance, CometZone zone, double peakKPa, double limitKPa, bool breaks, bool creaks, double burn, bool destroyed)
            {
                ReferenceId = referenceId;
                Prefab = prefab;
                Distance = distance;
                Zone = zone;
                PeakKPa = peakKPa;
                LimitKPa = limitKPa;
                Breaks = breaks;
                Creaks = creaks;
                Burn = burn;
                Destroyed = destroyed;
            }
        }

        /// <summary>A rock that comes down on something: the structure it lands on (0 for a player), where, how far out and when.</summary>
        public readonly struct Rock
        {
            public readonly long Structure;
            public readonly string Player;
            public readonly double X;
            public readonly double Y;
            public readonly double Z;
            public readonly double Distance;
            public readonly double Seconds;

            public Rock(long structure, string player, double x, double y, double z, double distance, double seconds)
            {
                Structure = structure;
                Player = player;
                X = x;
                Y = y;
                Z = z;
                Distance = distance;
                Seconds = seconds;
            }
        }
    }
}
