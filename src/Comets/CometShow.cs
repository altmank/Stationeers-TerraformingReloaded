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
    ///   after      debris raining back over the ground it was thrown across, the cloud climbing for
    ///              minutes to the height its yield gives it and spreading into its cap, a long
    ///              rumble, a storm passing. Gone as the darker sky clears, 330 s after the impact.
    /// Every size and time after the impact comes from the yield the wave implies (CometYield).
    /// A player who joins in the middle picks it up at the time it has reached and skips what has
    /// already happened.
    /// </summary>
    internal sealed class CometShow : MonoBehaviour
    {
        public static CometShow Running { get; private set; }

        private const float ApproachSeconds = (float)CometEvent.ApproachSeconds;
        private static readonly float EndSeconds = (float)CometEvent.EndSeconds;

        /// <summary>No more particles than this across the whole show, whatever its size and reactions.</summary>
        private const int MaxParticles = CometLook.MaxParticles;

        /// <summary>
        /// The most the head's light is raised over the reference comet's: past four times it the
        /// game's lighting only whites out the ground, so a bigger body shows as a bigger head.
        /// </summary>
        private const float MaxHeadLight = 4f;

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

        /// <summary>Every size and time the show is drawn at, from the comet's yield (<see cref="CometYield"/>).</summary>
        private CometSizes _sizes;

        /// <summary>The head's size, as the 0.16 show's scale, and its light, held to what the lights can show.</summary>
        private float _headScale;
        private float _headBright;

        /// <summary>The thermal pulse's peak irradiance where this camera stood at the impact, against the reference comet's.</summary>
        private float _flashIrradiance;

        /// <summary>The fireball's frame, rising inside the cap.</summary>
        private Transform _ball;
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
        private Glow _ballFace;
        private ParticleSystem _boils;
        private Material _boilMaterial;
        private Gradient _ballColours;
        private ParticleSystem _secondFireball;
        private ParticleSystem _ejecta;
        private ParticleSystem _stem;
        private float _stemGrowth;
        private ParticleSystem _stemGlow;
        private ParticleSystem _capTop;
        private ParticleSystem _capUnder;
        private Material _underMaterial;
        private ParticleSystem _capGlow;

        /// <summary>The cap's cloud, drawn at a radius of one and scaled to the cap each frame, so every puff grows with it.</summary>
        private Transform _capCloud;

        /// <summary>How the cloud is drawn, layer by layer, from the yield's sizes (<see cref="CometLook"/>).</summary>
        private readonly CometLook _look = CometLook.Shipped;

        /// <summary>Seconds after the impact at which the drawn sizes are written to the log.</summary>
        private readonly List<float> _milestones = new List<float>();
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
            _sizes = CometYield.Shipped.For(_plan.Moles, Effective.Celestial);
            _headScale = (float)_sizes.HeadScale;
            _headBright = Mathf.Clamp((float)_sizes.HeadLight, 0.05f, MaxHeadLight);
            _gasColour = CometPalette.Blend(_plan.Types, _plan.Shares);
            _bands = CometPalette.Bands(_plan.Types, _plan.Shares);
            _impact = comet.Impact;
            _start = comet.Start;
            _travel = comet.Travel;
            _t = (float)(CometEvent.Clock - comet.StartClock);
            _joinedAt = _t;

            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[TR comet] event {0} drawn from its yield: {1}; the show ends {2:0} s after first sight",
                comet.Sequence, _sizes, EndSeconds));

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
            _originalDepth = _camera.depthTextureMode;
            _camera.depthTextureMode |= DepthTextureMode.Depth;

            // Made now, the cloud's sheet on a worker thread, rather than when first heard or seen, so nothing hitches the frame it first plays in.
            _ = CometArt.Roar;
            _ = CometArt.Boom;
            _ = CometArt.Rumble;
            _ = CometArt.Thump;
            _ = CometArt.SmokeSheet;
            CometArt.PrepareBillow();
            _ = CometArt.FireballSheet;
            // As loud as the wave is strong where this machine's listener is when the show starts.
            _sound = new CometSound(gameObject, (float)_rules.Loudness(_rules.PeakKPa(_plan.Moles, Vector3.Distance(Listener(), _impact))));
            _drawn = new GameObject("Drawn").transform;
            _drawn.SetParent(transform, false);
            FollowDrawnGround();
            KeepInView();
            if (_t < ApproachSeconds)
            {
                BuildHead();
            }
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[TR comet] show of event {0}: {1:N0} mol, yield {2:0.###E+0} kt, impact {3} at {4:N0} m, drawn {5:0.0} m lower for this camera, start {6}, joined at t={7:0.0} s, far clip {8:N0} -> {9:N0}, shaders {10}, sound {11}",
                comet.Sequence, _plan.Moles, _sizes.Kilotons, _impact, Vector3.Distance(eye, _impact), _drop, _start, _joinedAt, _originalFar, _camera.farClipPlane, CometArt.ShaderReport, _sound.Report));
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
            main.startSize = new ParticleSystem.MinMaxCurve(140f * _headScale, 260f * _headScale);
            main.startColor = new ParticleSystem.MinMaxGradient(_bands) { mode = ParticleSystemGradientMode.RandomColor };
            var velocity = _tail.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            // A bigger body sheds a longer tail as well as a wider one.
            Vector3 back = -_travel * 260f * (_headScale / (float)CometYield.HeadReferenceScale);
            velocity.x = new ParticleSystem.MinMaxCurve(back.x);
            velocity.y = new ParticleSystem.MinMaxCurve(back.y + 15f);
            velocity.z = new ParticleSystem.MinMaxCurve(back.z);
            var shape = _tail.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 40f * _headScale;
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
            main.startSpeed = new ParticleSystem.MinMaxCurve(20f * _headScale, 80f * _headScale);
            main.startSize = new ParticleSystem.MinMaxCurve(10f * _headScale, 30f * _headScale);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.55f), _gasColour);
            main.gravityModifier = 3f;
            shape = _sparks.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 30f * _headScale;
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
            main.startSize = new ParticleSystem.MinMaxCurve(60f * _headScale, 110f * _headScale);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.42f, 0.38f, 0.36f, 0.85f), Color.Lerp(new Color(0.45f, 0.42f, 0.4f), _gasColour, 0.25f) * new Color(1f, 1f, 1f, 0.7f));
            shape = _contrail.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 25f * _headScale;
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
            _hotTrail.widthMultiplier = 90f * _headScale;
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

            // Every length and time below comes from the yield (CometYield): R the fireball, tR its
            // growth, H the cloud's top, the dust's and the rocks' reach. What multiplies them is the
            // drawing's own proportion, the same at every size.
            float r = (float)_sizes.FireballMetres;
            float grow = Mathf.Max(0.6f, (float)_sizes.FireballSeconds);
            float surge = Mathf.Max(r, (float)_sizes.SurgeMetres);
            float rocks = Mathf.Max(r, (float)_sizes.DebrisMetres);
            float surgeSeconds = surge / (float)CometRules.SpeedOfSound;
            float gravity = Physics.gravity.magnitude;
            // Nothing outlives the show: a puff born at the impact is gone before its end.
            float longest = 0.9f * (EndSeconds - ApproachSeconds);

            _flashLight = NewLight("Flash", at);
            _flashLight.transform.localPosition = Vector3.up * r;

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
            bool burst = !_late;

            // The fireball rises inside the cap as the cloud climbs, so it is drawn in a frame of its
            // own that follows the cloud (Aftermath), and its particles move with it.
            _ball = new GameObject("Ball").transform;
            _ball.SetParent(at, false);
            _capTransform = new GameObject("Cap").transform;
            _capTransform.SetParent(at, false);
            _capTransform.localPosition = Vector3.up * CapHeight(0f);

            // The fireball: one face as wide as the ball is now, a glowing surface boiling with
            // bright cells and darker toward its limb, white-hot while it grows and cooling through
            // orange and the comet's colours to a dull red; and bulges boiling off its edge, so its
            // outline churns. Both drawn before the glow and the smoke, so the smoke covers them.
            float outward = Mathf.Log(10f) / grow;
            _ballColours = Keys(
                new[] { (hotWhite, 0f), (new Color(1f, 0.86f, 0.5f), 0.2f), (Color.Lerp(orange, _gasColour, 0.25f), 0.45f), (new Color(0.85f, 0.3f, 0.1f), 0.72f), (new Color(0.35f, 0.1f, 0.05f), 1f) },
                new[] { (1f, 0f), (1f, 1f) });
            _ballFace = NewGlow("Fireball", CometArt.FireballSheet, additive: false, CometArt.BallQueue);
            _ballFace.Soft(0.05f * r);
            CometLayer boils = _look.Layer(_sizes, "boils");
            float boilLife = Mathf.Clamp(0.45f * grow, 1.5f, 30f);
            _boilMaterial = CometArt.Blended(CometArt.FireballSheet, new Color(1f, 1f, 1f, 0.5f), CometArt.BoilQueue);
            _boils = NewParticles("Boils", _ball, _boilMaterial, boils.Count, _ball);
            var main = _boils.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f * boilLife, boilLife);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.03f * r / grow, 0.08f * r / grow);
            main.startSize = new ParticleSystem.MinMaxCurve((float)boils.PuffMinMetres, (float)boils.PuffMaxMetres);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = hotWhite;
            var shape = _boils.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            shape.radius = (float)_look.BallRadius(_sizes, 0.0) * 0.85f;
            shape.radiusThickness = 0.15f;
            if (burst)
            {
                Burst(_boils, boils.Count / 2, 0f);
            }
            SizeOverLife(_boils, 0.7f, (float)boils.GrowsTo);
            Rotate(_boils, 0.3f * Mathf.Min(1f, 5f / grow));
            Animate(_boils, 2f);
            Soften(_boils, r);
            ColourOverLife(_boils, Fade(Color.white, 1f, 0f, 0.15f));
            ScreenShare(_boils, boils.ScreenShare);

            // Burning: a second, larger fireball a second or two after the first, rolling orange-red.
            if (_react.Burning > 0.02)
            {
                float burn = (float)_react.Burning;
                _secondFireball = NewParticles("Second fireball", _ball, Additive(CometArt.SmokeSheet, CometArt.InnerQueue), 90, _ball);
                main = _secondFireball.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(Mathf.Min(longest, Mathf.Max(5f, 3f * grow)), Mathf.Min(longest, Mathf.Max(9f, 5f * grow)));
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.45f * r * outward, 0.65f * r * outward);
                main.startSize = new ParticleSystem.MinMaxCurve(r * 0.7f, r * 1.2f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = Color.white;
                shape = _secondFireball.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                shape.radius = r * 0.3f;
                if (burst)
                {
                    Burst(_secondFireball, Mathf.RoundToInt(20 + 60 * burn), 1.4f);
                }
                Slow(_secondFireball, outward);
                SizeOverLife(_secondFireball, 0.6f, 1.5f);
                Rotate(_secondFireball, 0.3f * Mathf.Min(1f, 5f / grow));
                Animate(_secondFireball, 1f);
                Soften(_secondFireball, r);
                ColourOverLife(_secondFireball, Keys(
                    new[] { (new Color(1f, 0.9f, 0.6f), 0f), (new Color(1f, 0.5f, 0.15f), 0.2f), (new Color(0.95f, 0.25f, 0.08f), 0.55f), (ember, 1f) },
                    new[] { (0.95f, 0f), (0.85f, 0.5f), (0f, 1f) }));
            }

            // Ejecta: rock and ice thrown high under the world's own gravity, glowing, on trails, the
            // fastest landing at the edge of the falling rocks zone and none beyond it.
            _ejecta = NewParticles("Ejecta", at, Additive(CometArt.Glow, CometArt.OuterQueue), 220);
            float launch = gravity > 0.05f ? (float)CometYield.LaunchSpeed(rocks, gravity) : rocks / 30f;
            float flight = gravity > 0.05f ? 2f * launch / gravity : 30f;
            main = _ejecta.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(Mathf.Min(longest, Mathf.Max(2f, 0.35f * flight)), Mathf.Min(longest, Mathf.Max(3f, flight)));
            main.startSpeed = new ParticleSystem.MinMaxCurve(launch * 0.45f, launch);
            main.startSize = new ParticleSystem.MinMaxCurve(rocks * 0.006f, rocks * 0.015f);
            main.startColor = new ParticleSystem.MinMaxGradient(hotWhite, new Color(1f, 0.7f, 0.35f));
            main.gravityModifier = gravity > 0.05f ? 1f : 0f;
            shape = _ejecta.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 42f;
            shape.radius = r * 0.3f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            if (burst)
            {
                Burst(_ejecta, 140, 0f);
            }
            ColourOverLife(_ejecta, Keys(
                new[] { (Color.white, 0f), (orange, 0.35f), (_gasColour, 0.7f), (ember, 1f) },
                new[] { (1f, 0f), (1f, 0.75f), (0f, 1f) }));
            Trails(_ejecta, 0.35f, rocks * 0.01f);
            Collide(_ejecta);

            // The column: dust and smoke drawn up from the ground behind the rising fireball, climbing
            // to the cap and widening as a plume does, so each puff is as wide as the column where it
            // is: born at the root and grown to the column's width at the cap by the time it gets
            // there. Puffs are lit from above, in darker and lighter masses. Steam turns it white and
            // billowing. Its climb and its growth follow the cloud each frame (Aftermath).
            float steam = (float)_react.Steam;
            CometLayer stem = _look.Layer(_sizes, "stem");
            float stemLife = Mathf.Min(longest, (float)CometLook.StemSeconds);
            _stem = NewParticles("Stem", at, Blended(CometArt.BillowSheet), stem.Count);
            main = _stem.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.85f * stemLife, stemLife);
            main.startSize = new ParticleSystem.MinMaxCurve((float)stem.PuffMinMetres, (float)stem.PuffMaxMetres);
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);
            main.startColor = new ParticleSystem.MinMaxGradient(Shaded(_bands, 0.45f * (1f - steam), Color.Lerp(new Color(0.35f, 0.3f, 0.27f), new Color(0.92f, 0.94f, 0.96f), steam),
                new[] { (0.55f, 0.95f), (0.85f, 0.9f), (1.15f, 0.85f) })) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _stem.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = Mathf.Atan((float)CometYield.StemSpread) * Mathf.Rad2Deg + 5f * steam;
            shape.radius = (float)stem.ReachMetres;
            shape.radiusThickness = 1f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            _stemGrowth = 1f;
            SizeAlongLife(_stem, _stemGrowth);
            Rotate(_stem, 0.04f);
            Animate(_stem, 1f);
            Soften(_stem, r);
            Billows(_stem, (float)CometLook.PuffFadeIn);
            ScreenShare(_stem, stem.ScreenShare);

            CometLayer stemGlow = _look.Layer(_sizes, "stem glow");
            _stemGlow = NewParticles("Stem glow", at, Additive(CometArt.SmokeSheet, CometArt.InnerQueue), stemGlow.Count);
            main = _stemGlow.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4.5f);
            main.startSize = new ParticleSystem.MinMaxCurve((float)stemGlow.PuffMinMetres, (float)stemGlow.PuffMaxMetres);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(_bands) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _stemGlow.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.ConeVolume;
            shape.angle = 6f;
            shape.radius = (float)stemGlow.ReachMetres;
            shape.length = r;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            SizeOverLife(_stemGlow, 0.7f, (float)stemGlow.GrowsTo);
            Animate(_stemGlow, 1f);
            Soften(_stemGlow, r);
            ColourOverLife(_stemGlow, Keys(
                new[] { (new Color(1f, 0.9f, 0.6f), 0f), (Color.white, 0.4f), (new Color(0.6f, 0.3f, 0.2f), 1f) },
                new[] { (1f, 0f), (0.6f, 0.5f), (0f, 1f) }));

            // The cap: a ring of cloud rolling over where the column stops rising, in the comet's
            // colours, its sunlit top lighter and its underside in shadow, lit warm by the fireball
            // below it and cooling as the fireball does. Drawn at a radius of one in a frame the cap's
            // radius scales each frame, so the cap climbs as one and every puff grows with it.
            _capCloud = new GameObject("Cap cloud").transform;
            _capCloud.SetParent(_capTransform, false);
            _capCloud.localScale = Vector3.one * (float)_sizes.CapRadiusAt(0.0);
            float capLife = Mathf.Min(longest, 80f);
            float capFrom = Mathf.Min(2f, 0.2f * grow);
            CometLayer capTop = _look.Layer(_sizes, "cap top");
            _capTop = NewParticles("Cap top", _capCloud, Blended(CometArt.BillowSheet), capTop.Count, _capCloud);
            _capTop.transform.localPosition = Vector3.up * (float)CometLook.CapTopLift;
            CapRing(_capTop, capTop, capLife, capFrom, burst,
                Shaded(_bands, 0.85f * (1f - 0.5f * steam), Color.Lerp(new Color(0.62f, 0.57f, 0.53f), new Color(0.95f, 0.96f, 0.98f), steam), new[] { (0.85f, 0.9f), (1.1f, 0.85f) }));
            CometLayer capUnder = _look.Layer(_sizes, "cap underside");
            _underMaterial = CometArt.Blended(CometArt.BillowSheet, new Color(0.5f, 0.5f, 0.5f, 0.5f));
            _capUnder = NewParticles("Cap underside", _capCloud, _underMaterial, capUnder.Count, _capCloud);
            _capUnder.transform.localPosition = Vector3.down * (float)CometLook.CapUnderDrop;
            CapRing(_capUnder, capUnder, capLife, capFrom, burst,
                Shaded(_bands, 0.6f * (1f - 0.5f * steam), Color.Lerp(new Color(0.4f, 0.36f, 0.33f), new Color(0.8f, 0.82f, 0.85f), steam), new[] { (0.5f, 0.95f), (0.75f, 0.9f) }));

            CometLayer capGlow = _look.Layer(_sizes, "cap glow");
            _capGlow = NewParticles("Cap glow", _capTransform, Additive(CometArt.SmokeSheet, CometArt.InnerQueue), capGlow.Count, _capTransform);
            main = _capGlow.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f * FireLife(), FireLife());
            main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.3f * outward, r * 0.6f * outward);
            main.startSize = new ParticleSystem.MinMaxCurve((float)capGlow.PuffMinMetres, (float)capGlow.PuffMaxMetres);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(_bands) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _capGlow.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = (float)capGlow.ReachMetres;
            shape.rotation = new Vector3(90f, 0f, 0f);
            Slow(_capGlow, outward);
            SizeOverLife(_capGlow, 0.7f, (float)capGlow.GrowsTo);
            Animate(_capGlow, 1f);
            ColourOverLife(_capGlow, Fade(Color.white, 0.8f, 0f, 0.2f));

            // The base surge: a low wall of dust thrown out along the ground with the shock, rolling
            // out to where the wave is still strong enough to throw a player and lingering there. Its
            // puffs stay below the fireball and never cover more than part of the screen, so as it
            // rolls over the eye the sky still shows through it.
            CometLayer surgeLayer = _look.Layer(_sizes, "base surge");
            _groundDust = NewParticles("Base surge", at, Blended(CometArt.BillowSheet), surgeLayer.Count);
            _groundDust.transform.localPosition = Vector3.up * (0.3f * (float)surgeLayer.PuffMaxMetres);
            float dustSlows = (float)CometRules.SpeedOfSound / Mathf.Max(1f, surge - 0.6f * r);
            main = _groundDust.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(Mathf.Min(longest, Mathf.Max(8f, 2.5f * surgeSeconds)), Mathf.Min(longest, Mathf.Max(14f, 4f * surgeSeconds)));
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f * (float)CometRules.SpeedOfSound, (float)CometRules.SpeedOfSound);
            main.startSize = new ParticleSystem.MinMaxCurve((float)surgeLayer.PuffMinMetres, (float)surgeLayer.PuffMaxMetres);
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.2f, 0.2f);
            main.startColor = new ParticleSystem.MinMaxGradient(Shaded(_bands, 0.3f, new Color(0.55f, 0.45f, 0.36f), new[] { (0.7f, 0.9f), (1f, 0.85f) })) { mode = ParticleSystemGradientMode.RandomColor };
            shape = _groundDust.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = r * 0.6f;
            shape.radiusThickness = 0f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            if (burst)
            {
                Burst(_groundDust, Mathf.RoundToInt(0.8f * surgeLayer.Count), 0f);
            }
            Slow(_groundDust, dustSlows);
            SizeOverLife(_groundDust, 0.8f, (float)surgeLayer.GrowsTo);
            Rotate(_groundDust, 0.05f);
            Animate(_groundDust, 1f);
            Soften(_groundDust, (float)surgeLayer.PuffMaxMetres);
            Billows(_groundDust, 0.05f);
            ScreenShare(_groundDust, surgeLayer.ScreenShare);

            // Dry ice: a dense grey-white fog rolling out along the ground.
            if (_react.DryIce > 0.02)
            {
                _fog = NewParticles("Dry ice fog", at, Blended(CometArt.SmokeSheet), 200);
                main = _fog.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(22f, 38f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.15f, r * 0.3f);
                main.startSize = new ParticleSystem.MinMaxCurve(r * 0.8f, r * 1.6f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.86f, 0.88f, 0.9f, 0.85f), new Color(0.7f, 0.72f, 0.75f, 0.7f));
                main.gravityModifier = 0.02f;
                shape = _fog.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = r * 0.8f;
                shape.radiusThickness = 0.3f;
                shape.rotation = new Vector3(90f, 0f, 0f);
                Slow(_fog, 1f / 8f);
                SizeOverLife(_fog, 0.7f, 3.2f);
                Rotate(_fog, 0.08f);
                Animate(_fog, 0.6f);
                Soften(_fog, r);
                ColourOverLife(_fog, Fade(Color.white, 0.9f, 0f, 0.15f));
            }

            // Toxic: a sickly yellow-green haze that settles low and lingers.
            if (_react.Toxic > 0.02)
            {
                _haze = NewParticles("Toxic haze", at, Blended(CometArt.SmokeSheet), 160);
                main = _haze.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(40f, 70f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.03f, r * 0.08f);
                main.startSize = new ParticleSystem.MinMaxCurve(r * 1.2f, r * 2.4f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.72f, 0.8f, 0.22f, 0.6f), new Color(0.55f, 0.66f, 0.18f, 0.45f));
                main.gravityModifier = 0.01f;
                shape = _haze.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = r * 1.8f;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                Slow(_haze, 1f / 15f);
                SizeOverLife(_haze, 0.8f, 2.6f);
                Rotate(_haze, 0.05f);
                Animate(_haze, 0.5f);
                Soften(_haze, r);
                ColourOverLife(_haze, Fade(Color.white, 0.85f, 0f, 0.25f));
            }

            // Ionised: an electric blue-violet shimmer flickering through the cap.
            if (_react.Ionised > 0.02)
            {
                _shimmer = NewParticles("Shimmer", _capTransform, Additive(CometArt.Glow, CometArt.InnerQueue), 200, _capTransform);
                main = _shimmer.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.5f);
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(r * 0.3f, r * 0.9f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.6f, 1f), new Color(0.75f, 0.45f, 1f));
                shape = _shimmer.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = r * 1.4f;
                ColourOverLife(_shimmer, Keys(
                    new[] { (Color.white, 0f), (new Color(0.6f, 0.6f, 1f), 0.3f), (new Color(0.5f, 0.3f, 1f), 1f) },
                    new[] { (0f, 0f), (1f, 0.1f), (0f, 1f) }));
            }

            // The thrown rocks coming back down over the falling rocks zone, from the top of the
            // highest throw (a quarter of the range, at 45 degrees), under the world's gravity. In
            // the drawn frame, so those far out land on the ground as drawn; those near the player
            // stop where they hit the ground there.
            _debrisRain = NewParticles("Debris rain", at, Additive(CometArt.Glow, CometArt.OuterQueue), 180);
            float apex = rocks / 4f;
            float fall = gravity > 0.05f ? Mathf.Sqrt(2f * apex / gravity) : 30f;
            float drift = gravity > 0.05f ? Mathf.Sqrt(gravity * rocks / 2f) * 0.5f : rocks / 60f;
            _debrisRain.transform.localPosition = Vector3.up * apex;
            _debrisRain.transform.rotation = Quaternion.identity;
            main = _debrisRain.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f * fall, 1.05f * fall);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(rocks * 0.002f, rocks * 0.005f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.9f, 0.7f), new Color(1f, 0.6f, 0.3f));
            main.gravityModifier = gravity > 0.05f ? 1f : 0f;
            shape = _debrisRain.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(2f * rocks, 10f, 2f * rocks);
            var velocity = _debrisRain.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-drift, drift);
            velocity.y = new ParticleSystem.MinMaxCurve(gravity > 0.05f ? -0.1f * launch : -apex / fall, 0f);
            velocity.z = new ParticleSystem.MinMaxCurve(-drift, drift);
            ColourOverLife(_debrisRain, Keys(
                new[] { (Color.white, 0f), (orange, 0.5f), (ember, 1f) },
                new[] { (1f, 0f), (1f, 0.8f), (0f, 1f) }));
            Trails(_debrisRain, 0.22f, rocks * 0.004f);
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
            _flashIrradiance = (float)CometYield.Shipped.FlashIrradiance(_sizes.Kilotons, Vector3.Distance(_camera.transform.position, _drawn.TransformPoint(_impact)));

            Budget();
            foreach (ParticleSystem system in new[] { _boils, _secondFireball, _ejecta, _stem, _stemGlow, _capTop, _capUnder, _capGlow, _groundDust, _fog, _haze, _shimmer, _debrisRain })
            {
                if (system != null)
                {
                    system.Play();
                }
            }

            StringBuilder log = new StringBuilder();
            log.Append(string.Format(CultureInfo.InvariantCulture, "[TR comet] event {0} cloud layers at full size:", _event.Sequence));
            foreach (CometLayer layer in _look.Layers(_sizes))
            {
                log.Append(" ").Append(layer).Append(";");
            }
            log.Append(" particles");
            foreach (ParticleSystem system in _systems)
            {
                log.Append(string.Format(CultureInfo.InvariantCulture, " {0} {1}", system.name, system.main.maxParticles));
            }
            Debug.Log(log.ToString());
            foreach (float moment in new[] { 0.5f, grow, 5f, 30f, 120f, 300f })
            {
                if (!_milestones.Contains(moment))
                {
                    _milestones.Add(moment);
                }
            }
            _milestones.Sort();
        }

        /// <summary>One of the cap's two rings of cloud, in the cap's frame at a radius of one.</summary>
        private void CapRing(ParticleSystem system, CometLayer layer, float life, float from, bool burst, Gradient colours)
        {
            float radius = (float)_sizes.CapRadiusMetres;
            var main = system.main;
            main.loop = true;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f * life, life);
            main.startSize = new ParticleSystem.MinMaxCurve((float)(layer.PuffMinMetres / radius), (float)(layer.PuffMaxMetres / radius));
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.001f, 0.004f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.25f, 0.25f);
            main.startColor = new ParticleSystem.MinMaxGradient(colours) { mode = ParticleSystemGradientMode.RandomColor };
            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Donut;
            shape.radius = (float)CometLook.CapRingShare;
            shape.donutRadius = (float)CometLook.CapTubeShare;
            shape.radiusThickness = 1f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            if (burst)
            {
                Burst(system, layer.Count / 2, from);
            }
            Slow(system, 1f / 30f);
            SizeOverLife(system, 0.8f, (float)layer.GrowsTo);
            Rotate(system, 0.03f);
            Animate(system, 1f);
            Billows(system, 0.1f);
            ScreenShare(system, layer.ScreenShare);
        }

        /// <summary>Seconds the fire's glow takes to fade: the fireball's growth and a cooling after, longer when it burns.</summary>
        private float FireLife()
        {
            return (3f + (float)_sizes.FireballSeconds) * (1f + 1.5f * (float)_react.Burning) * (1f - 0.5f * (float)_react.Steam);
        }

        /// <summary>The cap's centre above the impact, this long after it: three quarters of the way up the cloud.</summary>
        private float CapHeight(float s) => 0.75f * (float)_sizes.TopAt(s);

        /// <summary>The fireball's centre: on the ground at the impact, then rising inside the cap.</summary>
        private float BallHeight(float s) => Mathf.Max(0f, CapHeight(s) - 0.75f * (float)_sizes.FireballMetres);

        /// <summary>How fast the cloud's top is climbing, this long after the impact, metres a second.</summary>
        private float RiseSpeed(float s)
        {
            return (float)((_sizes.CloudTopMetres - _sizes.FireballMetres) / CometYield.RiseSeconds * Math.Exp(-s / CometYield.RiseSeconds));
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

        /// <summary>
        /// Raises this camera's far clip, never lowers it, so the whole show stays in view from where
        /// the camera is now: the comet's start while it approaches, and the cloud at its largest,
        /// drawn as low as the ground is drawn there (<see cref="CometYield.FarClipMetres"/>). Every
        /// frame, as the player may walk away from it; restored when the show ends.
        /// </summary>
        private void KeepInView()
        {
            Vector3 eye = _camera.transform.position;
            float across = Vector2.Distance(new Vector2(eye.x, eye.z), new Vector2(_impact.x, _impact.z));
            float needed = (float)CometYield.Shipped.FarClipMetres(across, Mathf.Abs(_drop) + Mathf.Abs(eye.y - _impact.y), _sizes);
            if (!_impacted)
            {
                needed = Mathf.Max(needed, Vector3.Distance(eye, _drawn.TransformPoint(_start)) * 1.3f);
            }
            if (_camera.farClipPlane < needed)
            {
                _camera.farClipPlane = needed;
            }
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
            Drawn(KeepInView);
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

        /// <summary>
        /// 0 at night to 1 in full day: glow reads at night, smoke by day. <paramref name="cloud"/> is
        /// how much of the smoke is still drawn as the show ends (<see cref="CometLook.CloudFade"/>).
        /// </summary>
        private void Daylight(float cloud = 1f)
        {
            _day = Mathf.Clamp01((OrbitalSimulation.WorldSunVector.y + 0.05f) / 0.3f);
            float glow = Mathf.Lerp(1f, 0.55f, _day);
            float smoke = SmokeAlpha() * cloud;
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
            _coma.Show(path, 420f * _headScale * (1f + 0.7f * heat), now, (1.1f + 1.4f * heat) * flicker * glowGain, 1f);
            _core.Show(path, 110f * _headScale * (1f + 0.8f * heat), Color.white, 2f, 1f);

            _headLight.color = now;
            // The head's light grows with the body's ablating area (CometYield.HeadLight); its reach,
            // where that light falls to what the 0.16 head's did at its edge, with its square root.
            _headLight.intensity = (1.5f + 12f * heat * heat) * _headBright;
            _headLight.range = (2500f + 5000f * heat) * Mathf.Sqrt(_headBright);

            var emission = _sparks.emission;
            emission.rateOverTime = 260f * heat * heat;
            emission = _contrail.emission;
            emission.rateOverTime = 70f * heat;
            emission = _tail.emission;
            emission.rateOverTime = 60f + 80f * heat;

            _hotTrail.widthMultiplier = 90f * _headScale * (0.5f + 1.6f * heat);
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
            // The gas first: nothing drawn or heard may stand between the comet and the planet. A comet brought
            // down by rocket delivers its own at the end of its fall (CometLand), whether or not a show plays.
            if (_host && !_late && _event.RocketComet == 0)
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

            // The impact's one pass over every structure and every player: what ground zero destroys, what the
            // fireball burns, what the wave breaks or makes creak and where the falling rocks come down. Every
            // machine makes the same list for the sounds; the host changes the world.
            if (!_plan.Preview)
            {
                List<CometZonePass.Timed> pass = CometZonePass.Run(_event, _impact, moles, _sizes, Effective.Celestial, _rules, _host,
                    (clips, at) =>
                    {
                        if (_render)
                        {
                            _sound.Structure(clips, at);
                        }
                    }, out string zones);
                foreach (CometZonePass.Timed timed in pass)
                {
                    _cues.Add(new Cue(timed.At, timed.Act));
                }
                log.Append("; ").Append(zones);

                // Players outdoors past ground zero: the host damages suits through the game's collision hit.
                if (_host)
                {
                    ZoneReach reach = CometZones.Shipped.ReachFor(moles, Effective.Celestial);
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
                        if (push.SuitImpulse > 0.0 && CometZones.Shipped.ZoneAt(distance, reach) != CometZone.GroundZero)
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
                    int pops = 8 + (int)(4f * _plan.Scale);
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
            float end = EndSeconds - ApproachSeconds;
            float cloud = (float)_look.CloudFade(s, end);
            float feed = (float)_look.CloudFeed(s, end);
            Daylight(cloud);
            float glowGain = Mathf.Lerp(1f, 0.55f, _day);
            float ionised = (float)_react.Ionised;
            float burning = (float)_react.Burning;
            float steam = (float)_react.Steam;
            float r = (float)_sizes.FireballMetres;
            float grow = (float)CometLook.GrowSeconds(_sizes);
            float surge = Mathf.Max(r, (float)_sizes.SurgeMetres);
            float surgeSeconds = surge / (float)CometRules.SpeedOfSound;
            float top = (float)_sizes.TopAt(s);
            float capRadius = (float)_sizes.CapRadiusAt(s);
            float capHeight = CapHeight(s);
            float ballHeight = BallHeight(s);
            float fireLife = FireLife();
            float peak = (float)_sizes.FlashPeakSeconds;
            float heat = (float)_look.BallHeat(_sizes, s);
            float ballRadius = (float)_look.BallRadius(_sizes, s);

            // The flash: a white frame for a few tenths of a second at most, then the thermal pulse,
            // warm, as long as the yield makes it and never so strong that the ground stops reading
            // through it (CometLook). Bluer when the cloud is ionised.
            float first = (float)_look.FlashFirst(s);
            float thermal = (float)_look.Thermal(s, peak, _flashIrradiance);
            float overlay = _late ? 0f : (float)_look.FlashOverlay(s, peak, _flashIrradiance, _flashView);
            Color warm = Color.Lerp(new Color(1f, 0.8f, 0.55f), new Color(0.75f, 0.8f, 1f), ionised);
            Color flashColour = Color.Lerp(warm, Color.white, first >= thermal ? 1f : first / Mathf.Max(1e-4f, thermal));
            _flash.color = new Color(flashColour.r, flashColour.g, flashColour.b, overlay);

            _flashLight.transform.localPosition = Vector3.up * (ballHeight + ballRadius);
            _flashLight.intensity = (float)_look.FlashLight(s, peak, _flashIrradiance, fireLife, burning);
            _flashLight.range = Mathf.Max(20000f, 2f * surge);
            _flashLight.color = Color.Lerp(Color.Lerp(new Color(1f, 0.6f, 0.25f), _gasColour, 0.35f), Color.white, Mathf.Exp(-s / 0.8f));

            _flashCore.Show(_impact + Vector3.up * (ballHeight + 0.3f * ballRadius), 2.5f * ballRadius, Color.Lerp(new Color(1f, 0.8f, 0.5f), Color.white, Mathf.Exp(-s / 0.7f)),
                _late ? 0f : (float)_look.CoreGain(s, peak, _flashIrradiance), 1f);

            // The fireball's face: as wide as the ball now, its cells boiling a cycle every few
            // seconds (slower the bigger it is) and turning slowly, white-hot while it grows, then
            // cooling through orange to a dull red and fading as the cloud closes over it.
            Color ballColour = _ballColours.Evaluate(1f - heat);
            float faceAlpha = Mathf.Clamp01(heat / 0.15f);
            float cycle = Mathf.Max(1.5f, 0.5f * grow);
            _ballFace.Roll = s * 10f * Mathf.Min(1f, 5f / grow);
            _ballFace.Frame(Mathf.FloorToInt(s / cycle * CometArt.SheetTiles * CometArt.SheetTiles), CometArt.SheetTiles);
            _ballFace.Show(_impact + Vector3.up * (ballHeight + 0.15f * ballRadius), 2.06f * ballRadius, ballColour, 0.7f + 1.3f * heat, faceAlpha);
            var shape = _boils.shape;
            shape.radius = 0.85f * ballRadius;
            var main = _boils.main;
            float boilBright = 0.5f + 0.5f * heat;
            main.startColor = new Color(ballColour.r * boilBright, ballColour.g * boilBright, ballColour.b * boilBright, faceAlpha);
            var emission = _boils.emission;
            emission.rateOverTime = heat > 0.2f ? _boils.main.maxParticles / Mathf.Max(0.5f, _boils.main.startLifetime.constantMax) : 0f;

            _fireGlow.Show(_impact + Vector3.up * (ballHeight + 0.3f * ballRadius), 2.6f * ballRadius * (1f + 0.4f * burning),
                Color.Lerp(Color.Lerp(new Color(1f, 0.55f, 0.2f), new Color(1f, 0.35f, 0.1f), burning), _gasColour, Mathf.Clamp01(s / Mathf.Max(8f, grow))), 0.8f * heat * glowGain, 1f);
            // The shock front leaves at the speed of sound, as the wave that breaks things does, and
            // is drawn out to where it stops throwing dust.
            float front = r + (float)CometRules.SpeedOfSound * s;
            // The ring texture is brightest at 0.86 of the quad's half-width.
            const float ringAt = 0.86f;
            if (s < surgeSeconds)
            {
                _shock.Show(_impact + Vector3.up * r * 0.3f, 2f * front / ringAt, new Color(1f, 0.95f, 0.85f), 1f, Mathf.Exp(-s / (0.3f * surgeSeconds)));
            }
            else
            {
                _shock.Hide();
            }
            // Steam: a white condensation dome flashing out behind the shock.
            if (steam > 0.02f && s < 0.5f * surgeSeconds)
            {
                _steamDome.Show(_impact + Vector3.up * r * 0.2f, 2f * Mathf.Max(1.5f * r, 0.9f * front), new Color(0.95f, 0.97f, 1f), 1f, 0.8f * steam * Mathf.Exp(-s / (0.2f * surgeSeconds)) * Mathf.Clamp01(s / 0.08f));
            }
            else
            {
                _steamDome.Hide();
            }
            _groundRing.Show(_impact + Vector3.up * 3f, 2f * surge * (1f - Mathf.Exp(-s / surgeSeconds)) / ringAt, new Color(0.62f, 0.52f, 0.42f), 1f, 0.8f * Mathf.Exp(-s / (1.5f * surgeSeconds + 4f)));
            float skyFade = Mathf.Clamp01((end - s) / 12f);
            _skyGlow.Show(_impact + Vector3.up * top * 0.5f, 2.2f * Mathf.Max(top, capRadius), _gasColour, 0.3f * Mathf.Exp(-s / Mathf.Max(20f, 2f * grow)) * Mathf.Clamp01(s / 0.5f) * skyFade * glowGain, 1f);

            // The cloud: the cap and the fireball in it climbing, the column below climbing up to it,
            // each element sized to the cloud as it is now. The cloud is fed until shortly before the
            // show ends and fades as a whole over its last seconds (CometLook.CloudFade).
            _capTransform.localPosition = Vector3.up * capHeight;
            _capCloud.localScale = Vector3.one * capRadius;
            _ball.localPosition = Vector3.up * ballHeight;
            float rise = RiseSpeed(s);
            float fire = 1f - 0.6f * steam;
            float smokeMore = 1f + 0.35f * _day;

            // The stem's puffs climb from the root to the cap over their life, growing to the
            // column's width there; the curve is set again only when that width has moved on.
            float stemLength = Mathf.Max(r, capHeight);
            main = _stem.main;
            float stemLife = main.startLifetime.constantMax;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.9f * stemLength / stemLife, 1.1f * stemLength / stemLife);
            float growth = (float)(CometLook.StemRadius(_sizes, stemLength) / CometLook.StemRadius(_sizes, 0.0));
            if (Mathf.Abs(growth / _stemGrowth - 1f) > 0.02f)
            {
                _stemGrowth = growth;
                SizeAlongLife(_stem, _stemGrowth);
            }
            shape = _stemGlow.shape;
            shape.length = Mathf.Min(Mathf.Max(r, ballHeight + r), 3f * r);
            main = _stemGlow.main;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f * rise, rise);
            if (_shimmer != null)
            {
                shape = _shimmer.shape;
                shape.radius = Mathf.Max(1.4f * r, 0.7f * capRadius);
            }

            // The cap's underside, lit warm by the fireball below while it glows, then in shadow.
            float warmth = Mathf.Pow(heat, 0.7f) * glowGain;
            Color under = Color.Lerp(new Color(0.42f, 0.42f, 0.44f), new Color(0.95f, 0.6f, 0.38f), warmth);
            _underMaterial.SetColor(_underMaterial.HasProperty("_TintColor") ? "_TintColor" : "_Color", new Color(under.r, under.g, under.b, SmokeAlpha() * cloud));

            emission = _stem.emission;
            emission.rateOverTime = _stem.main.maxParticles / stemLife * (1f + 2f * Mathf.Exp(-s / 20f)) * (1f + 0.5f * steam) * smokeMore * feed;
            emission = _stemGlow.emission;
            emission.rateOverTime = s < 4f * fireLife ? 20f * Mathf.Exp(-s / (0.5f * fireLife)) * fire : 0f;
            float capFrom = Mathf.Min(2f, 0.2f * grow);
            foreach (ParticleSystem cap in new[] { _capTop, _capUnder })
            {
                float capLife = cap.main.startLifetime.constantMax;
                emission = cap.emission;
                emission.rateOverTime = s > capFrom ? cap.main.maxParticles / (0.8f * capLife) * (1f + 2f * Mathf.Exp(-s / 40f)) * smokeMore * feed : 0f;
            }
            emission = _capGlow.emission;
            emission.rateOverTime = s > 1.5f && s < 2.5f * fireLife ? 10f * Mathf.Exp(-(s - 1.5f) / (0.7f * fireLife)) * fire : 0f;
            emission = _debrisRain.emission;
            emission.rateOverTime = s > 4f && s < 35f ? 7f * Mathf.Exp(-(s - 4f) / 12f) : 0f;
            if (_fog != null)
            {
                emission = _fog.emission;
                emission.rateOverTime = s > 0.5f && s < 30f ? 12f * (float)_react.DryIce * Mathf.Exp(-(s - 0.5f) / 12f) : 0f;
            }
            if (_haze != null)
            {
                emission = _haze.emission;
                emission.rateOverTime = s > 3f && s < 60f ? 5f * (float)_react.Toxic * Mathf.Exp(-(s - 3f) / 25f) : 0f;
            }
            if (_shimmer != null)
            {
                emission = _shimmer.emission;
                float flicker = 0.5f + 0.5f * Mathf.Sin(s * 23f) * Mathf.Sin(s * 7.3f + 1f);
                emission.rateOverTime = s > 0.8f && s < 25f ? 60f * ionised * flicker * Mathf.Exp(-(s - 0.8f) / 8f) : 0f;
            }

            if (_milestones.Count > 0 && s >= _milestones[0])
            {
                _milestones.RemoveAt(0);
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[TR comet] event {0} at {1:0.0} s: fireball {2:N0} m at {3:N0} m up, heat {4:0.00}; stem {5:N0} m long, {6:N0} m wide at the root and {7:N0} m at the cap, puffs climbing {8:N0} m/s; cap {9:N0} m up, {10:N0} m across, underside warmth {11:0.00}; flash {12:0.00}, light {13:0.0}; sky {14:0.00} dimmer; live particles {15:N0}",
                    _event.Sequence, s, ballRadius, ballHeight, heat, stemLength, 2.0 * CometLook.StemRadius(_sizes, 0.0), 2.0 * CometLook.StemRadius(_sizes, stemLength), stemLength / stemLife,
                    capHeight, 2f * capRadius, warmth, overlay, _flashLight.intensity, CometSky.Dim, LiveParticles()));
            }

            if (_shakeFrom >= 0f)
            {
                CameraController.SetCameraShake(_shakeLevel * Mathf.Exp(-(_t - _shakeFrom) / 1.6f));
            }
            // The storm loop fading in and out after the sound has passed, muffled indoors.
            if (s >= _stormFrom)
            {
                float since = s - _stormFrom;
                float level = Mathf.Clamp01(since / 8f) * Mathf.Clamp01((70f - since) / 25f);
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

        /// <param name="space">The frame its particles move in: by default the drawn frame for what is in it, else the world.</param>
        private ParticleSystem NewParticles(string name, Transform parent, Material material, int max, Transform space = null)
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
            if (space != null || (_drawn != null && parent.IsChildOf(_drawn)))
            {
                main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                main.customSimulationSpace = space != null ? space : _drawn;
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

        /// <summary>
        /// Linear drag, dv/dt = -k v, the same at every frame rate: a particle leaving at v comes to rest
        /// v / k away, 90 % of the way there after ln 10 / k seconds. No speed limit.
        /// </summary>
        private static void Slow(ParticleSystem system, float perSecond)
        {
            var limit = system.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = new ParticleSystem.MinMaxCurve(1e9f);
            limit.dampen = 0f;
            limit.drag = new ParticleSystem.MinMaxCurve(Mathf.Max(0f, perSecond));
            limit.multiplyDragByParticleSize = false;
            limit.multiplyDragByParticleVelocity = false;
        }

        /// <summary>
        /// Fades a system's puffs against the ground over a tenth of their size rather than the art's
        /// fixed 30 m, so a puff hundreds of metres across meets the ground softly too.
        /// </summary>
        private static void Soften(ParticleSystem system, float puffMetres)
        {
            Material material = system.GetComponent<ParticleSystemRenderer>().sharedMaterial;
            if (material != null && material.HasProperty("_InvFade"))
            {
                material.SetFloat("_InvFade", 1f / Mathf.Max(30f, 0.1f * puffMetres));
            }
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

        /// <summary>Grows each puff evenly from its start size to <paramref name="to"/> times it, as a puff climbing at an even speed up a column widening evenly.</summary>
        private static void SizeAlongLife(ParticleSystem system, float to)
        {
            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, to));
        }

        /// <summary>The smoke materials' tint alpha by night and day: the shader doubles it.</summary>
        private float SmokeAlpha() => (float)CometLook.SmokeTint * Mathf.Lerp(1f, (float)CometLook.DaySmoke, _day);

        /// <summary>
        /// A cloud layer's puffs over their life: in over <paramref name="fadeIn"/> of it, held at the
        /// look's puff opacity, out to nothing from <see cref="CometLook.PuffFadeFrom"/>; and half of
        /// them mirrored left to right, so neighbours differ while the light stays on top.
        /// </summary>
        private void Billows(ParticleSystem system, float fadeIn)
        {
            float alpha = (float)_look.PuffAlpha;
            ColourOverLife(system, Keys(
                new[] { (Color.white, 0f), (Color.white, 1f) },
                new[] { (0f, 0f), (alpha, fadeIn), (alpha, (float)CometLook.PuffFadeFrom), (0f, 1f) }));
            system.GetComponent<ParticleSystemRenderer>().flip = new Vector3(0.5f, 0f, 0f);
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

        /// <summary>
        /// The comet's colour bands toward smoke, <paramref name="keep"/> of the gas colour over
        /// <paramref name="smoke"/>, each band split into the shades given (a brightness and an
        /// opacity), for a system that picks a random colour from it: a cloud of darker and lighter
        /// masses in every gas's colour. Unity holds eight keys, so the largest bands are kept.
        /// </summary>
        private static Gradient Shaded(Gradient bands, float keep, Color smoke, (float Shade, float Alpha)[] shades)
        {
            GradientColorKey[] source = bands.colorKeys;
            int kept = Mathf.Max(1, Mathf.Min(source.Length, 8 / shades.Length));
            List<GradientColorKey> colours = new List<GradientColorKey>();
            List<GradientAlphaKey> alphas = new List<GradientAlphaKey>();
            float from = 0f;
            for (int k = 0; k < kept; k++)
            {
                float to = k == kept - 1 ? 1f : source[k].time;
                Color colour = Color.Lerp(smoke, source[k].color * 0.9f, keep);
                for (int j = 0; j < shades.Length; j++)
                {
                    float at = from + (to - from) * (j + 1) / shades.Length;
                    Color shaded = colour * shades[j].Shade;
                    colours.Add(new GradientColorKey(new Color(Mathf.Clamp01(shaded.r), Mathf.Clamp01(shaded.g), Mathf.Clamp01(shaded.b)), at));
                    alphas.Add(new GradientAlphaKey(shades[j].Alpha, at));
                }
                from = to;
            }
            Gradient gradient = new Gradient { mode = GradientMode.Fixed };
            gradient.SetKeys(colours.ToArray(), alphas.ToArray());
            return gradient;
        }

        /// <summary>The most of the screen's height one puff of this system may cover, however near it is.</summary>
        private static void ScreenShare(ParticleSystem system, double share)
        {
            system.GetComponent<ParticleSystemRenderer>().maxParticleSize = (float)share;
        }

        private int LiveParticles()
        {
            int count = 0;
            foreach (ParticleSystem system in _systems)
            {
                if (system != null)
                {
                    count += system.particleCount;
                }
            }
            return count;
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

            /// <summary>Degrees the quad is turned about the line to the eye.</summary>
            public float Roll;

            /// <summary>Shows one frame of a sheet of tiles by tiles, top-left first, as a particle system steps through it.</summary>
            public void Frame(int index, int tiles)
            {
                int frames = tiles * tiles;
                int f = ((index % frames) + frames) % frames;
                _material.mainTextureScale = new Vector2(1f / tiles, 1f / tiles);
                _material.mainTextureOffset = new Vector2(f % tiles / (float)tiles, (tiles - 1 - f / tiles) / (float)tiles);
            }

            /// <summary>Fades the quad against the ground over this many metres rather than the art's 30.</summary>
            public void Soft(float metres)
            {
                if (_material.HasProperty("_InvFade"))
                {
                    _material.SetFloat("_InvFade", 1f / Mathf.Max(30f, metres));
                }
            }

            public void Face(Vector3 eye, Quaternion facing)
            {
                _transform.rotation = Flat ? Quaternion.Euler(90f, 0f, 0f) : Roll != 0f ? facing * Quaternion.Euler(0f, 0f, Roll) : facing;
            }
        }
    }
}
