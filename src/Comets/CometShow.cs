using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using TerraformingReloaded.Patching;
using UnityEngine;
using UnityEngine.UI;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// One comet, from first sight to the dust settling, and the gas it brings arriving at the
    /// moment it lands. Everything is built in code when it starts and destroyed when it ends.
    ///
    /// The sequence, with t in seconds from the command:
    ///   0 to 16    a glowing head and tail high in the sky, slow at first, then falling faster and
    ///              faster, heating from blue-white to orange, shedding sparks and smoke, its light
    ///              sweeping the ground, a roar and a shake building under it.
    ///   16         impact, silent: a flash that whites out the view, a fireball, a shock front,
    ///              debris thrown into the sky on trails, a column rising into a mushroom cap in the
    ///              colours of what the comet was made of, a glow on the horizon. The gas goes in.
    ///   16 + d/340 the sound arrives, as far behind the light as the impact is away: the boom, a
    ///              hard shake, a gust of dust past the player.
    ///   after      burning debris falling out of the sky, the cloud spreading and fading, a long
    ///              rumble. Gone at about two minutes.
    /// Local only: the host sees it, joining players do not.
    /// </summary>
    internal sealed class CometShow : MonoBehaviour
    {
        public static CometShow Running { get; private set; }

        private const float ApproachSeconds = 16f;
        private const float SpeedOfSound = 340f;
        private const float EndSeconds = 115f;

        private CometPlan _plan;
        private Camera _camera;
        private float _originalFar;
        private float _t;
        private bool _impacted;
        private bool _boomed;
        private bool _rumbled;
        private bool _roared;
        private float _boomAt;
        private float _flashView;

        private Vector3 _start;
        private Vector3 _impact;
        private Vector3 _toImpact;
        private float _distance;
        private float _scale;
        private float _radius;
        private float _plumeHeight;
        private Color _gasColour;
        private Gradient _bands;

        // Before impact.
        private Transform _head;
        private Glow _coma;
        private Glow _core;
        private Light _headLight;
        private ParticleSystem _tail;
        private ParticleSystem _sparks;
        private ParticleSystem _contrail;
        private TrailRenderer _hotTrail;

        // After.
        private Light _flashLight;
        private Glow _flashCore;
        private Glow _fireGlow;
        private Glow _shock;
        private Glow _skyGlow;
        private Glow _groundRing;
        private ParticleSystem _fireball;
        private ParticleSystem _ejecta;
        private ParticleSystem _stem;
        private ParticleSystem _stemGlow;
        private ParticleSystem _cap;
        private ParticleSystem _capGlow;
        private ParticleSystem _groundDust;
        private ParticleSystem _debrisRain;
        private Transform _capTransform;
        private Image _flash;
        private AudioSource _audio;

        private readonly List<Glow> _glows = new List<Glow>();

        /// <summary>Starts a comet toward the player's view. Null on success, or why it could not.</summary>
        public static string Launch(CometPlan plan)
        {
            if (Running != null)
            {
                return "A comet is already on its way. Wait for it to land, or: terraform comet stop";
            }
            Camera camera = CameraController.CurrentCamera ? CameraController.CurrentCamera : Camera.main;
            if (camera == null)
            {
                return "There is no camera to watch it from, so there is nothing to show.";
            }
            GameObject root = new GameObject("TR Comet");
            CometShow show = root.AddComponent<CometShow>();
            try
            {
                show.Build(plan, camera);
            }
            catch
            {
                Destroy(root);
                throw;
            }
            Running = show;
            return null;
        }

        public static bool Stop()
        {
            if (Running == null)
            {
                return false;
            }
            Destroy(Running.gameObject);
            Running = null;
            return true;
        }

        /// <summary>Where it will land and how far away, for the command's reply.</summary>
        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:N0} m away, {1:0}° to the {2} of where you are looking, in {3:0} s",
                _distance, Math.Abs(_plan.TurnDegrees), _plan.TurnDegrees < 0 ? "left" : "right", ApproachSeconds);
        }

        private void Build(CometPlan plan, Camera camera)
        {
            _plan = plan;
            _camera = camera;
            _scale = plan.Scale;
            _radius = 120f * _scale;
            _plumeHeight = 1100f * _scale;
            _gasColour = CometPalette.Blend(plan.Types, plan.Shares);
            _bands = CometPalette.Bands(plan.Types, plan.Shares);

            Vector3 eye = camera.transform.position;
            Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.ProjectOnPlane(camera.transform.up, Vector3.up);
            }
            forward = Quaternion.AngleAxis(plan.TurnDegrees, Vector3.up) * forward.normalized;
            _distance = plan.Range ?? (1500f + 900f * _scale);
            _impact = Ground(eye + forward * _distance, eye.y - 1.7f);
            _toImpact = forward;

            // In from beyond the impact and off to one side, so it crosses the view on its way down
            // instead of coming straight at the player and foreshortening to a dot.
            float side = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            Vector3 across = Quaternion.AngleAxis(side * 40f, Vector3.up) * -forward;
            Vector3 travel = (across * Mathf.Cos(24f * Mathf.Deg2Rad) - Vector3.up * Mathf.Sin(24f * Mathf.Deg2Rad)).normalized;
            float pathLength = 13000f + 2000f * _scale;
            _start = _impact - travel * pathLength;

            _originalFar = camera.farClipPlane;
            camera.farClipPlane = Mathf.Max(_originalFar, Vector3.Distance(eye, _start) * 1.3f, _distance * 4f);

            // Made now rather than when first heard, so the boom does not hitch the frame it plays in.
            _ = CometArt.Roar;
            _ = CometArt.Boom;
            _ = CometArt.Rumble;
            BuildHead(travel);
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.spatialBlend = 0f;
            _audio.playOnAwake = false;
            _audio.volume = 1f;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[TR comet] launched: {0:N0} mol, scale {1:0.00}, impact {2} at {3:N0} m, start {4}, far clip {5:N0} -> {6:N0}, shaders {7}",
                plan.Moles, _scale, _impact, _distance, _start, _originalFar, camera.farClipPlane, CometArt.ShaderReport));
        }

        private static Vector3 Ground(Vector3 at, float fallbackY)
        {
            Vector3 above = new Vector3(at.x, fallbackY + 4000f, at.z);
            if (Physics.Raycast(above, Vector3.down, out RaycastHit hit, 9000f, ~0, QueryTriggerInteraction.Ignore))
            {
                return hit.point;
            }
            return new Vector3(at.x, fallbackY, at.z);
        }

        // ---------------------------------------------------------------- the head

        private void BuildHead(Vector3 travel)
        {
            _head = new GameObject("Head").transform;
            _head.SetParent(transform, false);
            _head.position = _start;
            _head.rotation = Quaternion.LookRotation(travel);

            _coma = NewGlow("Coma", CometArt.Glow, additive: true);
            _core = NewGlow("Core", CometArt.Glow, additive: true);

            _headLight = NewLight("Head light", _head);
            _headLight.color = new Color(0.8f, 0.9f, 1f);

            // The tail: shed backwards along the path, so it streams behind the head even while the
            // head is still slow and far away.
            _tail = NewParticles("Tail", _head, CometArt.Additive(CometArt.Glow, new Color(0.5f, 0.5f, 0.5f, 0.5f)), 600);
            var main = _tail.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(140f * _scale, 260f * _scale);
            main.startColor = new ParticleSystem.MinMaxGradient(_bands) { mode = ParticleSystemGradientMode.RandomColor };
            var velocity = _tail.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            Vector3 back = -travel * 260f;
            velocity.x = new ParticleSystem.MinMaxCurve(back.x);
            velocity.y = new ParticleSystem.MinMaxCurve(back.y + 15f);
            velocity.z = new ParticleSystem.MinMaxCurve(back.z);
            var shape = _tail.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 40f * _scale;
            SizeOverLife(_tail, 0.6f, 2.2f);
            ColourOverLife(_tail, Fade(Color.white, 0.55f, 0f));
            var emission = _tail.emission;
            emission.rateOverTime = 60f;
            _tail.Play();

            // Sparks: pieces breaking off as it heats.
            _sparks = NewParticles("Sparks", _head, CometArt.Additive(CometArt.Glow, new Color(1f, 1f, 1f, 0.5f)), 800);
            main = _sparks.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 2.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(40f, 160f);
            main.startSize = new ParticleSystem.MinMaxCurve(10f * _scale, 30f * _scale);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.55f), _gasColour);
            main.gravityModifier = 3f;
            shape = _sparks.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 30f * _scale;
            ColourOverLife(_sparks, Fade(Color.white, 1f, 0f));
            emission = _sparks.emission;
            emission.rateOverTime = 0f;
            _sparks.Play();

            // The smoke it leaves in the sky, which hangs there long after it lands.
            _contrail = NewParticles("Contrail", _head, CometArt.Blended(CometArt.Smoke, new Color(0.5f, 0.5f, 0.5f, 0.5f)), 900);
            main = _contrail.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(35f, 55f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(60f * _scale, 110f * _scale);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.42f, 0.38f, 0.36f, 0.75f), Color.Lerp(new Color(0.45f, 0.42f, 0.4f), _gasColour, 0.25f) * new Color(1f, 1f, 1f, 0.6f));
            shape = _contrail.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 25f * _scale;
            SizeOverLife(_contrail, 1f, 4.5f);
            ColourOverLife(_contrail, Fade(Color.white, 1f, 0f, 0.15f));
            emission = _contrail.emission;
            emission.rateOverTime = 0f;
            _contrail.Play();

            _hotTrail = _head.gameObject.AddComponent<TrailRenderer>();
            _hotTrail.sharedMaterial = CometArt.Additive(CometArt.Streak, new Color(1f, 1f, 1f, 0.5f));
            _hotTrail.time = 2.5f;
            _hotTrail.minVertexDistance = 20f;
            _hotTrail.widthMultiplier = 90f * _scale;
            _hotTrail.widthCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.15f);
            _hotTrail.alignment = LineAlignment.View;
            _hotTrail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _hotTrail.receiveShadows = false;
            _hotTrail.colorGradient = TrailGradient(new Color(0.8f, 0.9f, 1f));
            _hotTrail.emitting = true;
        }

        private Gradient TrailGradient(Color hot)
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(hot, 0.2f), new GradientColorKey(_gasColour, 0.6f), new GradientColorKey(_gasColour * 0.6f, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.3f), new GradientAlphaKey(0f, 1f) });
            return gradient;
        }

        // ---------------------------------------------------------------- the impact

        private void BuildImpact()
        {
            Transform at = new GameObject("Impact").transform;
            at.SetParent(transform, false);
            at.position = _impact;

            _flashLight = NewLight("Flash", at);
            _flashLight.transform.position = _impact + Vector3.up * _radius;

            _flashCore = NewGlow("Flash core", CometArt.Glow, additive: true);
            _fireGlow = NewGlow("Fire glow", CometArt.Glow, additive: true);
            _shock = NewGlow("Shock front", CometArt.Ring, additive: true);
            _skyGlow = NewGlow("Sky glow", CometArt.Glow, additive: true);
            _groundRing = NewGlow("Ground ring", CometArt.Ring, additive: false);
            _groundRing.Flat = true;

            Color hotWhite = new Color(1f, 0.97f, 0.9f);
            Color orange = new Color(1f, 0.55f, 0.18f);
            Color ember = new Color(0.55f, 0.12f, 0.05f);

            // Fireball: a burst of hot gas rolling outward and cooling through the comet's colours.
            _fireball = NewParticles("Fireball", at, CometArt.Additive(CometArt.Smoke, new Color(1f, 1f, 1f, 0.5f)), 200);
            var main = _fireball.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 0.5f, _radius * 1.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.8f, _radius * 1.7f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = Color.white;
            var shape = _fireball.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            shape.radius = _radius * 0.3f;
            Burst(_fireball, 110);
            Drag(_fireball, 0.06f);
            SizeOverLife(_fireball, 0.7f, 2.0f);
            Rotate(_fireball, 0.4f);
            ColourOverLife(_fireball, Keys(
                new[] { (hotWhite, 0f), (new Color(1f, 0.85f, 0.4f), 0.12f), (orange, 0.3f), (_gasColour, 0.55f), (ember, 1f) },
                new[] { (1f, 0f), (0.9f, 0.4f), (0f, 1f) }));

            // Ejecta: rock and ice thrown high, glowing, on trails, falling back down.
            _ejecta = NewParticles("Ejecta", at, CometArt.Additive(CometArt.Glow, new Color(1f, 1f, 1f, 0.5f)), 260);
            float rise = 4.5f;
            float height = _plumeHeight * 0.7f;
            float g = 2f * height / (rise * rise);
            main = _ejecta.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 12f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(g * rise * 0.45f, g * rise * 1.05f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.07f, _radius * 0.18f);
            main.startColor = new ParticleSystem.MinMaxGradient(hotWhite, new Color(1f, 0.7f, 0.35f));
            main.gravityModifier = g / 9.81f;
            shape = _ejecta.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 42f;
            shape.radius = _radius * 0.3f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            Burst(_ejecta, 140);
            ColourOverLife(_ejecta, Keys(
                new[] { (Color.white, 0f), (orange, 0.35f), (_gasColour, 0.7f), (ember, 1f) },
                new[] { (1f, 0f), (1f, 0.75f), (0f, 1f) }));
            Trails(_ejecta, 0.35f, _radius * 0.12f);
            Collide(_ejecta);

            // The column: dark smoke and dust climbing, lit from inside by the fire at its root.
            _stem = NewParticles("Stem", at, CometArt.Blended(CometArt.Smoke, new Color(0.5f, 0.5f, 0.5f, 0.5f)), 900);
            main = _stem.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(11f, 17f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_plumeHeight / 9f, _plumeHeight / 6f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.6f, _radius * 1.0f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(Darken(_bands, 0.45f, new Color(0.35f, 0.3f, 0.27f), 0.6f)) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _stem.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 7f;
            shape.radius = _radius * 0.35f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            Drag(_stem, 0.025f);
            SizeOverLife(_stem, 0.6f, 2.2f);
            Rotate(_stem, 0.15f);
            ColourOverLife(_stem, Fade(Color.white, 0.92f, 0f, 0.12f));

            _stemGlow = NewParticles("Stem glow", at, CometArt.Additive(CometArt.Smoke, new Color(1f, 1f, 1f, 0.5f)), 400);
            main = _stemGlow.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_plumeHeight / 8f, _plumeHeight / 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.5f, _radius * 0.9f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(_bands) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _stemGlow.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 6f;
            shape.radius = _radius * 0.25f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            SizeOverLife(_stemGlow, 0.7f, 1.6f);
            ColourOverLife(_stemGlow, Keys(
                new[] { (new Color(1f, 0.9f, 0.6f), 0f), (Color.white, 0.4f), (new Color(0.6f, 0.3f, 0.2f), 1f) },
                new[] { (1f, 0f), (0.6f, 0.5f), (0f, 1f) }));

            // The cap: the column flattening out where it stops rising, in the comet's colours.
            _capTransform = new GameObject("Cap").transform;
            _capTransform.SetParent(at, false);
            _capTransform.position = _impact + Vector3.up * _radius;
            _cap = NewParticles("Cap smoke", _capTransform, CometArt.Blended(CometArt.Smoke, new Color(0.5f, 0.5f, 0.5f, 0.5f)), 900);
            main = _cap.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(16f, 28f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 0.4f, _radius * 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 1.1f, _radius * 2.2f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(Darken(_bands, 0.85f, new Color(0.55f, 0.5f, 0.47f), 0.25f)) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _cap.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = _radius * 0.7f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            Drag(_cap, 0.02f);
            SizeOverLife(_cap, 0.6f, 2.4f);
            Rotate(_cap, 0.1f);
            ColourOverLife(_cap, Fade(Color.white, 0.88f, 0f, 0.1f));

            _capGlow = NewParticles("Cap glow", _capTransform, CometArt.Additive(CometArt.Smoke, new Color(1f, 1f, 1f, 0.5f)), 300);
            main = _capGlow.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 0.3f, _radius * 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 1.0f, _radius * 1.8f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(_bands) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _capGlow.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = _radius * 0.5f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            SizeOverLife(_capGlow, 0.7f, 1.8f);
            ColourOverLife(_capGlow, Fade(Color.white, 0.8f, 0f, 0.2f));

            // The base surge: dust thrown out along the ground in a ring.
            _groundDust = NewParticles("Base surge", at, CometArt.Blended(CometArt.Smoke, new Color(0.5f, 0.5f, 0.5f, 0.5f)), 300);
            main = _groundDust.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(8f, 14f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 2.5f, _radius * 4.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.7f, _radius * 1.4f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.45f, 0.36f, 0.85f), Color.Lerp(new Color(0.5f, 0.42f, 0.35f), _gasColour, 0.3f) * new Color(1f, 1f, 1f, 0.75f));
            shape = _groundDust.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = _radius * 0.6f;
            shape.radiusThickness = 0f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            Burst(_groundDust, 180);
            Drag(_groundDust, 0.05f);
            SizeOverLife(_groundDust, 0.6f, 2.8f);
            Rotate(_groundDust, 0.2f);
            ColourOverLife(_groundDust, Fade(Color.white, 0.85f, 0f, 0.05f));

            // Burning pieces coming back down out of the sky, around the player too.
            _debrisRain = NewParticles("Debris rain", transform, CometArt.Additive(CometArt.Glow, new Color(1f, 1f, 1f, 0.5f)), 200);
            Vector3 middle = Vector3.Lerp(_camera.transform.position, _impact, 0.55f);
            _debrisRain.transform.position = new Vector3(middle.x, _impact.y + _plumeHeight * 1.1f, middle.z);
            _debrisRain.transform.rotation = Quaternion.identity;
            main = _debrisRain.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 8f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.05f, _radius * 0.12f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.9f, 0.7f), new Color(1f, 0.6f, 0.3f));
            main.gravityModifier = 6f;
            shape = _debrisRain.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(_distance * 1.6f, 10f, _distance * 1.6f);
            var velocity = _debrisRain.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-60f, 60f);
            velocity.y = new ParticleSystem.MinMaxCurve(-120f, -40f);
            velocity.z = new ParticleSystem.MinMaxCurve(-60f, 60f);
            ColourOverLife(_debrisRain, Keys(
                new[] { (Color.white, 0f), (orange, 0.5f), (ember, 1f) },
                new[] { (1f, 0f), (1f, 0.8f), (0f, 1f) }));
            Trails(_debrisRain, 0.22f, _radius * 0.05f);
            Collide(_debrisRain);

            // The flash: the view whites out, how much depending on whether the player was facing it.
            GameObject canvasObject = new GameObject("Flash overlay");
            canvasObject.transform.SetParent(transform, false);
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            GameObject imageObject = new GameObject("Flash");
            imageObject.transform.SetParent(canvasObject.transform, false);
            _flash = imageObject.AddComponent<Image>();
            _flash.raycastTarget = false;
            RectTransform rect = _flash.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            _flash.color = new Color(1f, 1f, 1f, 0f);
            Vector3 look = _camera.transform.forward;
            Vector3 toImpact = (_impact - _camera.transform.position).normalized;
            _flashView = 0.35f + 0.65f * Mathf.Clamp01(Vector3.Dot(look, toImpact));

            _fireball.Play();
            _ejecta.Play();
            _stem.Play();
            _stemGlow.Play();
            _cap.Play();
            _capGlow.Play();
            _groundDust.Play();
            _debrisRain.Play();
        }

        // ---------------------------------------------------------------- the clock

        private void Update()
        {
            try
            {
                Step(Time.deltaTime);
            }
            catch (Exception e)
            {
                Debug.LogError("[TR comet] stopped: " + e);
                Destroy(gameObject);
            }
        }

        private void Step(float dt)
        {
            if (_camera == null)
            {
                Destroy(gameObject);
                return;
            }
            _t += dt;
            if (!_impacted)
            {
                Approach();
                if (_t >= ApproachSeconds)
                {
                    Impact();
                }
            }
            else
            {
                Aftermath(_t - ApproachSeconds);
            }
            Vector3 eye = _camera.transform.position;
            Quaternion facing = _camera.transform.rotation;
            foreach (Glow glow in _glows)
            {
                glow.Face(eye, facing);
            }
            if (_t >= EndSeconds)
            {
                Destroy(gameObject);
            }
        }

        private void Approach()
        {
            float x = Mathf.Clamp01(_t / ApproachSeconds);
            const float k = 4.6f;
            float u = (Mathf.Exp(k * x) - 1f) / (Mathf.Exp(k) - 1f);
            _head.position = Vector3.LerpUnclamped(_start, _impact, u);
            float heat = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1f, x));

            Color cool = Color.Lerp(new Color(0.75f, 0.88f, 1f), _gasColour, 0.45f);
            Color hot = new Color(1f, 0.72f, 0.38f);
            Color now = Color.Lerp(cool, hot, heat);
            float flicker = 1f + 0.08f * Mathf.Sin(_t * 31f) * heat;
            _coma.Show(_head.position, 420f * _scale * (1f + 0.7f * heat), now, (1.1f + 1.4f * heat) * flicker, 1f);
            _core.Show(_head.position, 110f * _scale * (1f + 0.8f * heat), Color.white, 2f, 1f);

            _headLight.transform.position = _head.position;
            _headLight.color = now;
            _headLight.intensity = 1.5f + 12f * heat * heat;
            _headLight.range = 2500f + 5000f * heat;

            var emission = _sparks.emission;
            emission.rateOverTime = 260f * heat * heat;
            emission = _contrail.emission;
            emission.rateOverTime = 70f * heat;
            emission = _tail.emission;
            emission.rateOverTime = 60f + 80f * heat;

            _hotTrail.widthMultiplier = 90f * _scale * (0.5f + 1.6f * heat);
            _hotTrail.colorGradient = TrailGradient(now);

            if (!_roared && _t >= ApproachSeconds - CometArt.Roar.length)
            {
                _roared = true;
                _audio.PlayOneShot(CometArt.Roar, 0.9f);
            }
            if (heat > 0f)
            {
                CameraController.SetCameraShake(0.7f * heat * heat * heat);
            }
        }

        private void Impact()
        {
            _impacted = true;
            _head.position = _impact;
            foreach (ParticleSystem system in new[] { _tail, _sparks, _contrail })
            {
                var emission = system.emission;
                emission.rateOverTime = 0f;
            }
            _hotTrail.emitting = false;
            _coma.Hide();
            _core.Hide();
            _headLight.enabled = false;
            BuildImpact();
            _boomAt = Mathf.Min(14f, Vector3.Distance(_camera.transform.position, _impact) / SpeedOfSound);
            Debug.Log("[TR comet] impact at " + _impact + ", sound in " + _boomAt.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            Deliver();
        }

        private void Aftermath(float s)
        {
            // Flash: up in a frame or two, gone in about a second, warm as it goes.
            float flash = s < 0.05f ? s / 0.05f : Mathf.Exp(-(s - 0.05f) / 0.45f);
            _flash.color = Color.Lerp(new Color(1f, 0.85f, 0.65f), Color.white, flash) * new Color(1f, 1f, 1f, 0.97f * _flashView * flash);

            _flashLight.intensity = 45f * Mathf.Exp(-s / 0.6f) + 5f * Mathf.Exp(-s / 8f);
            _flashLight.range = 20000f;
            _flashLight.color = Color.Lerp(Color.Lerp(new Color(1f, 0.6f, 0.25f), _gasColour, 0.35f), Color.white, Mathf.Exp(-s / 0.8f));

            _flashCore.Show(_impact + Vector3.up * _radius * 0.5f, 7f * _radius * (1f - Mathf.Exp(-s / 0.2f)), Color.Lerp(new Color(1f, 0.8f, 0.5f), Color.white, Mathf.Exp(-s / 0.7f)), 2f * Mathf.Exp(-s / 1.4f), 1f);
            _fireGlow.Show(_impact + Vector3.up * _radius * (0.8f + 0.4f * Mathf.Min(s, 6f)), 4.5f * _radius * (0.6f + 0.4f * Mathf.Sqrt(Mathf.Min(s, 30f))),
                Color.Lerp(new Color(1f, 0.55f, 0.2f), _gasColour, Mathf.Clamp01(s / 8f)), 1.5f * Mathf.Exp(-s / 6f), 1f);
            if (s < 5f)
            {
                _shock.Show(_impact + Vector3.up * _radius * 0.3f, _radius * 2f + 1400f * _scale * s, new Color(1f, 0.95f, 0.85f), 1.6f, Mathf.Exp(-s / 0.9f));
            }
            else
            {
                _shock.Hide();
            }
            _groundRing.Show(_impact + Vector3.up * 3f, 2f * 16f * _radius * (1f - Mathf.Exp(-s / 2.5f)), new Color(0.62f, 0.52f, 0.42f), 1f, 0.8f * Mathf.Exp(-s / 6f));
            _skyGlow.Show(_impact + Vector3.up * _plumeHeight * 0.5f, 28f * _radius, _gasColour, 0.55f * Mathf.Exp(-s / 30f) * Mathf.Clamp01(s / 0.5f), 1f);

            float capRise = 1f - Mathf.Exp(-s / 7f);
            _capTransform.position = _impact + Vector3.up * (_radius + _plumeHeight * capRise);
            var emission = _stem.emission;
            emission.rateOverTime = s < 45f ? 55f * Mathf.Exp(-s / 12f) : 0f;
            emission = _stemGlow.emission;
            emission.rateOverTime = s < 12f ? 60f * Mathf.Exp(-s / 3f) : 0f;
            emission = _cap.emission;
            emission.rateOverTime = s > 2f && s < 50f ? 30f * Mathf.Exp(-(s - 2f) / 14f) : 0f;
            emission = _capGlow.emission;
            emission.rateOverTime = s > 1.5f && s < 14f ? 30f * Mathf.Exp(-(s - 1.5f) / 4f) : 0f;
            emission = _debrisRain.emission;
            emission.rateOverTime = s > 4f && s < 35f ? 7f * Mathf.Exp(-(s - 4f) / 12f) : 0f;

            if (!_boomed && s >= _boomAt)
            {
                _boomed = true;
                _audio.PlayOneShot(CometArt.Boom, 1f);
                Gust();
            }
            if (_boomed)
            {
                float since = s - _boomAt;
                CameraController.SetCameraShake(Mathf.Min(2.5f, 2.4f * _scale / 1.5f) * Mathf.Exp(-since / 1.6f));
            }
            if (!_rumbled && s >= _boomAt + 2f)
            {
                _rumbled = true;
                _audio.PlayOneShot(CometArt.Rumble, 0.8f);
            }
            if (s > EndSeconds - ApproachSeconds - 12f)
            {
                float fade = Mathf.Clamp01((EndSeconds - ApproachSeconds - s) / 12f);
                _skyGlow.Show(_impact + Vector3.up * _plumeHeight * 0.5f, 28f * _radius, _gasColour, 0.55f * Mathf.Exp(-s / 30f) * fade, 1f);
            }
        }

        /// <summary>The shock wave reaching the player: dust blown past, away from the impact.</summary>
        private void Gust()
        {
            Vector3 eye = _camera.transform.position;
            Vector3 away = Vector3.ProjectOnPlane(eye - _impact, Vector3.up).normalized;
            ParticleSystem gust = NewParticles("Gust", transform, CometArt.Blended(CometArt.Smoke, new Color(0.5f, 0.5f, 0.5f, 0.5f)), 120);
            gust.transform.position = eye - away * 45f;
            var main = gust.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.5f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(5f, 16f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.6f, 0.5f, 0.42f, 0.55f), new Color(0.5f, 0.42f, 0.36f, 0.35f));
            var shape = gust.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 35f;
            var velocity = gust.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            Vector3 v = away * 70f;
            velocity.x = new ParticleSystem.MinMaxCurve(v.x - 8f, v.x + 8f);
            velocity.y = new ParticleSystem.MinMaxCurve(-2f, 6f);
            velocity.z = new ParticleSystem.MinMaxCurve(v.z - 8f, v.z + 8f);
            SizeOverLife(gust, 0.7f, 2.2f);
            ColourOverLife(gust, Fade(Color.white, 1f, 0f, 0.1f));
            Burst(gust, 70);
            gust.Play();
        }

        /// <summary>The gas reaching the planet, the moment the comet lands.</summary>
        private void Deliver()
        {
            if (_plan.Preview)
            {
                Say("Comet landed (preview: nothing reached the planet).");
                return;
            }
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tank == null || WorldSetting.Current?.Data?.GlobalAtmosphereData == null)
            {
                Say("Comet landed, but there is no planet loaded to take its gas.");
                return;
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder text = new StringBuilder();
            double beforeMol = 0.0;
            double beforeKPa = 0.0;
            double afterMol = 0.0;
            double afterKPa = 0.0;
            Planet.UnderTankLock(() =>
            {
                beforeMol = tank.TotalQuantityGas().ToDouble();
                beforeKPa = Planet.PressureKPa(tank);
                for (int i = 0; i < _plan.Types.Length; i++)
                {
                    Planet.AddGas(tank, _plan.Types[i], _plan.Moles * _plan.Shares[i], _plan.Kelvin);
                }
                afterMol = tank.TotalQuantityGas().ToDouble();
                afterKPa = Planet.PressureKPa(tank);
            });
            text.Append(string.Format(c, "Comet landed: {0:N0} mol delivered (", _plan.Moles));
            for (int i = 0; i < _plan.Types.Length; i++)
            {
                text.Append(string.Format(c, "{0}{1} {2:N0}", i > 0 ? ", " : "", _plan.Types[i], _plan.Moles * _plan.Shares[i]));
            }
            text.Append(string.Format(c, "). Planet gas {0:N0} -> {1:N0} mol, {2:0.###} -> {3:0.###} kPa.", beforeMol, afterMol, beforeKPa, afterKPa));
            if (Effective.MaxPressureKPa.HasValue && afterKPa > Effective.MaxPressureKPa.Value)
            {
                text.Append(string.Format(c, " This world's pressure ceiling is {0:0.###} kPa, so the next planet tick scales the planet back down to it.", Effective.MaxPressureKPa.Value));
            }
            Say(text.ToString());
        }

        private static void Say(string text)
        {
            Debug.Log("[TR comet] " + text);
            try
            {
                ConsoleWindow.Print(text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TR comet] console print failed: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (_camera != null && _originalFar > 0f)
            {
                _camera.farClipPlane = _originalFar;
            }
            if (Running == this)
            {
                Running = null;
            }
        }

        // ---------------------------------------------------------------- building blocks

        private Glow NewGlow(string name, Texture texture, bool additive)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = CometArt.Quad;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Material material = additive ? CometArt.Additive(texture, Color.clear) : CometArt.Blended(texture, Color.clear);
            renderer.sharedMaterial = material;
            Glow glow = new Glow(go.transform, material, renderer);
            glow.Hide();
            _glows.Add(glow);
            return glow;
        }

        private Light NewLight(string name, Transform parent)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.intensity = 0f;
            light.range = 1000f;
            return light;
        }

        private static ParticleSystem NewParticles(string name, Transform parent, Material material, int max)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            ParticleSystem system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = system.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 60f;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var emission = system.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            var shape = system.shape;
            shape.enabled = false;
            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.maxParticleSize = 4f;
            renderer.minParticleSize = 0f;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return system;
        }

        private static void Burst(ParticleSystem system, int count)
        {
            var emission = system.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
        }

        private static void Drag(ParticleSystem system, float dampen)
        {
            var limit = system.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = new ParticleSystem.MinMaxCurve(0f);
            limit.dampen = dampen;
        }

        private static void Rotate(ParticleSystem system, float radiansPerSecond)
        {
            var rotation = system.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-radiansPerSecond, radiansPerSecond);
        }

        private static void SizeOverLife(ParticleSystem system, float from, float to)
        {
            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, from, 0f, (to - from) * 2f), new Keyframe(1f, to, 0f, 0f)));
        }

        private static void ColourOverLife(ParticleSystem system, Gradient gradient)
        {
            var colour = system.colorOverLifetime;
            colour.enabled = true;
            colour.color = new ParticleSystem.MinMaxGradient(gradient);
        }

        private static void Trails(ParticleSystem system, float lifetime, float minVertexDistance)
        {
            var trails = system.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.PerParticle;
            trails.ratio = 1f;
            trails.lifetime = new ParticleSystem.MinMaxCurve(lifetime);
            trails.minVertexDistance = Mathf.Max(2f, minVertexDistance);
            trails.worldSpace = true;
            trails.dieWithParticles = true;
            trails.inheritParticleColor = true;
            trails.sizeAffectsWidth = true;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.8f, 1f, 0f));
            trails.colorOverTrail = new ParticleSystem.MinMaxGradient(Fade(Color.white, 1f, 0f));
            ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
            renderer.trailMaterial = CometArt.Additive(CometArt.Streak, new Color(1f, 1f, 1f, 0.5f));
        }

        private static void Collide(ParticleSystem system)
        {
            var collision = system.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.Medium;
            collision.lifetimeLoss = 1f;
            collision.bounce = 0f;
            collision.sendCollisionMessages = false;
        }

        private static Gradient Fade(Color colour, float alphaFrom, float alphaTo, float fadeInUntil = 0f)
        {
            Gradient gradient = new Gradient();
            GradientAlphaKey[] alpha = fadeInUntil > 0f
                ? new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alphaFrom, fadeInUntil), new GradientAlphaKey(alphaTo, 1f) }
                : new[] { new GradientAlphaKey(alphaFrom, 0f), new GradientAlphaKey(alphaTo, 1f) };
            gradient.SetKeys(new[] { new GradientColorKey(colour, 0f), new GradientColorKey(colour, 1f) }, alpha);
            return gradient;
        }

        private static Gradient Keys((Color colour, float time)[] colours, (float alpha, float time)[] alphas)
        {
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                Array.ConvertAll(colours, k => new GradientColorKey(k.colour, k.time)),
                Array.ConvertAll(alphas, k => new GradientAlphaKey(k.alpha, k.time)));
            return gradient;
        }

        /// <summary>The comet's colour bands, darkened toward smoke: <paramref name="keep"/> of the gas colour over <paramref name="smoke"/>.</summary>
        private static Gradient Darken(Gradient bands, float keep, Color smoke, float alpha)
        {
            GradientColorKey[] keys = bands.colorKeys;
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i].color = Color.Lerp(smoke, keys[i].color * 0.9f, keep);
            }
            Gradient gradient = new Gradient { mode = GradientMode.Fixed };
            gradient.SetKeys(keys, new[] { new GradientAlphaKey(alpha, 0f), new GradientAlphaKey(alpha, 1f) });
            return gradient;
        }

        /// <summary>A textured square facing the camera (or lying flat), sized in metres.</summary>
        private sealed class Glow
        {
            private readonly Transform _transform;
            private readonly Material _material;
            private readonly Renderer _renderer;
            private readonly int _tint;
            public bool Flat;

            public Glow(Transform transform, Material material, Renderer renderer)
            {
                _transform = transform;
                _material = material;
                _renderer = renderer;
                _tint = material.HasProperty("_TintColor") ? Shader.PropertyToID("_TintColor") : Shader.PropertyToID("_Color");
            }

            /// <summary><paramref name="gain"/> 1 is the colour as given; the shaders reach 2, which blooms.</summary>
            public void Show(Vector3 position, float size, Color colour, float gain, float alpha)
            {
                _renderer.enabled = size > 0f && alpha > 0.002f && gain > 0.002f;
                _transform.position = position;
                _transform.localScale = Vector3.one * Mathf.Max(0.01f, size);
                _material.SetColor(_tint, new Color(colour.r * gain * 0.5f, colour.g * gain * 0.5f, colour.b * gain * 0.5f, Mathf.Clamp01(alpha) * 0.5f));
            }

            public void Hide() => _renderer.enabled = false;

            public void Face(Vector3 eye, Quaternion facing)
            {
                _transform.rotation = Flat ? Quaternion.Euler(90f, 0f, 0f) : facing;
            }
        }
    }
}
