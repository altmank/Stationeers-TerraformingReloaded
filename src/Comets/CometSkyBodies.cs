using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Objects;
using HarmonyLib;
using Objects.Rockets;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Every comet parked at Entry as a body in the game's own sky, circling the planet being played, the
    /// way the game's moons circle it (docs/COMET-ROCKETS.md, "The comet in the sky"). The game has no way to
    /// add a body after a world loads, so this is done with the game's own public pieces (a template, a
    /// CelestialBody, the planet's list of the bodies that circle it) and its two private lists of every
    /// body, by reflection.
    ///
    /// Each machine adds the same body from the comet list it already has: the orbit comes from the comet's
    /// id, the world and the Celestial terraforming settings, all the same everywhere, and the body's place is
    /// the game's own pure function of the sky's clock, which the game sends to every player. So the comet is
    /// in the same place on every machine with nothing sent while it orbits.
    ///
    /// The lists are never changed in place. The Ground Telescope looks through the list of every body on the
    /// occlusion thread (GroundTelescope.OnThreadUpdate, OrbitalSimulation.RaycastSky), which the game runs
    /// beside the main thread with no lock, and the Rocket Celestial Tracker counts and indexes it from the
    /// logic tick on the game tick's thread. Both read the list's field afresh each time. So a change is a new
    /// list, published by one reference write once it is complete: a reader holds the old list or the new,
    /// neither of which is touched again. The game itself writes the lists only while a world loads.
    ///
    /// A list that would be shorter than the one it replaces waits for the game tick's own main-thread
    /// moment (<see cref="ApplySafePoint"/>). The Rocket Celestial Tracker counts the list and then indexes
    /// it, in two reads, from the logic tick, which runs in the part of the game tick that leaves the main
    /// thread; a list made shorter between the two reads would be indexed past its end. The game tick is one
    /// loop that awaits each switch of thread, so none of its own off-thread work overlaps the moment on the
    /// main thread just before it leaves it, where the sea's level is set (GlobalAtmosphereLiquid.UpdateLiquid).
    /// A list made no shorter, a comet joining the sky, is published at once: every index valid in the old
    /// list is valid in the new.
    ///
    /// Main thread only, every frame (<see cref="Step"/>, from CometDirector). A world load or exit empties
    /// the game's lists; a body this no longer finds there is made again the next frame.
    /// </summary>
    public static class CometSkyBodies
    {
        /// <summary>The start of every comet body's id: its CelestialHash is Animator.StringToHash of this and the comet's id.</summary>
        public const string IdPrefix = "TRComet";

        private static AccessTools.FieldRef<OrbitalSimulation, List<Celestial>> _all;
        private static AccessTools.FieldRef<OrbitalSimulation, Dictionary<int, Celestial>> _byHash;
        private static volatile bool _installed;
        private static volatile bool _safePoint;
        private static volatile bool _waiting;
        private static volatile Shown[] _shown = new Shown[0];
        private static int _faults;

        /// <summary>One comet in the sky: its id, the game's body for it, and the orbit it was given.</summary>
        public sealed class Shown
        {
            public readonly int CometId;
            public readonly CelestialBody Body;
            public readonly SkyOrbit Orbit;

            internal Shown(int cometId, CelestialBody body, SkyOrbit orbit)
            {
                CometId = cometId;
                Body = body;
                Orbit = orbit;
            }
        }

        /// <summary>The sky bodies are installed (Patcher's "comet sky body").</summary>
        public static bool Installed => _installed;

        /// <summary>A comet leaves the sky at the game tick's own main-thread moment (Patcher's "comet sky safe point").</summary>
        public static bool SafePointInstalled => _safePoint;

        /// <summary>A shorter list is waiting for the game tick's main-thread moment; for tools/LiveCheck.</summary>
        public static bool LeavingWaits => _waiting;

        /// <summary>How many times a shorter list was published at the game tick's main-thread moment, and in a frame; for tools/LiveCheck.</summary>
        public static int ShortenedAtSafePoint;
        public static int ShortenedInFrame;

        /// <summary>
        /// Patcher's "comet sky safe point": a postfix on the sea's level, which the game tick sets on the main
        /// thread just before it leaves it for its off-thread work, the logic tick among it. Refused unless the
        /// game tick still does those in that order (<see cref="SafePointOrder"/>), from its own code.
        /// </summary>
        public static void ApplySafePoint(Harmony harmony, Func<MethodBase, List<string>> calls = null)
        {
            MethodInfo liquid = AccessTools.DeclaredMethod(typeof(GlobalAtmosphereLiquid), nameof(GlobalAtmosphereLiquid.UpdateLiquid), Type.EmptyTypes);
            MethodInfo tick = AccessTools.DeclaredMethod(typeof(GameManager), "GameTick");
            Type machine = tick?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            MethodInfo body = machine == null ? null : AccessTools.DeclaredMethod(machine, "MoveNext");
            if (liquid == null || body == null)
            {
                throw new MissingMethodException("the game tick, or the sea's level it sets, is gone");
            }
            string wrong = SafePointOrder((calls ?? CallsOf)(body));
            if (wrong != null)
            {
                throw new InvalidOperationException("the game tick no longer works the way the comet's leaving the sky relies on: " + wrong);
            }
            harmony.Patch(liquid, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(CometSkyBodies), nameof(SafePointPostfix))));
            _safePoint = true;
        }

        /// <summary>Every method a body calls, in the order its code has them, as declaring type and name.</summary>
        private static List<string> CallsOf(MethodBase method)
        {
            List<string> calls = new List<string>();
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
            {
                if (instruction.operand is MethodBase callee)
                {
                    calls.Add(callee.DeclaringType?.Name + "." + callee.Name);
                }
            }
            return calls;
        }

        /// <summary>
        /// Null when the game tick's body has the sea's level set once, before its one switch to other threads,
        /// and the logic tick after that switch and before its one switch back to the main thread; else why not.
        /// The calls are as declaring type and name, in the order the body's code has them.
        /// </summary>
        public static string SafePointOrder(IList<string> calls)
        {
            int liquid = -1;
            int away = -1;
            int logic = -1;
            int back = -1;
            int liquids = 0;
            int aways = 0;
            int backs = 0;
            for (int i = 0; i < calls.Count; i++)
            {
                switch (calls[i])
                {
                    case "GlobalAtmosphereLiquid.UpdateLiquid":
                        liquid = i;
                        liquids++;
                        break;
                    case "UniTask.SwitchToThreadPool":
                        away = i;
                        aways++;
                        break;
                    case "LogicStack.LogicStackTick":
                        logic = i;
                        break;
                    case "UniTask.SwitchToMainThread":
                        back = i;
                        backs++;
                        break;
                }
            }
            if (liquids != 1 || aways != 1 || backs != 1 || logic < 0)
            {
                return string.Format(CultureInfo.InvariantCulture, "it sets the sea's level {0} times, leaves the main thread {1} times, comes back {2} times, and {3} the logic tick",
                    liquids, aways, backs, logic < 0 ? "never runs" : "runs");
            }
            if (!(liquid < away && away < logic && logic < back))
            {
                return "the sea's level is not set on the main thread before the tick leaves it, or the logic tick does not run while it is away";
            }
            return null;
        }

        /// <summary>The game tick's main-thread moment: a shorter list that waited for it is published now.</summary>
        private static void SafePointPostfix()
        {
            if (!_waiting)
            {
                return;
            }
            try
            {
                Keep(true);
            }
            catch (Exception e)
            {
                Fault("A comet could not be taken out of the sky: " + e.Message);
            }
        }

        /// <summary>
        /// Patcher's "comet sky body": nothing is patched, but the two private lists of every body must be
        /// there with the types this writes, and the sky rules must pass their own check.
        /// </summary>
        public static void Apply()
        {
            if (AccessTools.Field(typeof(OrbitalSimulation), "_bodies")?.FieldType != typeof(List<Celestial>)
                || AccessTools.Field(typeof(OrbitalSimulation), "_bodiesLookup")?.FieldType != typeof(Dictionary<int, Celestial>))
            {
                throw new MissingFieldException("the sky no longer keeps its bodies in the two lists the comets join");
            }
            if (AccessTools.Constructor(typeof(CelestialBody), new[] { typeof(OrbitalSimulation), typeof(CelestialBodyTemplate) }) == null
                || AccessTools.Field(typeof(Celestial), nameof(Celestial.Bodies))?.FieldType != typeof(List<CelestialBody>))
            {
                throw new MissingMemberException("a sky body can no longer be made from a template, or a body no longer keeps the bodies that circle it");
            }
            string broken = CometSkyRules.Verdict;
            if (broken != null)
            {
                throw new InvalidOperationException("the sky rules did not pass their own check: " + broken);
            }
            _all = AccessTools.FieldRefAccess<OrbitalSimulation, List<Celestial>>("_bodies");
            _byHash = AccessTools.FieldRefAccess<OrbitalSimulation, Dictionary<int, Celestial>>("_bodiesLookup");
            _installed = true;
        }

        /// <summary>Every comet in the sky now, in comet id order; any thread.</summary>
        public static Shown[] All => _shown;

        /// <summary>The sky body of comet <paramref name="cometId"/>, or null; any thread.</summary>
        public static Shown For(int cometId)
        {
            foreach (Shown shown in _shown)
            {
                if (shown.CometId == cometId)
                {
                    return shown;
                }
            }
            return null;
        }

        /// <summary>The planet being played as the game's sky turns it, or null with no sky.</summary>
        public static SkyFrame? Frame()
        {
            OrbitalSimulation sky = OrbitalSimulation.System;
            RotatingCelestialBody planet = sky?.PlayerBody;
            if (!OrbitalSimulation.IsValid || planet?.Orbit == null)
            {
                return null;
            }
            UnityEngine.Vector3 axis = planet.RotationAxis;
            return new SkyFrame(new SkyVector(axis.x, axis.y, axis.z), OrbitalSimulation.Latitude, OrbitalSimulation.Longitude, planet.LongitudeAtEpoch,
                planet.SolDegreesPerDay, planet.IsRetrogradeRotation, planet.Orbit.Period, sky.TimeScale);
        }

        /// <summary>The orbit comet <paramref name="cometId"/> is given on this world, by the settings in force.</summary>
        public static SkyOrbit OrbitOf(SkyFrame frame, int cometId)
        {
            CelestialTuning tuning = Effective.Celestial;
            return CometSkyRules.Shipped.OrbitFor(frame, tuning.OrbitSeconds, tuning.OrbitInclinationDegrees, CometSkyRules.PhaseDegrees(cometId), CometSkyRules.CometOrbitAu);
        }

        /// <summary>Main thread, every frame: the sky holds a body for every comet parked at Entry, and for nothing else.</summary>
        internal static void Step() => Keep(false);

        /// <summary>
        /// The sky's comets made to match the comets parked at Entry. A list shorter than the one in force waits
        /// for the game tick's main-thread moment (<paramref name="safePoint"/>) when that is installed.
        /// </summary>
        private static void Keep(bool safePoint)
        {
            if (!_installed)
            {
                return;
            }
            OrbitalSimulation sky = OrbitalSimulation.System;
            SkyFrame? frame = Frame();
            Shown[] shown = _shown;
            if (frame == null || CelestialRules.Verdict != null)
            {
                if (shown.Length > 0)
                {
                    _shown = new Shown[0];
                }
                return;
            }
            // A world load or exit empties the game's lists; what is no longer there was taken out with them.
            List<Shown> standing = new List<Shown>();
            foreach (Shown one in shown)
            {
                if (sky.Find<Celestial>(one.Body.Hash) == one.Body && one.Body.OrbitingBody == sky.PlayerBody)
                {
                    standing.Add(one);
                }
            }
            List<int> parked = new List<int>();
            foreach (MapComet comet in CometMap.All)
            {
                if (comet.State == MapCometState.Arrived)
                {
                    parked.Add(comet.Id);
                }
            }
            parked.Sort();
            bool same = standing.Count == parked.Count && standing.Count == shown.Length;
            for (int i = 0; same && i < parked.Count; i++)
            {
                same = standing[i].CometId == parked[i] && standing[i].Orbit.SameAs(OrbitOf(frame.Value, parked[i]));
            }
            if (!same)
            {
                _waiting = !Rebuild(sky, frame.Value, standing, parked, safePoint || !_safePoint);
            }
            else
            {
                _waiting = false;
            }
        }

        /// <summary>
        /// The game's lists with these comets' bodies, in comet id order after every body of the world's own,
        /// made new and published whole. A body whose comet and orbit are unchanged is kept. A list shorter than
        /// the one in force is published only when <paramref name="mayShorten"/>; false when it waits.
        /// </summary>
        private static bool Rebuild(OrbitalSimulation sky, SkyFrame frame, List<Shown> standing, List<int> parked, bool mayShorten)
        {
            RotatingCelestialBody planet = sky.PlayerBody;
            HashSet<Celestial> leaving = new HashSet<Celestial>();
            foreach (Shown old in _shown)
            {
                leaving.Add(old.Body);
            }
            List<Shown> next = new List<Shown>();
            foreach (int id in parked)
            {
                SkyOrbit orbit = OrbitOf(frame, id);
                Shown kept = standing.Find(s => s.CometId == id && s.Orbit.SameAs(orbit));
                if (kept != null)
                {
                    next.Add(kept);
                    continue;
                }
                CelestialBody body = Make(sky, id, orbit, leaving, out string refused);
                if (body == null)
                {
                    Fault("Comet " + id + " could not be put in the sky: " + refused);
                    continue;
                }
                next.Add(new Shown(id, body, orbit));
            }

            List<Celestial> before = _all(sky);
            List<Celestial> after = new List<Celestial>(before.Count + next.Count);
            foreach (Celestial body in before)
            {
                if (!leaving.Contains(body))
                {
                    after.Add(body);
                }
            }
            Dictionary<int, Celestial> byHash = new Dictionary<int, Celestial>(_byHash(sky));
            foreach (Celestial body in leaving)
            {
                if (byHash.TryGetValue(body.Hash, out Celestial there) && there == body)
                {
                    byHash.Remove(body.Hash);
                }
            }
            List<CelestialBody> circling = new List<CelestialBody>(planet.Bodies.Count + next.Count);
            foreach (CelestialBody body in planet.Bodies)
            {
                if (!leaving.Contains(body))
                {
                    circling.Add(body);
                }
            }
            foreach (Shown one in next)
            {
                after.Add(one.Body);
                byHash[one.Body.Hash] = one.Body;
                circling.Add(one.Body);
            }

            if (after.Count < before.Count)
            {
                if (!mayShorten)
                {
                    return false;
                }
                if (_safePoint)
                {
                    ShortenedAtSafePoint++;
                }
                else
                {
                    ShortenedInFrame++;
                }
            }
            GuardTrackers(before, after);
            // The lookup and the planet's own list are read on the main thread only; the list of every body is
            // read on other threads, so it goes last, once everything it leads to is in place.
            Volatile.Write(ref _byHash(sky), byHash);
            planet.Bodies = circling;
            Volatile.Write(ref _all(sky), after);
            _shown = next.ToArray();

            CultureInfo c = CultureInfo.InvariantCulture;
            foreach (Shown one in next)
            {
                if (!standing.Contains(one))
                {
                    Log.Info(string.Format(c, "[TR comet sky] comet {0} is in the sky as body {1} of {2} (hash {3}): period {4:R} d, inclination {5:R} deg, node {6:R} rad, radius {7:R} AU, epoch {8:R} deg",
                        one.CometId, after.IndexOf(one.Body), after.Count, one.Body.Hash, one.Orbit.PeriodDays, one.Orbit.InclinationDegrees, one.Orbit.AscendingNodeRadians,
                        one.Orbit.SemiMajorAxisAu, one.Orbit.EpochDegrees));
                }
            }
            foreach (Celestial gone in leaving)
            {
                if (!after.Contains(gone))
                {
                    Log.Info("[TR comet sky] " + gone.Name + " left the sky" + (after.Count < before.Count ? (_safePoint ? ", at the game tick's main-thread moment" : ", in a frame") : ""));
                }
            }
            return true;
        }

        /// <summary>A comet's body, placed for the sky's present time before anything can see it. A body of the same id may only be one of the comets' own, leaving.</summary>
        private static CelestialBody Make(OrbitalSimulation sky, int id, SkyOrbit orbit, HashSet<Celestial> leaving, out string refused)
        {
            refused = null;
            string name = "Comet " + id.ToString(CultureInfo.InvariantCulture);
            CelestialBodyTemplate template = new CelestialBodyTemplate
            {
                Id = IdPrefix + id.ToString(CultureInfo.InvariantCulture),
                Name = new LocalizedStringReference(name),
                Hidden = true,
                LongitudeAtEpoch = (float)orbit.EpochDegrees,
                OrbitData = new OrbitData
                {
                    Period = orbit.PeriodDays,
                    Inclination = (float)orbit.InclinationDegrees,
                    Eccentricity = 0f,
                    SemiMajorAxisAu = orbit.SemiMajorAxisAu,
                    LongitudeOfAscendingNode = orbit.AscendingNodeRadians,
                    ArgumentOfPeriapsis = orbit.PeriapsisRadians,
                },
            };
            CelestialBody body = new CelestialBody(sky, template) { OrbitingBody = sky.PlayerBody };
            Celestial there = sky.Find<Celestial>(body.Hash);
            if (there != null && !leaving.Contains(there))
            {
                refused = "the sky already has a body of hash " + body.Hash + " (" + there.Name + ")";
                return null;
            }
            body.Initialize(sky.PlayerBody);
            // Until the game next places every body its direction would be nothing, which the telescope takes
            // for any direction at all; so it is placed now.
            body.Set(sky.SimulationTimeSeconds);
            body.SetPlayerVectorTo(sky.PlayerBody);
            return body;
        }

        /// <summary>
        /// The Rocket Celestial Tracker keeps an index into the list of every body, saved and sent, and asks for
        /// that index each logic tick; an index past the end of a shorter list throws. Before a list in which an
        /// index no longer means the body it did is published, every tracker pointing at or past the first change
        /// goes back to the first body, which every list has.
        /// </summary>
        private static void GuardTrackers(List<Celestial> before, List<Celestial> after)
        {
            int changed = 0;
            while (changed < before.Count && changed < after.Count && before[changed] == after[changed])
            {
                changed++;
            }
            if (changed >= before.Count)
            {
                return;
            }
            int moved = 0;
            GridController.AllStructuresPool.ForEach((Action<Structure>)(structure =>
            {
                if (structure is RocketCelestialTracker tracker && tracker.Index >= changed)
                {
                    tracker.Index = 0;
                    moved++;
                }
            }));
            if (moved > 0)
            {
                Log.Info("[TR comet sky] " + moved + " Rocket Celestial Tracker(s) pointed at a comet's place in the sky's list and were set back to the first body");
            }
        }

        private static void Fault(string message)
        {
            if (++_faults <= 5)
            {
                Log.Warn("[TR comet sky] " + message);
            }
        }
    }
}
