using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Assets.Scripts;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace TerraformingReloaded.CometShot
{
    /// <summary>
    /// Screenshots of a comet, for judging how it looks without sitting at the game. Started by
    /// tools/CometShot/run.ps1 in an off-screen instance (batch mode, graphics on): waits for the world,
    /// stands a camera on the ground, runs terraform comet, and renders that camera to PNG files at
    /// the times asked for. The flash is a screen overlay, which a camera render leaves out, so it is
    /// painted onto each frame here from the overlay's own colour.
    /// Does nothing unless TR_COMETSHOT_ARGS is set.
    /// </summary>
    [BepInPlugin("xceled.stationeers.terraformingreloaded.cometshot", "TR CometShot", "0.0.1")]
    public class CometShot : BaseUnityPlugin
    {
        private string _command;
        private string _outDir;
        private List<float> _times;
        private int _width;
        private int _height;
        private float _pitch;
        private float _yaw;
        private float _settle;

        private int _stage;
        private float _stageStart;
        private float _fired;
        private Camera _camera;
        private Vector3 _eye;
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
            _outDir = Environment.GetEnvironmentVariable("TR_COMETSHOT_DIR") ?? Path.Combine(Path.GetTempPath(), "tr-cometshot");
            _times = (Environment.GetEnvironmentVariable("TR_COMETSHOT_TIMES") ?? "2,8,13,15,15.6,16.05,16.3,17,19,22,26,32,45,70")
                .Split(',').Select(s => float.Parse(s.Trim(), c)).OrderBy(t => t).ToList();
            _width = int.Parse(Environment.GetEnvironmentVariable("TR_COMETSHOT_W") ?? "1600", c);
            _height = int.Parse(Environment.GetEnvironmentVariable("TR_COMETSHOT_H") ?? "900", c);
            _pitch = float.Parse(Environment.GetEnvironmentVariable("TR_COMETSHOT_PITCH") ?? "-6", c);
            _yaw = float.Parse(Environment.GetEnvironmentVariable("TR_COMETSHOT_YAW") ?? "0", c);
            _settle = float.Parse(Environment.GetEnvironmentVariable("TR_COMETSHOT_SETTLE") ?? "20", c);
            Directory.CreateDirectory(_outDir);
            Logger.LogInfo("CometShot: armed, '" + _command + "' -> " + _outDir);
        }

        private void Update()
        {
            try
            {
                if (WorldManager.IsGamePaused && _stage > 0)
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
            if (_stage != 3)
            {
                return;
            }
            try
            {
                Pose();
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
                + ", hdr " + _camera.allowHDR + ", path " + _camera.actualRenderingPath + ", components: "
                + string.Join(", ", _camera.GetComponents<Component>().Select(x => x.GetType().Name + (x is Behaviour b ? (b.enabled ? "" : "(off)") : ""))));
            Logger.LogInfo("CometShot: cameras in scene: " + string.Join(", ", Camera.allCameras.Select(x => x.name + (x.enabled ? "" : "(off)") + " d" + x.depth)));

            Vector3 from = new Vector3(0f, 3000f, 0f);
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 6000f, ~0, QueryTriggerInteraction.Ignore))
            {
                _eye = hit.point + Vector3.up * 1.8f;
                Logger.LogInfo("CometShot: ground at " + hit.point + " (" + hit.collider.name + ")");
            }
            else
            {
                _eye = new Vector3(0f, 300f, 0f);
                Logger.LogInfo("CometShot: no ground found under the origin; standing at " + _eye);
            }
            foreach (float bearing in new[] { 0f, 90f, 180f, 270f })
            {
                foreach (float range in new[] { 200f, 1000f, 3000f })
                {
                    Vector3 probe = _eye + Quaternion.AngleAxis(bearing, Vector3.up) * Vector3.forward * range + Vector3.up * 3000f;
                    bool found = Physics.Raycast(probe, Vector3.down, out RaycastHit h, 7000f, ~0, QueryTriggerInteraction.Ignore);
                    Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "CometShot: probe bearing {0} range {1}: {2}", bearing, range, found ? h.point.y.ToString("0.0", CultureInfo.InvariantCulture) : "no ground"));
                }
            }
            Pose();
            _target = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32);
            _readback = new Texture2D(_width, _height, TextureFormat.RGB24, false);
        }

        private void Pose()
        {
            _camera.transform.position = _eye;
            _camera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void Capture(float since)
        {
            RenderTexture previousTarget = _camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            _camera.targetTexture = _target;
            _camera.Render();
            RenderTexture.active = _target;
            _readback.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
            _camera.targetTexture = previousTarget;
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
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "CometShot: shot t={0:0.00} -> {1} (far {2:N0})", since, file, _camera.farClipPlane));
        }

        private void Finish()
        {
            if (_stage == 9)
            {
                return;
            }
            _stage = 9;
            BepInEx.Logging.Logger.CreateLogSource("CometShot").LogInfo("CometShot: done");
            File.WriteAllText(Path.Combine(_outDir, "done.txt"), "done");
        }
    }
}
