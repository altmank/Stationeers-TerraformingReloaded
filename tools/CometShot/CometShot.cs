using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using BepInEx;
using HarmonyLib;
using TerrainSystem;
using TerrainSystem.Lods;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace TerraformingReloaded.CometShot
{
    /// <summary>
    /// Screenshots of a comet, for judging how it looks without sitting at the game. Started by
    /// tools/CometShot/run.ps1 in an off-screen instance (batch mode, graphics on): waits for the world,
    /// stands a camera on the ground, runs terraform comet, and renders the camera stack to PNG files
    /// at the times asked for. The flash is a screen overlay, which a camera render leaves out, so it
    /// is painted onto each frame here from the overlay's own colour.
    ///
    /// A batch-mode instance is a dedicated server, and the game leaves out everything only a player
    /// needs to see, so the driver puts those parts back for its own camera:
    ///   - terrain: meshes are built only around a registered LOD requester, normally the local
    ///     player's body, and a server has none, so the camera registers itself as one;
    ///   - sky and sun: the world's skybox, sun light and atmospheric scattering are set up only off
    ///     batch mode, so the driver sets them up from the world's own settings, and drives the sun's
    ///     brightness each frame through the game's own routine;
    ///   - the camera stack: the main camera draws over cameras behind it and does not clear, so the
    ///     whole stack is rendered, into a target cleared first.
    /// Does nothing unless TR_COMETSHOT_ARGS is set.
    /// </summary>
    [BepInPlugin("xceled.stationeers.terraformingreloaded.cometshot", "TR CometShot", "0.0.3")]
    public class CometShot : BaseUnityPlugin
    {
        /// <summary>The density the game counts as solid ground (127 / 255).</summary>
        private const float SolidDensity = 0.49803922f;

        private const float EyeHeight = 1.8f;

        private string _command;
        private string _outDir;
        private List<float> _times;
        private int _width;
        private int _height;
        private float _pitch;
        private string _yaw;
        private float _settle;
        private string _spot;
        private float _sunHeight;
        private float _terrainWait;

        private int _stage;
        private float _stageStart;
        private float _lastReport;
        private float _fired;
        private Camera _camera;
        private List<Camera> _stack = new List<Camera>();
        private Vector3 _eye;
        private Quaternion _look;
        private CameraLodRequester _requester;
        private MethodInfo _setSolarIntensity;
        private RenderTexture _target;
        private Texture2D _readback;
        private int _shot;

        private void Awake()
        {
            _command = Environment.GetEnvironmentVariable("TR_COMETSHOT_ARGS");
            if (string.IsNullOrEmpty(_command))
            {
                enabled = false;
                return;
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            _outDir = Setting("TR_COMETSHOT_DIR", Path.Combine(Path.GetTempPath(), "tr-cometshot"));
            _times = Setting("TR_COMETSHOT_TIMES", "2,8,13,15,15.6,16.05,16.3,17,19,22,26,32,45,70")
                .Split(',').Select(s => float.Parse(s.Trim(), c)).OrderBy(t => t).ToList();
            _width = int.Parse(Setting("TR_COMETSHOT_W", "1600"), c);
            _height = int.Parse(Setting("TR_COMETSHOT_H", "900"), c);
            _pitch = float.Parse(Setting("TR_COMETSHOT_PITCH", "-6"), c);
            _yaw = Setting("TR_COMETSHOT_YAW", "centre");
            _settle = float.Parse(Setting("TR_COMETSHOT_SETTLE", "20"), c);
            _spot = Setting("TR_COMETSHOT_SPOT", "start");
            string sun = Setting("TR_COMETSHOT_SUN", "0.35");
            _sunHeight = sun == "any" ? float.NaN : float.Parse(sun, c);
            _terrainWait = float.Parse(Setting("TR_COMETSHOT_TERRAIN_WAIT", "90"), c);
            Directory.CreateDirectory(_outDir);
            Logger.LogInfo("CometShot: armed, '" + _command + "' -> " + _outDir);
        }

        private static string Setting(string name, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private void Update()
        {
            try
            {
                if (WorldManager.IsGamePaused && (_stage > 0 || WorldSetting.Current != null))
                {
                    WorldManager.SetGamePause(pauseGame: false);
                }
                switch (_stage)
                {
                    case 0:
                        if (WorldSetting.Current != null && Assets.Scripts.PlanetaryAtmosphereSimulation.GetGlobalGasMix() != null && GameManager.GameTickCount > 5)
                        {
                            _stage = 1;
                            _stageStart = Time.realtimeSinceStartup;
                            Logger.LogInfo("CometShot: world is up, settling");
                        }
                        break;
                    case 1:
                        if (Time.realtimeSinceStartup - _stageStart >= _settle)
                        {
                            SetUp();
                            _stage = 4;
                            _stageStart = Time.realtimeSinceStartup;
                        }
                        break;
                    case 4:
                        if (TerrainReady())
                        {
                            Probe();
                            _stage = 2;
                        }
                        break;
                    case 2:
                        Logger.LogInfo("CometShot: firing 'terraform " + _command + "'");
                        string reply = Util.Commands.CommandLine.CommandsMap["terraform"].Execute(_command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Prepend("comet").ToArray());
                        Logger.LogInfo("CometShot: reply: " + reply);
                        _fired = Time.time;
                        _stage = 3;
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.LogError("CometShot: failed: " + e);
                Finish();
            }
        }

        private void LateUpdate()
        {
            if (_stage < 2 || _stage > 4)
            {
                return;
            }
            try
            {
                Pose();
                Sunlight();
                if (_stage != 3)
                {
                    return;
                }
                float since = Time.time - _fired;
                if (_times.Count > 0 && since >= _times[0])
                {
                    _times.RemoveAt(0);
                    Capture(since);
                }
                if (_times.Count == 0)
                {
                    Finish();
                }
            }
            catch (Exception e)
            {
                Logger.LogError("CometShot: failed: " + e);
                Finish();
            }
        }

        // ------------------------------------------------------------ setting the scene

        private void SetUp()
        {
            _camera = CameraController.CurrentCamera ? CameraController.CurrentCamera : Camera.main;
            string source = _camera ? (CameraController.CurrentCamera ? "CameraController" : "Camera.main") : "new";
            if (_camera == null)
            {
                _camera = new GameObject("CometShot camera").AddComponent<Camera>();
                _camera.fieldOfView = 70f;
                _camera.farClipPlane = 3000f;
            }
            Logger.LogInfo("CometShot: camera " + source + " '" + _camera.name + "', fov " + _camera.fieldOfView + ", far " + _camera.farClipPlane
                + ", hdr " + _camera.allowHDR + ", path " + _camera.actualRenderingPath + ", screen " + Screen.width + "x" + Screen.height + ", device " + SystemInfo.graphicsDeviceType
                + ", components: " + string.Join(", ", _camera.GetComponents<Component>().Select(x => x.GetType().Name + (x is Behaviour b ? (b.enabled ? "" : "(off)") : ""))));
            foreach (Camera camera in Camera.allCameras.OrderBy(x => x.depth))
            {
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "CometShot: scene camera '{0}' depth {1}, clear {2}, mask 0x{3:X8}, fov {4}, near {5}, far {6}, target {7}, parent {8}",
                    camera.name, camera.depth, camera.clearFlags, camera.cullingMask, camera.fieldOfView, camera.nearClipPlane, camera.farClipPlane,
                    camera.targetTexture ? camera.targetTexture.name : "screen", camera.transform.parent ? camera.transform.parent.name : "none"));
            }

            Stand();
            SkyAndSun();
            _stack = Camera.allCameras
                .Where(x => x == _camera || (x.targetTexture == null && x.name.IndexOf("ImGui", StringComparison.OrdinalIgnoreCase) < 0))
                .OrderBy(x => x.depth)
                .ToList();
            Logger.LogInfo("CometShot: rendering the stack " + string.Join(" < ", _stack.Select(x => "'" + x.name + "'")));

            _requester = new CameraLodRequester(_eye);
            LodManager.EnqueueRequesterToUpdate(_requester);
            Pose();
            _target = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32);
            _readback = new Texture2D(_width, _height, TextureFormat.RGB24, false);
        }

        /// <summary>Where the camera stands: a start location of the world by default, on the voxel ground.</summary>
        private void Stand()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            Vector3 flat;
            string what;
            string[] parts = _spot.Split(',');
            if (parts.Length == 2)
            {
                flat = new Vector3(float.Parse(parts[0], c), 0f, float.Parse(parts[1], c));
                what = "asked";
            }
            else
            {
                StartLocationData spot = WorldSetting.Current.Data.StartLocationDatas
                    .Where(x => x != null && x.GetType() == typeof(StartLocationData) && x.IsValid())
                    .FirstOrDefault(x => _spot == "start" || string.Equals(x.Id, _spot, StringComparison.OrdinalIgnoreCase));
                if (spot == null)
                {
                    flat = Vector3.zero;
                    what = "no start location '" + _spot + "' in " + WorldSetting.Current.Id + ", so the origin";
                }
                else
                {
                    flat = spot.WorldPosition();
                    what = "start location " + spot.Id;
                }
            }
            float? ground = Surface(flat.x, flat.z);
            _eye = new Vector3(flat.x, (ground ?? 300f) + EyeHeight, flat.z);
            float yaw = _yaw == "centre"
                ? (new Vector2(flat.x, flat.z).sqrMagnitude < 1f ? 0f : Mathf.Atan2(-flat.x, -flat.z) * Mathf.Rad2Deg)
                : float.Parse(_yaw, c);
            _look = Quaternion.Euler(_pitch, yaw, 0f);
            Logger.LogInfo(string.Format(c, "CometShot: standing at {0} ({1}), voxel ground {2}, world size {3}, curvature {4}, facing yaw {5:0.0} pitch {6:0.0}",
                _eye, what, ground.HasValue ? ground.Value.ToString("0.0", c) : "none", VoxelConstants.Size, VoxelConstants.WorldCurvature, yaw, _pitch));
        }

        /// <summary>
        /// What WorldManager.InitializeWorldEnvironment sets up only off batch mode: the world's
        /// skybox, its sun light, and the atmospheric scattering.
        /// </summary>
        private void SkyAndSun()
        {
            WorldSetting world = WorldSetting.Current;
            if (world.SkyBox != null)
            {
                RenderSettings.skybox = Instantiate(world.SkyBox);
                RenderSettings.ambientMode = AmbientMode.Skybox;
            }
            string sun = "none in the world settings";
            if (world.Sun != null && WorldManager.Instance != null && CursorManager.Instance != null)
            {
                GameObject made = Instantiate(world.Sun);
                made.name = "Sun";
                Light light = made.GetComponent<Light>();
                WorldManager.Instance.WorldSun.TargetLight = light;
                WorldManager.Instance.WorldSun.Color = light.color;
                WorldManager.Instance.WorldSun.Intensity = light.intensity;
                light.shadowBias = 0.2f;
                light.shadowNormalBias = 0f;
                RenderSettings.sun = light;
                OrbitalSimulation.AssignSun(light);
                sun = "'" + light.name + "' intensity " + light.intensity.ToString("0.00", CultureInfo.InvariantCulture) + ", shadows " + light.shadows;
            }
            _setSolarIntensity = AccessTools.Method(typeof(OrbitalSimulation), "SetSolarIntensity");

            string scattering = "off for this world";
            CursorManager cursor = CursorManager.Instance;
            if (WorldManager.AtmosphericScattering && cursor != null && cursor.AtmosphericScattering != null && cursor.AtmosphericScatteringCamera != null)
            {
                bool supported = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan;
                cursor.AtmosphericScattering.Import(world);
                cursor.AtmosphericScattering.enabled = supported;
                cursor.AtmosphericScatteringCamera.enabled = supported;
                scattering = supported ? "on" : "unsupported on " + SystemInfo.graphicsDeviceType;
            }

            string time = "left as it was";
            if (!float.IsNaN(_sunHeight))
            {
                // A day on any world is shorter than this many ten-second steps.
                int steps = 0;
                while (OrbitalSimulation.WorldSunVector.y < _sunHeight && steps < 20000)
                {
                    OrbitalSimulation.SimulateTime(10.0);
                    steps++;
                }
                time = steps == 0 ? "already up" : string.Format(CultureInfo.InvariantCulture, "moved on {0:N0} s", steps * 10);
            }
            DynamicGI.UpdateEnvironment();
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "CometShot: sky {0}, sun {1}, scattering {2}, sun height {3:0.000} ({4}), solar intensity routine {5}",
                RenderSettings.skybox ? "'" + RenderSettings.skybox.name + "'" : "none", sun, scattering, OrbitalSimulation.WorldSunVector.y, time, _setSolarIntensity != null ? "found" : "missing"));
        }

        /// <summary>The sun's brightness, which a batch-mode instance never sets: the game's own routine.</summary>
        private void Sunlight()
        {
            if (_setSolarIntensity != null && OrbitalSimulation.WorldSun != null)
            {
                _setSolarIntensity.Invoke(null, null);
            }
        }

        /// <summary>Waits for the terrain around the camera to be built and given colliders.</summary>
        private bool TerrainReady()
        {
            float waited = Time.realtimeSinceStartup - _stageStart;
            bool busy = LodManager.Instance == null || LodManager.Instance.AwaitingTerrainGeneration;
            bool hit = GroundHit(_eye + Vector3.up * 50f, 400f, out RaycastHit ground);
            if (!busy && hit)
            {
                _eye = ground.point + Vector3.up * EyeHeight;
                _requester.Move(_eye);
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "CometShot: terrain ready after {0:0.0} s: ground hit at {1} on '{2}', meshes {3}",
                    waited, ground.point, ground.collider.name, Meshes()));
                return true;
            }
            if (waited - _lastReport >= 5f)
            {
                _lastReport = waited;
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "CometShot: waiting for terrain, {0:0} s, building {1}, ground hit {2}, meshes {3}",
                    waited, busy, hit, Meshes()));
            }
            if (waited >= _terrainWait)
            {
                Logger.LogWarning(string.Format(CultureInfo.InvariantCulture, "CometShot: NO TERRAIN after {0:0} s (building {1}, ground hit {2}, meshes {3}); shooting anyway",
                    waited, busy, hit, Meshes()));
                return true;
            }
            return false;
        }

        /// <summary>The nearest thing hit straight down, passing through the cameras (the main one carries a collider).</summary>
        private static bool GroundHit(Vector3 from, float length, out RaycastHit ground)
        {
            ground = Physics.RaycastAll(from, Vector3.down, length, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.collider.GetComponentInParent<Camera>() == null)
                .OrderBy(h => h.distance)
                .FirstOrDefault();
            return ground.collider != null;
        }

        /// <summary>Active terrain meshes per LOD level, finest first.</summary>
        private static string Meshes()
        {
            if (LodManager.Instance == null)
            {
                return "no LodManager";
            }
            return string.Join("/", Enumerable.Range(0, 6).Select(level => LodManager.Instance.LodMeshCacheCounts(level).active.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// The ground along the view, from the voxels (colliders exist only next to a requester), and
        /// how far the terrain shader draws it below that at this distance from the camera.
        /// </summary>
        private void Probe()
        {
            TerrainCurvature.Refresh();
            Vector3 forward = Vector3.ProjectOnPlane(_look * Vector3.forward, Vector3.up).normalized;
            foreach (float range in new[] { 500f, 1000f, 1750f, 2000f, 2250f, 2500f, 2750f, 3000f })
            {
                Vector3 at = _eye + forward * range;
                float? ground = Surface(at.x, at.z);
                string drawn = ground.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "ground {0:0.0} ({1:+0.0;-0.0} from the eye), drawn {2:0.0} lower", ground.Value, ground.Value - _eye.y, TerrainCurvature.Drop(new Vector3(at.x, ground.Value, at.z), _eye))
                    : "outside the voxel world";
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "CometShot: along the view at {0:N0} m ({1:0}, {2:0}): {3}", range, at.x, at.z, drawn));
            }
        }

        /// <summary>The top of the voxel ground at a point, scanned down the column as the game's spawn does.</summary>
        private static float? Surface(float x, float z)
        {
            int half = VoxelConstants.Offset;
            if (x < -half || x >= half || z < -half || z >= half)
            {
                return null;
            }
            for (int y = 1023; y >= 0; y--)
            {
                if (VoxelTerrain.GetDensityAtSize(new Vector3(x, y, z), 1) >= SolidDensity)
                {
                    return y + 1f;
                }
            }
            return null;
        }

        // ------------------------------------------------------------ the pictures

        private void Pose()
        {
            _camera.transform.SetPositionAndRotation(_eye, _look);
            foreach (Camera camera in _stack)
            {
                if (camera == _camera || camera.transform.IsChildOf(_camera.transform))
                {
                    continue;
                }
                camera.transform.SetPositionAndRotation(_eye, _look);
                if (camera.depth < _camera.depth)
                {
                    camera.fieldOfView = _camera.fieldOfView;
                }
            }
            CameraController.CameraPosition = _eye;
            CameraController.EffectiveCameraPosition = _eye;
        }

        private void Capture(float since)
        {
            DynamicGI.UpdateEnvironment();
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = _target;
            GL.Clear(true, true, Color.black);
            foreach (Camera camera in _stack)
            {
                if (camera == null || !camera.enabled || !camera.gameObject.activeInHierarchy)
                {
                    continue;
                }
                RenderTexture previousTarget = camera.targetTexture;
                camera.targetTexture = _target;
                camera.Render();
                camera.targetTexture = previousTarget;
            }
            RenderTexture.active = _target;
            _readback.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
            RenderTexture.active = previousActive;

            // The overlay flash, painted on the way it would be over the screen.
            Image flash = GameObject.Find("TR Comet/Flash overlay/Flash")?.GetComponent<Image>();
            if (flash != null && flash.color.a > 0.002f)
            {
                Color32[] pixels = _readback.GetPixels32();
                Color f = flash.color;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color p = pixels[i];
                    pixels[i] = Color.Lerp(p, new Color(f.r, f.g, f.b), f.a);
                }
                _readback.SetPixels32(pixels);
            }
            _readback.Apply(false);
            string file = Path.Combine(_outDir, string.Format(CultureInfo.InvariantCulture, "shot{0:00}_t{1:000.00}.png", _shot++, since));
            File.WriteAllBytes(file, _readback.EncodeToPNG());
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "CometShot: shot t={0:0.00} -> {1} (far {2:N0}, sun height {3:0.000}, sun {4:0.00})",
                since, file, _camera.farClipPlane, OrbitalSimulation.WorldSunVector.y, OrbitalSimulation.WorldSun ? OrbitalSimulation.WorldSun.intensity : 0f));
            ImpactOnGround();
        }

        /// <summary>
        /// Once the comet has landed: the impact's height against this driver's own read of the
        /// voxels, the drop the game's TerrainCurvature draws there for this camera, and where the
        /// show put the impact, which must be the ground as drawn. Worked out here apart from the
        /// mod's own rule, so the two are checked against each other.
        /// </summary>
        private void ImpactOnGround()
        {
            Transform impact = GameObject.Find("TR Comet/Drawn/Impact")?.transform;
            if (impact == null || impact.parent == null || impact.parent.parent == null)
            {
                return;
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            Vector3 real = impact.parent.parent.TransformPoint(impact.localPosition);
            int half = VoxelConstants.Offset;
            bool inside = real.x >= -half && real.x < half && real.z >= -half && real.z < half;
            float columnX = inside ? real.x : Mathf.Clamp(real.x, -half + 0.5f, half - 0.5f);
            float columnZ = inside ? real.z : Mathf.Clamp(real.z, -half + 0.5f, half - 0.5f);
            float? ground = Surface(columnX, columnZ);
            TerrainCurvature.Refresh();
            Vector3 eye = _camera.transform.position;
            float drop = TerrainCurvature.Drop(real, eye);
            float groundDrawn = (ground ?? real.y) - drop;
            float showDrawn = impact.position.y;
            bool height = ground.HasValue && Mathf.Abs(real.y - ground.Value) < 0.5f;
            bool drawn = Mathf.Abs(showDrawn - groundDrawn) < 0.5f;
            Logger.LogInfo(string.Format(c,
                "CometShot: impact at ({0:0.0}, {1:0.0}), {2:N0} m from the camera{3}: voxel ground {4}, event height {5:0.0} -> height {6}; drawn {7:0.0} m lower for this camera, ground drawn at {8:0.0}, show drawn at {9:0.0} (frame {10:+0.0;-0.0}) -> on the drawn ground {11}",
                real.x, real.z, Vector2.Distance(new Vector2(real.x, real.z), new Vector2(eye.x, eye.z)),
                inside ? "" : string.Format(c, ", outside the voxel world, read at its edge ({0:0.0}, {1:0.0})", columnX, columnZ),
                ground.HasValue ? ground.Value.ToString("0.0", c) : "none", real.y, height ? "PASS" : "FAIL",
                drop, groundDrawn, showDrawn, impact.parent.localPosition.y, drawn ? "PASS" : "FAIL"));
        }

        private void Finish()
        {
            if (_stage == 9)
            {
                return;
            }
            _stage = 9;
            if (_requester != null)
            {
                _requester.BeingDestroyed = true;
                LodManager.EnqueueRequesterToRemove(_requester);
            }
            BepInEx.Logging.Logger.CreateLogSource("CometShot").LogInfo("CometShot: done");
            File.WriteAllText(Path.Combine(_outDir, "done.txt"), "done");
        }

        /// <summary>
        /// The camera as a terrain LOD requester, the way the game's own cinematic camera is one: the
        /// terrain manager builds meshes, and colliders at the finest level, only around requesters.
        /// </summary>
        private sealed class CameraLodRequester : ILodRequester
        {
            private Vector3 _centre;

            public CameraLodRequester(Vector3 centre) => _centre = centre;

            public bool ShouldRender => true;

            public HashSet<Vector3Int>[] RequestedLods { get; set; } = LodHelper.InitArray(6);

            public LodInfo LodInfo => LodManager.PlayerLodInfo.Levels != null ? LodManager.PlayerLodInfo : LodManager.LodHighQuality;

            public Vector3 CenterPosition => _centre;

            public string DisplayName => "CometShot camera";

            public ushort NetworkUpdateFlags { get; set; }

            /// <summary>Synthetic, beside the cinematic camera's long.MinValue + 1.</summary>
            public long ReferenceId { get; set; } = long.MinValue + 2;

            public bool BeingDestroyed { get; set; }

            public int ThreadCost => 1;

            public bool CanThread() => !BeingDestroyed;

            public string DebugName() => DisplayName;

            public void PrintDebugInfo(bool verbose = false)
            {
            }

            public void OnAssignedReference()
            {
            }

            public void Move(Vector3 centre)
            {
                if ((centre - _centre).sqrMagnitude > 64f)
                {
                    _centre = centre;
                    LodManager.EnqueueRequesterToUpdate(this);
                }
            }
        }
    }
}
