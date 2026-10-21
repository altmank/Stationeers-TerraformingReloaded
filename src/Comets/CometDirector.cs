using System;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Networking;
using TerraformingReloaded.Patching;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The one comet event in force, on every machine, and the show that plays it. Lives for the
    /// session on a hidden object of its own, so it is there for an event that arrives with the
    /// planet sync before any show exists.
    ///
    /// On the host the event is made here from a console command, sent at once, and kept as the
    /// current event until its darker sky has cleared, which is what the planet sync carries to a
    /// player who joins in the middle. On a client the event arrives from the host, from either
    /// route, and the show picks it up at the time it has reached. Everything that touches Unity
    /// happens in <see cref="Update"/>, on the main thread.
    /// </summary>
    internal sealed class CometDirector : MonoBehaviour
    {
        private static CometDirector _instance;
        private static readonly object Arrivals = new object();
        private static CometEvent _arrived;
        private static int _sequence = new System.Random().Next(1, 1 << 30);

        private CometShow _show;

        /// <summary>The last event called off, so a planet sync written just before it cannot start it again.</summary>
        private CometEvent _calledOff;

        /// <summary>The event in force: on the host the one it sends, on a client the one it plays.</summary>
        public static CometEvent Current { get; private set; }

        /// <summary>The show playing on this machine, if any.</summary>
        public static CometShow Showing => _instance != null ? _instance._show : null;

        /// <summary>Main thread only. Safe to call again.</summary>
        public static void Install()
        {
            if (_instance != null)
            {
                return;
            }
            GameObject holder = new GameObject("TR Comet director") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(holder);
            _instance = holder.AddComponent<CometDirector>();
            if (!GameManager.IsBatchMode)
            {
                holder.AddComponent<CometSkyGlow>();
            }
        }

        /// <summary>The host's event for the planet sync, while there is still something of it to see.</summary>
        public static CometEvent ForSync()
        {
            CometEvent current = Current;
            return current != null && !NetworkManager.IsClient && CometEvent.Clock < current.OverClock ? current : null;
        }

        /// <summary>An event from the host, from the message or the planet sync. Any thread.</summary>
        public static void FromHost(CometEvent comet)
        {
            lock (Arrivals)
            {
                _arrived = comet;
            }
        }

        /// <summary>
        /// Host, main thread: picks where the comet lands, makes the event, sends it and starts the
        /// show here. Null on success, or why it could not.
        /// </summary>
        public static string Launch(CometPlan plan, out CometSite site)
        {
            site = null;
            Install();
            if (Current != null && CometEvent.Clock < Current.StartClock + CometEvent.EndSeconds)
            {
                return "A comet is already on its way. Wait for it to land and settle, or: terraform comet stop";
            }
            int seed = new System.Random().Next();
            site = CometSite.Pick(plan, CometRules.Shipped, seed, out string refused);
            if (site == null)
            {
                return refused;
            }
            CometEvent comet = new CometEvent
            {
                Sequence = ++_sequence,
                StartClock = CometEvent.Clock + CometEvent.LeadSeconds,
                Plan = plan,
                Impact = site.Impact,
                Start = site.Start,
                Travel = site.Travel,
                Seed = seed,
                Reactions = CometReactions.Of(plan.Types, plan.Shares, PlanetOxidiserShare()),
            };
            Current = comet;
            CometNet.Broadcast(comet);
            _instance.Play(comet);
            CultureInfo c = CultureInfo.InvariantCulture;
            Debug.Log(string.Format(c, "[TR comet] event {0}: {1:N0} mol, scale {2:0.00}, seed {3}, starts at clock {4:0.00}, impact {5} (clock {6:0.00}), start {7}, reactions {8}. {9}",
                comet.Sequence, plan.Moles, plan.Scale, seed, comet.StartClock, comet.Impact, comet.ImpactClock, comet.Start, comet.Reactions, site.Log));
            return null;
        }

        /// <summary>
        /// Host, main thread: the impact event of a comet brought down by rocket (CometLand), the console comet's
        /// own, landing where and when its fall ends. Nothing about where it lands is decided here: no safe
        /// distance, no turn. A mineral comet shows the air it throws up and no reaction.
        /// </summary>
        internal static CometEvent Land(CometLanding landing)
        {
            Install();
            double total = landing.TotalMoles;
            int count = Math.Max(1, landing.Gases.Length);
            CometPlan plan = new CometPlan
            {
                Moles = landing.ShowMoles,
                Types = landing.Gases.Length > 0 ? (Assets.Scripts.Atmospherics.Chemistry.GasType[])landing.Gases.Clone()
                    : new[] { Assets.Scripts.Atmospherics.Chemistry.GasType.CarbonDioxide },
                Shares = new double[count],
            };
            for (int i = 0; i < count; i++)
            {
                plan.Shares[i] = landing.Gases.Length == 0 ? 1.0 : total > 0.0 ? landing.Moles[i] / total : 1.0 / count;
            }
            Vector3 impact = new Vector3((float)landing.ImpactX, (float)landing.ImpactY, (float)landing.ImpactZ);
            Vector3 travel = new Vector3((float)landing.TravelX, (float)landing.TravelY, (float)landing.TravelZ).normalized;
            CometEvent comet = new CometEvent
            {
                Sequence = ++_sequence,
                StartClock = landing.ImpactClock - CometEvent.ApproachSeconds,
                Plan = plan,
                Impact = impact,
                Start = impact - travel * CometSite.PathLength(plan.Scale),
                Travel = travel,
                Seed = landing.Seed,
                Reactions = landing.Kind == CometKind.Ice ? CometReactions.Of(plan.Types, plan.Shares, PlanetOxidiserShare()) : new CometReactions.Reactions(0.0, 0.0, 0.0, 0.0, 0.0),
                RocketComet = landing.CometId,
            };
            Current = comet;
            CometNet.Broadcast(comet);
            _instance.Play(comet);
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[TR comet] event {0}: comet {1} brought down by rocket, {2:N0} mol, scale {3:0.00}, seed {4}, starts at clock {5:R}, impact {6} (clock {7:R}), reactions {8}",
                comet.Sequence, landing.CometId, plan.Moles, plan.Scale, comet.Seed, comet.StartClock, comet.Impact, comet.ImpactClock, comet.Reactions));
            return comet;
        }

        /// <summary>Host: stops the comet here and on every client. False when there is none.</summary>
        public static bool CallOff()
        {
            CometEvent current = Current;
            if (current == null)
            {
                return false;
            }
            Current = null;
            CometEvent off = new CometEvent
            {
                Sequence = current.Sequence,
                StartClock = current.StartClock,
                Plan = current.Plan,
                Impact = current.Impact,
                Start = current.Start,
                Travel = current.Travel,
                Seed = current.Seed,
                Reactions = current.Reactions,
                CalledOff = true,
            };
            CometNet.Broadcast(off);
            _instance?.EndShow();
            CometSky.Dim = 0f;
            return true;
        }

        /// <summary>The share of the planet's air, by moles, that can feed a fire.</summary>
        private static double PlanetOxidiserShare()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tank == null)
            {
                return 0.0;
            }
            double oxidiser = 0.0;
            double total = 0.0;
            Planet.UnderTankLock(() =>
            {
                total = tank.TotalQuantityGas().ToDouble();
                foreach (Assets.Scripts.Atmospherics.Chemistry.GasType type in FireGases.Oxidisers)
                {
                    oxidiser += tank.Get(type).ToDouble();
                }
            });
            return total > 0.0 ? oxidiser / total : 0.0;
        }

        private void Play(CometEvent comet)
        {
            EndShow();
            _show = CometShow.Play(comet);
        }

        private void EndShow()
        {
            if (_show != null)
            {
                Destroy(_show.gameObject);
            }
            _show = null;
        }

        private void Update()
        {
            // The comets on the space map, apart from the impact event: a fault in one costs only itself.
            try
            {
                CometMap.Step();
            }
            catch (Exception e)
            {
                CometMap.Fault("the comets on the space map stopped being stepped this frame: " + e.Message);
            }
            // The parked comets in the sky, then the rockets aiming them, which read the sky.
            try
            {
                CometSkyBodies.Step();
                CometAim.Step();
            }
            catch (Exception e)
            {
                CometMap.Fault("the comets in the sky stopped being kept this frame: " + e.Message);
            }
            // The comets brought down, falling: each warns once, and on the host lands when its fall is over.
            try
            {
                CometLand.Step();
            }
            catch (Exception e)
            {
                CometLand.Fault("the falling comets stopped being stepped this frame: " + e.Message);
            }
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Debug.LogError("[TR comet] director: " + e);
                Current = null;
                CometSky.Dim = 0f;
                EndShow();
            }
        }

        private void Step()
        {
            CometEvent arrived;
            lock (Arrivals)
            {
                arrived = _arrived;
                _arrived = null;
            }
            bool inWorld = WorldSetting.Current != null && PlanetaryAtmosphereSimulation.GetGlobalGasMix() != null;
            if (!inWorld)
            {
                // A save in the middle of an event drops it: nothing of it survives leaving the world.
                Current = null;
                CometSky.Dim = 0f;
                EndShow();
                return;
            }
            double now = CometEvent.Clock;
            if (arrived != null && NetworkManager.IsClient)
            {
                if (arrived.CalledOff)
                {
                    _calledOff = arrived;
                    if (arrived.SameAs(Current))
                    {
                        Current = null;
                        EndShow();
                    }
                }
                else if (!arrived.SameAs(Current) && !arrived.SameAs(_calledOff) && now < arrived.OverClock && now > arrived.StartClock - 60.0)
                {
                    Current = arrived;
                    Debug.Log(string.Format(CultureInfo.InvariantCulture, "[TR comet] event {0} from the host, {1:0.0} s after it began", arrived.Sequence, now - arrived.StartClock));
                    if (now < arrived.StartClock + CometEvent.EndSeconds)
                    {
                        Play(arrived);
                    }
                }
            }
            CometEvent current = Current;
            if (current != null && (now >= current.OverClock || now < current.StartClock - 60.0))
            {
                Current = current = null;
            }
            CometSky.Dim = current == null ? 0f
                : (float)CometRules.Shipped.SkyDim(current.Plan.Scale, now - current.ImpactClock);
        }
    }
}
