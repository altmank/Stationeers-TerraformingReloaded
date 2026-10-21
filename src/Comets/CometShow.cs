using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using TerraformingReloaded.Patching;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// One comet event played on this machine, from first sight to the dust settling, against the
    /// clock every machine shares (docs/COMET.md). Every machine plays the same event from the same
    /// numbers: what it looks and sounds like, and the push on its own player. Only the host puts the
    /// gas in, breaks structures and damages suits. A dedicated server plays the timeline without
    /// drawing or sounding anything.
    ///
    /// The sequence, with t in seconds from first sight:
    ///   0 to 15    a glowing head and tail high in the sky, slow at first, then falling faster and
    ///              faster, heating from blue-white to orange, shedding sparks and smoke, its light
    ///              sweeping the ground, the air rush and a roar building under it, the sonic boom as
    ///              it comes through the sky.
    ///   15         impact, silent: a flash, a fireball, a shock front, debris thrown into the sky, a
    ///              column rising into a cap in the colours of what the comet was made of, and a layer
    ///              for each reaction its material makes. The gas goes in.
    ///   15 + d/340 the sound and the wave reach a point d metres away: the thump through the ground,
    ///              the boom, a gust, glass breaking at the structures it reaches, a push on a player
    ///              outdoors.
    ///   after      debris bursting in the sky, the cloud spreading and fading, a long rumble, a
    ///              storm passing. Gone at about two minutes; the darker sky lasts five.
    /// A player who joins in the middle picks it up at the time it has reached and skips what has
    /// already happened.
    /// </summary>
    internal sealed class CometShow : MonoBehaviour
    {
        public static CometShow Running { get; private set; }

        private const float ApproachSeconds = (float)CometEvent.ApproachSeconds;
        private const float EndSeconds = (float)CometEvent.EndSeconds;

        /// <summary>No more particles than this across the whole show, whatever its size and reactions.</summary>
        private const int MaxParticles = 6000;

        private CometEvent _event;
        private CometPlan _plan;
        private CometRules _rules;
        private CometReactions.Reactions _react;
        private bool _render;
        private bool _host;
        private Camera _camera;
        private float _originalFar;
        private DepthTextureMode _originalDepth;
        private float _t;
        private float _joinedAt;
        private float _lastTarget = float.NaN;
        private bool _impacted;
        private bool _late;
        private bool _roared;
        private bool _entered;
        private float _flashView;
        private System.Random _random;
        private CometSound _sound;
        private float _day;

        /// <summary>
        /// Everything that stands on the ground or flies down to it, placed at real positions and
        /// moved as one to where this camera sees the ground (<see cref="FollowDrawnGround"/>).
        /// </summary>
        private Transform _drawn;

        /// <summary>How far below its real height the ground at the impact is drawn for this camera, this frame.</summary>
        private float _drop;

        private Vector3 _start;
        private Vector3 _impact;
        private Vector3 _travel;
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
        private Glow _steamDome;
        private ParticleSystem _fireball;
        private ParticleSystem _secondFireball;
        private ParticleSystem _ejecta;
        private ParticleSystem _stem;
        private ParticleSystem _stemGlow;
        private ParticleSystem _cap;
        private ParticleSystem _capGlow;
        private ParticleSystem _groundDust;
        private ParticleSystem _debrisRain;
        private ParticleSystem _fog;
        private ParticleSystem _haze;
        private ParticleSystem _shimmer;
        private Transform _capTransform;
        private Image _flash;

        private readonly List<Glow> _glows = new List<Glow>();
        private readonly List<ParticleSystem> _systems = new List<ParticleSystem>();
        private readonly List<(Material Material, Color Tint, bool Glow)> _materials = new List<(Material, Color, bool)>();
        private readonly List<Cue> _cues = new List<Cue>();
        private int _nextCue;

        private readonly FrameTimes _approachFrames = new FrameTimes();
        private readonly FrameTimes _impactFrames = new FrameTimes();
        private readonly FrameTimes _afterFrames = new FrameTimes();

        /// <summary>Something that happens a set time after the impact.</summary>
        private readonly struct Cue
        {
            public readonly float At;
            public readonly Action Act;

            public Cue(float at, Action act)
            {
                At = at;
                Act = act;
            }
        }

        /// <summary>Starts this machine's show of an event, at the time it has reached.</summary>
        public static CometShow Play(CometEvent comet)
        {
            GameObject root = new GameObject("TR Comet");
            CometShow show = root.AddComponent<CometShow>();
            try
            {
                show.Build(comet);
            }
            catch
            {
                Destroy(root);
                throw;
            }
            Running = show;
            return show;
        }

        private void Build(CometEvent comet)
        {
            _event = comet;
            _plan = comet.Plan;
            _rules = CometRules.Shipped;
            _react = comet.Reactions;
            _host = !NetworkManager.IsClient;
            _random = new System.Random(comet.Seed);
            _scale = _plan.Scale;
            _radius = 120f * _scale;
            _plumeHeight = 1100f * _scale;
            _gasColour = CometPalette.Blend(_plan.Types, _plan.Shares);
            _bands = CometPalette.Bands(_plan.Types, _plan.Shares);
            _impact = comet.Impact;
            _start = comet.Start;
            _travel = comet.Travel;
            _t = (float)(CometEvent.Clock - comet.StartClock);
            _joinedAt = _t;

            _camera = CameraController.CurrentCamera ? CameraController.CurrentCamera : Camera.main;
            _render = _camera != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            if (!_render)
            {
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "[TR comet] event {0}: nothing to draw on this machine; the timeline runs without a show", comet.Sequence));
                return;
            }

            try
            {
                BuildView();
            }
            catch (Exception e)
            {
                Debug.LogError("[TR comet] the show could not be drawn here; the impact still happens. " + e);
                StopDrawing();
            }
        }

        private void BuildView()
        {
            CometEvent comet = _event;
            Vector3 eye = _camera.transform.position;
            _originalFar = _camera.farClipPlane;
            _camera.farClipPlane = Mathf.Max(_originalFar, Vector3.Distance(eye, _start) * 1.3f, Vector3.Distance(eye, _impact) * 4f);
            _originalDepth = _camera.depthTextureMode;
            _camera.depthTextureMode |= DepthTextureMode.Depth;

            // Made now rather than when first heard or seen, so nothing hitches the frame it first plays in.
            _ = CometArt.Roar;
            _ = CometArt.Boom;
            _ = CometArt.Rumble;
            _ = CometArt.Thump;
            _ = CometArt.SmokeSheet;
            _sound = new CometSound(gameObject, _scale);
            _drawn = new GameObject("Drawn").transform;
            _drawn.SetParent(transform, false);
            FollowDrawnGround();
            if (_t < ApproachSeconds)
            {
                BuildHead();
            }
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[TR comet] show of event {0}: {1:N0} mol, scale {2:0.00}, impact {3} at {4:N0} m, drawn {5:0.0} m lower for this camera, start {6}, joined at t={7:0.0} s, far clip {8:N0} -> {9:N0}, shaders {10}, sound {11}",
                comet.Sequence, _plan.Moles, _scale, _impact, Vector3.Distance(eye, _impact), _drop, _start, _joinedAt, _originalFar, _camera.farClipPlane, CometArt.ShaderReport, _sound.Report));
        }

        // ---------------------------------------------------------------- the head

        private void BuildHead()
        {
            _head = new GameObject("Head").transform;
            _head.SetParent(_drawn, false);
            _head.localPosition = _start;
            _head.rotation = Quaternion.LookRotation(_travel);

            _coma = NewGlow("Coma", CometArt.Glow, additive: true, CometArt.OuterQueue);
            _core = NewGlow("Core", CometArt.Glow, additive: true, CometArt.OuterQueue);

            _headLight = NewLight("Head light", _head);
            _headLight.color = new Color(0.8f, 0.9f, 1f);

            // The tail: shed backwards along the path, so it streams behind the head even while the
            // head is still slow and far away.
            _tail = NewParticles("Tail", _head, Additive(CometArt.Glow, CometArt.OuterQueue), 500);
            var main = _tail.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(140f * _scale, 260f * _scale);
            main.startColor = new ParticleSystem.MinMaxGradient(_bands) { mode = ParticleSystemGradientMode.RandomColor };
            var velocity = _tail.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            Vector3 back = -_travel * 260f;
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
            _sparks = NewParticles("Sparks", _head, Additive(CometArt.Glow, CometArt.OuterQueue), 600);
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

            // The smoke it leaves in the sky, which hangs there long after it lands. Opaque enough to
            // read against a bright sky.
            _contrail = NewParticles("Contrail", _head, Blended(CometArt.SmokeSheet), 700);
            main = _contrail.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(35f, 55f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(60f * _scale, 110f * _scale);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.42f, 0.38f, 0.36f, 0.85f), Color.Lerp(new Color(0.45f, 0.42f, 0.4f), _gasColour, 0.25f) * new Color(1f, 1f, 1f, 0.7f));
            shape = _contrail.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 25f * _scale;
            SizeOverLife(_contrail, 1f, 4.5f);
            ColourOverLife(_contrail, Fade(Color.white, 1f, 0f, 0.15f));
            Animate(_contrail, 1f);
            emission = _contrail.emission;
            emission.rateOverTime = 0f;
            _contrail.Play();

            _hotTrail = _head.gameObject.AddComponent<TrailRenderer>();
            _hotTrail.sharedMaterial = Additive(CometArt.Streak, CometArt.OuterQueue);
            _hotTrail.time = 2.5f;
            _hotTrail.minVertexDistance = 20f;
            _hotTrail.widthMultiplier = 90f * _scale;
            _hotTrail.widthCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.15f);
            _hotTrail.alignment = LineAlignment.View;
            _hotTrail.shadowCastingMode = ShadowCastingMode.Off;
            _hotTrail.receiveShadows = false;
            _hotTrail.colorGradient = TrailGradient(new Color(0.8f, 0.9f, 1f));
            _hotTrail.emitting = true;
            Budget();
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
            at.SetParent(_drawn, false);
            at.localPosition = _impact;

            _flashLight = NewLight("Flash", at);
            _flashLight.transform.localPosition = Vector3.up * _radius;

            _flashCore = NewGlow("Flash core", CometArt.Glow, additive: true, CometArt.OuterQueue);
            _fireGlow = NewGlow("Fire glow", CometArt.Glow, additive: true, CometArt.InnerQueue);
            _shock = NewGlow("Shock front", CometArt.Ring, additive: true, CometArt.OuterQueue);
            _skyGlow = NewGlow("Sky glow", CometArt.Glow, additive: true, CometArt.InnerQueue);
            _groundRing = NewGlow("Ground ring", CometArt.Ring, additive: false, CometArt.SmokeQueue);
            _groundRing.Flat = true;
            _steamDome = NewGlow("Condensation dome", CometArt.Glow, additive: false, CometArt.OuterQueue);

            Color hotWhite = new Color(1f, 0.97f, 0.9f);
            Color orange = new Color(1f, 0.55f, 0.18f);
            Color ember = new Color(0.55f, 0.12f, 0.05f);
            float fire = 1f - 0.6f * (float)_react.Steam;
            bool burst = !_late;

            // Fireball: a burst of hot gas rolling outward and cooling through the comet's colours,
            // inside the smoke, so the smoke covers it rather than it being pasted over the smoke.
            _fireball = NewParticles("Fireball", at, Additive(CometArt.SmokeSheet, CometArt.InnerQueue), 160);
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
            if (burst)
            {
                Burst(_fireball, Mathf.RoundToInt(110 * fire), 0f);
            }
            Drag(_fireball, 0.06f);
            SizeOverLife(_fireball, 0.7f, 2.0f);
            Rotate(_fireball, 0.4f);
            Animate(_fireball, 1f);
            ColourOverLife(_fireball, Keys(
                new[] { (hotWhite, 0f), (new Color(1f, 0.85f, 0.4f), 0.12f), (orange, 0.3f), (_gasColour, 0.55f), (ember, 1f) },
                new[] { (1f, 0f), (0.9f, 0.4f), (0f, 1f) }));

            // Burning: a second, larger fireball a second or two after the first, rolling orange-red.
            if (_react.Burning > 0.02)
            {
                float burn = (float)_react.Burning;
                _secondFireball = NewParticles("Second fireball", at, Additive(CometArt.SmokeSheet, CometArt.InnerQueue), 180);
                main = _secondFireball.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 9f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 0.8f, _radius * 2.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(_radius * 1.2f, _radius * 2.4f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = Color.white;
                shape = _secondFireball.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                shape.radius = _radius * 0.5f;
                if (burst)
                {
                    Burst(_secondFireball, Mathf.RoundToInt(40 + 120 * burn), 1.4f);
                }
                Drag(_secondFireball, 0.05f);
                SizeOverLife(_secondFireball, 0.6f, 2.4f);
                Rotate(_secondFireball, 0.3f);
                Animate(_secondFireball, 1f);
                ColourOverLife(_secondFireball, Keys(
                    new[] { (new Color(1f, 0.9f, 0.6f), 0f), (new Color(1f, 0.5f, 0.15f), 0.2f), (new Color(0.95f, 0.25f, 0.08f), 0.55f), (ember, 1f) },
                    new[] { (0.95f, 0f), (0.85f, 0.5f), (0f, 1f) }));
            }

            // Ejecta: rock and ice thrown high, glowing, on trails, falling back down.
            _ejecta = NewParticles("Ejecta", at, Additive(CometArt.Glow, CometArt.OuterQueue), 220);
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
            if (burst)
            {
                Burst(_ejecta, 140, 0f);
            }
            ColourOverLife(_ejecta, Keys(
                new[] { (Color.white, 0f), (orange, 0.35f), (_gasColour, 0.7f), (ember, 1f) },
                new[] { (1f, 0f), (1f, 0.75f), (0f, 1f) }));
            Trails(_ejecta, 0.35f, _radius * 0.12f);
            Collide(_ejecta);

            // The column: smoke and dust climbing, lit from inside by the fire at its root. Steam turns
            // it white and billowing.
            float steam = (float)_react.Steam;
            _stem = NewParticles("Stem", at, Blended(CometArt.SmokeSheet), 800);
            main = _stem.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(11f, 17f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_plumeHeight / 9f, _plumeHeight / 6f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.6f, _radius * (1.0f + 0.3f * steam));
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(Darken(_bands, 0.45f * (1f - steam), Color.Lerp(new Color(0.35f, 0.3f, 0.27f), new Color(0.92f, 0.94f, 0.96f), steam), 0.75f)) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _stem.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 7f + 5f * steam;
            shape.radius = _radius * 0.35f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            Drag(_stem, 0.025f);
            SizeOverLife(_stem, 0.6f, 2.2f + 0.8f * steam);
            Rotate(_stem, 0.15f);
            Animate(_stem, 1f);
            ColourOverLife(_stem, Fade(Color.white, 0.95f, 0f, 0.12f));

            _stemGlow = NewParticles("Stem glow", at, Additive(CometArt.SmokeSheet, CometArt.InnerQueue), 300);
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
            Animate(_stemGlow, 1f);
            ColourOverLife(_stemGlow, Keys(
                new[] { (new Color(1f, 0.9f, 0.6f), 0f), (Color.white, 0.4f), (new Color(0.6f, 0.3f, 0.2f), 1f) },
                new[] { (1f, 0f), (0.6f, 0.5f), (0f, 1f) }));

            // The cap: the column flattening out where it stops rising, in the comet's colours.
            _capTransform = new GameObject("Cap").transform;
            _capTransform.SetParent(at, false);
            _capTransform.localPosition = Vector3.up * _radius;
            _cap = NewParticles("Cap smoke", _capTransform, Blended(CometArt.SmokeSheet), 800);
            main = _cap.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(16f, 28f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 0.4f, _radius * 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 1.1f, _radius * 2.2f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(Darken(_bands, 0.85f * (1f - 0.5f * steam), Color.Lerp(new Color(0.55f, 0.5f, 0.47f), new Color(0.95f, 0.96f, 0.98f), steam), 0.4f)) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _cap.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = _radius * 0.7f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            Drag(_cap, 0.02f);
            SizeOverLife(_cap, 0.6f, 2.4f);
            Rotate(_cap, 0.1f);
            Animate(_cap, 1f);
            ColourOverLife(_cap, Fade(Color.white, 0.9f, 0f, 0.1f));

            _capGlow = NewParticles("Cap glow", _capTransform, Additive(CometArt.SmokeSheet, CometArt.InnerQueue), 240);
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
            Animate(_capGlow, 1f);
            ColourOverLife(_capGlow, Fade(Color.white, 0.8f, 0f, 0.2f));

            // The base surge: dust thrown out along the ground in a ring.
            _groundDust = NewParticles("Base surge", at, Blended(CometArt.SmokeSheet), 260);
            main = _groundDust.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(8f, 14f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 2.5f, _radius * 4.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.7f, _radius * 1.4f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.45f, 0.36f, 0.9f), Color.Lerp(new Color(0.5f, 0.42f, 0.35f), _gasColour, 0.3f) * new Color(1f, 1f, 1f, 0.8f));
            shape = _groundDust.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = _radius * 0.6f;
            shape.radiusThickness = 0f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            if (burst)
            {
                Burst(_groundDust, 180, 0f);
            }
            Drag(_groundDust, 0.05f);
            SizeOverLife(_groundDust, 0.6f, 2.8f);
            Rotate(_groundDust, 0.2f);
            Animate(_groundDust, 1f);
            ColourOverLife(_groundDust, Fade(Color.white, 0.88f, 0f, 0.05f));

            // Dry ice: a dense grey-white fog rolling out along the ground.
            if (_react.DryIce > 0.02)
            {
                _fog = NewParticles("Dry ice fog", at, Blended(CometArt.SmokeSheet), 360);
                main = _fog.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(22f, 38f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 0.6f, _radius * 1.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.8f, _radius * 1.6f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.86f, 0.88f, 0.9f, 0.85f), new Color(0.7f, 0.72f, 0.75f, 0.7f));
                main.gravityModifier = 0.02f;
                shape = _fog.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = _radius * 0.8f;
                shape.radiusThickness = 0.3f;
                shape.rotation = new Vector3(90f, 0f, 0f);
                Drag(_fog, 0.03f);
                SizeOverLife(_fog, 0.7f, 3.2f);
                Rotate(_fog, 0.08f);
                Animate(_fog, 0.6f);
                ColourOverLife(_fog, Fade(Color.white, 0.9f, 0f, 0.15f));
            }

            // Toxic: a sickly yellow-green haze that settles low and lingers.
            if (_react.Toxic > 0.02)
            {
                _haze = NewParticles("Toxic haze", at, Blended(CometArt.SmokeSheet), 300);
                main = _haze.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(40f, 70f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(_radius * 0.2f, _radius * 0.7f);
                main.startSize = new ParticleSystem.MinMaxCurve(_radius * 1.2f, _radius * 2.4f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.72f, 0.8f, 0.22f, 0.6f), new Color(0.55f, 0.66f, 0.18f, 0.45f));
                main.gravityModifier = 0.01f;
                shape = _haze.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = _radius * 1.8f;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                Drag(_haze, 0.04f);
                SizeOverLife(_haze, 0.8f, 2.6f);
                Rotate(_haze, 0.05f);
                Animate(_haze, 0.5f);
                ColourOverLife(_haze, Fade(Color.white, 0.85f, 0f, 0.25f));
            }

            // Ionised: an electric blue-violet shimmer flickering through the cloud.
            if (_react.Ionised > 0.02)
            {
                _shimmer = NewParticles("Shimmer", _capTransform, Additive(CometArt.Glow, CometArt.InnerQueue), 300);
                main = _shimmer.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.5f);
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(_radius * 0.3f, _radius * 0.9f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.6f, 1f), new Color(0.75f, 0.45f, 1f));
                shape = _shimmer.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = _radius * 1.4f;
                ColourOverLife(_shimmer, Keys(
                    new[] { (Color.white, 0f), (new Color(0.6f, 0.6f, 1f), 0.3f), (new Color(0.5f, 0.3f, 1f), 1f) },
                    new[] { (0f, 0f), (1f, 0.1f), (0f, 1f) }));
            }

            // Burning pieces coming back down out of the sky, around the player too. Left out of the
            // drawn frame: it falls all the way from the player to the impact, and is seen landing
            // near the player, where the ground is drawn where it is.
            _debrisRain = NewParticles("Debris rain", transform, Additive(CometArt.Glow, CometArt.OuterQueue), 180);
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
            float spread = Vector3.Distance(_camera.transform.position, _impact) * 1.6f;
            shape.scale = new Vector3(spread, 10f, spread);
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
            Vector3 toImpact = (_drawn.TransformPoint(_impact) - _camera.transform.position).normalized;
            _flashView = _late ? 0f : 0.35f + 0.65f * Mathf.Clamp01(Vector3.Dot(look, toImpact));

            Budget();
            foreach (ParticleSystem system in new[] { _fireball, _secondFireball, _ejecta, _stem, _stemGlow, _cap, _capGlow, _groundDust, _fog, _haze, _shimmer, _debrisRain })
            {
                if (system != null)
                {
                    system.Play();
                }
            }
        }

        // ---------------------------------------------------------------- the clock

        /// <summary>
        /// The terrain shader draws ground lower the further it is from the camera (the world's
        /// curvature), so a show placed at real heights floats above the distant ground it lands on.
        /// The drawn frame is moved by the drop at the impact for this camera, and recentred with the
        /// terrain in the orbital view, so the impact sits on the ground as drawn and the approach
        /// still ends there. The path moves as one and stays straight: the game curves nothing in the
        /// sky. Per viewer and per frame, so it is never sent.
        /// </summary>
        private void FollowDrawnGround()
        {
            Vector3 drawn = CometTerrain.Drawn(_impact, _camera.transform.position, out _drop);
            _drawn.localPosition = drawn - _impact;
        }

        private void Update()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Debug.LogError("[TR comet] stopped: " + e);
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// Drops everything drawn and heard, keeping the timeline: the host's gas, breaks and suit
        /// damage happen whether or not this machine can show them.
        /// </summary>
        private void StopDrawing()
        {
            _render = false;
            _sound?.Stop();
            foreach (Transform child in transform)
            {
                Destroy(child.gameObject);
            }
        }

        private void Drawn(Action draw)
        {
            if (!_render)
            {
                return;
            }
            try
            {
                draw();
            }
            catch (Exception e)
            {
                Debug.LogError("[TR comet] drawing stopped; the impact still happens. " + e);
                StopDrawing();
            }
        }

        private void Step()
        {
            if (_render && _camera == null)
            {
                StopDrawing();
            }
            // The clock every machine shares, followed smoothly: a client's copy is corrected by every
            // state update from the host, and a jump in it would jerk the comet across the sky.
            float dt = Time.deltaTime;
            // The clock stands still while the game is paused, and so does the show.
            float target = (float)(CometEvent.Clock - _event.StartClock);
            if (target != _lastTarget)
            {
                _t += dt;
            }
            _lastTarget = target;
            float behind = target - _t;
            _t = Mathf.Abs(behind) > 1f ? target : _t + behind * Mathf.Clamp01(dt * 2f);
            if (_t < 0f)
            {
                return;
            }
            Drawn(FollowDrawnGround);
            float frame = Time.unscaledDeltaTime;
            if (!_impacted)
            {
                if (_t < ApproachSeconds)
                {
                    if (_head != null)
                    {
                        Drawn(Approach);
                    }
                    _approachFrames.Add(frame);
                }
                else
                {
                    Impact();
                }
            }
            else
            {
                float s = _t - ApproachSeconds;
                Drawn(() => Aftermath(s));
                (s < 10f ? _impactFrames : _afterFrames).Add(frame);
                while (_nextCue < _cues.Count && _cues[_nextCue].At <= s)
                {
                    Cue cue = _cues[_nextCue++];
                    try
                    {
                        cue.Act();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[TR comet] a moment of the impact failed and was skipped: " + e.Message);
                    }
                }
            }
            Drawn(() =>
            {
                Vector3 eye = _camera.transform.position;
                Quaternion facing = _camera.transform.rotation;
                foreach (Glow glow in _glows)
                {
                    glow.Face(eye, facing);
                }
            });
            if (_t >= EndSeconds)
            {
                Destroy(gameObject);
            }
        }

        /// <summary>0 at night to 1 in full day: glow reads at night, smoke by day.</summary>
        private void Daylight()
        {
            _day = Mathf.Clamp01((OrbitalSimulation.WorldSunVector.y + 0.05f) / 0.3f);
            float glow = Mathf.Lerp(1f, 0.55f, _day);
            float smoke = Mathf.Lerp(0.5f, 0.64f, _day);
            foreach ((Material material, Color tint, bool isGlow) in _materials)
            {
                Color c = isGlow ? new Color(tint.r * glow, tint.g * glow, tint.b * glow, tint.a) : new Color(tint.r, tint.g, tint.b, smoke);
                if (material.HasProperty("_TintColor"))
                {
                    material.SetColor("_TintColor", c);
                }
                else
                {
                    material.color = c;
                }
            }
        }

        private void Approach()
        {
            Daylight();
            float x = Mathf.Clamp01(_t / ApproachSeconds);
            const float k = 4.6f;
            float u = (Mathf.Exp(k * x) - 1f) / (Mathf.Exp(k) - 1f);
            Vector3 path = Vector3.LerpUnclamped(_start, _impact, u);
            _head.localPosition = path;
            float heat = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1f, x));
            float glowGain = Mathf.Lerp(1f, 0.7f, _day);

            Color cool = Color.Lerp(new Color(0.75f, 0.88f, 1f), _gasColour, 0.45f);
            Color hot = new Color(1f, 0.72f, 0.38f);
            Color now = Color.Lerp(cool, hot, heat);
            float flicker = 1f + 0.08f * Mathf.Sin(_t * 31f) * heat;
            _coma.Show(path, 420f * _scale * (1f + 0.7f * heat), now, (1.1f + 1.4f * heat) * flicker * glowGain, 1f);
            _core.Show(path, 110f * _scale * (1f + 0.8f * heat), Color.white, 2f, 1f);

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

            Vector3 listener = _camera.transform.position;
            if (_t > ApproachSeconds * 0.35f)
            {
                _sound.Rush(listener, _head.position, heat);
            }
            if (!_roared && _t >= ApproachSeconds - CometArt.Roar.length)
            {
                _roared = true;
                if (_joinedAt < ApproachSeconds - CometArt.Roar.length + 1f)
                {
                    _sound.Roar();
                }
            }
            if (!_entered && _t >= ApproachSeconds * 0.6f)
            {
                _entered = true;
                if (_joinedAt < ApproachSeconds * 0.6f + 1f)
                {
                    _sound.Entry(listener, _head.position);
                }
            }
            if (heat > 0f)
            {
                CameraController.SetCameraShake(0.7f * heat * heat * heat);
            }
        }

        private void Impact()
        {
            _impacted = true;
            _late = _joinedAt > ApproachSeconds + 0.5f;
            // The gas first: nothing drawn or heard may stand between the comet and the planet.
            if (_host && !_late)
            {
                try
                {
                    Deliver();
                }
                catch (Exception e)
                {
                    Debug.LogError("[TR comet] the comet's gas could not be added: " + e);
                }
            }
            Drawn(() =>
            {
                if (_head != null)
                {
                    foreach (ParticleSystem system in new[] { _tail, _sparks, _contrail })
                    {
                        var emission = system.emission;
                        emission.rateOverTime = 0f;
                    }
                    _hotTrail.emitting = false;
                    _coma.Hide();
                    _core.Hide();
                    _headLight.enabled = false;
                }
                _sound.EndRush();
                BuildImpact();
            });
            Schedule();
        }

        /// <summary>
        /// Everything that happens as the sound and the wave spread out, each at its own time after
        /// the impact. A player who joined after a moment has passed skips it.
        /// </summary>
        private void Schedule()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            float joined = _joinedAt - ApproachSeconds;
            double moles = _plan.Moles;
            StringBuilder log = new StringBuilder();
            log.Append(string.Format(c, "[TR comet] event {0} impact at {1}", _event.Sequence, _impact));

            // Structures: every machine works out the same list for the sound; the host breaks them.
            if (!_plan.Preview)
            {
                List<CometWave.Hit> hits = CometWave.Structures(_impact, moles, _rules);
                int breaks = 0;
                foreach (CometWave.Hit hit in hits)
                {
                    CometWave.Hit h = hit;
                    if (h.Breaks)
                    {
                        breaks++;
                    }
                    When((float)_rules.ArrivalSeconds(h.Distance), () =>
                    {
                        if (h.Breaks && _host)
                        {
                            CometWave.Break(h.Structure);
                        }
                        if (_render && h.Sound != 0)
                        {
                            _sound.Structure(h.Sound, h.At);
                        }
                    });
                }
                log.Append(string.Format(c, "; structures: {0} break, {1} creak", breaks, hits.Count - breaks));
                if (hits.Count > 0)
                {
                    log.Append(string.Format(c, ", nearest {0} at {1:N0} m ({2:0.#} kPa against {3:0} kPa)",
                        hits[0].Structure != null ? hits[0].Structure.PrefabName : "?", hits[0].Distance, hits[0].PeakKPa, hits[0].LimitKPa));
                }

                // Players outdoors: the host damages suits through the game's collision hit.
                if (_host)
                {
                    foreach (Human human in Human.AllHumans)
                    {
                        if (human == null || human.OrganBrain == null || !human.OrganBrain.IsOnline || human.IsDead)
                        {
                            continue;
                        }
                        float distance = Vector3.Distance(human.transform.position, _impact);
                        double peak = _rules.PeakKPa(moles, distance);
                        CometRules.Push push = _rules.PushFor(peak);
                        log.Append(string.Format(c, "; player {0} at {1:N0} m, {2:0.#} kPa, band {3}{4}", human.DisplayName, distance, peak, push.Band,
                            CometWave.Outdoors(human) ? " outdoors" : " indoors"));
                        if (push.SuitImpulse > 0.0)
                        {
                            Human who = human;
                            When((float)_rules.ArrivalSeconds(distance), () =>
                            {
                                if (CometWave.Hurt(who, _impact, _rules.PushFor(_rules.PeakKPa(moles, Vector3.Distance(who.transform.position, _impact)))))
                                {
                                    Debug.Log("[TR comet] the wave damaged the suit of " + who.DisplayName);
                                }
                            });
                        }
                    }
                }
            }

            // This machine's listener: its own player, or the camera where there is no player.
            Human local = InventoryManager.ParentHuman;
            Vector3? ear = local != null ? local.transform.position : _render ? _camera.transform.position : (Vector3?)null;
            if (ear.HasValue)
            {
                float distance = Vector3.Distance(ear.Value, _impact);
                double peak = _rules.PeakKPa(moles, distance);
                float arrives = (float)_rules.ArrivalSeconds(distance);
                log.Append(string.Format(c, "; here {0:N0} m away, {1:0.#} kPa, the sound in {2:0.0} s", distance, peak, arrives));
                When(arrives, () =>
                {
                    Vector3 here = Listener();
                    double felt = _rules.PeakKPa(moles, Vector3.Distance(here, _impact));
                    if (!_plan.Preview && CometWave.Shove(_impact, _rules.PushFor(felt)))
                    {
                        Debug.Log(string.Format(CultureInfo.InvariantCulture, "[TR comet] the wave threw this player at {0:0.#} kPa", felt));
                    }
                    CameraController.SetCameraShake((float)_rules.Shake(felt));
                    _shakeFrom = _t;
                    _shakeLevel = (float)_rules.Shake(felt);
                    if (_render)
                    {
                        Human player = InventoryManager.ParentHuman;
                        bool outdoors = player == null || CometWave.Outdoors(player);
                        _sound.Thump();
                        _sound.Gust(here, outdoors, Mathf.Clamp01(0.4f + (float)felt / 100f));
                        if (outdoors)
                        {
                            Gust(here);
                        }
                    }
                });
                if (_render)
                {
                    When(arrives + 0.05f, () => _sound.Boom());
                    When(arrives + 0.2f, () => _sound.Explosions(Listener(), _drawn != null ? _drawn.TransformPoint(_impact) : _impact));
                    When(arrives + 2.5f, () => _sound.Tail());
                    _stormFrom = arrives + 3f;
                    // Debris bursting high in the sky, at random points the seed decides.
                    int pops = 8 + (int)(4f * _scale);
                    for (int i = 0; i < pops; i++)
                    {
                        float at = arrives + 4f + (float)_random.NextDouble() * 36f;
                        Vector3 offset = Quaternion.AngleAxis((float)_random.NextDouble() * 360f, Vector3.up) * Vector3.forward * (150f + (float)_random.NextDouble() * 500f)
                            + Vector3.up * (150f + (float)_random.NextDouble() * 300f);
                        When(at, () => _sound.Pop(Listener() + offset));
                    }
                }
            }
            _cues.Sort((a, b) => a.At.CompareTo(b.At));
            while (_nextCue < _cues.Count && _cues[_nextCue].At < joined)
            {
                _nextCue++;
            }
            Debug.Log(log.ToString());
        }

        private float _shakeFrom = -1f;
        private float _shakeLevel;
        private float _stormFrom = float.MaxValue;

        private void When(float at, Action act) => _cues.Add(new Cue(at, act));

        private Vector3 Listener()
        {
            Human local = InventoryManager.ParentHuman;
            return local != null ? local.transform.position : _camera != null ? _camera.transform.position : _impact;
        }

        private void Aftermath(float s)
        {
            Daylight();
            float glowGain = Mathf.Lerp(1f, 0.55f, _day);
            float ionised = (float)_react.Ionised;
            float burning = (float)_react.Burning;
            float steam = (float)_react.Steam;

            // Flash: up in a frame or two, gone in about a second, warm as it goes; sharper and bluer
            // when the cloud is ionised.
            float decay = 0.45f * (1f - 0.5f * ionised);
            float flash = s < 0.05f ? s / 0.05f : Mathf.Exp(-(s - 0.05f) / decay);
            Color flashColour = Color.Lerp(Color.Lerp(new Color(1f, 0.85f, 0.65f), new Color(0.75f, 0.8f, 1f), ionised), Color.white, flash);
            _flash.color = flashColour * new Color(1f, 1f, 1f, Mathf.Min(1f, 0.97f * (1f + 0.15f * ionised)) * _flashView * flash);

            _flashLight.intensity = (45f * Mathf.Exp(-s / 0.6f) + 5f * Mathf.Exp(-s / (8f * (1f + burning)))) * (1f + burning);
            _flashLight.range = 20000f;
            _flashLight.color = Color.Lerp(Color.Lerp(new Color(1f, 0.6f, 0.25f), _gasColour, 0.35f), Color.white, Mathf.Exp(-s / 0.8f));

            _flashCore.Show(_impact + Vector3.up * _radius * 0.5f, 7f * _radius * (1f - Mathf.Exp(-s / 0.2f)), Color.Lerp(new Color(1f, 0.8f, 0.5f), Color.white, Mathf.Exp(-s / 0.7f)),
                2f * Mathf.Exp(-s / 1.4f) * (_late ? 0f : 1f), 1f);
            float fireLife = 6f * (1f + 1.5f * burning) * (1f - 0.5f * steam);
            _fireGlow.Show(_impact + Vector3.up * _radius * (0.8f + 0.4f * Mathf.Min(s, 6f)), 4.5f * _radius * (0.6f + 0.4f * Mathf.Sqrt(Mathf.Min(s, 30f))) * (1f + 0.4f * burning),
                Color.Lerp(Color.Lerp(new Color(1f, 0.55f, 0.2f), new Color(1f, 0.35f, 0.1f), burning), _gasColour, Mathf.Clamp01(s / 8f)), 1.5f * Mathf.Exp(-s / fireLife) * glowGain, 1f);
            if (s < 5f)
            {
                _shock.Show(_impact + Vector3.up * _radius * 0.3f, _radius * 2f + 1400f * _scale * s, new Color(1f, 0.95f, 0.85f), 1.6f, Mathf.Exp(-s / 0.9f));
            }
            else
            {
                _shock.Hide();
            }
            // Steam: a white condensation dome flashing out with the shock.
            if (steam > 0.02f && s < 4f)
            {
                _steamDome.Show(_impact + Vector3.up * _radius * 0.2f, _radius * 3f + 900f * _scale * Mathf.Sqrt(s), new Color(0.95f, 0.97f, 1f), 1f, 0.8f * steam * Mathf.Exp(-s / 0.8f) * Mathf.Clamp01(s / 0.08f));
            }
            else
            {
                _steamDome.Hide();
            }
            _groundRing.Show(_impact + Vector3.up * 3f, 2f * 16f * _radius * (1f - Mathf.Exp(-s / 2.5f)), new Color(0.62f, 0.52f, 0.42f), 1f, 0.8f * Mathf.Exp(-s / 6f));
            float skyFade = Mathf.Clamp01((EndSeconds - ApproachSeconds - s) / 12f);
            _skyGlow.Show(_impact + Vector3.up * _plumeHeight * 0.5f, 28f * _radius, _gasColour, 0.55f * Mathf.Exp(-s / 30f) * Mathf.Clamp01(s / 0.5f) * skyFade * glowGain, 1f);

            float capRise = 1f - Mathf.Exp(-s / 7f);
            _capTransform.localPosition = Vector3.up * (_radius + _plumeHeight * capRise);
            float fire = 1f - 0.6f * steam;
            float smokeMore = 1f + 0.35f * _day;
            var emission = _stem.emission;
            emission.rateOverTime = s < 45f ? 55f * Mathf.Exp(-s / 12f) * (1f + 0.5f * steam) * smokeMore : 0f;
            emission = _stemGlow.emission;
            emission.rateOverTime = s < 12f ? 60f * Mathf.Exp(-s / (3f * (1f + burning))) * fire : 0f;
            emission = _cap.emission;
            emission.rateOverTime = s > 2f && s < 50f ? 30f * Mathf.Exp(-(s - 2f) / 14f) * smokeMore : 0f;
            emission = _capGlow.emission;
            emission.rateOverTime = s > 1.5f && s < 14f ? 30f * Mathf.Exp(-(s - 1.5f) / (4f * (1f + burning))) * fire : 0f;
            emission = _debrisRain.emission;
            emission.rateOverTime = s > 4f && s < 35f ? 7f * Mathf.Exp(-(s - 4f) / 12f) : 0f;
            if (_fog != null)
            {
                emission = _fog.emission;
                emission.rateOverTime = s > 0.5f && s < 30f ? 22f * (float)_react.DryIce * Mathf.Exp(-(s - 0.5f) / 12f) : 0f;
            }
            if (_haze != null)
            {
                emission = _haze.emission;
                emission.rateOverTime = s > 3f && s < 60f ? 8f * (float)_react.Toxic * Mathf.Exp(-(s - 3f) / 25f) : 0f;
            }
            if (_shimmer != null)
            {
                emission = _shimmer.emission;
                float flicker = 0.5f + 0.5f * Mathf.Sin(s * 23f) * Mathf.Sin(s * 7.3f + 1f);
                emission.rateOverTime = s > 0.8f && s < 25f ? 90f * ionised * flicker * Mathf.Exp(-(s - 0.8f) / 8f) : 0f;
            }

            if (_shakeFrom >= 0f)
            {
                CameraController.SetCameraShake(_shakeLevel * Mathf.Exp(-(_t - _shakeFrom) / 1.6f));
            }
            // The storm loop fading in and out after the sound has passed, muffled indoors.
            if (s >= _stormFrom)
            {
                float since = s - _stormFrom;
                float level = Mathf.Clamp01(since / 8f) * Mathf.Clamp01((70f - since) / 25f) * Mathf.Clamp01(0.5f + 0.2f * _scale);
                Human local = InventoryManager.ParentHuman;
                _sound.Storming(Listener(), level, local != null && !CometWave.Outdoors(local));
            }
        }

        /// <summary>The wave reaching the player: dust blown past, away from the impact.</summary>
        private void Gust(Vector3 eye)
        {
            Vector3 away = Vector3.ProjectOnPlane(eye - _impact, Vector3.up).normalized;
            ParticleSystem gust = NewParticles("Gust", transform, Blended(CometArt.SmokeSheet), 120);
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
            Animate(gust, 1f);
            ColourOverLife(gust, Fade(Color.white, 1f, 0f, 0.1f));
            Burst(gust, 70, 0f);
            gust.Play();
        }

        /// <summary>The gas reaching the planet, the moment the comet lands. Host only.</summary>
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
            _sound?.Stop();
            if (_camera != null && _originalFar > 0f)
            {
                _camera.farClipPlane = _originalFar;
                _camera.depthTextureMode = _originalDepth;
            }
            if (Running == this)
            {
                Running = null;
            }
            if (_render)
            {
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "[TR comet] frame time, ms mean / worst: approach {0}, first 10 s after impact {1}, after {2}; particle cap {3}",
                    _approachFrames, _impactFrames, _afterFrames, MaxParticles));
            }
        }

        // ---------------------------------------------------------------- building blocks

        private Material Additive(Texture texture, int queue)
        {
            Color tint = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Material material = CometArt.Additive(texture, tint, queue);
            _materials.Add((material, tint, true));
            return material;
        }

        private Material Blended(Texture texture)
        {
            Color tint = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            Material material = CometArt.Blended(texture, tint);
            _materials.Add((material, tint, false));
            return material;
        }

        private Glow NewGlow(string name, Texture texture, bool additive, int queue)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(_drawn, false);
            go.AddComponent<MeshFilter>().sharedMesh = CometArt.Quad;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Material material = additive ? CometArt.Additive(texture, Color.clear, queue) : CometArt.Blended(texture, Color.clear, queue);
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

        private ParticleSystem NewParticles(string name, Transform parent, Material material, int max)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            ParticleSystem system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            // The same seed on every machine, so a show looks the same wherever it is watched from.
            system.useAutoRandomSeed = false;
            system.randomSeed = (uint)(_event.Seed + 7919 * (_systems.Count + 1));
            var main = system.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 60f;
            main.maxParticles = max;
            // In the drawn frame for what stands on the ground or flies down to it, so particles
            // already out move with the ground as drawn; the rest, around the player, in the world.
            if (_drawn != null && parent.IsChildOf(_drawn))
            {
                main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                main.customSimulationSpace = _drawn;
            }
            else
            {
                main.simulationSpace = ParticleSystemSimulationSpace.World;
            }
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
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _systems.Add(system);
            return system;
        }

        /// <summary>Holds the whole show under <see cref="MaxParticles"/>, shrinking every system alike.</summary>
        private void Budget()
        {
            int total = 0;
            foreach (ParticleSystem system in _systems)
            {
                total += system.main.maxParticles;
            }
            if (total <= MaxParticles)
            {
                return;
            }
            float share = MaxParticles / (float)total;
            foreach (ParticleSystem system in _systems)
            {
                var main = system.main;
                main.maxParticles = Mathf.Max(8, Mathf.FloorToInt(main.maxParticles * share));
            }
        }

        private static void Burst(ParticleSystem system, int count, float at)
        {
            var emission = system.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(at, (short)Mathf.Max(1, count)) });
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

        /// <summary>Steps each puff through <see cref="CometArt.SmokeSheet"/> over its life, <paramref name="cycles"/> times, from a random frame.</summary>
        private static void Animate(ParticleSystem system, float cycles)
        {
            var sheet = system.textureSheetAnimation;
            sheet.enabled = true;
            sheet.mode = ParticleSystemAnimationMode.Grid;
            sheet.numTilesX = CometArt.SheetTiles;
            sheet.numTilesY = CometArt.SheetTiles;
            sheet.animation = ParticleSystemAnimationType.WholeSheet;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
            sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, CometArt.SheetTiles * CometArt.SheetTiles - 1f);
            sheet.cycleCount = Mathf.Max(1, Mathf.RoundToInt(cycles));
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

        private void Trails(ParticleSystem system, float lifetime, float minVertexDistance)
        {
            var trails = system.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.PerParticle;
            trails.ratio = 1f;
            trails.lifetime = new ParticleSystem.MinMaxCurve(lifetime);
            trails.minVertexDistance = Mathf.Max(2f, minVertexDistance);
            // Trail points stay with their particles: in the world, or in the drawn frame as it moves.
            trails.worldSpace = system.main.simulationSpace == ParticleSystemSimulationSpace.World;
            trails.dieWithParticles = true;
            trails.inheritParticleColor = true;
            trails.sizeAffectsWidth = true;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.8f, 1f, 0f));
            trails.colorOverTrail = new ParticleSystem.MinMaxGradient(Fade(Color.white, 1f, 0f));
            ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
            renderer.trailMaterial = Additive(CometArt.Streak, CometArt.OuterQueue);
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

        /// <summary>Frame times over one part of the show, for the log.</summary>
        private sealed class FrameTimes
        {
            private int _count;
            private double _sum;
            private float _worst;

            public void Add(float seconds)
            {
                _count++;
                _sum += seconds;
                _worst = Mathf.Max(_worst, seconds);
            }

            public override string ToString()
            {
                return _count == 0 ? "-" : string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} over {2} frames", _sum / _count * 1000.0, _worst * 1000f, _count);
            }
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

            /// <summary>
            /// At a real position, in the drawn frame. <paramref name="gain"/> 1 is the colour as given;
            /// the shaders reach 2, which blooms.
            /// </summary>
            public void Show(Vector3 position, float size, Color colour, float gain, float alpha)
            {
                _renderer.enabled = size > 0f && alpha > 0.002f && gain > 0.002f;
                _transform.localPosition = position;
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
