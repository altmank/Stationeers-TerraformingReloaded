using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using Objects.Rockets;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// A comet parked at Entry, aimed (docs/COMET-ROCKETS.md, "Bring it down"). A rocket at Entry aims it with
    /// its payload bay's PositionX and PositionZ, the payload drop's own target. Where it would come down if it
    /// were brought down now is the sky rule's point under it (CelestialRules.SkyPoint, from the direction the
    /// game's sky shows it in), moved toward the target by the reach of the fuel it has left
    /// (CelestialRules.ReachMetres, CelestialRules.Landing), inside the disc around the launch pad nearest the
    /// target. A bay whose target is not a number (a chip wrote NaN) aims untracked: the comet would come down at
    /// the sky point alone, in the disc around the pad nearest the world's origin, with no fuel spent steering
    /// (CelestialRules.Descend). A Deorbit action fires the burn at the landing its bay's aim gives that frame
    /// (CometLand.Deorbit).
    ///
    /// The bay's target is not saved or sent by the game. The host reads every bay of every rocket at Entry
    /// each frame and sends the targets with the comets (CometMap's list, layout 3); every machine then works
    /// out the rest from its own copy, so the Map motherboard's two icons stand in the same place everywhere.
    /// The host alone also reads it out to logic, through the bay (<see cref="ApplyReadouts"/>).
    ///
    /// Main thread for <see cref="Step"/>; what it publishes is read on any thread.
    /// </summary>
    public static class CometAim
    {
        /// <summary>How often the structures on the ground are gathered again, and the comet's next highest point worked out again.</summary>
        private const double SlowSeconds = 1.0;

        /// <summary>A bay's target as the host read it; NaN for one that is not a point on the ground, which aims untracked.</summary>
        public readonly struct Target
        {
            public readonly long BayId;
            public readonly float X;
            public readonly float Z;

            public Target(long bayId, float x, float z)
            {
                BayId = bayId;
                X = x;
                Z = z;
            }

            /// <summary>The target is a point on the ground; else the bay aims untracked.</summary>
            public bool Tracked => !float.IsNaN(X) && !float.IsNaN(Z) && !float.IsInfinity(X) && !float.IsInfinity(Z);
        }

        /// <summary>One bay at Entry aiming one parked comet, as this machine works it out. A plain struct: net472 has no IsExternalInit.</summary>
        public readonly struct Aim
        {
            public readonly long BayId;
            public readonly string RocketName;
            public readonly int CometId;

            /// <summary>The bay's target; NaN untracked.</summary>
            public readonly double TargetX;
            public readonly double TargetZ;

            /// <summary>The bay has a target; else the comet would come down at the sky point alone.</summary>
            public readonly bool Tracked;

            /// <summary>The launch pad nearest the target, the disc's centre; the target itself when the map has no pad.</summary>
            public readonly double PadX;
            public readonly double PadZ;
            public readonly bool OnPad;

            /// <summary>The disc's radius on this world and planet size (CelestialTuning.DiscRadiusAt).</summary>
            public readonly double Radius;

            /// <summary>The sky rule's point under the comet now.</summary>
            public readonly double SkyX;
            public readonly double SkyZ;

            /// <summary>How far, in metres, the fuel the comet has left can move where it lands.</summary>
            public readonly double Reach;

            /// <summary>How far the burn would move it toward the target, in metres, and the moles of fuel that takes (CelestialRules.Descend).</summary>
            public readonly double Steered;
            public readonly double FuelSpent;

            /// <summary>The fuel the comet has at Entry, which all of this was worked from.</summary>
            public readonly double Fuel;

            /// <summary>Where it would land if it were brought down now.</summary>
            public readonly double LandX;
            public readonly double LandZ;

            /// <summary>Host: metres from the landing to the nearest structure on the ground; NaN with none, or on a joining player's game.</summary>
            public readonly double NearestStructureMetres;

            /// <summary>When, on the shared clock, the comet is next at its highest in the sky; NaN when not found.</summary>
            public readonly double NextHighestClock;

            /// <summary>The direction the game's sky shows the comet in, which all of this was worked from.</summary>
            public readonly SkyVector Direction;

            /// <summary>The frame this was worked out in (UnityEngine.Time.frameCount) and the clock then.</summary>
            public readonly int Frame;
            public readonly double Clock;

            /// <summary>Where a rocket comes to rest on the pad, its height the ground the impact falls back to with no voxels to read.</summary>
            public readonly double PadY;

            public Aim(long bayId, string rocketName, int cometId, double targetX, double targetZ, bool tracked, double padX, double padY, double padZ, bool onPad, double radius,
                double skyX, double skyZ, double reach, double steered, double fuelSpent, double fuel, double landX, double landZ, double nearestStructureMetres, double nextHighestClock,
                SkyVector direction, int frame, double clock)
            {
                BayId = bayId;
                RocketName = rocketName;
                CometId = cometId;
                TargetX = targetX;
                TargetZ = targetZ;
                Tracked = tracked;
                PadY = padY;
                Steered = steered;
                FuelSpent = fuelSpent;
                Fuel = fuel;
                Frame = frame;
                Clock = clock;
                PadX = padX;
                PadZ = padZ;
                OnPad = onPad;
                Radius = radius;
                SkyX = skyX;
                SkyZ = skyZ;
                Reach = reach;
                LandX = landX;
                LandZ = landZ;
                NearestStructureMetres = nearestStructureMetres;
                NextHighestClock = nextHighestClock;
                Direction = direction;
            }
        }

        private static volatile Target[] _targets = new Target[0];
        private static volatile Aim[] _aims = new Aim[0];
        private static volatile bool _readouts;
        private static readonly Dictionary<int, double> NextHighest = new Dictionary<int, double>();
        private static double _slowAt = double.NegativeInfinity;

        /// <summary>Host: where every structure on the ground stands, x and z in turn, gathered at most once a second.</summary>
        private static float[] _ground = new float[0];
        private static int _faults;

        /// <summary>Every bay at Entry aiming a parked comet, as this machine works it out now; any thread.</summary>
        public static Aim[] All => _aims;

        /// <summary>The bays' targets this machine has: read by the host, sent to everyone else.</summary>
        internal static Target[] Targets => _targets;

        /// <summary>The aim of this bay, or null; any thread.</summary>
        public static Aim? ForBay(long bayId)
        {
            foreach (Aim aim in _aims)
            {
                if (aim.BayId == bayId)
                {
                    return aim;
                }
            }
            return null;
        }

        /// <summary>Every world start: nothing of the last world's aims is left.</summary>
        internal static void WorldStart()
        {
            _targets = new Target[0];
            _aims = new Aim[0];
            NextHighest.Clear();
            _slowAt = double.NegativeInfinity;
            _ground = new float[0];
        }

        /// <summary>A joining player's game: the host's targets, with the comets.</summary>
        internal static void FromHost(Target[] targets)
        {
            if (targets != null && NetworkManager.IsClient)
            {
                _targets = targets;
            }
        }

        // ---- every frame -------------------------------------------------------------------------------------

        /// <summary>Main thread, every frame, on every machine.</summary>
        internal static void Step()
        {
            SpaceMapNode entry = SpaceMap.Current?.EntryNode;
            if (entry == null || GameManager.GameState != GameState.Running)
            {
                return;
            }
            CometSkyBodies.Shown shown = Parked(out MapComet comet);
            if (!NetworkManager.IsClient)
            {
                // Nothing is aimed while no comet is parked, so nothing is sent for it either.
                Target[] read = shown == null ? new Target[0] : ReadTargets(entry);
                if (!Same(read, _targets))
                {
                    _targets = read;
                    CometMap.MarkChanged();
                }
            }
            Target[] targets = _targets;
            SkyFrame? frame = CometSkyBodies.Frame();
            if (targets.Length == 0 || shown == null || frame == null)
            {
                if (_aims.Length > 0)
                {
                    _aims = new Aim[0];
                }
                return;
            }
            double now = CometEvent.Clock;
            bool slow = now - _slowAt >= SlowSeconds || now < _slowAt;
            if (slow)
            {
                _slowAt = now;
                if (!NetworkManager.IsClient)
                {
                    _ground = Ground();
                }
            }
            CelestialTuning tuning = Effective.Celestial;
            double radius = DiscRadius(tuning);
            List<UnityEngine.Vector3> pads = Pads(entry);
            UnityEngine.Vector3 seen = shown.Body.WorldVector;
            SkyVector direction = new SkyVector(seen.x, seen.y, seen.z);
            double fuel = comet.At(now).FuelLeft;
            double highest = NextHighestClock(frame.Value, shown, now, slow);
            int frameCount = UnityEngine.Time.frameCount;
            Aim[] aims = new Aim[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                Target target = targets[i];
                bool tracked = target.Tracked;
                // Untracked, the disc is the one around the pad nearest the world's origin, the same on every machine.
                bool onPad = NearestPad(pads, tracked ? target.X : 0.0, tracked ? target.Z : 0.0, out double padX, out double padY, out double padZ);
                GroundPoint sky = CelestialRules.Shipped.SkyPoint(padX, padZ, direction.X, direction.Y, direction.Z, radius);
                Descent descent = CelestialRules.Shipped.Descend(sky, tracked ? new GroundPoint(target.X, target.Z) : (GroundPoint?)null, padX, padZ, radius,
                    comet.CometKg, comet.Engines, fuel, tuning.CorrectionMetresPerDeltaV, tuning.EntryWindowSeconds, comet.FuelKgPerMole);
                GroundPoint land = descent.Land;
                double nearest = NetworkManager.IsClient ? double.NaN : Nearest(_ground, land.X, land.Z);
                RocketPayloadBay bay = Referencable.Find<RocketPayloadBay>(target.BayId);
                string rocket = bay?.RocketNetwork?.Rocket?.DisplayName ?? "a rocket";
                aims[i] = new Aim(target.BayId, rocket, comet.Id, tracked ? target.X : double.NaN, tracked ? target.Z : double.NaN, tracked, padX, padY, padZ, onPad, radius,
                    sky.X, sky.Z, descent.Reach, descent.Steered, descent.FuelSpent, fuel, land.X, land.Z, nearest, highest, direction, frameCount, now);
            }
            _aims = aims;
        }

        /// <summary>
        /// The comet the rockets at Entry aim: the first in the sky, by id, that is still parked. One brought down
        /// is out of the parked comets at once, while its body may stay in the sky until the game tick's
        /// main-thread moment takes it out (CometSkyBodies).
        /// </summary>
        private static CometSkyBodies.Shown Parked(out MapComet comet)
        {
            foreach (CometSkyBodies.Shown shown in CometSkyBodies.All)
            {
                MapComet found = CometMap.Find(shown.CometId);
                if (found != null && found.State == MapCometState.Arrived)
                {
                    comet = found;
                    return shown;
                }
            }
            comet = null;
            return null;
        }

        /// <summary>The disc in force on this world and planet size.</summary>
        public static double DiscRadius(CelestialTuning tuning)
        {
            double curvature = WorldSetting.Current?.Data?.TerrainSettings?.Curvature?.Value ?? 0f;
            double size = CometMap.PlanetSize();
            return tuning.DiscRadiusAt(curvature, size > 0.0 ? size : CelestialRules.StandardPlanetSize);
        }

        /// <summary>Host: every payload bay of every rocket standing at Entry, and its target, by bay id.</summary>
        private static Target[] ReadTargets(SpaceMapNode entry)
        {
            List<Target> read = new List<Target>();
            foreach (Rocket rocket in Rocket.AllRockets)
            {
                if (rocket?.CurrentNode != entry || rocket.RocketState != RocketState.InSpace || rocket.RocketNetwork == null)
                {
                    continue;
                }
                foreach (RocketPayloadBay bay in rocket.RocketNetwork.RocketPayloadBays)
                {
                    if (bay == null || bay.IsBeingDestroyed)
                    {
                        continue;
                    }
                    float x = (float)bay.GetLogicValue(LogicType.PositionX);
                    float z = (float)bay.GetLogicValue(LogicType.PositionZ);
                    // A chip can write anything; a target that is not a point on the ground aims untracked.
                    Target target = new Target(bay.ReferenceId, x, z);
                    read.Add(target.Tracked ? target : new Target(bay.ReferenceId, float.NaN, float.NaN));
                }
            }
            read.Sort((a, b) => a.BayId.CompareTo(b.BayId));
            return read.ToArray();
        }

        private static bool Same(Target[] a, Target[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].BayId != b[i].BayId || !a[i].X.Equals(b[i].X) || !a[i].Z.Equals(b[i].Z))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Where a rocket comes to rest on each launch pad on the ground under Entry.</summary>
        public static List<UnityEngine.Vector3> Pads(SpaceMapNode entry)
        {
            List<UnityEngine.Vector3> pads = new List<UnityEngine.Vector3>();
            foreach (NodeConnection connection in entry.ChildConnections)
            {
                SpaceMapNode child = connection?.Child;
                if (child?.NodeType == NodeType.LaunchPad && child.Owner != null && !child.Owner.IsOrbital)
                {
                    pads.Add(child.Owner.RocketTransformPosition);
                }
            }
            return pads;
        }

        /// <summary>The pad nearest a target, along the ground; the target itself, and false, when there is none.</summary>
        public static bool NearestPad(List<UnityEngine.Vector3> pads, double x, double z, out double padX, out double padZ) => NearestPad(pads, x, z, out padX, out _, out padZ);

        /// <summary>The same, with where a rocket rests on that pad, its height; 0 with no pad.</summary>
        public static bool NearestPad(List<UnityEngine.Vector3> pads, double x, double z, out double padX, out double padY, out double padZ)
        {
            padX = x;
            padY = 0.0;
            padZ = z;
            double best = double.PositiveInfinity;
            foreach (UnityEngine.Vector3 pad in pads)
            {
                double dx = pad.x - x;
                double dz = pad.z - z;
                double d = dx * dx + dz * dz;
                if (d < best)
                {
                    best = d;
                    padX = pad.x;
                    padY = pad.y;
                    padZ = pad.z;
                }
            }
            return best < double.PositiveInfinity;
        }

        /// <summary>Where every structure on the ground stands, x and z in turn, rockets in space aside. Main thread.</summary>
        private static float[] Ground()
        {
            List<float> ground = new List<float>();
            GridController.AllStructuresPool.ForEach((Action<Structure>)(structure =>
            {
                if (structure == null || structure.IsBeingDestroyed || structure.RocketData?.Network?.Rocket?.RocketState == RocketState.InSpace)
                {
                    return;
                }
                UnityEngine.Vector3 at = structure.ThingTransformPosition;
                ground.Add(at.x);
                ground.Add(at.z);
            }));
            return ground.ToArray();
        }

        /// <summary>Metres along the ground from a point to the nearest of these structures; NaN with none.</summary>
        public static double Nearest(float[] ground, double x, double z)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i + 1 < ground.Length; i += 2)
            {
                double dx = ground[i] - x;
                double dz = ground[i + 1] - z;
                best = Math.Min(best, dx * dx + dz * dz);
            }
            return best < double.PositiveInfinity ? Math.Sqrt(best) : double.NaN;
        }

        /// <summary>The clock at which this comet is next at its highest, worked out again at most once a second and whenever it has passed.</summary>
        private static double NextHighestClock(SkyFrame frame, CometSkyBodies.Shown shown, double now, bool slow)
        {
            if (!slow && NextHighest.TryGetValue(shown.CometId, out double kept) && kept >= now)
            {
                return kept;
            }
            double orbit = CelestialRules.OrbitRealSeconds(shown.Orbit.PeriodDays, frame.PeriodDays, frame.TimeScale);
            double seconds = CometSkyRules.Shipped.NextHighestSeconds(frame, shown.Orbit, OrbitalSimulation.System.SimulationTimeSeconds, 3.0 * orbit);
            double at = now + seconds;
            NextHighest[shown.CometId] = at;
            return at;
        }

        // ---- the payload bay's readouts ------------------------------------------------------------------------

        /// <summary>
        /// The readouts a chip times a burn by, on the payload bay of a rocket at Entry, through logic values no
        /// rocket device uses (docs/COMET-ROCKETS.md, "Readouts"): where the comet lands if it is brought down
        /// now (TargetX, TargetZ), how far that is from the nearest structure (DistanceKm), the seconds until it
        /// is next at its highest (TimeToDestination), and how far its spare fuel can move the landing (Maximum,
        /// metres). NaN where no comet is parked or the bay is not at Entry. PositionX and PositionZ stay the
        /// game's own target.
        /// </summary>
        public static void ApplyReadouts(Harmony harmony)
        {
            MethodInfo canRead = AccessTools.DeclaredMethod(typeof(RocketPayloadBay), nameof(RocketPayloadBay.CanLogicRead), new[] { typeof(LogicType) });
            MethodInfo read = AccessTools.DeclaredMethod(typeof(RocketPayloadBay), nameof(RocketPayloadBay.GetLogicValue), new[] { typeof(LogicType) });
            if (canRead == null || read == null)
            {
                throw new MissingMethodException("the payload bay no longer answers its own logic reads");
            }
            harmony.Patch(canRead, postfix: Body(nameof(CanLogicReadPostfix)));
            harmony.Patch(read, postfix: Body(nameof(GetLogicValuePostfix)));
            _readouts = true;
        }

        /// <summary>The bay readouts are installed (Patcher's "comet aim readouts").</summary>
        public static bool ReadoutsInstalled => _readouts;

        private static HarmonyMethod Body(string name) =>
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometAim), name) ?? throw new MissingMethodException(nameof(CometAim), name));

        /// <summary>The logic values the bay reads a comet's aim out through.</summary>
        public static bool IsReadout(LogicType type) =>
            type == LogicType.TargetX || type == LogicType.TargetZ || type == LogicType.DistanceKm || type == LogicType.TimeToDestination || type == LogicType.Maximum;

        private static void CanLogicReadPostfix(LogicType logicType, ref bool __result)
        {
            if (IsReadout(logicType))
            {
                __result = true;
            }
        }

        private static void GetLogicValuePostfix(RocketPayloadBay __instance, LogicType logicType, ref double __result)
        {
            if (!IsReadout(logicType))
            {
                return;
            }
            try
            {
                __result = Reading(__instance.ReferenceId, logicType);
            }
            catch (Exception e)
            {
                __result = double.NaN;
                Fault("A payload bay's comet readout could not be worked out: " + e.Message);
            }
        }

        /// <summary>What a bay reads out through one of the comet's logic values; NaN with no aim.</summary>
        public static double Reading(long bayId, LogicType type)
        {
            Aim? found = ForBay(bayId);
            if (found == null)
            {
                return double.NaN;
            }
            Aim aim = found.Value;
            switch (type)
            {
                case LogicType.TargetX:
                    return aim.LandX;
                case LogicType.TargetZ:
                    return aim.LandZ;
                case LogicType.DistanceKm:
                    return aim.NearestStructureMetres / 1000.0;
                case LogicType.TimeToDestination:
                    return double.IsNaN(aim.NextHighestClock) ? double.NaN : Math.Max(0.0, aim.NextHighestClock - CometEvent.Clock);
                case LogicType.Maximum:
                    return aim.Reach;
                default:
                    return double.NaN;
            }
        }

        // ---- the map motherboard's icons --------------------------------------------------------------------------

        /// <summary>One icon on the Map motherboard: where it stands on the ground, what it is called, and its colour.</summary>
        public readonly struct Icon
        {
            public readonly long Id;
            public readonly string Name;
            public readonly double X;
            public readonly double Z;
            public readonly bool IsTarget;

            /// <summary>The aim it is drawn for, by its place in <see cref="All"/> as the icons were made.</summary>
            public readonly int Aim;

            public Icon(long id, string name, double x, double z, bool isTarget, int aim)
            {
                Id = id;
                Name = name;
                X = x;
                Z = z;
                IsTarget = isTarget;
                Aim = aim;
            }
        }

        /// <summary>
        /// The icons of every bay aiming a parked comet: its target, and where the comet lands if it is brought
        /// down now. An untracked bay has no target, so only the second.
        /// </summary>
        public static Icon[] Icons() => IconsOf(_aims);

        /// <summary>The icons of these aims.</summary>
        public static Icon[] IconsOf(Aim[] aims)
        {
            List<Icon> icons = new List<Icon>(aims.Length * 2);
            CultureInfo c = CultureInfo.InvariantCulture;
            for (int i = 0; i < aims.Length; i++)
            {
                Aim aim = aims[i];
                long id = -(1L << 42) - 2L * Math.Abs(aim.BayId);
                if (aim.Tracked)
                {
                    icons.Add(new Icon(id, string.Format(c, "Comet {0}: the target of {1}", aim.CometId, aim.RocketName), aim.TargetX, aim.TargetZ, true, i));
                }
                icons.Add(new Icon(id - 1L, string.Format(c, aim.Tracked ? "Comet {0}: lands here if brought down now" : "Comet {0}: lands here if brought down now, untracked", aim.CometId),
                    aim.LandX, aim.LandZ, false, i));
            }
            return icons.ToArray();
        }

        private static void Fault(string message)
        {
            if (++_faults <= 3)
            {
                Log.Warn("[TR comet aim] " + message);
            }
        }
    }
}
