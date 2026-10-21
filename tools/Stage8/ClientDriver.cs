using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using Objects.Rockets;
using Objects.Rockets.UI;
using TerraformingReloaded.Comets;
using TerraformingReloaded.Patching;
using UI.Motherboard;
using UnityEngine;

namespace TerraformingReloaded.Stage8
{
    /// <summary>
    /// The rendered client's end: joins the server the way the game's own join command does, a moment after the
    /// game's data has loaded, and judges what its own game shows against what the server wrote, at the same
    /// clock. Phase "first" judges the join, the planet sync, the comet list, the rocket map's comet icon, the sky
    /// body, the Map motherboard's icons, comet A's warning, fall, impact, push, suit damage, structures and darker
    /// sky, then calls the console comet B with serverrun and judges it. Phase "late" joins during comet C. Phase
    /// "rejoin" joins the loaded save during comet D's fall. Pictures are the game's own screen captures, of the
    /// whole screen as drawn.
    /// </summary>
    internal sealed class ClientDriver : IDriver
    {
        private const double JoinSeconds = 900.0;
        private static readonly double[] RocketShots = { -8.0, -3.0, 0.3, 1.5, 4.0, 10.0, 25.0, 45.0 };
        private static readonly double[] ConsoleShots = { -8.0, -3.0, 0.3, 1.5, 4.0, 10.0, 25.0 };
        private const float Pitch = 8f;

        private readonly string _phase;
        private readonly string _address = Environment.GetEnvironmentVariable("TR_STAGE8_ADDRESS") ?? "127.0.0.1:27016";
        private string _stage = "boot";
        private float _stageReal;
        private float _initializedAt = -1f;
        private float _joinAskedAt = -1f;
        private static bool _finishedJoining;
        private float _posWritten = -10f;
        private double _joinedClock = double.NaN;
        private float _addedSeenAt = -1f;

        // Facing and standing.
        private bool _hold;
        private float _yaw;
        private Vector3 _standAt;
        private float _placedAt = -1f;
        private string _placeJudge;
        private float _placeChecked;

        // Pictures, by the shared clock.
        private readonly List<(double Clock, string Name)> _shots = new List<(double, string)>();
        private int _shotCount;

        // The comet list, compared at the server's clocks.
        private string _viewsSeen;
        private int _viewsCompared;
        private int _viewsEqual;
        private bool _viewsFlying;
        private bool _viewsParked;
        private bool _listJudged;
        private string _firstMismatch;

        // The rocket map.
        private string _mapStage = "wait";
        private float _mapAt;
        private RocketCanvas _canvas;

        // Comet A.
        private Dictionary<string, string> _fall;
        private bool _warnedSeen;
        private float _warnedAt;
        private Vector3 _warnedFrom;
        private bool _eventJudged;
        private double _arrival;
        private Vector3 _beforeWave;
        private bool _pushJudged;
        private float _suitBefore = float.NaN;
        private float _suitReadAt = -1f;
        private float _structuresAt = -1f;
        private bool _structuresJudged;
        private bool _dimJudged;
        private bool _suitJudged;

        // Comet B.
        private Dictionary<string, string> _want;
        private Dictionary<string, string> _consoleEvent;
        private bool _consoleJudged;
        private float _consoleReadyAt = -1f;
        private bool _consolePushJudged;
        private double _consoleArrival;
        private Vector3 _consoleBefore;

        public ClientDriver(string phase)
        {
            _phase = phase;
            NetworkClient.ClientFinishedJoining += () => _finishedJoining = true;
        }

        private static double Clock => Bus.Clock;

        private static Human Me => InventoryManager.ParentHuman;

        private void Next(string stage)
        {
            _stage = stage;
            _stageReal = Time.realtimeSinceStartup;
            Bus.Info("stage " + stage);
        }

        private readonly Dictionary<string, float> _waits = new Dictionary<string, float>();

        /// <summary>Like <see cref="Overdue"/>, timed from the first call with this <paramref name="what"/> rather than from the stage's start.</summary>
        private bool TooLong(string what, double seconds)
        {
            if (!_waits.TryGetValue(what, out float since))
            {
                _waits[what] = Time.realtimeSinceStartup;
                return false;
            }
            if (Time.realtimeSinceStartup - since > seconds)
            {
                Bus.Abort(what + " did not happen within " + seconds.ToString("0", Bus.C) + " s");
                _stage = "done";
                return true;
            }
            return false;
        }

        private bool Overdue(string what, double seconds)
        {
            if (Time.realtimeSinceStartup - _stageReal > seconds)
            {
                Bus.Abort(what + " did not happen within " + seconds.ToString("0", Bus.C) + " s");
                _stage = "done";
                return true;
            }
            return false;
        }

        public void Step()
        {
            if (_stage == "done" || Bus.Failed)
            {
                return;
            }
            Application.runInBackground = true;
            switch (_stage)
            {
                case "boot": Boot(); return;
                case "joining": Joining(); return;
            }
            if (GameManager.GameState != GameState.Running || Me == null)
            {
                return;
            }
            WritePosition();
            Hold();
            switch (_phase)
            {
                case "first": StepFirst(); break;
                case "late": StepLate(); break;
                case "rejoin": StepRejoin(); break;
            }
        }

        public void Late()
        {
            if (Bus.Failed || GameManager.GameState != GameState.Running || Me == null)
            {
                return;
            }
            Shoot();
            if (_phase == "first" && !_dimJudged)
            {
                JudgeDim();
            }
        }

        // ---- the join ---------------------------------------------------------------------------------------------

        private void Boot()
        {
            if (!WorldManager.IsInitialized)
            {
                return;
            }
            if (_initializedAt < 0f)
            {
                _initializedAt = Time.realtimeSinceStartup;
                Bus.Info("the game's data has loaded and the main menu is up");
            }
            // As StationeersLaunchPad's own auto-join: a join in the frame the data loads crashes the terrain's LOD manager.
            if (Time.realtimeSinceStartup - _initializedAt < 1.25f || !Bus.Has("server.ready"))
            {
                return;
            }
            _joinAskedAt = Time.realtimeSinceStartup;
            Bus.Info("join " + _address);
            Util.Commands.CommandLine.Process("join " + _address);
            Next("joining");
        }

        private void Joining()
        {
            bool running = GameManager.GameState == GameState.Running && NetworkManager.IsClient && NetworkClient.OwnerHuman != null && Me != null;
            if (!running || !Gate.HostPlanet)
            {
                Overdue("joining the server (ClientFinishedJoining, OwnerHuman, Running, the host's planet)", JoinSeconds);
                return;
            }
            string name = _phase == "first" ? "client joined" : _phase == "late" ? "client joined late" : "client joined again";
            Bus.Judge(name, true, string.Format(Bus.C, "{0} s after asking | ClientFinishedJoining {1} | OwnerHuman {2} ({3}) | game state {4} | client {5} | the host's planet {6} | gate {7}",
                Bus.F(Time.realtimeSinceStartup - _joinAskedAt, 1), _finishedJoining, NetworkClient.OwnerHuman.DisplayName, NetworkClient.OwnerHuman.ReferenceId, GameManager.GameState,
                NetworkManager.LocalClientId, Gate.HostPlanet, Gate.Describe()));
            _joinedClock = Clock;
            Vector3 at = Me.transform.position;
            Bus.Write("client.ready", new Dictionary<string, string>
            {
                { "name", Me.DisplayName }, { "id", NetworkManager.LocalClientId.ToString(Bus.C) }, { "x", Bus.R(at.x) }, { "y", Bus.R(at.y) }, { "z", Bus.R(at.z) },
                { "utc", DateTime.UtcNow.Ticks.ToString(Bus.C) }, { "phase", _phase },
            });
            Next(_phase == "first" ? "sync" : _phase == "late" ? "late" : "rejoin");
        }

        private void WritePosition()
        {
            if (Time.realtimeSinceStartup - _posWritten < 1f)
            {
                return;
            }
            _posWritten = Time.realtimeSinceStartup;
            Vector3 at = Me.transform.position;
            Bus.Write("client.pos", new Dictionary<string, string>
            {
                { "x", Bus.R(at.x) }, { "y", Bus.R(at.y) }, { "z", Bus.R(at.z) }, { "utc", DateTime.UtcNow.Ticks.ToString(Bus.C) }, { "clock", Bus.R(Clock) },
            });
        }

        // ---- standing, facing, pictures -----------------------------------------------------------------------

        /// <summary>
        /// Stands this game's own player at a point on the ground, facing a bearing, and judges it there once both
        /// this game and the server have it so (<paramref name="judge"/>). The owner's game sends its body's place
        /// and turn to the server every 50 ms (NetworkClient.OwnerToServerStream, Human.PrepareUpdateMessage:
        /// ThingTransformPosition and EntityRotation, the turn of CharacterRotationY), so the body has to be where the
        /// game moves it, not only its transform: the owner's rigidbody is interpolated and puts its transform back
        /// where the rigidbody is every frame.
        /// </summary>
        private void Stand(float x, float z, float yaw, string judge)
        {
            _standAt = SpawnPoint.GetSafePoint(new Vector3(x, 1f, z), Vector3.up);
            _yaw = yaw;
            _hold = true;
            _placedAt = Time.realtimeSinceStartup;
            _placeJudge = judge;
            _placeChecked = 0f;
            Place("asked");
        }

        /// <summary>
        /// The move itself, as Human.ForceSetPosition moves a body on the host (it does nothing on a player's own
        /// game): the place, the transform, the rigidbody, no momentum, the interpolation reset; and the turn as the
        /// game's own scripted spawns set it, through the camera controller's RotationY (TutorialSpawnPoint), which
        /// CameraController.LateUpdate gives CharacterRotationY.
        /// </summary>
        private void Place(string why)
        {
            Human me = Me;
            if (me.ParentSlot == null && me.MovementController != null && me.MovementController.ControlMode != MovementController.Mode.Animation
                && me.State == EntityState.Alive && !me.IsRagdoll)
            {
                Bus.Info("the player's movement was " + me.MovementController.ControlMode + "; set to walking (Animation), as the game does when a body leaves a seat");
                me.MovementController.ControlMode = MovementController.Mode.Animation;
            }
            me.ThingTransformPosition = _standAt;
            Rigidbody body = me.ActiveRigidbody;
            if (body != null)
            {
                body.position = _standAt;
                body.MovePosition(_standAt);
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            me.ResetInterpolation();
            Physics.SyncTransforms();
            Face();
            Bus.Info(string.Format(Bus.C, "stood at {0}, facing {1:0.#} deg ({2}) | {3}", _standAt, _yaw, why, Describe()));
        }

        private void Face()
        {
            if (CameraController.Instance != null)
            {
                CameraController.Instance.RotationY = _yaw;
                CameraController.Instance.RotationX = Pitch;
            }
            Me.CharacterRotationY.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        /// <summary>The player's and the camera's state, for a placement that does not take.</summary>
        private string Describe()
        {
            Human me = Me;
            CameraController camera = CameraController.Instance;
            Rigidbody body = me.ActiveRigidbody;
            return string.Format(Bus.C,
                "body at {0}, turned {1:0.#} deg, camera looking {2:0.#} deg | state {3}, ragdoll {4}, movement {5}, in slot {6} | rigidbody kinematic {7}, interpolation {8} | camera tracks the player {9}, cinematic {10}, cursor shown {11}, window focused {12}",
                me.transform.position, me.CharacterRotationY.eulerAngles.y, CameraYaw(), me.State, me.IsRagdoll, me.MovementController?.ControlMode, me.ParentSlot != null,
                body?.isKinematic, body?.interpolation, camera != null && camera.TrackedEntity == me, CameraController.CinematicMode, Cursor.visible, Application.isFocused);
        }

        private static float CameraYaw()
        {
            Camera camera = CameraController.CurrentCamera;
            if (camera == null)
            {
                return float.NaN;
            }
            Vector3 f = camera.transform.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Every frame: the way the character looks, as the mouse would set it. While a placement is to be judged:
        /// the body put back if it fell through ground not yet built or the move did not take, and judged once this
        /// game and the server (its server.body, from the stream this game sends) both have it at the spot, turned
        /// the way asked.
        /// </summary>
        private void Hold()
        {
            if (!_hold)
            {
                return;
            }
            Face();
            if (_placeJudge == null)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now - _placeChecked < 0.5f)
            {
                return;
            }
            _placeChecked = now;
            Human me = Me;
            Vector3 at = me.transform.position;
            double here = Flat(at, _standAt);
            double turned = Math.Abs(Mathf.DeltaAngle(me.CharacterRotationY.eulerAngles.y, _yaw));
            double looks = Math.Abs(Mathf.DeltaAngle(CameraYaw(), _yaw));
            Dictionary<string, string> server = Bus.Read("server.body");
            double age = server == null ? double.NaN : (DateTime.UtcNow.Ticks - Bus.L(server, "utc")) / (double)TimeSpan.TicksPerSecond;
            Vector3 there = server == null ? new Vector3(float.NaN, 0f, float.NaN) : new Vector3((float)Bus.D(server, "x"), (float)Bus.D(server, "y"), (float)Bus.D(server, "z"));
            double seen = Flat(there, _standAt);
            double seenTurn = server == null ? double.NaN : Math.Abs(Mathf.DeltaAngle((float)Bus.D(server, "yaw"), _yaw));
            bool pass = here < 2.0 && turned < 3.0 && looks < 3.0 && seen < 3.0 && seenTurn < 5.0 && age < 3.0;
            if (pass || now - _placedAt > 30f)
            {
                Bus.Judge(_placeJudge, pass, string.Format(Bus.C,
                    "asked ({0:0.#}, {1:0.#}) facing {2:0.#} deg | here {3:0.###} m off, turned {4:0.#} deg off, the camera {5:0.#} deg off | the server has the body {6:0.###} m off, turned {7:0.#} deg off ({8:0.#} s old) | {9:0.#} s after asking | {10}",
                    _standAt.x, _standAt.z, _yaw, here, turned, looks, seen, seenTurn, age, now - _placedAt, Describe()));
                _placeJudge = null;
                return;
            }
            bool fell = now - _placedAt < 8f && at.y < _standAt.y - 3f;
            if (fell || (here > 2.0 && now - _placedAt > 2f && ((int)((now - _placedAt) * 2f)) % 10 == 0))
            {
                Place(fell ? "put back on the ground" : "the move had not taken, " + here.ToString("0.#", Bus.C) + " m off");
            }
        }

        private static double Flat(Vector3 a, Vector3 b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));

        private static float YawTo(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        private void Schedule(double impactClock, double[] offsets, string prefix)
        {
            foreach (double o in offsets)
            {
                _shots.Add((impactClock + o, string.Format(Bus.C, "{0}-t{1:+000.0;-000.0}", prefix, o)));
            }
            _shots.Sort((a, b) => a.Clock.CompareTo(b.Clock));
        }

        private void Shoot()
        {
            double now = Clock;
            if (_shots.Count == 0 || now < _shots[0].Clock)
            {
                return;
            }
            var shot = _shots[0];
            _shots.RemoveAt(0);
            Capture(shot.Name, now);
        }

        private void Capture(string name, double clock)
        {
            string file = Path.Combine(Bus.Out, "shots", string.Format(Bus.C, "{0:00}-{1}.png", _shotCount++, name));
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            ScreenCapture.CaptureScreenshot(file);
            Bus.Info(string.Format(Bus.C, "shot {0} at clock {1:R} | sky dim {2:0.###} | show drawn {3} | at {4}", file, clock, CometSky.Dim, ModState.ShowDrawn(), Me.transform.position));
        }

        // ---- phase first ------------------------------------------------------------------------------------------

        private void StepFirst()
        {
            WatchViews();
            WatchMap();
            switch (_stage)
            {
                case "sync": JudgeSync(); break;
                case "flying": WaitSky(); break;
                case "icons": JudgeIcons(); break;
                case "fall": WatchFall(); break;
                case "console": Console_(); break;
                case "console wait": ConsoleWait(); break;
                case "wait late":
                    if (Bus.Has("server.want-late"))
                    {
                        Bus.Write("client.first-done", new Dictionary<string, string> { { "clock", Bus.R(Clock) } });
                        Bus.Info("phase first done; waiting to be stopped");
                        _stage = "done";
                    }
                    break;
            }
        }

        /// <summary>
        /// The planet sync after the join: the host's planet states keep arriving, each one bit for bit a state the
        /// host wrote within 2 s of this game's clock when it read it, over several sync windows; and the host's
        /// change to its planet (1,000 mol of carbon dioxide added once this player is in) arrives as a new state.
        /// The state read at the join is left out: it is read before this game has the host's clock.
        /// </summary>
        private void JudgeSync()
        {
            Dictionary<string, string> added = Bus.Read("server.gas-added");
            Dictionary<string, string> host = Bus.Read("server.sync");
            Dictionary<string, string> settings = Bus.Read("server.settings");
            if (added == null || host == null || settings == null)
            {
                Overdue("the server's change to its planet after the join", 300.0);
                return;
            }
            double addedAt = Bus.D(added, "clock");
            if (_addedSeenAt < 0f)
            {
                _addedSeenAt = Time.realtimeSinceStartup;
            }
            List<(string Digest, double Clock)> written = new List<(string, double)>();
            int count = (int)Bus.L(host, "count");
            for (int i = 0; i < count; i++)
            {
                string w = Bus.S(host, "w" + i.ToString(Bus.C));
                int at = w?.LastIndexOf('@') ?? -1;
                if (at > 0)
                {
                    written.Add((w.Substring(0, at), double.Parse(w.Substring(at + 1), Bus.C)));
                }
            }
            var reads = SyncDigest.ReadSoFar();
            var after = reads.Where(r => r.Clock > _joinedClock + 0.5).ToArray();
            double Lag((string Digest, int Layout, double Clock) r) =>
                written.Where(w => w.Digest == r.Digest).Select(w => r.Clock - w.Clock).OrderBy(Math.Abs).DefaultIfEmpty(double.NaN).First();
            HashSet<string> before = new HashSet<string>(written.Where(w => w.Clock < addedAt).Select(w => w.Digest));
            bool fresh = after.Any(r => r.Clock >= addedAt && !before.Contains(r.Digest) && Math.Abs(Lag(r)) < 2.0);
            double span = after.Length > 0 ? after.Max(r => r.Clock) - after.Min(r => r.Clock) : 0.0;
            double window = Math.Max(1.0, Settings.SyncIntervalSeconds);
            bool enough = after.Length >= 3 && span >= 2.0 * window;
            if (!(fresh && enough) && Time.realtimeSinceStartup - _addedSeenAt < 120f)
            {
                return;
            }
            double worst = after.Length > 0 ? after.Max(r => Math.Abs(Lag(r))) : double.NaN;
            bool layouts = reads.All(r => r.Layout == 7 || r.Layout == -1) && reads.Any(r => r.Layout == 7);
            bool sync = fresh && enough && worst < 2.0 && layouts;
            Bus.Judge("client planet sync", sync, string.Format(Bus.C,
                "{0} planet states read since the join (clock {1:R}) over {2:0.#} s, each the very state the host wrote within {3:0.###} s of this clock at its reading | the host's change at clock {4:R} {5} | {6} read in all, the host wrote {7} | layout bytes {8} | host sync window {9:0.#} s",
                after.Length, _joinedClock, span, worst, addedAt, fresh ? "arrived as a new state" : "did NOT arrive", reads.Length, written.Count,
                string.Join(",", reads.Select(r => r.Layout).Distinct()), window));
            string hostCelestial = Bus.S(settings, "celestial");
            string mine = string.Join(";", Effective.Celestial.ToArray().Select(Bus.R));
            bool same = hostCelestial == mine && Bus.S(settings, "ghg") == Bus.R(Effective.GhgResponseScale) && Bus.S(settings, "density") == Bus.R(Effective.DensityResponseScale)
                && Bus.S(settings, "albedo") == Bus.R(Effective.AirlessAlbedo) && Bus.S(settings, "sky") == Effective.DynamicSky.ToString();
            Bus.Judge("client host settings", same, "the host's Celestial terraforming settings " + hostCelestial + " | this game's " + mine + " | temperature and sky settings " + (same ? "equal" : "DIFFER"));
            double other = Bus.OtherClockNow("server", out double age);
            double off = Clock - other;
            Bus.Judge("client shared clock", Math.Abs(off) < 0.5 && age < 3.0, string.Format(Bus.C, "this game's clock {0:R}, the server's at the same wall time {1:R} ({2:0.###} s apart; the server's clock file {3:0.#} s old)", Clock, other, off, age));
            Next("flying");
        }

        /// <summary>Every time the server writes its comets at a clock, this game's comets at that clock.</summary>
        private void WatchViews()
        {
            Dictionary<string, string> host = Bus.Read("server.views");
            string t = Bus.S(host, "t");
            if (host == null || t == null || t == _viewsSeen || _listJudged)
            {
                return;
            }
            _viewsSeen = t;
            double clock = double.Parse(t, Bus.C);
            int count = (int)Bus.L(host, "count");
            if (count == 0)
            {
                return;
            }
            CometView[] mine = ModState.ViewsAt(clock);
            List<string> theirs = Enumerable.Range(0, count).Select(i => Bus.S(host, "v" + i.ToString(Bus.C))).ToList();
            List<string> ours = mine.Select(ServerDriver.Line).ToList();
            bool equal = theirs.Count == ours.Count && theirs.Zip(ours, SameLine).All(x => x);
            _viewsCompared++;
            if (equal)
            {
                _viewsEqual++;
            }
            else if (_firstMismatch == null)
            {
                _firstMismatch = string.Format(Bus.C, "at clock {0}: host [{1}] here [{2}]", t, string.Join(" / ", theirs), string.Join(" / ", ours));
            }
            _viewsFlying |= equal && mine.Any(v => v.State == MapCometState.Flying && v.AtNode == 0L);
            _viewsParked |= equal && mine.Any(v => v.State == MapCometState.Arrived);
            Bus.Info("comet list at clock " + t + " " + (equal ? "equal" : "DIFFERS") + " | " + string.Join(" / ", ours));
            if (_viewsFlying && _viewsParked && _viewsCompared >= 3)
            {
                _listJudged = true;
                Bus.Judge("client comet list", _viewsEqual == _viewsCompared, string.Format(Bus.C, "{0} of {1} comparisons at the server's clocks equal, one with a comet between two nodes and one with a comet parked at Entry{2}",
                    _viewsEqual, _viewsCompared, _firstMismatch == null ? "" : " | first difference " + _firstMismatch));
            }
        }

        /// <summary>The same state to the last bit, but for the doubles' last few.</summary>
        private static bool SameLine(string a, string b)
        {
            string[] x = a.Split('|');
            string[] y = b.Split('|');
            if (x.Length != y.Length)
            {
                return false;
            }
            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] == y[i])
                {
                    continue;
                }
                if (!(double.TryParse(x[i], System.Globalization.NumberStyles.Float, Bus.C, out double p) && double.TryParse(y[i], System.Globalization.NumberStyles.Float, Bus.C, out double q)
                    && ModState.Near(p, q, 1e-9)))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The rocket map opened on the Rocket Control motherboard while a comet is between two nodes: its icon where this game's comet stands.</summary>
        private void WatchMap()
        {
            switch (_mapStage)
            {
                case "wait":
                    Dictionary<string, string> flying = Bus.Read("server.flying");
                    Dictionary<string, string> charted = Bus.Read("server.charted");
                    if (flying == null || charted == null || !CometMap.Views().Any(v => v.State == MapCometState.Flying && v.AtNode == 0L))
                    {
                        return;
                    }
                    // The nodes the server charted, charted here too: the map is built from them when it opens.
                    string[] ids = (Bus.S(charted, "ids") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!ids.All(id => SpaceMapNode.Get(long.Parse(id, Bus.C))?.IsCharted == true))
                    {
                        return;
                    }
                    RocketMotherboard board = Referencable.Find<RocketMotherboard>(Bus.L(flying, "rocketboard"));
                    _canvas = RocketCanvas.Instance;
                    if (board == null || _canvas == null)
                    {
                        if (Time.realtimeSinceStartup - _stageReal > 60f)
                        {
                            Bus.Judge("client rocket map icon", false, "no Rocket Control motherboard " + Bus.L(flying, "rocketboard") + " on this game, or no rocket screen");
                            _mapStage = "done";
                        }
                        return;
                    }
                    _canvas.Show(board);
                    (AccessTools.Field(typeof(RocketCanvas), "_tabWell")?.GetValue(_canvas) as TraderUI.TabWell)?.Select(1);
                    _mapAt = Time.realtimeSinceStartup;
                    _mapStage = "open";
                    Bus.Info("opened the rocket map on motherboard " + board.ReferenceId);
                    return;
                case "open":
                    if (Time.realtimeSinceStartup - _mapAt < 2f)
                    {
                        return;
                    }
                    JudgeMapIcons();
                    Capture("map-rocket", Clock);
                    _mapAt = Time.realtimeSinceStartup;
                    _mapStage = "shot";
                    return;
                case "shot":
                    if (Time.realtimeSinceStartup - _mapAt < 1f)
                    {
                        return;
                    }
                    _canvas.Hide();
                    _mapStage = "done";
                    return;
            }
        }

        private void JudgeMapIcons()
        {
            IList icons = AccessTools.Field(typeof(CometMapIcons), "Icons")?.GetValue(null) as IList;
            if (icons == null || icons.Count == 0)
            {
                Bus.Judge("client rocket map icon", false, "the rocket map holds no comet icons");
                return;
            }
            Type icon = icons[0].GetType();
            FieldInfo viewField = AccessTools.Field(icon, "View");
            FieldInfo shownField = AccessTools.Field(icon, "Shown");
            FieldInfo idField = AccessTools.Field(icon, "CometId");
            FieldInfo root = AccessTools.Field(typeof(RocketIcon), "_rootTransform");
            MapView view = viewField.GetValue(icons[0]) as MapView;
            AccessTools.DeclaredMethod(typeof(MapView), "Refresh", Type.EmptyTypes)?.Invoke(view, null);
            CometView[] comets = CometMap.Views();
            List<string> lines = new List<string>();
            bool pass = true;
            bool between = false;
            int at = 0;
            foreach (object one in icons)
            {
                if (viewField.GetValue(one) as MapView != view)
                {
                    continue;
                }
                RocketIcon shown = shownField.GetValue(one) as RocketIcon;
                int id = (int)idField.GetValue(one);
                CometView comet = at < comets.Length ? comets[at] : default;
                at++;
                Vector3? from = SpaceMapNode.Get(comet.FromNode)?.MapLocation?.GetMapPosition();
                Vector3? to = SpaceMapNode.Get(comet.ToNode)?.MapLocation?.GetMapPosition();
                if (shown == null || from == null || to == null || comet.Id != id)
                {
                    pass = false;
                    lines.Add("comet " + id + ": no icon, no nodes on this map, or not the comet in its place in the list");
                    continue;
                }
                Vector3 expected = comet.AtNode != 0L ? (comet.AtNode == comet.ToNode ? to.Value : from.Value) : Vector3.Lerp(from.Value, to.Value, (float)comet.Progress);
                Vector3 got = shown.Transform.localPosition;
                Vector3 offset = (root?.GetValue(shown) as Transform)?.localPosition ?? new Vector3(float.NaN, 0f, 0f);
                bool here = (got - expected).magnitude < 1e-3f && Mathf.Abs(offset.x) < 1e-4f && Mathf.Abs(offset.y + 14f) < 1e-4f && shown.gameObject.activeInHierarchy;
                between |= here && comet.AtNode == 0L;
                pass &= here;
                lines.Add(string.Format(Bus.C, "comet {0} icon at {1} (offset {2}), its place {3}: {4} at progress {5:R} from {6} to {7}", id, got, offset, expected, here ? "there" : "NOT there",
                    comet.Progress, comet.FromNode, comet.ToNode));
            }
            Bus.Judge("client rocket map icon", pass && between && at == comets.Length, string.Format(Bus.C, "{0} icons for {1} comets on the rocket map, refreshed in this frame | {2}", at, comets.Length, string.Join(" / ", lines)));
        }

        private void WaitSky()
        {
            Dictionary<string, string> sky = Bus.Read("server.sky");
            if (sky == null)
            {
                Overdue("the server's comet A in the sky", 2400.0);
                return;
            }
            int comet = (int)Bus.L(sky, "comet");
            double t = Bus.D(sky, "t");
            if (!ModState.DirectionAt(comet, t, out SkyVector mine, out string why))
            {
                if (TooLong("comet " + comet + " in this game's sky", 60.0))
                {
                    Bus.Info(why);
                }
                return;
            }
            double dx = Math.Abs(mine.X - Bus.D(sky, "x"));
            double dy = Math.Abs(mine.Y - Bus.D(sky, "y"));
            double dz = Math.Abs(mine.Z - Bus.D(sky, "z"));
            double off = ModState.BodyOffRule(comet);
            Bus.Judge("client sky body", Math.Max(dx, Math.Max(dy, dz)) <= 1e-6 && off < 0.01, string.Format(Bus.C,
                "comet {0} at clock {1:R}: here ({2:R}, {3:R}, {4:R}), the server ({5}, {6}, {7}), at most {8:E2} apart | this game's body {9:0.######} deg from the rule now",
                comet, t, mine.X, mine.Y, mine.Z, Bus.S(sky, "x"), Bus.S(sky, "y"), Bus.S(sky, "z"), Math.Max(dx, Math.Max(dy, dz)), off));
            Bus.Write("client.sky-done", new Dictionary<string, string> { { "clock", Bus.R(Clock) } });
            Next("icons");
        }

        /// <summary>The bay's aim at the server's clock, worked out here, and the Map motherboard's two icons drawn where this game's aim puts them.</summary>
        private void JudgeIcons()
        {
            Dictionary<string, string> host = Bus.Read("server.aim");
            Dictionary<string, string> flying = Bus.Read("server.flying");
            if (host == null || flying == null)
            {
                Overdue("the server's aim", 600.0);
                return;
            }
            long bay = Bus.L(host, "bay");
            CometAim.Aim? found = CometAim.ForBay(bay);
            if (found == null || !found.Value.Tracked || found.Value.TargetX != Bus.D(host, "targetx"))
            {
                TooLong("this game's copy of the bay's target", 60.0);
                return;
            }
            CometAim.Aim aim = found.Value;
            double t = Bus.D(host, "t");
            if (!ModState.AimAt(aim, t, out GroundPoint sky, out Descent descent, out string why))
            {
                if (TooLong("this game's aim at the server's clock", 30.0))
                {
                    Bus.Info(why);
                }
                return;
            }
            double skyOff = Math.Max(Math.Abs(sky.X - Bus.D(host, "skyx")), Math.Abs(sky.Z - Bus.D(host, "skyz")));
            double landOff = Math.Max(Math.Abs(descent.Land.X - Bus.D(host, "landx")), Math.Abs(descent.Land.Z - Bus.D(host, "landz")));
            bool atClock = skyOff <= 1e-6 && landOff <= 1e-6 && ModState.Near(descent.Reach, Bus.D(host, "reach"), 1e-9);
            // This game's live aim, from the direction it used in its own frame.
            CometView comet = ModState.ViewsAt(aim.Clock).FirstOrDefault(v => v.Id == aim.CometId);
            ModState.Descend(aim, aim.Direction, comet, out GroundPoint liveSky, out Descent live, out _);
            bool selfConsistent = Math.Abs(live.Land.X - aim.LandX) <= 1e-6 && Math.Abs(live.Land.Z - aim.LandZ) <= 1e-6;
            // The Map motherboard, drawn here: the panel's own trackables with the comet's two icons among them.
            MapMotherboard board = Referencable.Find<MapMotherboard>(Bus.L(flying, "mapboard"));
            MapMotherboardPanel panel = board == null ? null : AccessTools.Field(typeof(MapMotherboard), "_panel")?.GetValue(board) as MapMotherboardPanel;
            string drawn = "no Map motherboard on this game";
            bool icons = false;
            if (panel != null)
            {
                if (!panel.TrackablesToggle.On)
                {
                    panel.TrackablesToggle.Set(true);
                }
                panel.UpdateTrackables();
                List<MapMotherboardPanelTrackableIconData> data = AccessTools.Field(typeof(MapMotherboardPanel), "_trackableData")?.GetValue(panel) as List<MapMotherboardPanelTrackableIconData>;
                CometAim.Icon[] mine = CometAim.Icons();
                icons = data != null && mine.Length == 2 && mine.All(i => data.Any(d => d.Id == i.Id && d.WorldPosition.x == (float)i.X && d.WorldPosition.z == (float)i.Z));
                drawn = string.Format(Bus.C, "the panel draws {0} trackables; the comet's icons {1}", data?.Count ?? -1,
                    string.Join(", ", mine.Select(i => string.Format(Bus.C, "{0} {1} at ({2:0.###}, {3:0.###}) {4}", i.IsTarget ? "target" : "landing", i.Id, i.X, i.Z,
                        data != null && data.Any(d => d.Id == i.Id && d.WorldPosition.x == (float)i.X && d.WorldPosition.z == (float)i.Z) ? "drawn" : "NOT drawn"))));
            }
            Bus.Judge("client map motherboard icons", atClock && selfConsistent && icons, string.Format(Bus.C,
                "bay {0} aiming comet {1} at ({2:R}, {3:R}) | at the server's clock {4:R}: sky point ({5:R}, {6:R}), lands ({7:R}, {8:R}), reach {9:R}; {10:E2} m and {11:E2} m from the server's | this game's live aim lands ({12:R}, {13:R}), the rule from its own direction {14} | {15}",
                bay, aim.CometId, aim.TargetX, aim.TargetZ, t, sky.X, sky.Z, descent.Land.X, descent.Land.Z, descent.Reach, skyOff, landOff, aim.LandX, aim.LandZ,
                selfConsistent ? "agrees" : "DIFFERS", drawn));
            Bus.Write("client.icons-done", new Dictionary<string, string> { { "clock", Bus.R(Clock) } });
            Next("fall");
        }

        // ---- comet A ---------------------------------------------------------------------------------------------

        private void WatchFall()
        {
            Dictionary<string, string> flying = Bus.Read("server.flying");
            int cometA = (int)Bus.L(flying, "a");
            if (!_warnedSeen)
            {
                LandingView seen = CometLand.Falling().FirstOrDefault(l => l.CometId == cometA);
                if (seen.CometId != cometA)
                {
                    Overdue("comet A's fall on this game", 3600.0);
                    return;
                }
                _warnedSeen = true;
                _warnedAt = Time.realtimeSinceStartup;
                _warnedFrom = Me.transform.position;
                double d = Math.Sqrt((seen.ImpactX - _warnedFrom.x) * (seen.ImpactX - _warnedFrom.x) + (seen.ImpactZ - _warnedFrom.z) * (seen.ImpactZ - _warnedFrom.z));
                Bus.Info(string.Format(Bus.C, "warned-at | comet {0} | player {1} | from ({2:R}, {3:R}) | expects '{4:N0} m from you' | lands in {5:0.#} s",
                    cometA, Me.DisplayName, _warnedFrom.x, _warnedFrom.z, d, seen.ImpactClock - Clock));
                return;
            }
            _fall = _fall ?? Bus.Read("server.fall");
            if (_fall == null || Time.realtimeSinceStartup - _warnedAt < 1.5f)
            {
                return;
            }
            double impactClock = Bus.D(_fall, "clock");
            Vector3 impact = new Vector3((float)Bus.D(_fall, "x"), (float)Bus.D(_fall, "y"), (float)Bus.D(_fall, "z"));
            if (_placedAt < 0f)
            {
                LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == cometA);
                bool same = l.CometId == cometA && Bus.R(l.ImpactClock) == Bus.S(_fall, "clock") && Bus.R(l.ImpactX) == Bus.S(_fall, "x") && Bus.R(l.ImpactZ) == Bus.S(_fall, "z")
                    && Bus.R(l.ShowMoles) == Bus.S(_fall, "moles");
                Bus.Judge("client fall A", same, string.Format(Bus.C, "this game's fall of comet {0}: lands ({1:R}, {2:R}) at clock {3:R}, {4:R} mol | the server's ({5}, {6}) at {7}, {8}",
                    cometA, l.ImpactX, l.ImpactZ, l.ImpactClock, l.ShowMoles, Bus.S(_fall, "x"), Bus.S(_fall, "z"), Bus.S(_fall, "clock"), Bus.S(_fall, "moles")));
                Vector3 spot = new Vector3((float)Bus.D(_fall, "spotx"), 0f, (float)Bus.D(_fall, "spotz"));
                Stand(spot.x, spot.z, YawTo(spot, impact), "client stood A");
                _suitBefore = SuitDamage();
                Schedule(impactClock, RocketShots, "rocket");
                double distance = Vector3.Distance(new Vector3(_standAt.x, 0f, _standAt.z), new Vector3(impact.x, 0f, impact.z));
                _arrival = impactClock + CometRules.Shipped.ArrivalSeconds(distance);
                Bus.Info(string.Format(Bus.C, "expects the wave at {0:0.#} kPa, {1:0.#} m from the impact, arriving at clock {2:R}", CometRules.Shipped.PeakKPa(Bus.D(_fall, "moles"), distance), distance, _arrival));
                return;
            }
            double now = Clock;
            if (!_eventJudged && now >= impactClock - 10.0)
            {
                _eventJudged = true;
                bool have = CometZoneReport.Current(out int seq, out Vector3 eventImpact, out double eventClock, out double moles);
                bool same = have && seq == (int)Bus.L(_fall, "seq") && eventImpact.x == (float)Bus.D(_fall, "x") && eventImpact.z == (float)Bus.D(_fall, "z")
                    && Bus.R(eventClock) == Bus.S(_fall, "clock") && Bus.R(moles) == Bus.S(_fall, "moles");
                bool drawn = ModState.ShowDrawn() && ModState.ShowPlaying();
                Bus.Judge("client impact event A", same && drawn, string.Format(Bus.C, "event {0} here: impact {1} at clock {2:R}, {3:R} mol | the server's event {4} at ({5}, {6}) clock {7} | show playing {8}, drawn {9}, {10:0.#} s before the impact",
                    seq, eventImpact, eventClock, moles, Bus.S(_fall, "seq"), Bus.S(_fall, "x"), Bus.S(_fall, "z"), Bus.S(_fall, "clock"), ModState.ShowPlaying(), ModState.ShowDrawn(), impactClock - now));
            }
            if (!_pushJudged)
            {
                if (now < _arrival - 1.0)
                {
                    _beforeWave = Me.transform.position;
                }
                else if (now >= _arrival + 3.0)
                {
                    _pushJudged = true;
                    double moved = Vector3.Distance(Me.transform.position, _beforeWave);
                    double distance = Vector3.Distance(new Vector3(_beforeWave.x, 0f, _beforeWave.z), new Vector3(impact.x, 0f, impact.z));
                    double peak = CometRules.Shipped.PeakKPa(Bus.D(_fall, "moles"), distance);
                    Bus.Judge("client pushed A", moved > 1.0 && peak > 200.0, string.Format(Bus.C, "outdoors {0:0.#} m from the impact, the wave {1:0.#} kPa there: this game threw its own player {2:0.###} m, from {3} to {4}",
                        distance, peak, moved, _beforeWave, Me.transform.position));
                }
            }
            if (!_suitJudged && Bus.Has("server.suit"))
            {
                if (_suitReadAt < 0f)
                {
                    _suitReadAt = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - _suitReadAt > 3f)
                {
                    _suitJudged = true;
                    Dictionary<string, string> suit = Bus.Read("server.suit");
                    float mine = SuitDamage();
                    float theirs = (float)(Bus.D(suit, "brute") + Bus.D(suit, "burn"));
                    Bus.Judge("client suit damage A", Math.Abs(mine - theirs) <= 1e-3f && mine > _suitBefore + 1e-3f, string.Format(Bus.C,
                        "this game's suit and helmet damage {0:R} (was {1:R}), the server's {2:R}", mine, _suitBefore, theirs));
                }
            }
            if (!_structuresJudged && Bus.Has("server.structures"))
            {
                if (_structuresAt < 0f)
                {
                    _structuresAt = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - _structuresAt > 3f)
                {
                    _structuresJudged = true;
                    JudgeStructures();
                }
            }
            if (_pushJudged && _suitJudged && _structuresJudged && _dimJudged && _shots.Count == 0)
            {
                Next("console");
            }
            else if (now > impactClock + 600.0)
            {
                Bus.Abort(string.Format(Bus.C, "comet A's judging did not finish within 600 s of its impact (push {0}, suit {1}, structures {2}, dim {3})", _pushJudged, _suitJudged, _structuresJudged, _dimJudged));
                _stage = "done";
            }
        }

        private float SuitDamage()
        {
            float total = 0f;
            foreach (Slot slot in new[] { Me.SuitSlot, Me.HelmetSlot })
            {
                IndestructableDamageState damage = slot?.Get()?.DamageState;
                if (damage != null)
                {
                    total += damage.Brute + damage.Burn;
                }
            }
            return total;
        }

        private void JudgeStructures()
        {
            Dictionary<string, string> host = Bus.Read("server.structures");
            int count = (int)Bus.L(host, "count");
            List<string> lines = new List<string>();
            bool pass = count > 0;
            int gone = 0;
            int broken = 0;
            for (int i = 0; i < count; i++)
            {
                string theirs = Bus.S(host, "s" + i.ToString(Bus.C));
                long id = long.Parse(theirs.Split('|')[0], Bus.C);
                string mine = ServerDriver.State(id);
                bool same = mine == theirs;
                pass &= same;
                gone += mine.Contains("|gone|") ? 1 : 0;
                broken += mine.Contains("|broken|") ? 1 : 0;
                lines.Add((same ? "" : "DIFFERS ") + "here " + mine + (same ? "" : " server " + theirs));
            }
            Bus.Judge("client structures", pass && gone >= 1 && broken >= 1, string.Format(Bus.C, "{0} structures by comet A's impact, {1} gone and {2} broken here, each as the server has it: {3}", count, gone, broken, string.Join(" ; ", lines)));
        }

        /// <summary>After the update of the frame: this game's darker sky against the rule, here and at the server's clock.</summary>
        private void JudgeDim()
        {
            Dictionary<string, string> host = Bus.Read("server.dim");
            if (host == null || !CometZoneReport.Current(out int seq, out _, out double impactClock, out double moles) || seq != (int)Bus.L(host, "seq"))
            {
                return;
            }
            _dimJudged = true;
            double t = Bus.D(host, "t");
            double atClock = CometRules.Shipped.SkyDim(moles, t - impactClock);
            double now = Clock;
            double live = CometRules.Shipped.SkyDim(moles, now - impactClock);
            bool pass = Math.Abs(atClock - Bus.D(host, "dim")) <= 1e-12 && Math.Abs(CometSky.Dim - (float)live) <= 1e-6f && CometSky.Dim > 0.02f
                && Bus.R(impactClock) == Bus.S(host, "impact");
            Bus.Judge("client sky dim", pass, string.Format(Bus.C, "at the server's clock {0:R}: here {1:R}, the server {2} | this frame, clock {3:R}: the sky dimmed by {4:R}, the rule {5:R}",
                t, atClock, Bus.S(host, "dim"), now, CometSky.Dim, live));
        }

        // ---- comet B --------------------------------------------------------------------------------------------

        private void Console_()
        {
            _want = Bus.Read("server.want-console");
            if (_want == null)
            {
                Overdue("the server asking for the console comet", 1200.0);
                return;
            }
            if (_consoleReadyAt < 0f)
            {
                Stand((float)Bus.D(_want, "x"), (float)Bus.D(_want, "z"), (float)Bus.D(_want, "yaw"), "client stood B");
                _consoleReadyAt = Time.realtimeSinceStartup;
                return;
            }
            // Called only once the server has the player there and turned that way (or the placement was judged a
            // failure), and a second after, so the server's copy of the turn has settled.
            if (_placeJudge != null || Time.realtimeSinceStartup - _consoleReadyAt < 3f)
            {
                return;
            }
            Vector3 at = Me.transform.position;
            Bus.Write("client.console-at", new Dictionary<string, string> { { "x", Bus.R(at.x) }, { "z", Bus.R(at.z) }, { "yaw", Bus.R(_yaw) } });
            string command = Bus.S(_want, "command");
            Bus.Info("console: " + command);
            Util.Commands.CommandLine.Process(command);
            Bus.Write("client.console-sent", new Dictionary<string, string> { { "x", Bus.R(at.x) }, { "z", Bus.R(at.z) }, { "yaw", Bus.R(_yaw) }, { "clock", Bus.R(Clock) } });
            _consoleBefore = at;
            Next("console wait");
        }

        private void ConsoleWait()
        {
            _consoleEvent = _consoleEvent ?? Bus.Read("server.console-event");
            if (_consoleEvent == null)
            {
                Overdue("the server's console comet event", 120.0);
                return;
            }
            double impactClock = Bus.D(_consoleEvent, "clock");
            Vector3 impact = new Vector3((float)Bus.D(_consoleEvent, "x"), (float)Bus.D(_consoleEvent, "y"), (float)Bus.D(_consoleEvent, "z"));
            double now = Clock;
            if (!_consoleJudged)
            {
                if (!CometZoneReport.Current(out int seq, out Vector3 eventImpact, out double eventClock, out double moles) || seq != (int)Bus.L(_consoleEvent, "seq"))
                {
                    TooLong("the console comet's event on this game", 30.0);
                    return;
                }
                _consoleJudged = true;
                bool same = eventImpact.x == impact.x && eventImpact.z == impact.z && Bus.R(eventClock) == Bus.S(_consoleEvent, "clock") && Bus.R(moles) == Bus.S(_consoleEvent, "moles");
                bool playing = ModState.ShowPlaying() && ModState.ShowDrawn();
                Bus.Judge("client console event", same && playing, string.Format(Bus.C, "event {0} here lands {1} at clock {2:R}, {3:R} mol; the server's ({4}, {5}) at {6} | show playing and drawn {7}",
                    seq, eventImpact, eventClock, moles, Bus.S(_consoleEvent, "x"), Bus.S(_consoleEvent, "z"), Bus.S(_consoleEvent, "clock"), playing));
                Vector3 me = Me.transform.position;
                Vector3 ahead = new Vector3(me.x, 0f, me.z) + Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward * 2500f;
                double off = Vector3.Distance(ahead, new Vector3(impact.x, 0f, impact.z));
                Bus.Judge("client console straight ahead", off < 40.0, string.Format(Bus.C, "this player at ({0:0.#}, {1:0.#}) faces {2:0.#} deg; 2,500 m straight ahead is ({3:0.#}, {4:0.#}); the comet lands {5:0.#} m from there",
                    me.x, me.z, _yaw, ahead.x, ahead.z, off));
                Schedule(impactClock, ConsoleShots, "console");
                double distance = Vector3.Distance(new Vector3(me.x, 0f, me.z), new Vector3(impact.x, 0f, impact.z));
                _consoleArrival = impactClock + CometRules.Shipped.ArrivalSeconds(distance);
                Bus.Info(string.Format(Bus.C, "expects the console wave at {0:0.#} kPa, {1:0.#} m from the impact, arriving at clock {2:R}", CometRules.Shipped.PeakKPa(Bus.D(_consoleEvent, "moles"), distance), distance, _consoleArrival));
                return;
            }
            if (!_consolePushJudged)
            {
                if (now < _consoleArrival - 1.0)
                {
                    _consoleBefore = Me.transform.position;
                }
                else if (now >= _consoleArrival + 3.0)
                {
                    _consolePushJudged = true;
                    double moved = Vector3.Distance(Me.transform.position, _consoleBefore);
                    double distance = Vector3.Distance(new Vector3(_consoleBefore.x, 0f, _consoleBefore.z), new Vector3(impact.x, 0f, impact.z));
                    double peak = CometRules.Shipped.PeakKPa(Bus.D(_consoleEvent, "moles"), distance);
                    Bus.Judge("client pushed B", moved > 0.5 && peak >= 50.0 && peak < 200.0, string.Format(Bus.C, "outdoors {0:0.#} m from the impact, the wave {1:0.#} kPa there: thrown {2:0.###} m",
                        distance, peak, moved));
                }
            }
            if (_consolePushJudged && _shots.Count == 0)
            {
                Next("wait late");
            }
        }

        // ---- phase late ----------------------------------------------------------------------------------------

        private bool _lateJudged;
        private float _lateAt = -1f;

        private void StepLate()
        {
            if (_stage != "late")
            {
                return;
            }
            Dictionary<string, string> late = Bus.Read("server.late-fired");
            if (late == null)
            {
                Overdue("the server's late comet", 60.0);
                return;
            }
            if (!_lateJudged)
            {
                if (!CometZoneReport.Current(out int seq, out Vector3 impact, out double impactClock, out double moles) || seq != (int)Bus.L(late, "seq"))
                {
                    Overdue("the late comet's event reaching this game", 60.0);
                    return;
                }
                if (_lateAt < 0f)
                {
                    _lateAt = Time.realtimeSinceStartup;
                    Bus.Info(string.Format(Bus.C, "late event {0} here at clock {1:R}, {2:R} s after its start at {3:R}", seq, Clock, Clock - (impactClock - 15.0), impactClock - 15.0));
                    Capture("late-joined", Clock);
                    return;
                }
                if (Time.realtimeSinceStartup - _lateAt < 3f)
                {
                    return;
                }
                _lateJudged = true;
                double since = Clock - (impactClock - 15.0);
                bool same = Bus.R(impactClock) == Bus.S(late, "clock") && impact.x == (float)Bus.D(late, "x") && impact.z == (float)Bus.D(late, "z") && Bus.R(moles) == Bus.S(late, "moles");
                bool playing = ModState.ShowPlaying() && ModState.ShowDrawn();
                Bus.Judge("client late join", same && playing && since > 0.0, string.Format(Bus.C, "joined {0:0.#} s into event {1}: its impact {2} at clock {3:R}, {4:R} mol here, the server's ({5}, {6}) at {7} | show playing and drawn {8}",
                    since, seq, impact, impactClock, moles, Bus.S(late, "x"), Bus.S(late, "z"), Bus.S(late, "clock"), playing));
                Capture("late-playing", Clock);
                if (impactClock > Clock + 1.0)
                {
                    Schedule(impactClock, new[] { 0.5, 4.0 }, "late");
                }
                return;
            }
            if (_shots.Count == 0)
            {
                Bus.Write("client.late-done", new Dictionary<string, string> { { "clock", Bus.R(Clock) } });
                Bus.Info("phase late done");
                _stage = "done";
            }
        }

        // ---- phase rejoin ----------------------------------------------------------------------------------------

        private bool _rejoinFallJudged;
        private bool _rejoinEventJudged;

        private void StepRejoin()
        {
            if (_stage != "rejoin")
            {
                return;
            }
            Dictionary<string, string> saving = Bus.Read("server.saving");
            if (saving == null)
            {
                Bus.Abort("no server.saving from phase make");
                _stage = "done";
                return;
            }
            int comet = (int)Bus.L(saving, "comet");
            double saved = Bus.D(saving, "clock");
            if (!_rejoinFallJudged)
            {
                LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == comet);
                if (l.CometId != comet)
                {
                    Overdue("comet D's fall on this game after the load", 60.0);
                    return;
                }
                _rejoinFallJudged = true;
                bool same = Math.Abs(l.ImpactClock - saved) <= 1e-6 && Math.Abs(l.ImpactX - Bus.D(saving, "x")) <= 1e-6 && Math.Abs(l.ImpactZ - Bus.D(saving, "z")) <= 1e-6 && Clock < l.ImpactClock;
                Bus.Judge("client fall after rejoin", same, string.Format(Bus.C, "comet {0} falls here, landing at clock {1:R} at ({2:R}, {3:R}), {4:0.#} s from now; saved to land at {5:R} at ({6}, {7})",
                    comet, l.ImpactClock, l.ImpactX, l.ImpactZ, l.ImpactClock - Clock, saved, Bus.S(saving, "x"), Bus.S(saving, "z")));
                Schedule(l.ImpactClock, new[] { -3.0, 1.0, 4.0 }, "rejoin");
                return;
            }
            if (!_rejoinEventJudged && Clock >= saved - 8.0)
            {
                bool have = CometZoneReport.Current(out int seq, out Vector3 impact, out double impactClock, out _);
                if (!have || Math.Abs(impactClock - saved) > 1e-6)
                {
                    TooLong("comet D's impact event on this game", 60.0);
                    return;
                }
                _rejoinEventJudged = true;
                bool playing = ModState.ShowPlaying() && ModState.ShowDrawn();
                Bus.Judge("client impact after rejoin", playing, string.Format(Bus.C, "event {0} lands {1} at clock {2:R} here, the saved clock {3:R} | show playing and drawn {4}", seq, impact, impactClock, saved, playing));
            }
            if (_rejoinEventJudged && Clock > saved + 6.0 && _shots.Count == 0)
            {
                Bus.Write("client.rejoin-done", new Dictionary<string, string> { { "clock", Bus.R(Clock) } });
                Bus.Info("phase rejoin done");
                _stage = "done";
            }
        }
    }
}
