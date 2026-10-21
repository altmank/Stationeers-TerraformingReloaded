using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Objects.Rockets;
using TerraformingReloaded.Comets;
using UnityEngine;

namespace TerraformingReloaded.Stage8
{
    /// <summary>
    /// The stage 8 driver (docs/COMET-ROCKETS.md, "Proof as it is built"): one plugin for both ends of a
    /// multiplayer run, a dedicated server and a rendered client on the same machine. TR_STAGE8_ROLE says which
    /// end this is (server or client), TR_STAGE8_PHASE which launch of it (server: make, load; client: first,
    /// late, rejoin), TR_STAGE8_OUT the run's folder, where the two ends hand each other what they have seen in
    /// small files and tools/Stage8/run.ps1 watches. Never touches Mod.Networking: everything between the two
    /// games goes through the game and the mod, and everything between the two drivers through those files.
    /// Does nothing unless TR_STAGE8_ROLE is set.
    /// </summary>
    [BepInPlugin("xceled.stationeers.terraformingreloaded.stage8", "TR Stage8", "0.0.1")]
    public class Stage8 : BaseUnityPlugin
    {
        private IDriver _driver;
        private bool _stopped;

        private void Awake()
        {
            string role = Environment.GetEnvironmentVariable("TR_STAGE8_ROLE");
            string outDir = Environment.GetEnvironmentVariable("TR_STAGE8_OUT");
            string phase = Environment.GetEnvironmentVariable("TR_STAGE8_PHASE") ?? "";
            if ((role != "server" && role != "client") || string.IsNullOrEmpty(outDir))
            {
                enabled = false;
                return;
            }
            Bus.Init(Logger, role, phase, outDir);
            Harmony harmony = new Harmony("xceled.stationeers.terraformingreloaded.stage8");
            SyncDigest.Install(harmony, role == "server");
            _driver = role == "server" ? new ServerDriver(phase) : (IDriver)new ClientDriver(phase);
            Bus.Info("armed, phase " + phase + ", out " + outDir + ", batch mode " + GameManager.IsBatchMode);
            if (!JudgeLoad(role, phase))
            {
                // Nothing more of this launch: a game loading other mods, or saving where it should not, is not the run.
                _stopped = true;
                return;
            }
            // For run.ps1: this launch got through StationeersLaunchPad's mod loading.
            Bus.Write(role + ".armed", new Dictionary<string, string> { { "phase", phase }, { "utc", DateTime.UtcNow.Ticks.ToString(Bus.C) } });
        }

        /// <summary>
        /// This launch loaded exactly the mod and the driver from its own data folder's mods, one copy each, and
        /// nothing from the Workshop, and the game's own data path is that folder (StationeersLaunchPad's
        /// SavePathOverride). Every mod assembly is loaded before any mod's entry point runs, so this sees them all.
        /// </summary>
        private bool JudgeLoad(string role, string phase)
        {
            string data = Environment.GetEnvironmentVariable("TR_STAGE8_DATA") ?? "";
            List<string> mods = new List<string>();
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string location;
                try
                {
                    location = assembly.IsDynamic ? "" : assembly.Location;
                }
                catch (Exception)
                {
                    location = "";
                }
                if (location.IndexOf("\\mods\\", StringComparison.OrdinalIgnoreCase) >= 0 || location.IndexOf("\\workshop\\content\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    mods.Add(location);
                }
            }
            string modsDir = Path.Combine(data, "mods") + "\\";
            bool ours = mods.Count == 2 && mods.All(m => m.StartsWith(modsDir, StringComparison.OrdinalIgnoreCase))
                && mods.Count(m => Path.GetFileName(m).Equals("TerraformingReloaded.dll", StringComparison.OrdinalIgnoreCase)) == 1
                && mods.Count(m => Path.GetFileName(m).Equals("TRStage8.dll", StringComparison.OrdinalIgnoreCase)) == 1;
            string defaultPath = StationSaveUtils.DefaultPath ?? "";
            bool path = data.Length > 0 && defaultPath.Length > 0 && Path.GetFullPath(defaultPath).TrimEnd('\\').Equals(Path.GetFullPath(data).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            string cfg = "";
            try
            {
                string file = Path.Combine(Paths.ConfigPath, "stationeers.launchpad.cfg");
                cfg = string.Join("; ", File.ReadAllLines(file).Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*(SavePathOverride|DisableSteam|RetainWorkshopMods|ModProfile|DevMode)\s*=")));
            }
            catch (Exception e)
            {
                cfg = "unreadable: " + e.Message;
            }
            return Bus.Judge(role + " mods and paths " + phase, ours && path, string.Format(Bus.C,
                "{0} mod assemblies: {1} | the game's data path {2} (expected {3}) | LaunchPad: {4}", mods.Count, string.Join(", ", mods), defaultPath, data, cfg))
                || Fail(role + " loaded mods other than the two, or reads a data folder other than its own");
        }

        private static bool Fail(string why)
        {
            Bus.Abort(why);
            return false;
        }

        private void Update()
        {
            if (_stopped || _driver == null)
            {
                return;
            }
            try
            {
                Bus.Tick();
                _driver.Step();
            }
            catch (Exception e)
            {
                _stopped = true;
                Bus.Abort("the driver threw: " + e);
            }
        }

        private void LateUpdate()
        {
            if (_stopped || _driver == null)
            {
                return;
            }
            try
            {
                _driver.Late();
            }
            catch (Exception e)
            {
                _stopped = true;
                Bus.Abort("the driver threw late in a frame: " + e);
            }
        }
    }

    internal interface IDriver
    {
        void Step();

        void Late();
    }

    /// <summary>The log, the judged lines, the handshake files and the shared clock, for both ends.</summary>
    internal static class Bus
    {
        public static readonly CultureInfo C = CultureInfo.InvariantCulture;

        private static ManualLogSource _log;
        private static string _role;
        private static string _phase;
        private static string _out;
        private static float _clockWritten = -10f;

        public static string Role => _role;

        public static string Phase => _phase;

        public static string Out => _out;

        public static bool Failed { get; private set; }

        public static void Init(ManualLogSource log, string role, string phase, string outDir)
        {
            _log = log;
            _role = role;
            _phase = phase;
            _out = outDir;
            Directory.CreateDirectory(outDir);
        }

        /// <summary>The clock every machine shares, the host's real-time seconds; NaN before a world.</summary>
        public static double Clock => OrbitalSimulation.System != null && OrbitalSimulation.IsValid ? OrbitalSimulation.System.TotalRealTimeSeconds : double.NaN;

        public static string R(double value) => value.ToString("R", C);

        public static string F(double value, int places = 3) => value.ToString("F" + places.ToString(C), C);

        public static void Info(string line) => _log.LogInfo("Stage8: " + _role + " " + line);

        /// <summary>A judged line: tools/Stage8/run.ps1 needs every one it expects, each PASS.</summary>
        public static bool Judge(string name, bool pass, string details)
        {
            string line = "judged " + name + " " + (pass ? "PASS" : "FAIL") + " | " + details + " | clock " + R(Clock);
            _log.LogInfo("Stage8: " + _role + " " + line);
            Append(_role + "-judged.txt", line);
            FlushLogs();
            return pass;
        }

        /// <summary>
        /// BepInEx writes LogOutput.log through a buffer, and run.ps1 stops a game by its PID, which loses what the
        /// buffer still held (bt: the late client's last judged line). Flushed after every judged line and marker.
        /// </summary>
        public static void FlushLogs()
        {
            try
            {
                foreach (ILogListener listener in BepInEx.Logging.Logger.Listeners)
                {
                    PropertyInfo writer = listener?.GetType().GetProperty("LogWriter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    (writer?.GetValue(listener) as TextWriter)?.Flush();
                }
            }
            catch (Exception)
            {
                // The judged copy in <role>-judged.txt is written without a buffer.
            }
        }

        /// <summary>The run cannot go on from here; the orchestrator stops on the file this leaves.</summary>
        public static void Abort(string why)
        {
            Failed = true;
            _log.LogInfo("Stage8: " + _role + " FAIL " + why);
            Write(_role + ".failed", new Dictionary<string, string> { { "phase", _phase }, { "why", why.Replace("\r", " ").Replace("\n", " ") } });
        }

        /// <summary>Every second: this end's clock against the wall clock, which both ends of one machine share.</summary>
        public static void Tick()
        {
            if (Time.realtimeSinceStartup - _clockWritten < 1f || double.IsNaN(Clock))
            {
                return;
            }
            _clockWritten = Time.realtimeSinceStartup;
            Write(_role + ".clock", new Dictionary<string, string> { { "clock", R(Clock) }, { "utc", DateTime.UtcNow.Ticks.ToString(C) }, { "paused", WorldManager.IsGamePaused.ToString() } });
        }

        // ---- the handshake files: key=value lines, written whole and moved into place --------------------------

        public static string PathOf(string name) => Path.Combine(_out, name);

        public static void Write(string name, Dictionary<string, string> values)
        {
            FlushLogs();
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in values)
            {
                text.Append(pair.Key).Append('=').Append(pair.Value ?? "").Append('\n');
            }
            string path = PathOf(name);
            string temp = path + ".tmp-" + _role;
            File.WriteAllText(temp, text.ToString());
            // The other end, or run.ps1, may hold the file open for the moment it reads it; the file is then
            // replaced in one step once it lets go. Only one end ever writes a given file.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temp, path, null, true);
                    }
                    else
                    {
                        File.Move(temp, path);
                    }
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 60)
                {
                    System.Threading.Thread.Sleep(Math.Min(40, 2 + 2 * attempt));
                }
            }
        }

        public static void Append(string name, string line)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using (FileStream stream = new FileStream(PathOf(name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.Write(line + "\n");
                    }
                    return;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // The judged file is a convenience copy; the log line is the record.
                    System.Threading.Thread.Sleep(5);
                }
            }
        }

        public static bool Has(string name) => File.Exists(PathOf(name));

        /// <summary>The file's values, or null while it is not there or is being moved into place.</summary>
        public static Dictionary<string, string> Read(string name)
        {
            string path = PathOf(name);
            if (!File.Exists(path))
            {
                return null;
            }
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    string text;
                    // Opened so that the writer can replace the file while this has it open.
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        text = reader.ReadToEnd();
                    }
                    Dictionary<string, string> values = new Dictionary<string, string>();
                    foreach (string line in text.Split('\n'))
                    {
                        int equals = line.IndexOf('=');
                        if (equals > 0)
                        {
                            values[line.Substring(0, equals)] = line.Substring(equals + 1).TrimEnd('\r');
                        }
                    }
                    return values;
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(3);
                }
            }
            return null;
        }

        public static double D(Dictionary<string, string> values, string key) =>
            values != null && values.TryGetValue(key, out string text) && double.TryParse(text, NumberStyles.Float, C, out double value) ? value : double.NaN;

        public static long L(Dictionary<string, string> values, string key) =>
            values != null && values.TryGetValue(key, out string text) && long.TryParse(text, NumberStyles.Integer, C, out long value) ? value : 0L;

        public static ulong U(Dictionary<string, string> values, string key) =>
            values != null && values.TryGetValue(key, out string text) && ulong.TryParse(text, NumberStyles.Integer, C, out ulong value) ? value : 0UL;

        public static string S(Dictionary<string, string> values, string key) =>
            values != null && values.TryGetValue(key, out string text) ? text : null;

        /// <summary>
        /// The other end's clock now, by the wall clock both ends read: its last clock file, moved on by the wall
        /// time since, while it was not paused. NaN with none.
        /// </summary>
        public static double OtherClockNow(string otherRole, out double ageSeconds)
        {
            Dictionary<string, string> other = Read(otherRole + ".clock");
            ageSeconds = double.NaN;
            if (other == null)
            {
                return double.NaN;
            }
            long ticks = L(other, "utc");
            ageSeconds = (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
            bool paused = S(other, "paused") == "True";
            return D(other, "clock") + (paused ? 0.0 : ageSeconds);
        }
    }

    /// <summary>The mod's own state that tools need and that is not public, read the way the mod reads it.</summary>
    internal static class ModState
    {
        private static readonly PropertyInfo AllComets = AccessTools.Property(typeof(CometMap), "All");
        private static readonly MethodInfo ViewOf = AccessTools.Method(typeof(CometMap), "View");
        private static readonly PropertyInfo Showing = AccessTools.Property(AccessTools.TypeByName("TerraformingReloaded.Comets.CometDirector"), "Showing");

        /// <summary>Every comet on the space map as it stands at clock <paramref name="clock"/>, by the mod's own rule.</summary>
        public static CometView[] ViewsAt(double clock)
        {
            if (AllComets == null || ViewOf == null)
            {
                throw new MissingMemberException("CometMap.All or CometMap.View is not where the driver reads them");
            }
            Array all = (Array)AllComets.GetValue(null);
            CometView[] views = new CometView[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                views[i] = (CometView)ViewOf.Invoke(null, new[] { all.GetValue(i), (object)clock });
            }
            return views;
        }

        public static string Describe(CometView v) => string.Format(Bus.C, "comet {0} {1} at {2} hop {3}/{4} {5}->{6} progress {7} share {8} fuel {9} kg {10} engines {11} start {12}",
            v.Id, v.State, v.AtNode, v.Hop, v.HopCount, v.FromNode, v.ToNode, Bus.R(v.Progress), Bus.R(v.RouteShare), Bus.R(v.FuelLeft), Bus.R(v.CometKg), v.Engines, Bus.R(v.StartClock));

        /// <summary>Two views the same to the last bit, but for the doubles' last few, which may round differently in a long run of steps.</summary>
        public static bool Same(CometView a, CometView b) =>
            a.Id == b.Id && a.State == b.State && a.AtNode == b.AtNode && a.Hop == b.Hop && a.HopCount == b.HopCount && a.FromNode == b.FromNode && a.ToNode == b.ToNode
            && Near(a.Progress, b.Progress, 1e-9) && Near(a.RouteShare, b.RouteShare, 1e-9) && Near(a.FuelLeft, b.FuelLeft, 1e-9) && Near(a.CometKg, b.CometKg, 1e-12)
            && a.Engines == b.Engines;

        public static bool Near(double a, double b, double relative) =>
            (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) <= relative * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));

        /// <summary>A show of the comet event is being drawn on this machine.</summary>
        public static bool ShowDrawn() => GameObject.Find("TR Comet/Drawn") != null;

        /// <summary>A show of the comet event is playing on this machine, drawn or not.</summary>
        public static bool ShowPlaying() => Showing != null && Showing.GetValue(null) != null;

        /// <summary>The direction comet <paramref name="cometId"/> is in at clock <paramref name="clock"/>, by the sky rule from the elements of this machine's own body.</summary>
        public static bool DirectionAt(int cometId, double clock, out SkyVector direction, out string why)
        {
            direction = default;
            CometSkyBodies.Shown shown = CometSkyBodies.For(cometId);
            SkyFrame? frame = CometSkyBodies.Frame();
            OrbitalSimulation sky = OrbitalSimulation.System;
            if (shown?.Body == null || frame == null || sky == null)
            {
                why = "comet " + cometId + " is not in this machine's sky";
                return false;
            }
            Orbit orbit = shown.Body.Orbit;
            SkyOrbit held = new SkyOrbit(orbit.Period, orbit.Inclination, orbit.LongitudeOfAscendingNode, orbit.ArgumentOfPeriapsis, orbit.SemiMajorAxis, shown.Body.LongitudeAtEpoch);
            double sim = sky.SimulationTimeSeconds - (sky.TotalRealTimeSeconds - clock) * sky.TimeScale;
            direction = CometSkyRules.Shipped.CometDirection(frame.Value, held, sim);
            why = null;
            return true;
        }

        /// <summary>The angle in degrees between the game's own body and the sky rule for it now, on this machine.</summary>
        public static double BodyOffRule(int cometId)
        {
            CometSkyBodies.Shown shown = CometSkyBodies.For(cometId);
            if (shown?.Body == null || !DirectionAt(cometId, Bus.Clock, out SkyVector rule, out _))
            {
                return double.NaN;
            }
            Vector3 seen = shown.Body.WorldVector;
            return SkyVector.AngleDegrees(new SkyVector(seen.x, seen.y, seen.z), rule);
        }

        public static double PremixKgPerMole() => CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));

        /// <summary>
        /// Where a bay's comet lands if brought down at clock <paramref name="clock"/>: the sky rule's direction
        /// then, the pad nearest the bay's target, the disc, and the descent, from this machine's own copies.
        /// </summary>
        public static bool AimAt(CometAim.Aim aim, double clock, out GroundPoint sky, out Descent descent, out string why)
        {
            sky = default;
            descent = default;
            if (!DirectionAt(aim.CometId, clock, out SkyVector direction, out why))
            {
                return false;
            }
            CometView comet = ViewsAt(clock).FirstOrDefault(v => v.Id == aim.CometId);
            if (comet.Id != aim.CometId)
            {
                why = "comet " + aim.CometId + " is not on this machine's list";
                return false;
            }
            return Descend(aim, direction, comet, out sky, out descent, out why);
        }

        /// <summary>The descent for a bay's aim from a given direction of its comet.</summary>
        public static bool Descend(CometAim.Aim aim, SkyVector direction, CometView comet, out GroundPoint sky, out Descent descent, out string why)
        {
            sky = default;
            descent = default;
            SpaceMapNode entry = SpaceMap.Current?.EntryNode;
            List<Vector3> pads = entry == null ? new List<Vector3>() : CometAim.Pads(entry);
            CelestialTuning tuning = Effective.Celestial;
            double radius = CometAim.DiscRadius(tuning);
            CometAim.NearestPad(pads, aim.Tracked ? aim.TargetX : 0.0, aim.Tracked ? aim.TargetZ : 0.0, out double padX, out double padZ);
            sky = CelestialRules.Shipped.SkyPoint(padX, padZ, direction.X, direction.Y, direction.Z, radius);
            GroundPoint? target = aim.Tracked ? new GroundPoint(aim.TargetX, aim.TargetZ) : (GroundPoint?)null;
            descent = CelestialRules.Shipped.Descend(sky, target, padX, padZ, radius, comet.CometKg, comet.Engines, comet.FuelLeft, tuning.CorrectionMetresPerDeltaV,
                tuning.EntryWindowSeconds, PremixKgPerMole());
            why = null;
            return true;
        }

        /// <summary>Distance from the impact at which the wave peaks at <paramref name="kpa"/>, by the mod's own wave rule.</summary>
        public static double AtPeak(double moles, double kpa)
        {
            double felt = Math.Min(Math.Max(0.0, moles), CometRules.WaveMolesCap);
            return CometRules.ReferenceMetres * Math.Sqrt(CometRules.ReferencePeakKPa * Math.Pow(felt / CometRules.ReferenceMoles, 2.0 / 3.0) / kpa);
        }
    }

    /// <summary>
    /// The planet sync, seen from both ends without touching it: the host records a digest of every planet state
    /// it writes into the mod's section, with its clock; the joining player's game records the layout byte and the
    /// digest of every state it reads and applies. The digest covers every gas field of the tank, the clouds and
    /// the ice caps and both stored heats, the same fields the mod sends, by their exact bits.
    /// </summary>
    internal static class SyncDigest
    {
        [ThreadStatic]
        private static bool _writing;

        [ThreadStatic]
        private static int _reading;

        private static readonly FieldInfo[] MixFields = typeof(GlobalGasMixSaveData)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(DoubleReference))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();

        private static readonly FieldInfo ReaderStream = AccessTools.Field(typeof(RocketBinaryReader), "_stream");
        private static readonly object Gate = new object();
        private static readonly List<(string Digest, double Clock)> Written = new List<(string, double)>();
        private static readonly List<(string Digest, int Layout, double Clock)> Read = new List<(string, int, double)>();
        private static string _lastWritten;
        private static string _lastRead;
        private static double _lastWrittenClock = double.NegativeInfinity;
        private static double _lastReadClock = double.NegativeInfinity;

        /// <summary>
        /// A state is kept when it differs from the last one kept or comes at least this long after it on the clock.
        /// The host writes the planet into some 20 updates in each sync window, so this keeps one or two a window
        /// while keeping every change. Keeping only changes would keep one state in all of a quiet world, where the
        /// planet does not move from one sync to the next, and the updates after the join could not be told apart.
        /// </summary>
        private const double KeepEverySeconds = 0.5;

        public static void Install(Harmony harmony, bool server)
        {
            MethodInfo write = AccessTools.Method(typeof(Sync), "Write", new[] { typeof(RocketBinaryWriter), typeof(bool) });
            MethodInfo read = AccessTools.Method(typeof(Sync), "Read", new[] { typeof(RocketBinaryReader) });
            MethodInfo save = AccessTools.Method(typeof(PlanetaryAtmosphereSimulation), "Save");
            MethodInfo load = AccessTools.Method(typeof(PlanetaryAtmosphereSimulation), "Load", new[] { typeof(PlanetaryAtmosphereSaveData), typeof(int) });
            if (write == null || read == null || save == null || load == null)
            {
                Bus.Info("sync digest NOT installed: the mod's sync or the game's planet save and load moved");
                return;
            }
            if (server)
            {
                harmony.Patch(write, prefix: new HarmonyMethod(AccessTools.Method(typeof(SyncDigest), nameof(WritePrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(SyncDigest), nameof(WriteFinalizer))));
                harmony.Patch(save, postfix: new HarmonyMethod(AccessTools.Method(typeof(SyncDigest), nameof(SavePostfix))));
            }
            else
            {
                harmony.Patch(read, prefix: new HarmonyMethod(AccessTools.Method(typeof(SyncDigest), nameof(ReadPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(SyncDigest), nameof(ReadFinalizer))));
                harmony.Patch(load, postfix: new HarmonyMethod(AccessTools.Method(typeof(SyncDigest), nameof(LoadPostfix))));
            }
            Bus.Info("sync digest installed on the " + (server ? "host's writes" : "joining player's reads"));
        }

        private static void WritePrefix(bool due) => _writing = due;

        private static Exception WriteFinalizer(Exception __exception)
        {
            _writing = false;
            return __exception;
        }

        private static void SavePostfix(PlanetaryAtmosphereSaveData __result)
        {
            if (!_writing || __result == null)
            {
                return;
            }
            string digest = Of(__result);
            lock (Gate)
            {
                double clock = Bus.Clock;
                if (digest == _lastWritten && clock - _lastWrittenClock < KeepEverySeconds)
                {
                    return;
                }
                _lastWritten = digest;
                _lastWrittenClock = clock;
                Written.Add((digest, clock));
                if (Written.Count > 400)
                {
                    Written.RemoveRange(0, 200);
                }
            }
        }

        /// <summary>The layout byte, peeked from the section without moving the reader: -1 when the stream cannot be peeked.</summary>
        private static void ReadPrefix(RocketBinaryReader reader)
        {
            _reading = -1;
            try
            {
                if (ReaderStream?.GetValue(reader) is Stream stream && stream.CanSeek)
                {
                    long at = stream.Position;
                    int layout = stream.ReadByte();
                    stream.Position = at;
                    _reading = layout;
                }
            }
            catch (Exception)
            {
                _reading = -1;
            }
        }

        private static Exception ReadFinalizer(Exception __exception)
        {
            _reading = 0;
            return __exception;
        }

        private static void LoadPostfix(PlanetaryAtmosphereSaveData saveData)
        {
            if (_reading == 0 || saveData == null)
            {
                return;
            }
            string digest = Of(saveData);
            lock (Gate)
            {
                double clock = Bus.Clock;
                if (digest == _lastRead && clock - _lastReadClock < KeepEverySeconds)
                {
                    return;
                }
                _lastRead = digest;
                _lastReadClock = clock;
                Read.Add((digest, _reading, clock));
                if (Read.Count > 400)
                {
                    Read.RemoveRange(0, 200);
                }
            }
        }

        public static (string Digest, double Clock)[] WrittenSoFar()
        {
            lock (Gate)
            {
                return Written.ToArray();
            }
        }

        public static (string Digest, int Layout, double Clock)[] ReadSoFar()
        {
            lock (Gate)
            {
                return Read.ToArray();
            }
        }

        private static string Of(PlanetaryAtmosphereSaveData data)
        {
            ulong hash = 14695981039346656037UL;
            int count = 0;
            void Add(double value)
            {
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
                for (int i = 0; i < 8; i++)
                {
                    hash ^= (bits >> (8 * i)) & 0xff;
                    hash *= 1099511628211UL;
                }
                count++;
            }
            foreach (GlobalGasMixSaveData mix in new[] { data.GlobalGasMix, data.LiquidClouds, data.IceClouds, data.IceCaps })
            {
                foreach (FieldInfo field in MixFields)
                {
                    double value = mix == null ? 0.0 : (field.GetValue(mix) as DoubleReference)?.Value ?? 0.0;
                    Add(double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value);
                }
            }
            Add(data.LatentOffset?.Value ?? 0.0);
            Add(data.ExternalOffset?.Value ?? 0.0);
            double tank = 0.0;
            if (data.GlobalGasMix != null)
            {
                foreach (FieldInfo field in MixFields)
                {
                    if (field.Name != "Volume" && field.Name != "Energy")
                    {
                        tank += (field.GetValue(data.GlobalGasMix) as DoubleReference)?.Value ?? 0.0;
                    }
                }
            }
            return hash.ToString("x16", Bus.C) + "/" + count.ToString(Bus.C) + "/" + Bus.R(tank);
        }
    }
}
