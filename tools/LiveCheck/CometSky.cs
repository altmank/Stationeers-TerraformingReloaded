using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Serialization;
using BepInEx.Logging;
using Networks;
using Objects.Rockets;
using TerraformingReloaded.Comets;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// -CometSky: a comet parked at Entry as a body in the game's sky, a Ground Telescope locking onto it, a
    /// rocket's payload bay aiming it, and the Map motherboard's icons (docs/COMET-ROCKETS.md, stage 5).
    ///
    /// Phase "make", a new world. The driver discovers a real site, makes a small water comet there with eight
    /// engines and a little more fuel than its route takes, and sends it; it parks at Entry. It spawns from the
    /// game's own prefabs a launch pad (StructureLaunchMount, which the game hangs under Entry as a pad), a
    /// rocket with a payload bay stood at Entry, and a Ground Telescope built facing north, and holds the
    /// telescope's power on, open, as a battery and a player would. Judged:
    ///   body      the comet is in the game's lists of bodies, circling the planet, with the orbit the rule
    ///             gives, and seen where the rule puts it;
    ///   frame     the rule's scene rotation, from the game's own positions, puts the Sun where the game's sky
    ///             does, and the planet's day angle is the rule's;
    ///   aim       the bay's readouts and the Map motherboard's two icons are the sky rule's point from the
    ///             direction the game shows the comet in, moved by the reserve toward the bay's target, its
    ///             distance to the nearest structure and its reach the rule's;
    ///   telescope aimed by its own logic values at the rule's bearing, the telescope locks onto the comet
    ///             (its CelestialHash), within 2 degrees, and reads back the orbit the rule set;
    ///   pass      the bay's time to the comet's next highest is when the game's sky has it highest.
    /// Then the world is saved, and the comet's direction at a chosen clock time, 30 s after the save, worked
    /// from the elements the game's body holds, is printed for run.ps1 to compare.
    ///
    /// Phase "load": the comet's body is back, with the same elements; at the same clock time the same
    /// direction; the telescope locks again; and a storm (WeatherManager.ImmediatelyActivateWeatherEvent)
    /// blanks the orbit readings while the lock itself stays. Phase "vanilla" is CometSkyVanilla.
    /// </summary>
    internal sealed class CometSkyCheck
    {
        private const uint StartTick = 20;
        private const double StageSeconds = 900.0;
        private const double ArrivedDegrees = 1.0;
        private const double SettleSeconds = 2.0;
        private const double TurnMarginSeconds = 30.0;
        private const double LowestTracked = 3.0;
        private const double AfterSave = 30.0;
        private const double PassWindow = 20.0;
        private const string Storm = "MarsDustStorm";

        private readonly ManualLogSource _log;
        private readonly Action<string[]> _command;
        private readonly bool _load;

        private enum Stage { Setup, Flying, Watching, Saving, Waiting, Relock, Storm, Done }

        private Stage _stage = Stage.Setup;
        private double _stageClock;
        private bool _failed;

        private int _cometId;
        private double _premix;
        private Rocket _rocket;
        private RocketPayloadBay _bay;
        private GroundTelescope _telescope;
        private float _targetX, _targetZ;
        private double _at = double.NaN;

        // The telescope, tracked while the comet is up.
        private double _trackedSince = double.NaN;
        private double _arrivedSince = double.NaN;
        private double _offAtStart = double.NaN;
        private bool _aimedLogged;
        private bool _lockJudged;

        // The next highest, predicted by the bay and seen in the game's sky.
        private double _predicted = double.NaN;
        private double _bestHeight = double.NegativeInfinity;
        private double _bestClock = double.NaN;
        private bool _passJudged;

        private bool _atJudged;
        private double _stormSince = double.NaN;

        private CometSkyCheck(ManualLogSource log, Action<string[]> command, bool load)
        {
            _log = log;
            _command = command;
            _load = load;
        }

        public static CometSkyCheck FromEnvironment(ManualLogSource log, Action<string[]> command)
        {
            string phase = Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETSKY");
            return phase == "make" || phase == "load" ? new CometSkyCheck(log, command, phase == "load") : null;
        }

        private static double Clock => OrbitalSimulation.System?.TotalRealTimeSeconds ?? 0.0;

        private static OrbitalSimulation Sky => OrbitalSimulation.System;

        private void Info(string line) => _log.LogInfo("LiveCheck: sky " + line);

        private void Fail(string why)
        {
            if (!_failed)
            {
                _failed = true;
                Info("FAIL " + why);
                _stage = Stage.Done;
            }
        }

        private void Next(Stage stage)
        {
            _stage = stage;
            _stageClock = Clock;
        }

        private bool Overdue(string what)
        {
            if (Clock - _stageClock > StageSeconds)
            {
                Fail(what + " did not happen within " + StageSeconds + " s");
                return true;
            }
            return false;
        }

        // ---- main thread ------------------------------------------------------------------------------

        public void Step()
        {
            if (_stage == Stage.Done || _failed)
            {
                return;
            }
            try
            {
                HoldPower();
                if (_load)
                {
                    StepLoad();
                }
                else
                {
                    StepMake();
                }
            }
            catch (Exception e)
            {
                Fail("the driver threw: " + e);
            }
        }

        private void StepMake()
        {
            switch (_stage)
            {
                case Stage.Setup when GameManager.GameTickCount >= StartTick:
                    SetUp();
                    break;
                case Stage.Flying:
                    WaitParked();
                    break;
                case Stage.Watching:
                    Watch();
                    break;
                case Stage.Saving when Clock >= _at:
                    SkyAt();
                    Info("phase make done");
                    _stage = Stage.Done;
                    break;
            }
        }

        private void SetUp()
        {
            SpaceMap map = SpaceMap.Current;
            Info(string.Format(CultureInfo.InvariantCulture, "parts | save {0} | sky body {1} | aim readouts {2} | sky rules {3} | planet size {4:R} | world {5}",
                CometMap.SaveInstalled ? "installed" : "MISSING", CometSkyBodies.Installed ? "installed" : "MISSING", CometAim.ReadoutsInstalled ? "installed" : "MISSING",
                CometSkyRules.Verdict ?? "pass", CometMap.PlanetSize(), WorldSetting.Current?.Id));
            if (map?.EntryNode == null || !CometMap.SaveInstalled || !CometSkyBodies.Installed || !CometAim.ReadoutsInstalled || CometSkyRules.Verdict != null)
            {
                Fail("no space map, or a comet part is not installed");
                return;
            }
            SpaceMapNode site = Discover(map);
            if (site == null)
            {
                return;
            }
            double route = RouteDeltaV(site);
            _premix = CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));
            CelestialTuning tuning = Effective.Celestial;
            double size = CometMap.PlanetSize();
            double k = tuning.SizeMultiplierAt(size > 0.0 ? size : CelestialRules.StandardPlanetSize);
            double kg = 100000.0 * Mole.MolarMass(Chemistry.GasType.Water) / 1000.0 * tuning.CometMassScale / k;
            const int engines = 8;
            double fuel = 1.02 * CelestialRules.Shipped.FuelNeeded(kg, engines, route, _premix) + 50.0;
            int index = CometMap.Sites().IndexOf(site) + 1;
            HashSet<int> before = new HashSet<int>(CometMap.Views().Select(v => v.Id));
            _command(new[] { "comet", "make", index.ToString(CultureInfo.InvariantCulture), "100000", "Water", "engines=" + engines.ToString(CultureInfo.InvariantCulture),
                "fuel=" + fuel.ToString("R", CultureInfo.InvariantCulture) });
            _cometId = CometMap.Views().Select(v => v.Id).FirstOrDefault(id => !before.Contains(id));
            if (_cometId == 0)
            {
                Fail("the mod's make verb made no comet");
                return;
            }
            _command(new[] { "comet", "send", _cometId.ToString(CultureInfo.InvariantCulture) });

            // The pad, the rocket with its bay at Entry, and the telescope facing north.
            LaunchMount pad = Spawn<LaunchMount>("StructureLaunchMount", new Vector3(300f, 300f, -400f));
            EngineFuselage mount = Spawn<EngineFuselage>("StructureEngineMountTypeA1", new Vector3(100f, 400f, 100f));
            RocketNetwork network = mount?.RocketNetwork;
            _rocket = network?.Rocket;
            _bay = Spawn<RocketPayloadBay>("StructureRocketLargePayloadBay", new Vector3(124f, 400f, 100f));
            _telescope = Spawn<GroundTelescope>("StructureGroundBasedTelescope", new Vector3(40f, 350f, 40f));
            if (_rocket == null || _bay == null || _telescope == null)
            {
                Fail("the rocket, its bay or the telescope did not spawn (rocket " + (_rocket != null) + ", bay " + (_bay != null) + ", telescope " + (_telescope != null) + ")");
                return;
            }
            _bay.RocketData = new RocketData(network, _bay.ThingTransformPosition - mount.ThingTransformPosition);
            network.AdoptFromLoad(_bay);
            network.RefreshRocket();
            _rocket.SetCurrentNode(map.EntryNode);
            _rocket.RocketState = RocketState.InSpace;
            List<Vector3> pads = CometAim.Pads(map.EntryNode);
            Vector3 home = pads.Count > 0 ? pads[0] : Vector3.zero;
            _targetX = home.x + 1500f;
            _targetZ = home.z + 800f;
            AimBay();
            HoldPower();
            Info(string.Format(CultureInfo.InvariantCulture,
                "set up | comet {0} from {1} ({2:R} delta-v) | {3:R} kg, {4} engines, {5:R} mol of fuel | pad {6} at ({7:0.##}, {8:0.##}) | rocket {9} (id {10}) bay {11} at {12} | telescope {13} at {14} facing {15} | target ({16:0.##}, {17:0.##})",
                _cometId, site.DisplayName, route, kg, engines, fuel, pad != null && pads.Count > 0 ? "on the map" : "MISSING", home.x, home.z, _rocket.DisplayName, _rocket.ReferenceId,
                _bay.ReferenceId, _rocket.CurrentNode?.DisplayName, _telescope.ReferenceId, _telescope.ThingTransformPosition, _telescope.transform.rotation.eulerAngles, _targetX, _targetZ));
            if (pads.Count == 0 || network.RocketPayloadBays.Count != 1)
            {
                Fail("the launch pad is not on the map under Entry, or the rocket did not take its bay");
                return;
            }
            Info(string.Format(CultureInfo.InvariantCulture, "ids | comet {0} | bay {1} | telescope {2} | target {3},{4}",
                _cometId, _bay.ReferenceId, _telescope.ReferenceId, _targetX.ToString("R", CultureInfo.InvariantCulture), _targetZ.ToString("R", CultureInfo.InvariantCulture)));
            Next(Stage.Flying);
        }

        private void AimBay()
        {
            _bay.SetLogicValue(LogicType.PositionX, _targetX);
            _bay.SetLogicValue(LogicType.PositionZ, _targetZ);
        }

        private void WaitParked()
        {
            CometView comet = CometMap.Views().FirstOrDefault(v => v.Id == _cometId);
            if (comet.State != MapCometState.Arrived || CometSkyBodies.For(_cometId) == null)
            {
                Overdue("the comet parking at Entry and going into the sky");
                return;
            }
            if (JudgeBody("body") && JudgeFrame())
            {
                Next(Stage.Watching);
            }
        }

        // ---- the body and the frame ---------------------------------------------------------------------

        /// <summary>The comet's body in the game's lists, its orbit the rule's, and seen where the rule puts it.</summary>
        private bool JudgeBody(string what)
        {
            CometSkyBodies.Shown shown = CometSkyBodies.For(_cometId);
            CelestialBody body = shown.Body;
            SkyFrame frame = CometSkyBodies.Frame().Value;
            SkyOrbit rule = CometSkyBodies.OrbitOf(frame, _cometId);
            int index = -1;
            for (int i = 0; i < OrbitalSimulation.GetCelestialCount(); i++)
            {
                if (OrbitalSimulation.GetCelestial(i) == body)
                {
                    index = i;
                }
            }
            bool listed = index >= 0 && Sky.Find<Celestial>(body.Hash) == body && body.OrbitingBody == Sky.PlayerBody && Sky.PlayerBody.Bodies.Contains(body)
                && body.Hash == Animator.StringToHash(CometSkyBodies.IdPrefix + _cometId.ToString(CultureInfo.InvariantCulture));
            Orbit orbit = body.Orbit;
            bool elements = orbit.Period == rule.PeriodDays && orbit.Inclination == (float)rule.InclinationDegrees && orbit.Eccentricity == 0f
                && orbit.SemiMajorAxis == rule.SemiMajorAxisAu && orbit.LongitudeOfAscendingNode == rule.AscendingNodeRadians && orbit.ArgumentOfPeriapsis == rule.PeriapsisRadians
                && body.LongitudeAtEpoch == (float)rule.EpochDegrees;
            double sim = Sky.SimulationTimeSeconds;
            SkyVector seen = Seen(body);
            SkyVector expected = CometSkyRules.Shipped.CometDirection(frame, rule, sim);
            double off = SkyVector.AngleDegrees(seen, expected);
            Bearing bearing = CometSkyRules.Shipped.BearingOf(seen);
            double orbitSeconds = CelestialRules.OrbitRealSeconds(orbit.Period, Sky.PlayerBody.Orbit.Period, Sky.TimeScale);
            bool pass = listed && elements && off < 0.01;
            Info(string.Format(CultureInfo.InvariantCulture,
                "{0} {1} | comet {2} is body {3} of {4}, hash {5}, round {6} | elements: period {7:R} d inclination {8:R} node {9:R} rad periapsis {10:R} radius {11:R} AU epoch {12:R} | once round in {13:0.###} s | seen at bearing {14:0.###} height {15:0.###}, {16:0.######} deg from the rule",
                what, pass ? "PASS" : "FAIL", _cometId, index, OrbitalSimulation.GetCelestialCount(), body.Hash, body.OrbitingBody?.Name, orbit.Period, orbit.Inclination, orbit.LongitudeOfAscendingNode,
                orbit.ArgumentOfPeriapsis, orbit.SemiMajorAxis, body.LongitudeAtEpoch, orbitSeconds, bearing.Azimuth, bearing.Height, off));
            if (!pass)
            {
                Fail("the comet is not in the game's sky with the rule's orbit where the rule puts it");
            }
            return pass;
        }

        /// <summary>The frame: the rule's scene rotation from the game's own positions puts the Sun where the game does.</summary>
        private bool JudgeFrame()
        {
            SkyFrame frame = CometSkyBodies.Frame().Value;
            double sim = Sky.SimulationTimeSeconds;
            RotatingCelestialBody planet = Sky.PlayerBody;
            Vector3d sunOffset = planet.Position - Sky.PrimaryBody.Position;
            SkyVector sun = CometSkyRules.Shipped.SkyDirection(frame, sim, new SkyVector(sunOffset.x, sunOffset.y, sunOffset.z));
            Vector3 game = OrbitalSimulation.WorldSunVector;
            SkyVector gameSun = new SkyVector(game.x, game.y, game.z);
            double off = SkyVector.AngleDegrees(sun, gameSun);
            double dayRule = CometSkyRules.Shipped.DayAngle(frame, sim);
            double anomaly = (planet.LongitudeAtEpoch + sim) % 360.0;
            Bearing sunBearing = CometSkyRules.Shipped.BearingOf(gameSun);
            string light = OrbitalSimulation.WorldSunTransform == null ? "none in batch mode"
                : string.Format(CultureInfo.InvariantCulture, "shines along {0:0.###} deg from the sun's direction reversed", Vector3.Angle(OrbitalSimulation.WorldSunTransform.forward, -game));
            bool pass = off < 0.01 && Math.Abs(dayRule - planet.CurrentAngle) < 1e-3 && Math.Abs(anomaly - planet.GetWrappedTrueAnomaly()) < 1e-6;
            Info(string.Format(CultureInfo.InvariantCulture,
                "frame {0} | the Sun by the rule from the game's positions is {1:0.######} deg from the game's sun | the game's sun at bearing {2:0.###} height {3:0.###} (its own readout: horizontal {4:0.###}, vertical {5:0.###}) | day angle {6:R}, the game's {7:R} | planet anomaly {8:R}, the game's {9:R} | axis ({10:R}, {11:R}, {12:R}) latitude {13:R} longitude {14:R} | the world's light {15}",
                pass ? "PASS" : "FAIL", off, sunBearing.Azimuth, sunBearing.Height, Mathf.Atan2(game.x, game.z) * 57.29578f, Mathf.Asin(game.y) * 57.29578f, dayRule, planet.CurrentAngle,
                anomaly, planet.GetWrappedTrueAnomaly(), frame.Axis.X, frame.Axis.Y, frame.Axis.Z, frame.LatitudeDegrees, frame.LongitudeDegrees, light));
            if (!pass)
            {
                Fail("the rule's frame does not put the Sun where the game's sky does");
            }
            return pass;
        }

        // ---- the aim, the telescope and the pass --------------------------------------------------------

        private void Watch()
        {
            if (Overdue("the telescope's lock and the comet's next highest"))
            {
                return;
            }
            Aimed();
            if (_failed)
            {
                return;
            }
            if (!_lockJudged)
            {
                Track(() =>
                {
                    _lockJudged = true;
                    JudgeLock("telescope");
                });
            }
            WatchPass();
            if (_lockJudged && _passJudged && !_failed)
            {
                Info(string.Format(CultureInfo.InvariantCulture, "saving | at clock {0:R} | sky time {1:R} | the comet's direction is printed at clock {2:R}", Clock, Sky.SimulationTimeSeconds, Clock + AfterSave));
                _at = Clock + AfterSave;
                Util.Commands.CommandLine.Process("file save");
                Next(Stage.Saving);
            }
        }

        private bool _aimJudged;

        /// <summary>Once: the bay's readouts and the two icons against the rule from the direction the game shows the comet in.</summary>
        private void Aimed()
        {
            if (_aimJudged)
            {
                return;
            }
            CometAim.Aim? found = CometAim.ForBay(_bay.ReferenceId);
            if (found == null)
            {
                return;
            }
            _aimJudged = true;
            CometAim.Aim aim = found.Value;
            CometView comet = CometMap.Views().First(v => v.Id == _cometId);
            CelestialTuning tuning = Effective.Celestial;
            // Every number again, here: the pad nearest the target, the disc, the sky point, the reserve, the landing.
            List<Vector3> pads = CometAim.Pads(SpaceMap.Current.EntryNode);
            Vector3 pad = pads.OrderBy(p => (p.x - _targetX) * (p.x - _targetX) + (p.z - _targetZ) * (p.z - _targetZ)).First();
            double curvature = WorldSetting.Current.Data.TerrainSettings?.Curvature?.Value ?? 0f;
            double size = CometMap.PlanetSize();
            double radius = tuning.DiscRadiusAt(curvature, size > 0.0 ? size : CelestialRules.StandardPlanetSize);
            SkyVector seen = Seen(CometSkyBodies.For(_cometId).Body);
            double drift = SkyVector.AngleDegrees(aim.Direction, seen);
            GroundPoint sky = CelestialRules.Shipped.SkyPoint(pad.x, pad.z, aim.Direction.X, aim.Direction.Y, aim.Direction.Z, radius);
            double reach = CelestialRules.Shipped.ReachMetres(comet.CometKg, comet.Engines, comet.FuelLeft, tuning.CorrectionMetresPerDeltaV, tuning.EntryWindowSeconds, _premix);
            GroundPoint land = CelestialRules.Shipped.Landing(sky, new GroundPoint(_targetX, _targetZ), reach, pad.x, pad.z, radius);
            double nearest = NearestGround(land.X, land.Z);
            CometAim.Icon[] icons = CometAim.Icons();
            CometAim.Icon targetIcon = icons.FirstOrDefault(i => i.IsTarget);
            CometAim.Icon landIcon = icons.FirstOrDefault(i => !i.IsTarget);
            double readX = _bay.GetLogicValue(LogicType.TargetX);
            double readZ = _bay.GetLogicValue(LogicType.TargetZ);
            double readKm = _bay.GetLogicValue(LogicType.DistanceKm);
            double readNext = _bay.GetLogicValue(LogicType.TimeToDestination);
            double readReach = _bay.GetLogicValue(LogicType.Maximum);
            bool readable = _bay.CanLogicRead(LogicType.TargetX) && _bay.CanLogicRead(LogicType.TargetZ) && _bay.CanLogicRead(LogicType.DistanceKm)
                && _bay.CanLogicRead(LogicType.TimeToDestination) && _bay.CanLogicRead(LogicType.Maximum);
            bool icon = icons.Length == 2 && Near(landIcon.X, land.X) && Near(landIcon.Z, land.Z) && Near(targetIcon.X, _targetX) && Near(targetIcon.Z, _targetZ);
            bool readouts = readable && Near(readX, land.X) && Near(readZ, land.Z) && Math.Abs(readKm - nearest / 1000.0) < 1e-6 && Math.Abs(readReach - reach) < 1e-6
                && readNext >= 0.0 && Near(aim.PadX, pad.x) && Near(aim.PadZ, pad.z) && aim.OnPad && Near(aim.Radius, radius);
            bool pass = icon && readouts && drift < 0.1;
            _predicted = Clock + readNext;
            Info(string.Format(CultureInfo.InvariantCulture,
                "aim {0} | pad ({1:0.##}, {2:0.##}) disc {3:R} m | direction {4:0.######} deg from the body's now | sky point ({5:0.###}, {6:0.###}) | reach {7:R} m (bay Maximum {8:R}) | lands ({9:0.###}, {10:0.###}) (bay TargetX {11:0.###} TargetZ {12:0.###}) | nearest structure {13:0.###} m (bay DistanceKm {14:R}) | next highest in {15:0.###} s (bay TimeToDestination) | icons: {16}",
                pass ? "PASS" : "FAIL", pad.x, pad.z, radius, drift, sky.X, sky.Z, reach, readReach, land.X, land.Z, readX, readZ, nearest, readKm, readNext,
                CometSkyIcons.Describe(icons, CometAim.All, Clock).Replace("\r", "").Replace("\n", " / ")));
            if (!pass)
            {
                Fail("the bay's readouts or the Map motherboard's icons are not the sky rule's from the direction the game shows the comet in");
            }
        }

        /// <summary>
        /// While the comet is up, the telescope aimed at it by its own logic values; the action once the dish, by
        /// its own Horizontal and Vertical, has been within <see cref="ArrivedDegrees"/> of the comet for
        /// <see cref="SettleSeconds"/>. The dish turns at the game's own rate (RotatableBehaviour: a ratio of
        /// 180 / range x the telescope's MovementSpeed a second, so 180 x MovementSpeed degrees a second on each
        /// axis), read from the telescope, so the longest turn it can be asked for, half way round, sets how long
        /// it is given: twice that, for a comet moving while it turns, and a margin. A comet that sets first is
        /// followed again from its next rising.
        /// </summary>
        private void Track(Action judge)
        {
            CometSkyBodies.Shown shown = CometSkyBodies.For(_cometId);
            if (shown == null)
            {
                return;
            }
            SkyVector comet = Seen(shown.Body);
            Bearing bearing = CometSkyRules.Shipped.BearingOf(comet);
            if (bearing.Height < LowestTracked)
            {
                _trackedSince = double.NaN;
                _arrivedSince = double.NaN;
                return;
            }
            TelescopeAim aim = CometSkyRules.Shipped.AimFor(bearing);
            _telescope.SetLogicValue(LogicType.Horizontal, aim.Horizontal);
            _telescope.SetLogicValue(LogicType.Vertical, aim.Vertical);
            double horizontalRate = 180.0 * _telescope.MovementSpeedHorizontal;
            double verticalRate = 180.0 * _telescope.MovementSpeedVertical;
            double longest = Math.Max(180.0 / horizontalRate, 90.0 / verticalRate);
            double allowed = 2.0 * longest + TurnMarginSeconds;
            double off = DishOff(comet);
            double now = Clock;
            if (double.IsNaN(_trackedSince))
            {
                _trackedSince = now;
                _offAtStart = off;
                _arrivedSince = double.NaN;
            }
            if (off <= ArrivedDegrees)
            {
                if (double.IsNaN(_arrivedSince))
                {
                    _arrivedSince = now;
                }
                if (now - _arrivedSince >= SettleSeconds)
                {
                    if (!_aimedLogged)
                    {
                        _aimedLogged = true;
                        Info(string.Format(CultureInfo.InvariantCulture,
                            "aimed | the dish turns {0:0.###} deg/s across and {1:0.###} deg/s up and down (MovementSpeed {2:R}, {3:R}) | it started {4:0.###} deg off the comet and was within {5} deg after {6:0.###} s, then held {7} s | allowed {8:0.#} s",
                            horizontalRate, verticalRate, _telescope.MovementSpeedHorizontal, _telescope.MovementSpeedVertical, _offAtStart, ArrivedDegrees,
                            _arrivedSince - _trackedSince, SettleSeconds, allowed));
                    }
                    judge();
                }
                return;
            }
            _arrivedSince = double.NaN;
            if (now - _trackedSince > allowed)
            {
                Fail(string.Format(CultureInfo.InvariantCulture,
                    "the telescope did not come within {0} deg of the comet in {1:0.#} s (it turns {2:0.###} and {3:0.###} deg/s; it started {4:0.###} deg off and is {5:0.###} deg off now, Horizontal {6:0.###} Vertical {7:0.###} for {8:0.###} and {9:0.###})",
                    ArrivedDegrees, allowed, horizontalRate, verticalRate, _offAtStart, off, _telescope.GetLogicValue(LogicType.Horizontal), _telescope.GetLogicValue(LogicType.Vertical),
                    aim.Horizontal, aim.Vertical));
            }
        }

        /// <summary>How far the dish looks from the comet, in degrees, by the telescope's own Horizontal and Vertical.</summary>
        private double DishOff(SkyVector comet)
        {
            double azimuth = _telescope.GetLogicValue(LogicType.Horizontal) * Math.PI / 180.0;
            double height = (90.0 - _telescope.GetLogicValue(LogicType.Vertical)) * Math.PI / 180.0;
            SkyVector dish = new SkyVector(Math.Sin(azimuth) * Math.Cos(height), Math.Sin(height), Math.Cos(azimuth) * Math.Cos(height));
            return SkyVector.AngleDegrees(dish, comet);
        }

        /// <summary>The telescope locked onto the comet, within 2 degrees, reading back the orbit the rule set.</summary>
        private bool JudgeLock(string what)
        {
            CometSkyBodies.Shown shown = CometSkyBodies.For(_cometId);
            CelestialBody body = shown.Body;
            SkyFrame frame = CometSkyBodies.Frame().Value;
            SkyOrbit rule = CometSkyBodies.OrbitOf(frame, _cometId);
            double sim = Sky.SimulationTimeSeconds;
            double hash = _telescope.GetLogicValue(LogicType.CelestialHash);
            double error = _telescope.GetLogicValue(LogicType.AlignmentError);
            double period = _telescope.GetLogicValue(LogicType.OrbitPeriod);
            double inclination = _telescope.GetLogicValue(LogicType.Inclination);
            double eccentricity = _telescope.GetLogicValue(LogicType.Eccentricity);
            double sma = _telescope.GetLogicValue(LogicType.SemiMajorAxis);
            double km = _telescope.GetLogicValue(LogicType.DistanceKm);
            double anomaly = _telescope.GetLogicValue(LogicType.TrueAnomaly);
            double parent = _telescope.GetLogicValue(LogicType.CelestialParentHash);
            double expectedAnomaly = CometSkyRules.TrueAnomalyReading(CometSkyRules.Shipped.Anomaly(rule, frame.PeriodDays, sim));
            double expectedKm = CometSkyRules.DistanceKmReading(rule.SemiMajorAxisAu);
            Bearing bearing = CometSkyRules.Shipped.BearingOf(Seen(body));
            Vector3 dish = _telescope.DishTransform.forward;
            double dishOff = SkyVector.AngleDegrees(new SkyVector(dish.x, dish.y, dish.z), Seen(body));
            bool pass = hash == body.Hash && error <= 2.0 && period == rule.PeriodDays && inclination == (float)rule.InclinationDegrees && eccentricity == 0.0
                && sma == rule.SemiMajorAxisAu && Math.Abs(km - expectedKm) < 0.01 && Math.Abs(anomaly - expectedAnomaly) < 1e-6 && parent == Sky.PlayerBody.Hash;
            Info(string.Format(CultureInfo.InvariantCulture,
                "{0} {1} | hash {2} (comet's {3}) | alignment error {4:0.###} deg | the dish is {5:0.###} deg off the comet | telescope Horizontal {6:0.###} Vertical {7:0.###} for bearing {8:0.###} height {9:0.###} | period {10:R} d (rule {11:R}) | inclination {12:R} (rule {13:R}) | eccentricity {14:R} | semi-major axis {15:R} AU (rule {16:R}) | distance {17:R} km (rule {18:R}) | true anomaly {19:R} (rule {20:R}) | parent {21} (planet's {22}) | weather {23}",
                what, pass ? "PASS" : "FAIL", hash, body.Hash, error, dishOff, _telescope.GetLogicValue(LogicType.Horizontal), _telescope.GetLogicValue(LogicType.Vertical), bearing.Azimuth, bearing.Height,
                period, rule.PeriodDays, inclination, (float)rule.InclinationDegrees, eccentricity, sma, rule.SemiMajorAxisAu, km, expectedKm, anomaly, expectedAnomaly, parent, Sky.PlayerBody.Hash,
                Weather.WeatherManager.IsWeatherEventRunning ? "a storm" : "clear"));
            if (!pass)
            {
                Fail("the telescope did not lock onto the comet within 2 degrees and read back the orbit the rule set");
            }
            return pass;
        }

        /// <summary>The comet's highest in the game's sky, around the moment the bay said it would be.</summary>
        private void WatchPass()
        {
            if (_passJudged || double.IsNaN(_predicted))
            {
                return;
            }
            double now = Clock;
            if (now < _predicted - PassWindow)
            {
                return;
            }
            double height = CometSkyRules.Shipped.BearingOf(Seen(CometSkyBodies.For(_cometId).Body)).Height;
            if (now <= _predicted + PassWindow)
            {
                if (height > _bestHeight)
                {
                    _bestHeight = height;
                    _bestClock = now;
                }
                return;
            }
            _passJudged = true;
            bool pass = Math.Abs(_bestClock - _predicted) <= 1.0 && _bestHeight > 0.0;
            Info(string.Format(CultureInfo.InvariantCulture, "pass {0} | the bay said highest at clock {1:R}; the game's sky had it highest, {2:0.###} deg up, at {3:R}, {4:0.###} s apart",
                pass ? "PASS" : "FAIL", _predicted, _bestHeight, _bestClock, _bestClock - _predicted));
            if (!pass)
            {
                Fail("the comet was not highest when the bay's time to its next highest said");
            }
        }

        // ---- across the save --------------------------------------------------------------------------

        /// <summary>
        /// The comet's direction at the chosen clock time, by the rule from the elements the game's body holds
        /// and the game's own frame, and the game's own direction this frame against the rule at this frame.
        /// </summary>
        private void SkyAt()
        {
            CometSkyBodies.Shown shown = CometSkyBodies.For(_cometId);
            CelestialBody body = shown.Body;
            SkyFrame frame = CometSkyBodies.Frame().Value;
            Orbit orbit = body.Orbit;
            SkyOrbit held = new SkyOrbit(orbit.Period, orbit.Inclination, orbit.LongitudeOfAscendingNode, orbit.ArgumentOfPeriapsis, orbit.SemiMajorAxis, body.LongitudeAtEpoch);
            double now = Clock;
            double sim = Sky.SimulationTimeSeconds;
            double simAt = sim - (now - _at) * Sky.TimeScale;
            SkyVector at = CometSkyRules.Shipped.CometDirection(frame, held, simAt);
            double off = SkyVector.AngleDegrees(Seen(body), CometSkyRules.Shipped.CometDirection(frame, held, sim));
            bool pass = off < 0.01;
            Info(string.Format(CultureInfo.InvariantCulture, "at {0} | clock {1:R} | direction ({2:F9}, {3:F9}, {4:F9}) | this frame, clock {5:R}: the game's direction is {6:0.######} deg from the rule's",
                pass ? "PASS" : "FAIL", _at, at.X, at.Y, at.Z, now, off));
            if (!pass)
            {
                Fail("the game's body is not where its own elements put it");
            }
        }

        // ---- phase load -------------------------------------------------------------------------------

        private void StepLoad()
        {
            if (GameManager.GameTickCount < 3)
            {
                return;
            }
            switch (_stage)
            {
                case Stage.Setup:
                    if (_stageClock == 0.0)
                    {
                        _stageClock = Clock;
                    }
                    Loaded();
                    break;
                case Stage.Waiting:
                    if (!_atJudged && Clock >= _at)
                    {
                        _atJudged = true;
                        SkyAt();
                        Next(Stage.Relock);
                    }
                    break;
                case Stage.Relock:
                    if (!Overdue("the telescope's lock after the load"))
                    {
                        Track(() =>
                        {
                            if (JudgeLock("relock"))
                            {
                                Weather.WeatherManager.ImmediatelyActivateWeatherEvent(Storm);
                                Next(Stage.Storm);
                            }
                        });
                    }
                    break;
                case Stage.Storm:
                    WatchStorm();
                    break;
            }
        }

        private void Loaded()
        {
            _cometId = (int)Long("TR_LIVECHECK_COMETSKY_COMET");
            _at = double.Parse(Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETSKY_AT") ?? "NaN", CultureInfo.InvariantCulture);
            string[] target = (Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETSKY_TARGET") ?? "").Split(',');
            _telescope = Referencable.Find<GroundTelescope>(Long("TR_LIVECHECK_COMETSKY_TELESCOPE"));
            _bay = Referencable.Find<RocketPayloadBay>(Long("TR_LIVECHECK_COMETSKY_BAY"));
            if (_cometId == 0 || double.IsNaN(_at) || target.Length != 2 || _telescope == null || _bay == null || CometSkyBodies.For(_cometId) == null)
            {
                if (Clock - _stageClock > 60.0)
                {
                    Fail(string.Format(CultureInfo.InvariantCulture, "the load brought back no comet {0} in the sky, telescope or bay (telescope {1}, bay {2}, in the sky {3})",
                        _cometId, _telescope != null, _bay != null, CometSkyBodies.For(_cometId) != null));
                }
                return;
            }
            _targetX = float.Parse(target[0], CultureInfo.InvariantCulture);
            _targetZ = float.Parse(target[1], CultureInfo.InvariantCulture);
            // The game keeps no payload bay's target across a load; the chip that set it sets it again.
            AimBay();
            if (!JudgeBody("readded"))
            {
                return;
            }
            if (Clock >= _at)
            {
                Fail(string.Format(CultureInfo.InvariantCulture, "the load ran on past the chosen clock {0:R} before the comet was back in the sky (clock {1:R})", _at, Clock));
                return;
            }
            Next(Stage.Waiting);
        }

        private void WatchStorm()
        {
            if (!Weather.WeatherManager.IsWeatherEventRunning)
            {
                Overdue("the storm starting");
                return;
            }
            if (double.IsNaN(_stormSince))
            {
                _stormSince = Clock;
            }
            Track(() => { });
            if (Clock - _stormSince < 3.0)
            {
                return;
            }
            CelestialBody body = CometSkyBodies.For(_cometId).Body;
            double hash = _telescope.GetLogicValue(LogicType.CelestialHash);
            double error = _telescope.GetLogicValue(LogicType.AlignmentError);
            double[] blank = { _telescope.GetLogicValue(LogicType.OrbitPeriod), _telescope.GetLogicValue(LogicType.DistanceKm), _telescope.GetLogicValue(LogicType.DistanceAu),
                _telescope.GetLogicValue(LogicType.Inclination), _telescope.GetLogicValue(LogicType.Eccentricity), _telescope.GetLogicValue(LogicType.SemiMajorAxis),
                _telescope.GetLogicValue(LogicType.TrueAnomaly) };
            double parent = _telescope.GetLogicValue(LogicType.CelestialParentHash);
            bool pass = hash == body.Hash && error <= 5.0 && blank.All(double.IsNaN) && parent == 0.0;
            Info(string.Format(CultureInfo.InvariantCulture, "storm {0} | {1} running | hash {2} (comet's {3}) | alignment error {4:0.###} | period, km, AU, inclination, eccentricity, semi-major axis, true anomaly: {5} | parent {6}",
                pass ? "PASS" : "FAIL", Weather.WeatherManager.CurrentWeatherEvent?.Id ?? "an event", hash, body.Hash, error,
                string.Join(", ", blank.Select(v => v.ToString("R", CultureInfo.InvariantCulture))), parent));
            if (!pass)
            {
                Fail("a storm did not blank the telescope's orbit readings while it kept its lock");
                return;
            }
            Info("done");
            _stage = Stage.Done;
        }

        // ---- helpers ----------------------------------------------------------------------------------

        private static long Long(string name) =>
            long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0L;

        private static SkyVector Seen(Celestial body)
        {
            Vector3 v = body.WorldVector;
            return new SkyVector(v.x, v.y, v.z);
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-3;

        /// <summary>The nearest structure on the ground to a point, by a scan of its own: rockets in space aside.</summary>
        private static double NearestGround(double x, double z)
        {
            double best = double.PositiveInfinity;
            GridController.AllStructuresPool.ForEach((Action<Structure>)(s =>
            {
                if (s == null || s.IsBeingDestroyed || s.RocketData?.Network?.Rocket?.RocketState == RocketState.InSpace)
                {
                    return;
                }
                Vector3 at = s.ThingTransformPosition;
                best = Math.Min(best, (at.x - x) * (at.x - x) + (at.z - z) * (at.z - z));
            }));
            return best < double.PositiveInfinity ? Math.Sqrt(best) : double.NaN;
        }

        /// <summary>The parts have no cable: the driver keeps the telescope on, powered and open, in place of a battery.</summary>
        private void HoldPower()
        {
            if (_telescope == null)
            {
                return;
            }
            if (!_telescope.OnOff)
            {
                OnServer.Interact(_telescope.InteractOnOff, 1);
            }
            if (!_telescope.Powered)
            {
                OnServer.Interact(_telescope.InteractPowered, 1);
            }
            if (!_telescope.IsOpen)
            {
                OnServer.Interact(_telescope.InteractOpen, 1);
            }
        }

        private static T Spawn<T>(string prefabName, Vector3 position) where T : Structure
        {
            Thing prefab = Prefab.Find(prefabName);
            if (prefab == null)
            {
                return null;
            }
            T made = OnServer.Create<T>(prefab, position, Quaternion.identity);
            if (made != null && made.BuildStates != null && made.BuildStates.Count > 0)
            {
                made.CurrentBuildStateIndex = made.BuildStates.Count - 1;
            }
            return made;
        }

        /// <summary>A new ice site by the game's own discovery, at the nodes nearest the orbit first.</summary>
        private SpaceMapNode Discover(SpaceMap map)
        {
            foreach (SpaceMapNode parent in map.Nodes.Where(n => n?.DiscoverData != null && n.DiscoverData.SiteGenerations.Count > 0).OrderBy(n => RouteDeltaV(n)).ToList())
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
                    if (made.Deposit != null && made.Deposit.TotalOreAtLocation > 0)
                    {
                        return made;
                    }
                }
            }
            Fail("discovery made no site on the map");
            return null;
        }

        private static double RouteDeltaV(SpaceMapNode node)
        {
            if (node == SpaceMap.Current.EntryNode || !SpaceMapPathFinder.GetPath(node, SpaceMap.Current.EntryNode, out List<NodeTransit> path) || path == null)
            {
                return 0.0;
            }
            return path.Sum(t => (double)t.GetDistance());
        }
    }

    /// <summary>
    /// -CometSky, phase "vanilla": the save phase make wrote, loaded by the game without the mod. Only the game's
    /// own types. The telescope must be there, the sky must be the world's own with no comet in it.
    /// </summary>
    internal sealed class CometSkyVanilla
    {
        private readonly ManualLogSource _log;
        private bool _done;

        private CometSkyVanilla(ManualLogSource log)
        {
            _log = log;
        }

        public static CometSkyVanilla FromEnvironment(ManualLogSource log) =>
            Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETSKY") == "vanilla" ? new CometSkyVanilla(log) : null;

        public void Step()
        {
            if (_done || GameManager.GameTickCount < 5)
            {
                return;
            }
            _done = true;
            long.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETSKY_TELESCOPE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id);
            GroundTelescope telescope = Referencable.Find<GroundTelescope>(id);
            int bodies = OrbitalSimulation.IsValid ? OrbitalSimulation.GetCelestialCount() : -1;
            int comets = 0;
            for (int i = 0; i < Math.Max(0, bodies); i++)
            {
                if (OrbitalSimulation.GetCelestial(i)?.Id?.StartsWith("TRComet", StringComparison.Ordinal) == true)
                {
                    comets++;
                }
            }
            bool pass = telescope != null && bodies > 0 && comets == 0;
            _log.LogInfo(string.Format(CultureInfo.InvariantCulture, "LiveCheck: sky vanilla {0} | telescope {1} {2} | sky bodies {3}, comets among them {4} | tick {5}",
                pass ? "PASS" : "FAIL", id, telescope != null ? "loaded" : "MISSING", bodies, comets, GameManager.GameTickCount));
        }
    }
}
