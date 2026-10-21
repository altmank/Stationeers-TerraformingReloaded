using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Serialization;
using BepInEx.Logging;
using Networks;
using Objects.Rockets;
using TerraformingReloaded.Comets;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// -CometZones: what an impact does near where it lands (docs/COMET-ROCKETS.md, "Near the impact", stage 7).
    ///
    /// Phase "make", a new world, the cap on rocks that hit set to <see cref="RockCap"/> with terraform set:
    ///   C  a console comet of a million moles of carbon dioxide, sent far from everything; while it falls, real
    ///      structures from the game's prefabs are built on a line out from where it lands, at distances worked out
    ///      from the zones and the wave in force (ground zero, the fireball, the inner and the middle and the outer
    ///      falling rocks, the wave past them, and beyond); once everything the impact's pass scheduled has
    ///      happened, each one's end state is judged against the table: destroyed in ground zero whatever its
    ///      limit, burned by the stated amount in the fireball, broken, creaking or holding by its own limit, a
    ///      rock's damage only where a rock came down and only in the falling rocks, the rocks the rule's for the
    ///      places the pass found, at most the cap; and that it was one pass over every structure in the world;
    ///   R  a comet brought down by rocket, untracked, by stage 6's Deorbit; the same line of structures built out
    ///      from where its fall says it lands, judged the same way;
    ///   then every structure's state is printed and the world saved.
    /// Phase "load": the same states, read back from the save.
    /// </summary>
    internal sealed class CometZonesCheck
    {
        private const uint StartTick = 20;
        private const double StageSeconds = 900.0;
        private const int RockCap = 4;
        private const double ConsoleMoles = 1e6;
        private const double ConsoleRange = 12000.0;
        private const string RocketMoles = "1000000";

        private readonly ManualLogSource _log;
        private readonly Action<string[]> _command;
        private readonly bool _load;

        private enum Stage { Setup, ConsoleBuild, ConsoleImpact, RocketSetup, RocketFlying, RocketAim, RocketOrder, RocketFired, RocketImpact, Save, Saved, Done }

        private Stage _stage = Stage.Setup;
        private double _stageClock;
        private bool _failed;

        private int _sequence;
        private Vector3 _impact;
        private double _impactClock;
        private double _moles;
        private string _case;
        private int _worldStructures;
        private readonly List<Placed> _placed = new List<Placed>();
        private readonly List<Placed> _all = new List<Placed>();

        private Rocket _rocket;
        private RocketPayloadBay _bay;
        private int _cometId;

        /// <summary>One structure built for the check, with what it was before the impact.</summary>
        private sealed class Placed
        {
            public string Case;
            public string Tag;
            public string Prefab;
            public long Id;
            public Structure Structure;
            public double Distance;
            public CometZone Intended;
            public double LimitKPa;
            public float Health;
            public bool Window;
        }

        private CometZonesCheck(ManualLogSource log, Action<string[]> command, bool load)
        {
            _log = log;
            _command = command;
            _load = load;
        }

        public static CometZonesCheck FromEnvironment(ManualLogSource log, Action<string[]> command)
        {
            string phase = Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETZONES");
            return phase == "make" || phase == "load" ? new CometZonesCheck(log, command, phase == "load") : null;
        }

        private static double Clock => OrbitalSimulation.System?.TotalRealTimeSeconds ?? 0.0;

        private static CultureInfo C => CultureInfo.InvariantCulture;

        private void Info(string line) => _log.LogInfo("LiveCheck: zones " + line);

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

        // ---- main thread ------------------------------------------------------------------------------

        public void Step()
        {
            if (_stage == Stage.Done || _failed)
            {
                return;
            }
            try
            {
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
                case Stage.ConsoleBuild:
                    BuildForConsole();
                    break;
                case Stage.ConsoleImpact:
                    if (Judged())
                    {
                        _command(new[] { "comet", "stop" });
                        Next(Stage.RocketSetup);
                    }
                    break;
                case Stage.RocketSetup when Clock - _stageClock >= 2.0:
                    SetUpRocket();
                    break;
                case Stage.RocketFlying:
                    WaitParked();
                    break;
                case Stage.RocketAim:
                    AimUntracked();
                    break;
                case Stage.RocketOrder:
                    Order();
                    break;
                case Stage.RocketFired:
                    BuildForRocket();
                    break;
                case Stage.RocketImpact:
                    if (Judged())
                    {
                        Next(Stage.Save);
                    }
                    break;
                case Stage.Save:
                    PrintStates("at the save");
                    Info("ids | " + string.Join(",", _all.Select(p => p.Id.ToString(C))));
                    Util.Commands.CommandLine.Process("file save");
                    Next(Stage.Saved);
                    break;
                case Stage.Saved when Clock - _stageClock >= 2.0:
                    Info("phase make done");
                    _stage = Stage.Done;
                    break;
            }
        }

        private void SetUp()
        {
            string verdict = CometRules.Verdict;
            Info(string.Format(C, "parts | comet rules {0} | celestial rules {1} | deorbit {2} | planet size {3:R} | world {4}",
                verdict ?? "pass", CelestialRules.Verdict ?? "pass", CometLand.Installed ? "installed" : "MISSING", CometMap.PlanetSize(), WorldSetting.Current?.Id));
            if (verdict != null || CelestialRules.Verdict != null)
            {
                Fail("the comet rules did not pass their own check");
                return;
            }
            _command(new[] { "set", "MaxDamagingRocks", RockCap.ToString(C) });
            if ((int)Math.Round(Effective.Celestial.MaxDamagingRocks) != RockCap)
            {
                Fail("terraform set did not set MaxDamagingRocks to " + RockCap + " (it is " + Effective.Celestial.MaxDamagingRocks.ToString(C) + ")");
                return;
            }
            _command(new[] { "comet", ConsoleMoles.ToString("R", C), "CarbonDioxide", "range=" + ConsoleRange.ToString("R", C) });
            if (!CometZoneReport.Current(out _sequence, out _impact, out _impactClock, out _moles))
            {
                Fail("the console comet was not sent");
                return;
            }
            _case = "C";
            Info(string.Format(C, "console | event {0} | {1:R} mol | lands at ({2:R}, {3:R}, {4:R}) at clock {5:R}, {6:0.0} s from now",
                _sequence, _moles, _impact.x, _impact.y, _impact.z, _impactClock, _impactClock - Clock));
            Next(Stage.ConsoleBuild);
        }

        private void BuildForConsole()
        {
            Vector3 out_ = new Vector3(_impact.x, 0f, _impact.z);
            Vector3 dir = out_.sqrMagnitude > 1f ? out_.normalized : Vector3.right;
            if (!Build(_impact, _moles, dir))
            {
                return;
            }
            Next(Stage.ConsoleImpact);
        }

        // ---- the line of structures ---------------------------------------------------------------------

        /// <summary>Distance from the impact at which the wave peaks at this many kPa.</summary>
        private static double AtPeak(double moles, double kpa)
        {
            double felt = Math.Min(Math.Max(0.0, moles), CometRules.WaveMolesCap);
            return CometRules.ReferenceMetres * Math.Sqrt(CometRules.ReferencePeakKPa * Math.Pow(felt / CometRules.ReferenceMoles, 2.0 / 3.0) / kpa);
        }

        /// <summary>
        /// The line: each group at a distance worked out from the zones and the wave in force, its members side by side
        /// across the line, each in a square of its own, a metre above the impact's ground.
        /// </summary>
        private bool Build(Vector3 impact, double moles, Vector3 dir)
        {
            if (Clock > _impactClock - 3.0)
            {
                Fail("too little of the fall was left to build in");
                return false;
            }
            ZoneReach reach = CometZones.Shipped.ReachFor(moles, Effective.Celestial);
            double gz = reach.GroundZero;
            double heat = reach.Heat;
            double debris = reach.Debris;
            // An airlock in the fireball that the wave does not break: past where the wave passes its 1,000 kPa and
            // inside the fireball, in the middle of what is left; left out when that is too narrow to stand in.
            double holds = AtPeak(moles, 1000.0);
            var groups = new List<(string Tag, double Distance, CometZone Zone, string[] Prefabs)>
            {
                ("ground zero, inner", 0.5 * gz, CometZone.GroundZero, new[] { "StructureReinforcedWall" }),
                ("ground zero, outer", 0.85 * gz, CometZone.GroundZero, new[] { "StructureAirlock", "StructureSolarPanel" }),
                ("fireball, near", gz + 0.3 * (heat - gz), CometZone.Fireball, new[] { "StructureReinforcedCompositeWindow", "StructureCompositeWindow", "StructureSolarPanel", "StructureFrame" }),
                ("falling rocks, inner edge", heat + Math.Max(8.0, 0.01 * heat), CometZone.Debris, Enumerable.Repeat("StructureFrame", 2 * RockCap).ToArray()),
                ("falling rocks, wave 400 kPa", AtPeak(moles, 400.0), CometZone.Debris, new[] { "StructureCompositeWall", "StructureManualHatch", "StructureWallIron" }),
                ("falling rocks, wave 220 kPa", AtPeak(moles, 220.0), CometZone.Debris, new[] { "StructureCompositeWindow", "StructureCompositeWall", "StructureFrame" }),
                ("wave, 180 kPa", AtPeak(moles, 180.0), CometZone.Wave, new[] { "StructureCompositeWindow", "StructureFrame" }),
                ("wave, 120 kPa", AtPeak(moles, 120.0), CometZone.Wave, new[] { "StructureCompositeWindow" }),
                ("beyond", 2.0 * debris, CometZone.Wave, new[] { "StructureSolarPanel" }),
            };
            if (heat - Math.Max(holds, gz) >= 16.0)
            {
                groups.Insert(3, ("fireball, wave under an airlock's limit", 0.5 * (Math.Max(holds, gz) + heat), CometZone.Fireball, new[] { "StructureAirlock", "StructureSolarPanel" }));
            }
            else
            {
                Info(string.Format(C, "layout {0} | no airlock in the fireball that the wave leaves standing: the wave passes 1,000 kPa out to {1:0.#} m and the fireball ends at {2:0.#} m", _case, holds, heat));
            }
            Vector3 across = Vector3.Cross(Vector3.up, dir).normalized;
            List<Placed> built = new List<Placed>();
            foreach (var group in groups)
            {
                CometZone zone = CometZones.Shipped.ZoneAt(group.Distance, reach);
                if (zone != group.Zone || Math.Abs(group.Distance - Edge(group.Distance, reach)) < 6.0)
                {
                    Fail(string.Format(C, "the line's group '{0}' at {1:0.#} m is in {2}, not {3}, or within 6 m of a zone's edge ({4})", group.Tag, group.Distance, zone, group.Zone, reach));
                    return false;
                }
                for (int i = 0; i < group.Prefabs.Length; i++)
                {
                    double side = (i - 0.5 * (group.Prefabs.Length - 1)) * 6.0;
                    Vector3 at = impact + dir * (float)group.Distance + across * (float)side + Vector3.up;
                    at = new Vector3(Mathf.Round(at.x / 2f) * 2f, Mathf.Round(at.y / 2f) * 2f + 1f, Mathf.Round(at.z / 2f) * 2f);
                    Structure made = Spawn(group.Prefabs[i], at);
                    if (made == null)
                    {
                        Fail("the game made no " + group.Prefabs[i]);
                        return false;
                    }
                    built.Add(new Placed
                    {
                        Case = _case,
                        Tag = group.Tag,
                        Prefab = group.Prefabs[i],
                        Id = made.ReferenceId,
                        Structure = made,
                        Distance = Vector3.Distance(impact, made.CenterPosition),
                        Intended = group.Zone,
                        LimitKPa = made.maxPressureDelta,
                        Health = made.DamageState != null ? made.DamageState.MaxDamage : float.NaN,
                        Window = made is Wall wall && wall.WallMaterialType == Wall.WallMaterial.Glass,
                    });
                }
            }
            foreach (Placed p in built)
            {
                if (CometZones.Shipped.ZoneAt(p.Distance, reach) != p.Intended)
                {
                    Fail(string.Format(C, "{0} {1} stands {2:0.#} m out, in {3}, not {4}", p.Tag, p.Prefab, p.Distance, CometZones.Shipped.ZoneAt(p.Distance, reach), p.Intended));
                    return false;
                }
            }
            _placed.Clear();
            _placed.AddRange(built);
            _all.AddRange(built);
            int count = 0;
            GridController.AllStructuresPool.ForEach((Action<Structure>)(s =>
            {
                if (s != null && !s.IsBeingDestroyed)
                {
                    count++;
                }
            }));
            _worldStructures = count;
            Info(string.Format(C, "built {0} | {1} structures on a line out from ({2:R}, {3:R}, {4:R}) toward ({5:0.###}, {6:0.###}) | {7} | the world holds {8} structures | impact in {9:0.0} s",
                _case, built.Count, impact.x, impact.y, impact.z, dir.x, dir.z, reach, count, _impactClock - Clock));
            return true;
        }

        private static double Edge(double metres, ZoneReach reach)
        {
            double best = double.MaxValue;
            double edge = 0.0;
            foreach (double e in new[] { reach.GroundZero, reach.Heat, reach.Debris })
            {
                if (Math.Abs(metres - e) < best)
                {
                    best = Math.Abs(metres - e);
                    edge = e;
                }
            }
            return edge;
        }

        private static Structure Spawn(string prefabName, Vector3 position)
        {
            Thing prefab = Prefab.Find(prefabName);
            if (prefab == null)
            {
                return null;
            }
            Structure made = OnServer.Create<Structure>(prefab, position, Quaternion.identity);
            if (made != null && made.BuildStates != null && made.BuildStates.Count > 0)
            {
                made.CurrentBuildStateIndex = made.BuildStates.Count - 1;
            }
            return made;
        }

        // ---- the judging ---------------------------------------------------------------------------------

        /// <summary>Once everything the impact's pass scheduled has happened, every structure of the line against the table. True when judged.</summary>
        private bool Judged()
        {
            CometZoneReport report = CometZoneReport.Last;
            if (report == null || report.Sequence != _sequence)
            {
                if (Clock > _impactClock + 30.0)
                {
                    Fail("no zone pass for event " + _sequence + " within 30 s of its impact");
                }
                return false;
            }
            if (Clock < _impactClock + report.LastSeconds + 3.0)
            {
                return false;
            }
            CometRules rules = CometRules.Shipped;
            CometZones zones = CometZones.Shipped;
            CelestialTuning tuning = Effective.Celestial;
            ZoneReach reach = zones.ReachFor(report.Moles, tuning);
            CometSizes sizes = CometYield.Shipped.For(report.Moles, tuning);
            double burn = zones.HeatBurn(0.5 * (reach.GroundZero + reach.Heat), reach, sizes);
            HashSet<long> rocked = new HashSet<long>(report.Rocks.Select(r => r.Structure));
            int failures = 0;
            foreach (Placed p in _placed)
            {
                CometZone zone = zones.ZoneAt(p.Distance, reach);
                double peak = rules.PeakKPa(report.Moles, p.Distance);
                bool breaks = p.LimitKPa > 0.0 && rules.Breaks(peak, p.LimitKPa);
                bool creaks = p.Window && p.LimitKPa > 0.0 && !breaks && rules.Creaks(peak, p.LimitKPa);
                double heat = zone == CometZone.Fireball ? burn : 0.0;
                bool rock = rocked.Contains(p.Id);
                double rockDamage = rock ? CometZones.RockForce : 0.0;
                CometZoneReport.Entry? entry = report.Entries.Where(e => e.ReferenceId == p.Id).Select(e => (CometZoneReport.Entry?)e).FirstOrDefault();
                Structure now = Referencable.Find<Structure>(p.Id);
                bool gone = now == null || now.IsBeingDestroyed;
                bool broken = !gone && now.IsBroken;
                float gotBurn = gone || now.DamageState == null ? float.NaN : now.DamageState.Burn;
                float gotBrute = gone || now.DamageState == null ? float.NaN : now.DamageState.Brute;
                string expected;
                bool pass;
                if (zone == CometZone.GroundZero)
                {
                    expected = "destroyed whatever its limit";
                    pass = gone && entry != null && entry.Value.Destroyed && !rock;
                }
                else
                {
                    bool overHealth = heat + rockDamage >= p.Health;
                    bool reportOk = (heat > 0.0 || breaks || creaks) == (entry != null)
                        && (entry == null || (entry.Value.Zone == zone && entry.Value.Breaks == breaks && entry.Value.Creaks == creaks && Math.Abs(entry.Value.Burn - heat) <= 1e-9 * Math.Max(1.0, heat)));
                    bool rockOk = !rock || zone == CometZone.Debris;
                    if (breaks || overHealth)
                    {
                        expected = string.Format(C, "{0}{1}{2}", breaks ? "broken by the wave" : "broken by its damage", heat > 0.0 ? ", burned " + heat.ToString("0.###", C) : "", rock ? ", a rock" : "");
                        pass = (gone || broken) && reportOk && rockOk;
                    }
                    else
                    {
                        expected = string.Format(C, "standing, burn {0:0.###}, brute {1:0.###}{2}", heat, rockDamage, creaks ? ", creaked" : "");
                        pass = !gone && !broken && Math.Abs(gotBurn - heat) <= 1e-3 * Math.Max(1.0, heat) && Math.Abs(gotBrute - rockDamage) <= 1e-3 * Math.Max(1.0, rockDamage) && reportOk && rockOk;
                    }
                }
                if (!pass)
                {
                    failures++;
                }
                Info(string.Format(C, "structure {0} {1} | {2} {3} id {4} | {5}: {6:0.#} m out, in {7} | wave {8:0.#} kPa, limit {9:0.#} kPa, health {10:0.#} | expected {11} | got {12}, burn {13:R}, brute {14:R} | the pass: {15}",
                    p.Case, pass ? "PASS" : "FAIL", p.Prefab, p.Tag, p.Id, p.Case, p.Distance, zone, peak, p.LimitKPa, p.Health, expected,
                    gone ? "gone" : broken ? "broken" : "standing", gotBurn, gotBrute,
                    entry == null ? "nothing" : string.Format(C, "{0}{1}{2}{3}", entry.Value.Zone, entry.Value.Destroyed ? ", destroy" : "", entry.Value.Breaks ? ", break" : entry.Value.Creaks ? ", creak" : "",
                        entry.Value.Burn > 0.0 ? ", burn " + entry.Value.Burn.ToString("0.###", C) : "")));
            }

            // The rocks: the rule's for the places the pass found, all in the falling rocks, all come down, at most the cap.
            int[] rule = zones.Rocks(report.SpotMetres.ToArray(), reach, report.Seed, report.RockCap);
            bool allIn = report.Rocks.All(r => zones.ZoneAt(r.Distance, reach) == CometZone.Debris);
            bool asRule = rule.Length == report.Rocks.Count;
            for (int i = 0; i < rule.Length && asRule; i++)
            {
                asRule = Math.Abs(report.SpotMetres[rule[i]] - report.Rocks[i].Distance) < 1e-6;
            }
            bool capped = report.Rocks.Count <= report.RockCap && report.RockCap == RockCap;
            bool landed = report.RocksLanded == report.Rocks.Count;
            int innerFrames = _placed.Count(p => p.Tag == "falling rocks, inner edge");
            bool onInner = _case != "C" || (report.Rocks.Count == RockCap && report.Rocks.All(r => _placed.Any(p => p.Id == r.Structure && p.Tag == "falling rocks, inner edge")));
            bool rocksPass = allIn && asRule && capped && landed && onInner;
            Info(string.Format(C, "rocks {0} {1} | {2} rocks of a cap of {3} on {4} places the pass found in the falling rocks ({5:0.#} to {6:0.#} m) | the rule gives {7} for those places and the event's seed | {8} came down | {9}",
                _case, rocksPass ? "PASS" : "FAIL", report.Rocks.Count, report.RockCap, report.SpotMetres.Count, reach.Inner, reach.Debris, rule.Length, report.RocksLanded,
                string.Join(", ", report.Rocks.Select(r => string.Format(C, "{0} at {1:0.#} m after {2:0.#} s", r.Structure, r.Distance, r.Seconds)))
                + (_case == "C" ? string.Format(C, " | the {0} frames at the inner edge take the cap, nearest first: {1}", innerFrames, onInner) : "")));
            bool onePass = report.Structures == _worldStructures && report.Milliseconds >= 0.0 && report.Host;
            Info(string.Format(C, "pass {0} {1} | event {2} | one pass over {3} structures (the world held {4} before the impact) and {5} players in {6:0.###} ms | {7} | burn {8:0.###} | destroyed {9}, burned {10}, broken {11}",
                _case, onePass ? "PASS" : "FAIL", report.Sequence, report.Structures, _worldStructures, report.People, report.Milliseconds, reach, report.Burn, report.Destroyed, report.Burned, report.Broken));
            if (failures > 0 || !rocksPass || !onePass)
            {
                Fail(string.Format(C, "case {0}: {1} structures did not end as the table says, the rocks {2}, the pass {3}", _case, failures, rocksPass ? "held" : "did not hold", onePass ? "held" : "did not hold"));
                return false;
            }
            return true;
        }

        private void PrintStates(string when)
        {
            foreach (Placed p in _all)
            {
                Info(State(p.Id) + " | " + p.Case + " " + p.Prefab + " " + p.Tag + " | " + when);
            }
        }

        private static string State(long id)
        {
            Structure s = Referencable.Find<Structure>(id);
            bool gone = s == null || s.IsBeingDestroyed;
            return string.Format(C, "state | {0} | {1} | burn {2} | brute {3}", id, gone ? "gone" : s.IsBroken ? "broken" : "standing",
                gone || s.DamageState == null ? "-" : s.DamageState.Burn.ToString("R", C), gone || s.DamageState == null ? "-" : s.DamageState.Brute.ToString("R", C));
        }

        // ---- the comet brought down by rocket ---------------------------------------------------------------

        private void SetUpRocket()
        {
            SpaceMap map = SpaceMap.Current;
            if (map?.EntryNode == null || !CometMap.SaveInstalled || !CometSkyBodies.Installed || !CometAim.ReadoutsInstalled || !CometLand.Installed)
            {
                Fail("no space map, or a comet part is not installed");
                return;
            }
            SpaceMapNode site = Discover(map);
            if (site == null)
            {
                return;
            }
            double route = RouteDeltaV(site);
            double premix = CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));
            int index = CometMap.Sites().IndexOf(site) + 1;
            HashSet<int> before = new HashSet<int>(CometMap.Views().Select(v => v.Id));
            string[] args = { "comet", "make", index.ToString(C), RocketMoles, "Water" };
            _command(args.Concat(new[] { "engines=8", "fuel=0" }).ToArray());
            CometView probe = CometMap.Views().FirstOrDefault(v => !before.Contains(v.Id));
            if (probe.Id == 0)
            {
                Fail("the mod's make verb made no comet");
                return;
            }
            before.Add(probe.Id);
            double fuel = 1.02 * CelestialRules.Shipped.FuelNeeded(probe.CometKg, 8, route, premix) + 500.0;
            _command(args.Concat(new[] { "engines=8", "fuel=" + fuel.ToString("R", C) }).ToArray());
            _cometId = CometMap.Views().Select(v => v.Id).FirstOrDefault(i => !before.Contains(i));
            if (_cometId == 0)
            {
                Fail("the mod's make verb made no comet with fuel");
                return;
            }
            _command(new[] { "comet", "send", _cometId.ToString(C) });
            Spawn("StructureLaunchMount", new Vector3(300f, 300f, -400f));
            EngineFuselage mount = Spawn("StructureEngineMountTypeA1", new Vector3(100f, 400f, 100f)) as EngineFuselage;
            RocketNetwork network = mount?.RocketNetwork;
            _rocket = network?.Rocket;
            _bay = Spawn("StructureRocketLargePayloadBay", new Vector3(124f, 400f, 100f)) as RocketPayloadBay;
            if (_rocket == null || _bay == null)
            {
                Fail("the rocket or its bay did not spawn");
                return;
            }
            _bay.RocketData = new RocketData(network, _bay.ThingTransformPosition - mount.ThingTransformPosition);
            network.AdoptFromLoad(_bay);
            network.RefreshRocket();
            _rocket.SetCurrentNode(map.EntryNode);
            _rocket.RocketState = RocketState.InSpace;
            Info(string.Format(C, "rocket set up | comet {0} of {1} mol of water from {2} ({3:R} delta-v), {4:R} kg | rocket {5} bay {6} at {7}",
                _cometId, RocketMoles, site.DisplayName, route, probe.CometKg, _rocket.ReferenceId, _bay.ReferenceId, _rocket.CurrentNode?.DisplayName));
            Next(Stage.RocketFlying);
        }

        private void WaitParked()
        {
            if (!CometMap.Views().Any(v => v.Id == _cometId && v.State == MapCometState.Arrived) || CometSkyBodies.For(_cometId) == null)
            {
                Overdue("the comet parking at Entry and going into the sky");
                return;
            }
            Next(Stage.RocketAim);
        }

        private void AimUntracked()
        {
            if (CometLand.Busy())
            {
                Overdue("the console comet's show ending");
                return;
            }
            CometAim.Aim? found = CometAim.ForBay(_bay.ReferenceId);
            _bay.SetLogicValue(LogicType.PositionX, double.NaN);
            _bay.SetLogicValue(LogicType.PositionZ, double.NaN);
            if (found == null || found.Value.CometId != _cometId)
            {
                Overdue("the bay aiming comet " + _cometId);
                return;
            }
            Next(Stage.RocketOrder);
        }

        private void Order()
        {
            CometAim.Aim? found = CometAim.ForBay(_bay.ReferenceId);
            if (found == null || found.Value.CometId != _cometId || found.Value.Tracked)
            {
                Overdue("the bay aiming untracked");
                return;
            }
            _rocket.RocketMode = CometModes.Deorbit;
            _case = "R";
            Next(Stage.RocketFired);
        }

        private void BuildForRocket()
        {
            LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == _cometId);
            if (l.CometId != _cometId || !CometZoneReport.Current(out _sequence, out _impact, out _impactClock, out _moles))
            {
                Overdue("comet " + _cometId + "'s burn and its event");
                return;
            }
            Vector3 fromPad = new Vector3((float)(l.ImpactX - l.PadX), 0f, (float)(l.ImpactZ - l.PadZ));
            Vector3 dir = fromPad.sqrMagnitude > 100f ? fromPad.normalized : Vector3.right;
            Info(string.Format(C, "rocket | comet {0} untracked, event {1} | {2:R} mol | lands at ({3:R}, {4:R}, {5:R}) at clock {6:R}, {7:0.0} s from now | sky point ({8:R}, {9:R}), pad ({10:R}, {11:R})",
                _cometId, _sequence, _moles, _impact.x, _impact.y, _impact.z, _impactClock, _impactClock - Clock, l.SkyX, l.SkyZ, l.PadX, l.PadZ));
            if (Math.Abs(_impact.x - l.ImpactX) > 1e-3 || Math.Abs(_impact.z - l.ImpactZ) > 1e-3 || Math.Abs(_moles - l.ShowMoles) > 1e-6 * Math.Max(1.0, l.ShowMoles))
            {
                Fail("the impact event is not the fall's");
                return;
            }
            if (!Build(_impact, _moles, dir))
            {
                return;
            }
            Next(Stage.RocketImpact);
        }

        private SpaceMapNode Discover(SpaceMap map)
        {
            foreach (SpaceMapNode parent in map.Nodes.Where(n => n?.DiscoverData != null && n.DiscoverData.SiteGenerations.Count > 0).OrderBy(n => RouteDeltaV(n)).ToList())
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
                    if (made.Deposit != null && made.Deposit.TotalOreAtLocation > 0)
                    {
                        return made;
                    }
                }
            }
            Fail("discovery made no site on the map");
            return null;
        }

        private static double RouteDeltaV(SpaceMapNode node)
        {
            if (node == SpaceMap.Current.EntryNode || !SpaceMapPathFinder.GetPath(node, SpaceMap.Current.EntryNode, out List<Assets.Scripts.Serialization.NodeTransit> path) || path == null)
            {
                return 0.0;
            }
            return path.Sum(t => (double)t.GetDistance());
        }

        // ---- phase load -------------------------------------------------------------------------------

        private void StepLoad()
        {
            if (GameManager.GameTickCount < 5)
            {
                return;
            }
            string ids = Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETZONES_IDS") ?? "";
            foreach (string one in ids.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                long id = long.Parse(one, NumberStyles.Integer, C);
                Info(State(id) + " | after the load");
            }
            Info("done");
            _stage = Stage.Done;
        }
    }
}
