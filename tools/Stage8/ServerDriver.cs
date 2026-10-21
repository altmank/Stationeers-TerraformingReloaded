using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using Networks;
using Objects.Rockets;
using TerraformingReloaded.Comets;
using TerraformingReloaded.Patching;
using UnityEngine;

namespace TerraformingReloaded.Stage8
{
    /// <summary>
    /// The dedicated server's end. Phase "make", a new Creative Mars2 world:
    ///   up       the world runs, unpaused, its planet ticking, every comet part of the mod installed;
    ///   client   the joined player's body stands where their own game says it does, wears a suit and helmet;
    ///   comets   two comets made at a site the game's own discovery made and sent to Entry (A to land near the
    ///            player, D to be saved in its fall), a launch pad, a rocket at Entry with a payload bay, and a
    ///            Rocket Control and a Map motherboard by the player;
    ///   A        once parked: its direction in the sky at a shared clock, the bay's aim at a shared clock, then the
    ///            bay's target held until the pass brings it in reach, Deorbit, a line of structures by the impact,
    ///            the player sent to stand where the wave peaks at 300 kPa, the suit damage, the zones, the sky;
    ///   B        a console comet the player calls from their own game with serverrun, aimed from them;
    ///   C        with the player gone, a console comet; the player's game joins again during it;
    ///   D        Deorbit, and a save 8 s into the fall.
    /// Phase "load": the save, held paused until the player's game has joined again, then D lands.
    /// Every comparison of the two ends is made by the client from the files this writes.
    /// </summary>
    internal sealed class ServerDriver : IDriver
    {
        private const string Station = "trstage8";
        private const double StageSeconds = 1500.0;
        private const int Engines = 8;
        private const string RocketMolesA = "300000";
        private const string RocketMolesD = "100000";
        private const double ConsoleMoles = 1e6;
        private const double ConsoleRange = 2500.0;
        private const double LateMoles = 1e7;
        private const double SuitPeakKPa = 300.0;
        private const double SaveAfterSeconds = 8.0;
        private const float WorldEdge = 1900f;

        private readonly bool _load;
        private string _stage = "boot";
        private double _stageClock = double.NaN;
        private float _stageReal;

        private int _bootTick = -1;
        private float _bootReal;
        private bool _unpaused;
        private float _pausedSince = -1f;

        private ulong _clientId;
        private float _seenReady = -1f;
        private string _clientName;
        private Human _human;

        private Rocket _rocket;
        private RocketPayloadBay _bay;
        private int _cometA;
        private int _cometD;
        private Vector3 _targetA;
        private float _viewsWritten = -10f;
        private float _syncWritten = -10f;

        // The landing in hand.
        private LandingView _landing;
        private int _sequence;
        private Vector3 _spot;
        private double _arrival;
        private (float Brute, float Burn) _suitBefore;
        private Vector3 _bodyBefore;
        private readonly List<long> _built = new List<long>();

        // The console comet.
        private Vector3 _consoleFrom;
        private float _consoleYaw;
        private double _consoleSentAt;
        private int _consoleSequence;
        private double _consoleImpactClock;
        private Vector3 _consoleImpact;

        public ServerDriver(string phase)
        {
            _load = phase == "load";
        }

        private static double Clock => Bus.Clock;

        private void Next(string stage)
        {
            _stage = stage;
            _stageClock = Clock;
            _stageReal = Time.realtimeSinceStartup;
            Bus.Info("stage " + stage);
        }

        private bool Overdue(string what, double seconds = StageSeconds)
        {
            if (Time.realtimeSinceStartup - _stageReal > seconds)
            {
                Bus.Abort(what + " did not happen within " + seconds.ToString("0", Bus.C) + " s");
                _stage = "done";
                return true;
            }
            return false;
        }

        public void Late()
        {
        }

        public void Step()
        {
            if (_stage == "done" || Bus.Failed)
            {
                return;
            }
            if (WorldSetting.Current == null || PlanetaryAtmosphereSimulation.GetGlobalGasMix() == null || GameManager.GameState != GameState.Running)
            {
                return;
            }
            KeepRunning();
            WriteViews();
            WriteSync();
            WriteBody();
            OpenLander();
            if (_load)
            {
                StepLoad();
            }
            else
            {
                StepMake();
            }
        }

        // ---- running --------------------------------------------------------------------------------------------

        /// <summary>
        /// A dedicated server with no player may pause its world again (seen on the test server). A pause of more than
        /// 2 s while nobody is joining is that, and is ended the way the game ends the pause of a join. A run held
        /// paused for a joining player (phase load) is left alone.
        /// </summary>
        private void KeepRunning()
        {
            bool hold = _load && _stage != "load-land" && _stage != "load-wait";
            bool joining = NetworkBase.Clients.Any(c => c != null && !c.IsHost && c.state != ClientState.Ready && c.state != ClientState.Disconnected);
            if (!WorldManager.IsGamePaused || hold || joining || !_unpaused)
            {
                _pausedSince = -1f;
                return;
            }
            if (_pausedSince < 0f)
            {
                _pausedSince = Time.realtimeSinceStartup;
            }
            else if (Time.realtimeSinceStartup - _pausedSince > 2f)
            {
                Unpause("the world paused itself with nobody joining");
                _pausedSince = -1f;
            }
        }

        private static void Unpause(string why)
        {
            if (NetworkBase.IsPaused)
            {
                AccessTools.Method(typeof(NetworkBase), "PauseEvent", new[] { typeof(bool) })?.Invoke(null, new object[] { false });
            }
            WorldManager.SetGamePause(pauseGame: false);
            Bus.Info("unpaused: " + why);
        }

        /// <summary>Every 5 s: every comet as it stands at this moment's clock, for the client to work out at the same clock.</summary>
        private void WriteViews()
        {
            if (Time.realtimeSinceStartup - _viewsWritten < 5f)
            {
                return;
            }
            _viewsWritten = Time.realtimeSinceStartup;
            double t = Clock;
            CometView[] views = ModState.ViewsAt(t);
            Dictionary<string, string> file = new Dictionary<string, string> { { "t", Bus.R(t) }, { "count", views.Length.ToString(Bus.C) } };
            for (int i = 0; i < views.Length; i++)
            {
                file["v" + i.ToString(Bus.C)] = Line(views[i]);
            }
            Bus.Write("server.views", file);
        }

        internal static string Line(CometView v) => string.Join("|", new[]
        {
            v.Id.ToString(Bus.C), ((int)v.State).ToString(Bus.C), v.AtNode.ToString(Bus.C), v.Hop.ToString(Bus.C), v.HopCount.ToString(Bus.C), v.FromNode.ToString(Bus.C),
            v.ToNode.ToString(Bus.C), Bus.R(v.Progress), Bus.R(v.RouteShare), Bus.R(v.FuelLeft), Bus.R(v.CometKg), v.Engines.ToString(Bus.C),
        });

        private float _bodyWritten = -10f;
        private float _landerSeen = -1f;
        private bool _landerOpened;

        /// <summary>
        /// A new player arrives seated in a landing capsule (LanderCapsule), locked in while it comes down; the host
        /// opens and unlocks it 3.5 s after it lands (LanderCapsule.TerminateDescent, WaitThenOpen), and only then can
        /// the player leave it. If a joined player's capsule is still locked a minute after the server first sees it,
        /// the server does what the end of a landing does, open and unlock it, and says so.
        /// </summary>
        private void OpenLander()
        {
            if (_human?.ParentSlot?.Parent is not LanderCapsule lander || _landerOpened)
            {
                return;
            }
            if (_landerSeen < 0f)
            {
                _landerSeen = Time.realtimeSinceStartup;
                Bus.Info(string.Format(Bus.C, "the player is seated in landing capsule {0} at {1}: locked {2}, open {3}", lander.ReferenceId, lander.transform.position, lander.IsLocked, lander.IsOpen));
            }
            if (!lander.IsLocked)
            {
                return;
            }
            if (Time.realtimeSinceStartup - _landerSeen < 60f)
            {
                return;
            }
            _landerOpened = true;
            if (!lander.IsOpen)
            {
                OnServer.Interact(lander.InteractOpen, 1);
            }
            OnServer.Interact(lander.InteractLock, 0);
            Bus.Info(string.Format(Bus.C, "the landing capsule {0} was still locked 60 s after the server first saw the player in it; opened and unlocked as the end of a landing does: locked {1}, open {2}",
                lander.ReferenceId, lander.IsLocked, lander.IsOpen));
        }

        /// <summary>Twice a second: where the server has the joined player's body and the way it faces, from what their game sends.</summary>
        private void WriteBody()
        {
            if (_human == null || Time.realtimeSinceStartup - _bodyWritten < 0.5f)
            {
                return;
            }
            _bodyWritten = Time.realtimeSinceStartup;
            Vector3 at = _human.transform.position;
            Vector3 f = Facing(_human);
            Bus.Write("server.body", new Dictionary<string, string>
            {
                { "x", Bus.R(at.x) }, { "y", Bus.R(at.y) }, { "z", Bus.R(at.z) }, { "yaw", Bus.R(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg) },
                { "utc", DateTime.UtcNow.Ticks.ToString(Bus.C) }, { "clock", Bus.R(Clock) },
                { "slot", _human.ParentSlot == null ? "none" : (_human.ParentSlot.Parent?.PrefabName ?? "a slot") + " " + _human.ParentSlot.Parent?.ReferenceId.ToString(Bus.C) },
            });
        }

        /// <summary>Every second: the planet states the host wrote into the mod's section, with its clock at each.</summary>
        private void WriteSync()
        {
            if (Time.realtimeSinceStartup - _syncWritten < 1f)
            {
                return;
            }
            _syncWritten = Time.realtimeSinceStartup;
            var written = SyncDigest.WrittenSoFar();
            Dictionary<string, string> file = new Dictionary<string, string> { { "count", written.Length.ToString(Bus.C) } };
            for (int i = 0; i < written.Length; i++)
            {
                file["w" + i.ToString(Bus.C)] = written[i].Digest + "@" + Bus.R(written[i].Clock);
            }
            Bus.Write("server.sync", file);
        }

        private void RunCommand(params string[] args)
        {
            string answer;
            try
            {
                answer = Util.Commands.CommandLine.CommandsMap.TryGetValue("terraform", out Util.Commands.CommandBase command) ? command.Execute(args) ?? "(null)" : "terraform is not registered";
            }
            catch (Exception e)
            {
                answer = "THREW " + e;
            }
            Bus.Info("cmd terraform " + string.Join(" ", args) + " -> " + answer.Replace("\r", "").Replace("\n", " / "));
        }

        // ---- phase make ------------------------------------------------------------------------------------------

        private void StepMake()
        {
            switch (_stage)
            {
                case "boot": Boot(); break;
                case "wait client": WaitClient(); break;
                case "flying": WaitParked(); break;
                case "sky": WaitClientFile("client.sky-done", "aim", 180.0); break;
                case "aim": Aim(); break;
                case "aim icons": WaitClientFile("client.icons-done", "aim gap", 240.0); break;
                case "aim gap": AimGap(); break;
                case "fired": WaitFired(); break;
                case "wave": Wave(); break;
                case "zones": Zones(); break;
                case "dim": Dim(); break;
                case "clear A": WaitClear("console"); break;
                case "console": Console_(); break;
                case "console sent": ConsoleSent(); break;
                case "console wave": ConsoleWave(); break;
                case "clear B": WaitClear("late"); break;
                case "late": Late_(); break;
                case "late fired": LateFired(); break;
                case "clear C": WaitClear("save"); break;
                case "save": AimD(); break;
                case "save fired": WaitFiredD(); break;
                case "saving": Saving(); break;
                case "saved": Saved(); break;
            }
        }

        private const float BootSteadySeconds = 5f;
        private const float BootLimitSeconds = 60f;
        private float _bootSteady = -1f;
        private int _bootRepauses;

        private void Boot()
        {
            // A new dedicated world pauses itself until a player acts, and was seen on bt to pause again within
            // seconds of being unpaused (REPORT-020: 8 ticks, then paused at the judgement). So while booting it is
            // unpaused whenever it is found paused, each time logged, and judged up only once it has run unpaused
            // for BootSteadySeconds in a row; after BootLimitSeconds it is judged as it stands.
            if (WorldManager.IsGamePaused)
            {
                Unpause(_unpaused ? "the new world paused itself again while booting (" + (++_bootRepauses) + ")" : "a new world pauses itself until a player acts");
                _bootSteady = -1f;
            }
            else if (_bootSteady < 0f)
            {
                _bootSteady = Time.realtimeSinceStartup;
            }
            _unpaused = true;
            if (_bootTick < 0)
            {
                _bootTick = (int)GameManager.GameTickCount;
                _bootReal = Time.realtimeSinceStartup;
                return;
            }
            bool steady = _bootSteady >= 0f && Time.realtimeSinceStartup - _bootSteady >= BootSteadySeconds;
            if (Time.realtimeSinceStartup - _bootReal < 10f || (!steady && Time.realtimeSinceStartup - _bootReal < BootLimitSeconds))
            {
                return;
            }
            long ticks = GameManager.GameTickCount - _bootTick;
            bool parts = CometMap.SaveInstalled && CometSkyBodies.Installed && CometSkyBodies.SafePointInstalled && CometAim.ReadoutsInstalled && CometLand.Installed
                && CometRules.Verdict == null && CelestialRules.Verdict == null;
            bool pass = ticks >= 5 && !WorldManager.IsGamePaused && parts && SpaceMap.Current?.EntryNode != null && NetworkManager_IsServer();
            Bus.Judge("server up", pass, string.Format(Bus.C, "world {0} | {1} game ticks since boot, " + Bus.R(Time.realtimeSinceStartup - _bootReal) + " s | re-paused " + _bootRepauses + " times while booting | paused {2} | comet parts {3} | rules {4} | celestial {5} | planet {6} | gate {7}",
                WorldSetting.Current.Id, ticks, WorldManager.IsGamePaused, parts ? "installed" : "MISSING", CometRules.Verdict ?? "pass", CelestialRules.Verdict ?? "pass",
                CometMap.PlanetSize(), Gate.Describe()));
            if (!pass)
            {
                Bus.Abort("the server is not up with the mod live");
                _stage = "done";
                return;
            }
            RunCommand("set", "MaxDamagingRocks", "0");
            bool rocks = (int)Math.Round(Effective.Celestial.MaxDamagingRocks) == 0;
            Bus.Judge("server no falling rocks", rocks, "MaxDamagingRocks " + Bus.R(Effective.Celestial.MaxDamagingRocks) + ", so the suit damage judged is the wave's alone");
            WriteSettings();
            Bus.Write("server.ready", new Dictionary<string, string> { { "clock", Bus.R(Clock) }, { "world", WorldSetting.Current.Id }, { "ticks", GameManager.GameTickCount.ToString(Bus.C) } });
            Next("wait client");
        }

        private static bool NetworkManager_IsServer() => Assets.Scripts.Networking.NetworkManager.IsServer && GameManager.IsBatchMode;

        /// <summary>The host's settings a joining player's game takes from the planet sync.</summary>
        private static void WriteSettings()
        {
            double[] celestial = Effective.Celestial.ToArray();
            Bus.Write("server.settings", new Dictionary<string, string>
            {
                { "celestial", string.Join(";", celestial.Select(Bus.R)) },
                { "ghg", Bus.R(Effective.GhgResponseScale) },
                { "density", Bus.R(Effective.DensityResponseScale) },
                { "albedo", Bus.R(Effective.AirlessAlbedo) },
                { "sky", Effective.DynamicSky.ToString() },
            });
        }

        private void WaitClient()
        {
            Dictionary<string, string> ready = Bus.Read("client.ready");
            if (ready == null)
            {
                Overdue("the client joining", 1200.0);
                return;
            }
            _clientId = Bus.U(ready, "id");
            _clientName = Bus.S(ready, "name");
            _human = Human.AllHumans.FirstOrDefault(h => h != null && h.OrganBrain != null && h.OrganBrain.IsOnline && h.OwnerClientId == _clientId);
            Dictionary<string, string> pos = Bus.Read("client.pos");
            if (_seenReady < 0f)
            {
                _seenReady = Time.realtimeSinceStartup;
            }
            if (_human == null || pos == null)
            {
                if (Time.realtimeSinceStartup - _seenReady > 120f)
                {
                    Bus.Abort("the joined player's character was not online on the server within 120 s of its game joining");
                    _stage = "done";
                }
                return;
            }
            Vector3 theirs = new Vector3((float)Bus.D(pos, "x"), (float)Bus.D(pos, "y"), (float)Bus.D(pos, "z"));
            double age = (DateTime.UtcNow.Ticks - Bus.L(pos, "utc")) / (double)TimeSpan.TicksPerSecond;
            Vector3 ours = _human.transform.position;
            double off = Vector3.Distance(theirs, ours);
            if (off > 1.5 && Time.realtimeSinceStartup - _seenReady < 60f)
            {
                return;
            }
            bool pass = off <= 1.5 && age < 5.0;
            Bus.Judge("server sees the client", pass, string.Format(Bus.C, "client {0} id {1}: its game puts it at {2}, {3:0.0} s ago; the server at {4}, {5:0.###} m apart | in {6}",
                _clientName, _clientId, theirs, age, ours, off,
                _human.ParentSlot == null ? "no slot" : "slot " + _human.ParentSlot.SlotIndex + " of " + _human.ParentSlot.Parent?.PrefabName + " (" + _human.ParentSlot.Parent?.GetType().Name + ")"));
            // A change to the planet once the player is in, for the client to see arrive with the planet sync: a quiet
            // world's planet does not move from one sync to the next. Added at the planet's own temperature.
            RunCommand("gas", "add", "CarbonDioxide", "1000");
            Bus.Write("server.gas-added", new Dictionary<string, string> { { "clock", Bus.R(Clock) }, { "moles", "1000" } });
            DressPlayer();
            SetUp();
        }

        /// <summary>A suit and a helmet on the player, for the wave's suit damage; Creative keeps them breathing without one.</summary>
        private void DressPlayer()
        {
            if (_human.SuitSlot.Get() == null)
            {
                OnServer.Create<DynamicThing>("ItemEvaSuit", _human.SuitSlot);
            }
            if (_human.HelmetSlot.Get() == null)
            {
                OnServer.Create<DynamicThing>("ItemSpaceHelmet", _human.HelmetSlot);
            }
        }

        private (float Brute, float Burn) Suit()
        {
            float brute = 0f;
            float burn = 0f;
            foreach (Slot slot in new[] { _human?.SuitSlot, _human?.HelmetSlot })
            {
                IndestructableDamageState damage = slot?.Get()?.DamageState;
                if (damage != null)
                {
                    brute += damage.Brute;
                    burn += damage.Burn;
                }
            }
            return (brute, burn);
        }

        private void SetUp()
        {
            bool dressed = _human.SuitSlot.Get() != null && _human.HelmetSlot.Get() != null;
            Vector3 by = _human.transform.position + Vector3.right * 2f + Vector3.up;
            DynamicThing rockets = Spawn<DynamicThing>("MotherboardRockets", by);
            DynamicThing map = Spawn<DynamicThing>("MotherboardMap", by + Vector3.forward * 1f);
            SpaceMap spaceMap = SpaceMap.Current;
            SpaceMapNode site = Discover(spaceMap);
            if (site == null)
            {
                return;
            }
            double route = RouteDeltaV(site);
            double premix = ModState.PremixKgPerMole();
            int index = CometMap.Sites().IndexOf(site) + 1;
            // A steers with the most its engines can burn in the fall, so its reach is the most it can be.
            double reserveA = Engines * CelestialRules.EngineMolesPerSecond * Effective.Celestial.EntryWindowSeconds + 500.0;
            _cometA = Make(index, RocketMolesA, "Water", route, premix, reserveA);
            _cometD = Make(index, RocketMolesD, "CarbonDioxide", route, premix, 500.0);
            if (_cometA == 0 || _cometD == 0)
            {
                return;
            }
            RunCommand("comet", "send", _cometA.ToString(Bus.C));
            RunCommand("comet", "send", _cometD.ToString(Bus.C));
            ChartRoute(_cometA);
            Spawn<LaunchMount>("StructureLaunchMount", new Vector3(300f, 300f, -400f));
            EngineFuselage mount = Spawn<EngineFuselage>("StructureEngineMountTypeA1", new Vector3(100f, 400f, 100f));
            RocketNetwork network = mount?.RocketNetwork;
            _rocket = network?.Rocket;
            _bay = Spawn<RocketPayloadBay>("StructureRocketLargePayloadBay", new Vector3(124f, 400f, 100f));
            bool built = rockets != null && map != null && _rocket != null && _bay != null;
            if (built)
            {
                _bay.RocketData = new RocketData(network, _bay.ThingTransformPosition - mount.ThingTransformPosition);
                network.AdoptFromLoad(_bay);
                network.RefreshRocket();
                _rocket.SetCurrentNode(spaceMap.EntryNode);
                _rocket.RocketState = RocketState.InSpace;
            }
            Bus.Judge("server set up", built && dressed, string.Format(Bus.C, "comets A {0} and D {1} from {2} ({3:R} delta-v) sent to Entry | rocket {4} bay {5} at {6} | rocket motherboard {7}, map motherboard {8} by the player | suit and helmet {9}",
                _cometA, _cometD, site.DisplayName, route, _rocket?.ReferenceId, _bay?.ReferenceId, _rocket?.CurrentNode?.DisplayName, rockets?.ReferenceId, map?.ReferenceId, dressed ? "worn" : "MISSING"));
            if (!built)
            {
                Bus.Abort("the rocket, its bay or a motherboard did not spawn");
                _stage = "done";
                return;
            }
            Bus.Write("server.flying", new Dictionary<string, string>
            {
                { "a", _cometA.ToString(Bus.C) }, { "d", _cometD.ToString(Bus.C) },
                { "rocketboard", rockets.ReferenceId.ToString(Bus.C) }, { "mapboard", map.ReferenceId.ToString(Bus.C) },
                { "bay", _bay.ReferenceId.ToString(Bus.C) },
            });
            Next("flying");
        }

        /// <summary>
        /// The rocket map draws only charted nodes and the sites under them (MapView.AddLocationsToMap and
        /// AddMapLocation), and a new world has charted only Entry, so a comet's icon has nowhere to stand. A player
        /// sees a site only after charting its way there; the game charts a node when its parent node's chart
        /// points reach the connection's difficulty (SpaceMapNode.OnChartPointsUpdated), which is what a rocket's
        /// scan gives. Every Static node on the comet's route and above it gets charted that way; one whose parent
        /// keeps no chart points is set charted directly. Both are sent to every player as the game sends them.
        /// </summary>
        private void ChartRoute(int cometId)
        {
            object comet = null;
            foreach (object c in (Array)AccessTools.Property(typeof(CometMap), "All").GetValue(null))
            {
                if ((int)AccessTools.Field(c.GetType(), "Id").GetValue(c) == cometId)
                {
                    comet = c;
                }
            }
            long[] route = comet == null ? new long[0] : (long[])AccessTools.Field(comet.GetType(), "Route").GetValue(comet) ?? new long[0];
            long site = comet == null ? 0L : (long)AccessTools.Field(comet.GetType(), "SiteId").GetValue(comet);
            List<string> done = new List<string>();
            HashSet<long> nodes = new HashSet<long>();
            foreach (long id in new[] { site }.Concat(route))
            {
                for (SpaceMapNode n = SpaceMapNode.Get(id); n != null; n = n.ParentConnection?.Parent)
                {
                    if (!nodes.Add(n.ReferenceId) || (n.NodeType != NodeType.Static && n.NodeType != NodeType.Entry) || n.IsCharted)
                    {
                        continue;
                    }
                    NodeConnection up = n.ParentConnection;
                    string how;
                    try
                    {
                        if (up?.Parent != null && up.Parent.ChartData != null)
                        {
                            up.Parent.ChartPoints = Math.Max(up.Parent.ChartPoints, up.Difficulty);
                        }
                        how = n.IsCharted ? "charted by its parent's chart points" : "set charted";
                        if (!n.IsCharted)
                        {
                            n.IsCharted = true;
                        }
                    }
                    catch (Exception e)
                    {
                        // The setter refreshes the rocket screen after it has set the flag and flagged it to be sent.
                        how = "charted, the rocket screen's refresh threw " + e.GetType().Name;
                    }
                    done.Add(n.DisplayName + " (" + n.ReferenceId.ToString(Bus.C) + "): " + how);
                }
            }
            List<long> shown = nodes.Where(id => SpaceMapNode.Get(id)?.IsCharted == true).ToList();
            Bus.Info("charted for the rocket map | route " + string.Join(",", route) + " from site " + site + " | " + (done.Count == 0 ? "nothing needed charting" : string.Join("; ", done)));
            Bus.Write("server.charted", new Dictionary<string, string> { { "ids", string.Join(";", shown.Select(id => id.ToString(Bus.C))) } });
        }

        private int Make(int index, string moles, string gas, double route, double premix, double reserve)
        {
            HashSet<int> before = new HashSet<int>(CometMap.Views().Select(v => v.Id));
            string[] args = { "comet", "make", index.ToString(Bus.C), moles, gas };
            RunCommand(args.Concat(new[] { "engines=" + Engines.ToString(Bus.C), "fuel=0" }).ToArray());
            CometView probe = CometMap.Views().FirstOrDefault(v => !before.Contains(v.Id));
            if (probe.Id == 0)
            {
                Bus.Abort("the mod's make verb made no comet of " + moles + " " + gas);
                _stage = "done";
                return 0;
            }
            before.Add(probe.Id);
            double reserveKg = reserve * premix;
            double fuel = 1.02 * CelestialRules.Shipped.FuelNeeded(probe.CometKg + reserveKg, Engines, route, premix) + reserve;
            RunCommand(args.Concat(new[] { "engines=" + Engines.ToString(Bus.C), "fuel=" + Bus.R(fuel) }).ToArray());
            int id = CometMap.Views().Select(v => v.Id).FirstOrDefault(i => !before.Contains(i));
            if (id == 0)
            {
                Bus.Abort("the mod's make verb made no comet of " + moles + " " + gas + " with fuel");
                _stage = "done";
            }
            return id;
        }

        private void WaitParked()
        {
            CometView[] views = CometMap.Views();
            bool parked = views.Any(v => v.Id == _cometA && v.State == MapCometState.Arrived) && CometSkyBodies.For(_cometA) != null;
            if (!parked)
            {
                Overdue("comet A parking at Entry and going into the sky", 3000.0);
                return;
            }
            double t = Clock;
            if (!ModState.DirectionAt(_cometA, t, out SkyVector dir, out string why))
            {
                Bus.Abort(why);
                _stage = "done";
                return;
            }
            double off = ModState.BodyOffRule(_cometA);
            Bus.Judge("server sky body", off < 0.01, string.Format(Bus.C, "comet {0} in the server's sky, the game's body {1:0.######} deg from the sky rule", _cometA, off));
            Bus.Write("server.sky", new Dictionary<string, string> { { "t", Bus.R(t) }, { "comet", _cometA.ToString(Bus.C) }, { "x", Bus.R(dir.X) }, { "y", Bus.R(dir.Y) }, { "z", Bus.R(dir.Z) } });
            Next("sky");
        }

        private void WaitClientFile(string file, string next, double seconds)
        {
            if (Bus.Has(file) || Time.realtimeSinceStartup - _stageReal > seconds)
            {
                if (!Bus.Has(file))
                {
                    Bus.Info("went on without " + file + " after " + seconds.ToString("0", Bus.C) + " s");
                }
                Next(next);
            }
        }

        // ---- A: aimed, brought down beside the player -------------------------------------------------------------

        /// <summary>A target well clear of the pad, the rocket and the player, inside the voxel world, held until the pass brings it in reach.</summary>
        private void Aim()
        {
            Vector3 player = _human.transform.position;
            Vector3[] avoid = { new Vector3(300f, 0f, -400f), new Vector3(100f, 0f, 100f), new Vector3(player.x, 0f, player.z) };
            Vector3[] candidates = { new Vector3(-900f, 0f, -900f), new Vector3(900f, 0f, -900f), new Vector3(900f, 0f, 900f), new Vector3(-900f, 0f, 900f) };
            _targetA = candidates.OrderByDescending(c => avoid.Min(a => Vector3.Distance(c, a))).First();
            _bay.SetLogicValue(LogicType.PositionX, _targetA.x);
            _bay.SetLogicValue(LogicType.PositionZ, _targetA.z);
            CometAim.Aim? found = CometAim.ForBay(_bay.ReferenceId);
            if (found == null || found.Value.CometId != _cometA || !found.Value.Tracked || (float)found.Value.TargetX != _targetA.x)
            {
                Overdue("the bay aiming comet A at its target", 120.0);
                return;
            }
            double t = Clock;
            CometAim.Aim aim = found.Value;
            if (!ModState.AimAt(aim, t, out GroundPoint sky, out Descent descent, out string why))
            {
                Bus.Abort(why);
                _stage = "done";
                return;
            }
            Bus.Write("server.aim", new Dictionary<string, string>
            {
                { "t", Bus.R(t) }, { "comet", _cometA.ToString(Bus.C) }, { "bay", _bay.ReferenceId.ToString(Bus.C) }, { "targetx", Bus.R(aim.TargetX) }, { "targetz", Bus.R(aim.TargetZ) },
                { "skyx", Bus.R(sky.X) }, { "skyz", Bus.R(sky.Z) }, { "landx", Bus.R(descent.Land.X) }, { "landz", Bus.R(descent.Land.Z) }, { "reach", Bus.R(descent.Reach) },
            });
            Bus.Info(string.Format(Bus.C, "aim | comet {0} target ({1:0.#}, {2:0.#}) | at clock {3:R}: sky point ({4:0.#}, {5:0.#}), lands ({6:0.#}, {7:0.#}), reach {8:0.#} m",
                _cometA, aim.TargetX, aim.TargetZ, t, sky.X, sky.Z, descent.Land.X, descent.Land.Z, descent.Reach));
            Next("aim icons");
        }

        private void AimGap()
        {
            CometAim.Aim? found = CometAim.ForBay(_bay.ReferenceId);
            if (found == null || found.Value.CometId != _cometA)
            {
                Overdue("the bay aiming comet A");
                return;
            }
            CometAim.Aim aim = found.Value;
            double gap = Math.Sqrt((aim.TargetX - aim.SkyX) * (aim.TargetX - aim.SkyX) + (aim.TargetZ - aim.SkyZ) * (aim.TargetZ - aim.SkyZ));
            if (!(gap < 0.8 * aim.Reach) || CometLand.Busy())
            {
                Overdue(string.Format(Bus.C, "a pass bringing the target within the reach ({0:0} m of {1:0} m now)", gap, aim.Reach), 3000.0);
                return;
            }
            _rocket.RocketMode = CometModes.Deorbit;
            Bus.Info(string.Format(Bus.C, "order A | comet {0} | target {1:0.#} m from the sky point, reach {2:0.#} m | rocket mode {3}", _cometA, gap, aim.Reach, _rocket.RocketMode));
            Next("fired");
        }

        private void WaitFired()
        {
            LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == _cometA);
            if (l.CometId != _cometA || !CometLand.EventFor(_cometA, out double eventClock, out Vector3 eventImpact) || !CometZoneReport.Current(out _sequence, out _, out _, out _))
            {
                Overdue("comet A's burn and its event", 60.0);
                return;
            }
            _landing = l;
            double toTarget = Math.Sqrt((l.ImpactX - _targetA.x) * (l.ImpactX - _targetA.x) + (l.ImpactZ - _targetA.z) * (l.ImpactZ - _targetA.z));
            bool burn = l.Tracked && toTarget < 1.0 && Math.Abs(eventClock - l.ImpactClock) < 1e-6 && Math.Abs(eventImpact.x - l.ImpactX) < 1e-3 && Math.Abs(eventImpact.z - l.ImpactZ) < 1e-3;
            // Where the player stands: on the line from the impact toward the world's middle, where the wave peaks at 300 kPa.
            ZoneReach reach = CometZones.Shipped.ReachFor(l.ShowMoles, Effective.Celestial);
            double d = ModState.AtPeak(l.ShowMoles, SuitPeakKPa);
            if (d < reach.Heat + 40.0)
            {
                d = reach.Heat + 40.0;
            }
            double peak = CometRules.Shipped.PeakKPa(l.ShowMoles, d);
            Vector3 impact = new Vector3((float)l.ImpactX, (float)l.ImpactY, (float)l.ImpactZ);
            Vector3 toMiddle = new Vector3(-impact.x, 0f, -impact.z);
            Vector3 dir = toMiddle.sqrMagnitude > 1f ? toMiddle.normalized : Vector3.right;
            _spot = impact + dir * (float)d;
            float yaw = Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg;
            bool spot = peak > 200.0 && d > reach.Heat && Mathf.Abs(_spot.x) < WorldEdge && Mathf.Abs(_spot.z) < WorldEdge;
            Bus.Judge("server burn A", burn && spot, string.Format(Bus.C,
                "comet {0} event {1} lands ({2:R}, {3:R}, {4:R}) at clock {5:R}, {6:0.###} m from its target | {7:R} mol | zones {8} | the player to stand {9:0.#} m out at ({10:0.#}, {11:0.#}), the wave {12:0.#} kPa there",
                _cometA, _sequence, l.ImpactX, l.ImpactY, l.ImpactZ, l.ImpactClock, toTarget, l.ShowMoles, reach, d, _spot.x, _spot.z, peak));
            Bus.Write("server.fall", new Dictionary<string, string>
            {
                { "comet", _cometA.ToString(Bus.C) }, { "seq", _sequence.ToString(Bus.C) }, { "x", Bus.R(l.ImpactX) }, { "y", Bus.R(l.ImpactY) }, { "z", Bus.R(l.ImpactZ) },
                { "clock", Bus.R(l.ImpactClock) }, { "moles", Bus.R(l.ShowMoles) }, { "spotx", Bus.R(_spot.x) }, { "spotz", Bus.R(_spot.z) }, { "yaw", Bus.R(yaw) },
                { "distance", Bus.R(d) }, { "peak", Bus.R(peak) },
            });
            Build(impact, l.ShowMoles, new Vector3(dir.z, 0f, -dir.x), reach);
            _arrival = l.ImpactClock + CometRules.Shipped.ArrivalSeconds(d);
            Next("wave");
        }

        /// <summary>A short line of structures out from the impact, away from the player: one in each zone the wave and the heat judge.</summary>
        private void Build(Vector3 impact, double moles, Vector3 dir, ZoneReach reach)
        {
            var groups = new List<(string Tag, double Distance, string Prefab)>
            {
                ("ground zero", 0.5 * reach.GroundZero, "StructureReinforcedWall"),
                ("fireball", reach.GroundZero + 0.5 * (reach.Heat - reach.GroundZero), "StructureCompositeWindow"),
                ("fireball", reach.GroundZero + 0.5 * (reach.Heat - reach.GroundZero), "StructureSolarPanel"),
                ("wave 400 kPa", Math.Max(reach.Heat + 20.0, ModState.AtPeak(moles, 400.0)), "StructureCompositeWall"),
                ("wave 400 kPa", Math.Max(reach.Heat + 20.0, ModState.AtPeak(moles, 400.0)), "StructureCompositeWindow"),
                ("wave 120 kPa", ModState.AtPeak(moles, 120.0), "StructureCompositeWindow"),
            };
            Vector3 across = Vector3.Cross(Vector3.up, dir).normalized;
            _built.Clear();
            List<string> made = new List<string>();
            for (int i = 0; i < groups.Count; i++)
            {
                double side = (i % 2 == 0 ? -1.0 : 1.0) * 6.0;
                Vector3 at = OnGrid(groups[i].Prefab, impact + dir * (float)groups[i].Distance + across * (float)side + Vector3.up);
                Structure s = Spawn<Structure>(groups[i].Prefab, at);
                if (s == null)
                {
                    made.Add(groups[i].Prefab + " NOT MADE");
                    continue;
                }
                _built.Add(s.ReferenceId);
                made.Add(string.Format(Bus.C, "{0} {1} id {2} at {3:0} m", groups[i].Tag, groups[i].Prefab, s.ReferenceId, Vector3.Distance(impact, s.CenterPosition)));
            }
            Bus.Info("built by A's impact | " + string.Join("; ", made));
            Bus.Write("server.built", new Dictionary<string, string> { { "ids", string.Join(";", _built.Select(id => id.ToString(Bus.C))) } });
        }

        /// <summary>Either side of the wave's arrival at the player: the suit's damage and the body's place, as the server has them.</summary>
        private void Wave()
        {
            double now = Clock;
            if (now < _arrival - 2.0)
            {
                _suitBefore = Suit();
                _bodyBefore = _human.transform.position;
                return;
            }
            if (now < _arrival + 4.0)
            {
                return;
            }
            var after = Suit();
            Vector3 body = _human.transform.position;
            double moved = Vector3.Distance(body, _bodyBefore);
            double atImpact = Vector3.Distance(new Vector3(_bodyBefore.x, 0f, _bodyBefore.z), new Vector3((float)_landing.ImpactX, 0f, (float)_landing.ImpactZ));
            double peak = CometRules.Shipped.PeakKPa(_landing.ShowMoles, atImpact);
            bool damaged = after.Brute + after.Burn > _suitBefore.Brute + _suitBefore.Burn + 1e-3f;
            Bus.Judge("server suit damage A", damaged && peak > 200.0, string.Format(Bus.C, "the player stood {0:0.#} m from the impact, the wave {1:0.#} kPa there | suit and helmet brute {2:R} -> {3:R}, burn {4:R} -> {5:R}",
                atImpact, peak, _suitBefore.Brute, after.Brute, _suitBefore.Burn, after.Burn));
            Bus.Judge("server saw the push A", moved > 1.0, string.Format(Bus.C, "the player's body, as their game sends it, moved {0:0.###} m from {1} to {2} across the wave's arrival at clock {3:R}",
                moved, _bodyBefore, body, _arrival));
            Bus.Write("server.suit", new Dictionary<string, string> { { "brute", Bus.R(after.Brute) }, { "burn", Bus.R(after.Burn) }, { "before", Bus.R(_suitBefore.Brute + _suitBefore.Burn) } });
            Next("zones");
        }

        private void Zones()
        {
            CometZoneReport report = CometZoneReport.Last;
            if (report == null || report.Sequence != _sequence || Clock < _landing.ImpactClock + report.LastSeconds + 4.0)
            {
                Overdue("the zone pass of comet A's impact", 300.0);
                return;
            }
            Dictionary<string, string> file = new Dictionary<string, string> { { "count", _built.Count.ToString(Bus.C) }, { "t", Bus.R(Clock) } };
            for (int i = 0; i < _built.Count; i++)
            {
                file["s" + i.ToString(Bus.C)] = State(_built[i]);
            }
            Bus.Write("server.structures", file);
            bool pass = report.Host && report.Destroyed >= 1 && report.Broken >= 1;
            Bus.Judge("server zone pass A", pass, string.Format(Bus.C, "event {0}: {1} structures and {2} players in one pass, destroyed {3}, burned {4}, broken {5} | players {6} | {7}",
                report.Sequence, report.Structures, report.People, report.Destroyed, report.Burned, report.Broken, string.Join(", ", report.Players), string.Join(" ; ", _built.Select(State))));
            Next("dim");
        }

        internal static string State(long id)
        {
            Structure s = Referencable.Find<Structure>(id);
            bool gone = s == null || s.IsBeingDestroyed;
            return string.Format(Bus.C, "{0}|{1}|{2}|{3}", id, gone ? "gone" : s.IsBroken ? "broken" : "standing",
                gone || s.DamageState == null ? "-" : Bus.R(s.DamageState.Burn), gone || s.DamageState == null ? "-" : Bus.R(s.DamageState.Brute));
        }

        private void Dim()
        {
            if (Clock < _landing.ImpactClock + 40.0)
            {
                return;
            }
            double t = Clock;
            double rule = CometRules.Shipped.SkyDim(_landing.ShowMoles, t - _landing.ImpactClock);
            Bus.Info(string.Format(Bus.C, "dim | at clock {0:R} the rule's {1:R}; the server's own CometSky.Dim {2:R} (a server draws no sky)", t, rule, CometSky.Dim));
            Bus.Write("server.dim", new Dictionary<string, string> { { "t", Bus.R(t) }, { "dim", Bus.R(rule) }, { "seq", _sequence.ToString(Bus.C) }, { "impact", Bus.R(_landing.ImpactClock) } });
            Next("clear A");
        }

        private void WaitClear(string next)
        {
            if (CometLand.Busy())
            {
                Overdue("the last comet's show ending", 900.0);
                return;
            }
            Next(next);
        }

        // ---- B: the console comet, called by the player -----------------------------------------------------------

        private void Console_()
        {
            Vector3[] corners = { new Vector3(1500f, 0f, 1500f), new Vector3(-1500f, 0f, 1500f), new Vector3(1500f, 0f, -1500f), new Vector3(-1500f, 0f, -1500f) };
            Vector3 avoid = new Vector3(_targetA.x, 0f, _targetA.z);
            Vector3 from = corners.OrderByDescending(c => Vector3.Distance(c, avoid)).First();
            float yaw = Mathf.Atan2(from.x, from.z) * Mathf.Rad2Deg;
            Bus.Write("server.want-console", new Dictionary<string, string>
            {
                { "x", Bus.R(from.x) }, { "z", Bus.R(from.z) }, { "yaw", Bus.R(yaw) },
                { "command", "serverrun terraform comet " + ConsoleMoles.ToString("R", Bus.C) + " CarbonDioxide range=" + ConsoleRange.ToString("R", Bus.C) + " turn=0" },
            });
            Next("console sent");
        }

        private void ConsoleSent()
        {
            Dictionary<string, string> sent = Bus.Read("client.console-sent");
            if (sent == null)
            {
                Overdue("the player calling the console comet", 300.0);
                return;
            }
            if (_consoleSentAt == 0.0)
            {
                _consoleSentAt = Time.realtimeSinceStartup;
                _consoleFrom = new Vector3((float)Bus.D(sent, "x"), 0f, (float)Bus.D(sent, "z"));
                _consoleYaw = (float)Bus.D(sent, "yaw");
                _suitBefore = Suit();
            }
            bool fresh = CometZoneReport.Current(out int seq, out Vector3 impact, out double impactClock, out double moles) && seq != _sequence;
            if (!fresh)
            {
                if (Time.realtimeSinceStartup - _consoleSentAt > 20.0)
                {
                    Bus.Judge("server console via serverrun", false, "no comet event 20 s after the player sent serverrun; the server calls it itself so the run goes on");
                    RunCommand("comet", ConsoleMoles.ToString("R", Bus.C), "CarbonDioxide", "range=" + ConsoleRange.ToString("R", Bus.C), "turn=0");
                    _consoleSentAt = Time.realtimeSinceStartup + 1e6f;
                }
                return;
            }
            _consoleSequence = seq;
            _consoleImpact = impact;
            _consoleImpactClock = impactClock;
            Vector3 body = _human.transform.position;
            Vector3 forward = Facing(_human);
            Vector3 expected = new Vector3(body.x, 0f, body.z) + forward * (float)ConsoleRange;
            double off = Vector3.Distance(new Vector3(impact.x, 0f, impact.z), expected);
            double toPlayer = Vector3.Distance(new Vector3(impact.x, 0f, impact.z), new Vector3(body.x, 0f, body.z));
            if (_consoleSentAt < 1e5f)
            {
                Bus.Judge("server console via serverrun", true, "a comet event followed the player's serverrun");
            }
            Bus.Judge("server console site", off < 40.0 && Math.Abs(toPlayer - ConsoleRange) < 5.0, string.Format(Bus.C,
                "event {0} lands at ({1:0.#}, {2:0.#}), {3:0.#} m from the player at ({4:0.#}, {5:0.#}) facing ({6:0.###}, {7:0.###}), {8:0.#} m from {9:0} m straight ahead of them",
                seq, impact.x, impact.z, toPlayer, body.x, body.z, forward.x, forward.z, off, ConsoleRange));
            Bus.Write("server.console-event", new Dictionary<string, string>
            {
                { "seq", seq.ToString(Bus.C) }, { "x", Bus.R(impact.x) }, { "y", Bus.R(impact.y) }, { "z", Bus.R(impact.z) }, { "clock", Bus.R(impactClock) }, { "moles", Bus.R(moles) },
            });
            _bodyBefore = body;
            _arrival = impactClock + CometRules.Shipped.ArrivalSeconds(toPlayer);
            Next("console wave");
        }

        private static Vector3 Facing(Human human)
        {
            Vector3 f = Vector3.ProjectOnPlane(human.EntityRotation * Vector3.forward, Vector3.up);
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }

        private void ConsoleWave()
        {
            double now = Clock;
            if (now < _arrival - 2.0)
            {
                _bodyBefore = _human.transform.position;
                _suitBefore = Suit();
                return;
            }
            if (now < _arrival + 4.0)
            {
                return;
            }
            var after = Suit();
            double moved = Vector3.Distance(_human.transform.position, _bodyBefore);
            double atImpact = Vector3.Distance(new Vector3(_bodyBefore.x, 0f, _bodyBefore.z), new Vector3(_consoleImpact.x, 0f, _consoleImpact.z));
            double peak = CometRules.Shipped.PeakKPa(ConsoleMoles, atImpact);
            bool unchanged = Math.Abs(after.Brute + after.Burn - (_suitBefore.Brute + _suitBefore.Burn)) <= 1e-3f;
            Bus.Judge("server console no suit damage", unchanged && peak < 200.0, string.Format(Bus.C, "the player stood {0:0.#} m out, the wave {1:0.#} kPa there, under the 200 kPa that damages a suit | suit and helmet {2:R} -> {3:R}",
                atImpact, peak, _suitBefore.Brute + _suitBefore.Burn, after.Brute + after.Burn));
            Bus.Judge("server saw the push B", moved > 0.5, string.Format(Bus.C, "the player's body moved {0:0.###} m across the console comet's wave", moved));
            Next("clear B");
        }

        // ---- C: a late join -----------------------------------------------------------------------------------

        private void Late_()
        {
            if (!Bus.Has("server.want-late"))
            {
                Bus.Write("server.want-late", new Dictionary<string, string> { { "clock", Bus.R(Clock) } });
            }
            bool gone = !Human.AllHumans.Any(h => h != null && h.OrganBrain != null && h.OrganBrain.IsOnline);
            if (!gone)
            {
                Overdue("the player's game leaving", 300.0);
                return;
            }
            int before = CometZoneReport.Current(out int seq, out _, out _, out _) ? seq : 0;
            RunCommand("comet", LateMoles.ToString("R", Bus.C), "CarbonDioxide", "preview");
            if (!CometZoneReport.Current(out int late, out Vector3 impact, out double impactClock, out double moles) || late == before)
            {
                Bus.Abort("the late comet was not called");
                _stage = "done";
                return;
            }
            Bus.Info(string.Format(Bus.C, "late | event {0} called with nobody online, lands at ({1:0.#}, {2:0.#}) at clock {3:R}", late, impact.x, impact.z, impactClock));
            Bus.Write("server.late-fired", new Dictionary<string, string>
            {
                { "seq", late.ToString(Bus.C) }, { "clock", Bus.R(impactClock) }, { "x", Bus.R(impact.x) }, { "y", Bus.R(impact.y) }, { "z", Bus.R(impact.z) }, { "moles", Bus.R(moles) },
            });
            Next("late fired");
        }

        private void LateFired()
        {
            if (!Bus.Has("client.late-done"))
            {
                Overdue("the player's game joining again during the late comet", 900.0);
                return;
            }
            Dictionary<string, string> ready = Bus.Read("client.ready");
            _clientId = ready != null ? Bus.U(ready, "id") : _clientId;
            _human = Human.AllHumans.FirstOrDefault(h => h != null && h.OrganBrain != null && h.OrganBrain.IsOnline && h.OwnerClientId == _clientId) ?? _human;
            Next("clear C");
        }

        // ---- D: brought down and saved in its fall ------------------------------------------------------------

        private void AimD()
        {
            CometAim.Aim? found = CometAim.ForBay(_bay.ReferenceId);
            _bay.SetLogicValue(LogicType.PositionX, double.NaN);
            _bay.SetLogicValue(LogicType.PositionZ, double.NaN);
            if (found == null || found.Value.CometId != _cometD || found.Value.Tracked)
            {
                Overdue("the bay aiming comet D untracked", 600.0);
                return;
            }
            _rocket.RocketMode = CometModes.Deorbit;
            Bus.Info("order D | comet " + _cometD + " untracked");
            Next("save fired");
        }

        private void WaitFiredD()
        {
            LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == _cometD);
            if (l.CometId != _cometD)
            {
                Overdue("comet D's burn", 60.0);
                return;
            }
            _landing = l;
            _stageClock = Clock;
            Next("saving");
        }

        private void Saving()
        {
            if (Clock - (_landing.ImpactClock - _landing.FallSeconds) < SaveAfterSeconds)
            {
                return;
            }
            LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == _cometD);
            bool falling = l.CometId == _cometD && Clock < l.ImpactClock;
            Bus.Judge("server fall saved", falling, string.Format(Bus.C, "comet {0} saved {1:0.#} s into its fall, landing at clock {2:R} at ({3:R}, {4:R}, {5:R})",
                _cometD, Clock - (l.ImpactClock - l.FallSeconds), l.ImpactClock, l.ImpactX, l.ImpactY, l.ImpactZ));
            Bus.Write("server.saving", new Dictionary<string, string>
            {
                { "comet", _cometD.ToString(Bus.C) }, { "clock", Bus.R(l.ImpactClock) }, { "x", Bus.R(l.ImpactX) }, { "y", Bus.R(l.ImpactY) }, { "z", Bus.R(l.ImpactZ) }, { "moles", Bus.R(l.ShowMoles) },
                { "bay", _bay.ReferenceId.ToString(Bus.C) },
            });
            Util.Commands.CommandLine.Process("file save");
            Next("saved");
        }

        private void Saved()
        {
            if (Time.realtimeSinceStartup - _stageReal < 5f)
            {
                return;
            }
            Bus.Write("server.make-done", new Dictionary<string, string> { { "clock", Bus.R(Clock) } });
            Bus.Info("phase make done");
            _stage = "done";
        }

        // ---- phase load -----------------------------------------------------------------------------------------

        private Dictionary<string, string> _saving;

        private void StepLoad()
        {
            switch (_stage)
            {
                case "boot":
                    if (!WorldManager.IsGamePaused)
                    {
                        WorldManager.SetGamePause(pauseGame: true);
                    }
                    _unpaused = true;
                    _saving = Bus.Read("server.saving");
                    if (_saving == null)
                    {
                        Bus.Abort("no server.saving from phase make");
                        _stage = "done";
                        return;
                    }
                    _cometD = (int)Bus.L(_saving, "comet");
                    Next("load-fall");
                    break;
                case "load-fall":
                    LoadFall();
                    break;
                case "load-hold":
                    if (Bus.Has("client.ready"))
                    {
                        if (WorldManager.IsGamePaused)
                        {
                            Unpause("the player's game has joined again");
                        }
                        Next("load-land");
                    }
                    else
                    {
                        Overdue("the player's game joining again after the load", 1200.0);
                    }
                    break;
                case "load-land":
                    LoadLand();
                    break;
                case "load-wait":
                    if (Bus.Has("client.rejoin-done") || Time.realtimeSinceStartup - _stageReal > 300f)
                    {
                        Bus.Write("server.load-done", new Dictionary<string, string> { { "clock", Bus.R(Clock) } });
                        Bus.Info("phase load done");
                        _stage = "done";
                    }
                    break;
            }
        }

        private void LoadFall()
        {
            LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == _cometD);
            bool back = l.CometId == _cometD && !double.IsNaN(l.ImpactClock) && CometLand.EventFor(_cometD, out _, out _);
            if (!back)
            {
                Overdue("comet D's fall back on the clock after the load", 120.0);
                return;
            }
            CometLand.EventFor(_cometD, out double eventClock, out Vector3 eventImpact);
            double saved = Bus.D(_saving, "clock");
            bool same = Math.Abs(l.ImpactClock - saved) <= 1e-6 && Math.Abs(eventClock - l.ImpactClock) < 1e-6 && Math.Abs(l.ImpactX - Bus.D(_saving, "x")) < 1e-6
                && Math.Abs(l.ImpactZ - Bus.D(_saving, "z")) < 1e-6 && Clock < l.ImpactClock;
            Bus.Judge("server fall back after load", same, string.Format(Bus.C, "comet {0} falls on, landing at clock {1:R} (saved to land at {2:R}), {3:0.#} s from now, at ({4:R}, {5:R}); its event lands at {6:R} at ({7:R}, {8:R}) | held paused for the player",
                _cometD, l.ImpactClock, saved, l.ImpactClock - Clock, l.ImpactX, l.ImpactZ, eventClock, eventImpact.x, eventImpact.z));
            WriteSettings();
            Bus.Write("server.ready", new Dictionary<string, string> { { "clock", Bus.R(Clock) }, { "world", WorldSetting.Current.Id }, { "phase", "load" } });
            Next("load-hold");
        }

        private void LoadLand()
        {
            LandingDelivery delivery = CometLand.Deliveries().LastOrDefault(d => d.CometId == _cometD);
            if (delivery == null)
            {
                Overdue("comet D landing after the load", 300.0);
                return;
            }
            double saved = Bus.D(_saving, "clock");
            bool onTime = delivery.Clock >= saved - 1e-6 && delivery.Clock - saved < 1.0;
            Bus.Judge("server landed at the saved clock", onTime, string.Format(Bus.C, "comet {0} landed at clock {1:R}, saved to land at {2:R}: {3:0.###} s late, the game's frame", _cometD, delivery.Clock, saved, delivery.Clock - saved));
            Bus.Write("server.landed", new Dictionary<string, string> { { "comet", _cometD.ToString(Bus.C) }, { "clock", Bus.R(delivery.Clock) } });
            Next("load-wait");
        }

        // ---- helpers -----------------------------------------------------------------------------------------

        private static float Odd(float v) => 2f * Mathf.Floor(v / 2f) + 1f;

        /// <summary>
        /// Where near <paramref name="near"/> a structure of this prefab stands as the game places one: a cell's centre
        /// (every coordinate odd) for one snapped to the grid, else the first face of that cell its grid can be taken
        /// out of again. The game registers a wall on the edge between four cells (two coordinates even) in the cell's
        /// list alone, and its removal then clears the cell's lookup at that grid and throws (StructuralArray.Clear).
        /// </summary>
        private static Vector3 OnGrid(string prefabName, Vector3 near)
        {
            Vector3 centre = new Vector3(Odd(near.x), Odd(near.y), Odd(near.z));
            if (!(Prefab.Find(prefabName) is Structure prefab) || prefab.PlacementType == PlacementSnap.Grid)
            {
                return centre;
            }
            Vector3[] steps = prefab.PlacementType == PlacementSnap.FaceMount
                ? new[] { Vector3.down, Vector3.up, Vector3.forward, Vector3.back, Vector3.right, Vector3.left }
                : new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down };
            foreach (Vector3 step in steps)
            {
                Vector3 at = centre + step;
                Grid3 grid = new Grid3(at);
                if (StructuralArray.Get(grid - grid.GridCenter(at + prefab.Bounds.center)) != StructureElement.Invalid)
                {
                    return at;
                }
            }
            return centre;
        }

        private static T Spawn<T>(string prefabName, Vector3 position) where T : Thing
        {
            Thing prefab = Prefab.Find(prefabName);
            if (prefab == null)
            {
                Bus.Info("no prefab " + prefabName);
                return null;
            }
            T made = OnServer.Create<T>(prefab, position, Quaternion.identity);
            if (made is Structure structure && structure.BuildStates != null && structure.BuildStates.Count > 0)
            {
                structure.CurrentBuildStateIndex = structure.BuildStates.Count - 1;
            }
            return made;
        }

        /// <summary>A new site with a deposit by the game's own discovery, at the nodes nearest the orbit first, as -CometLand finds one.</summary>
        private SpaceMapNode Discover(SpaceMap map)
        {
            foreach (SpaceMapNode parent in map.Nodes.Where(n => n?.DiscoverData != null && n.DiscoverData.SiteGenerations.Count > 0).OrderBy(RouteDeltaV).ToList())
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
                    if (made.Deposit != null && made.Deposit.TotalOreAtLocation > 0 && RouteDeltaV(made) > 0.0)
                    {
                        return made;
                    }
                }
            }
            Bus.Abort("discovery made no site on the map");
            _stage = "done";
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
    }
}
