using System;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The comet in the game's sky (docs/COMET-ROCKETS.md, "The comet in the sky"): the orbit a comet
    /// waiting at Entry is given, the game's own sky frame that turns that orbit into a direction in the
    /// world, the direction as a compass bearing and a height, the telescope angles that look along it,
    /// and when the comet is next at its highest. Plain arithmetic in doubles with no game or Unity calls:
    /// the game's rotations are worked the way Unity's Quaternion works them, so tools/PatchCheck runs
    /// <see cref="Check(CometSkyRules)"/> outside the game, and tools/LiveCheck holds the game's own
    /// directions, the Sun's among them, to these. The parts are virtual so PatchCheck can hand the check
    /// wrong rules and see each caught.
    ///
    /// The frame, from the game's code: a body's sky direction is the scene rotation of the planet's
    /// position less the body's (Celestial.SetPlayerVectorTo), and the scene rotation is a quarter turn
    /// back about x, then the latitude about x, then the day's angle plus the longitude about the planet's
    /// axis (OrbitalSimulation.GetSceneRotation). The result is in world space: y is up (the sun's light is
    /// placed along it and day is the sun above y = 0), z is north and x east (the grid's own North and
    /// East faces, Grid3.Face), and the game's own readout takes the sun's horizontal angle as atan2(x, z).
    /// </summary>
    public class CometSkyRules
    {
        public static readonly CometSkyRules Shipped = new CometSkyRules();

        /// <summary>The game's kilometres to astronomical units (OrbitalSimulation.KM_TO_AU).</summary>
        public const double KmToAu = 6.6845871222684464E-09;

        /// <summary>The game's astronomical unit in kilometres (OrbitalSimulation.AU_TO_KM).</summary>
        public const double AuToKm = 149597870.7;

        /// <summary>
        /// The radius of a waiting comet's orbit: Phobos's, 9,376 km (celestialbodies.xml), whose period the
        /// default orbit takes. The game has no radius or mass for the planet a low orbit could be worked out
        /// from, and the radius changes nothing but the telescope's distance readings.
        /// </summary>
        public const double CometOrbitKm = 9376.0;

        public static double CometOrbitAu => CometOrbitKm * KmToAu;

        /// <summary>The golden angle: comets parked together start this far apart round their orbit, so no two share a place in the sky.</summary>
        public const double PhaseStepDegrees = 137.50776405003785;

        /// <summary>Where comet <paramref name="id"/> is in its orbit at the sky's epoch, in degrees.</summary>
        public static double PhaseDegrees(int id) => (Math.Abs((long)id) * PhaseStepDegrees) % 360.0;

        // ---- the game's frame -----------------------------------------------------------------------------

        /// <summary>
        /// The planet's turn in its day, in degrees (RotatingCelestialBody.CurrentAngle): its orbit's
        /// anomaly, which at sky time t is its epoch plus t, through its own day's share of that orbit; a
        /// planet turning backwards counts down. Rounded to a float as the game holds it.
        /// </summary>
        public virtual double DayAngle(SkyFrame frame, double simulationTime)
        {
            double anomaly = frame.EpochDegrees + simulationTime;
            double angle = anomaly % frame.SolDegreesPerDay / frame.SolDegreesPerDay * 360.0;
            return frame.Retrograde ? (float)((360.0 - angle % 360.0) % 360.0) : (float)(angle % 360.0);
        }

        /// <summary>The scene rotation at sky time t (OrbitalSimulation.GetSceneRotation).</summary>
        public virtual SkyRotation SceneRotation(SkyFrame frame, double simulationTime)
        {
            return SkyRotation.AngleAxis(-90.0, SkyVector.Right)
                * SkyRotation.AngleAxis(frame.LatitudeDegrees, SkyVector.Right)
                * SkyRotation.AngleAxis(DayAngle(frame, simulationTime) + frame.LongitudeDegrees, frame.Axis);
        }

        /// <summary>
        /// The direction a body is seen in, at sky time t, from the planet's position less the body's, both
        /// in the sky's own frame (Celestial.SetPlayerVectorTo). Of unit length.
        /// </summary>
        public virtual SkyVector SkyDirection(SkyFrame frame, double simulationTime, SkyVector planetLessBody)
        {
            return SceneRotation(frame, simulationTime).Rotate(planetLessBody).Normalized;
        }

        // ---- the comet's orbit ----------------------------------------------------------------------------

        /// <summary>
        /// The orbit a comet waiting at Entry is given: once round in <paramref name="orbitSeconds"/> real
        /// seconds on this world (CelestialRules.OrbitPeriodDays), its pole <paramref name="tiltDegrees"/>
        /// from the planet's axis, tilted toward the side of the axis away from the sky frame's own pole, at
        /// Phobos's radius, round, starting <paramref name="phaseDegrees"/> into its orbit. The game takes an
        /// orbit's tilt from its own y axis and its node in radians (Orbit.GetLocalPosition), so the pole is
        /// turned into those: an orbit with node Ω and inclination i has its pole along
        /// (-sin Ω sin i, -cos i, cos Ω sin i). Inclination and phase are rounded to floats, as the game holds them.
        /// </summary>
        public virtual SkyOrbit OrbitFor(SkyFrame frame, double orbitSeconds, double tiltDegrees, double phaseDegrees, double semiMajorAxisAu)
        {
            SkyVector axis = frame.Axis.Normalized;
            SkyVector across = SkyVector.Up - axis * SkyVector.Up.Dot(axis);
            across = across.Length > 1e-9 ? across.Normalized : SkyVector.Right;
            double tilt = tiltDegrees * Math.PI / 180.0;
            SkyVector pole = -(axis * Math.Cos(tilt)) - across * Math.Sin(tilt);
            double inclination = Math.Acos(Math.Max(-1.0, Math.Min(1.0, -pole.Y)));
            double node = Math.Sin(inclination) > 1e-12 ? Math.Atan2(-pole.X, pole.Z) : 0.0;
            double period = CelestialRules.Shipped.OrbitPeriodDays(orbitSeconds, frame.PeriodDays, frame.TimeScale);
            return new SkyOrbit(period, (float)(inclination * 180.0 / Math.PI), node, 0.0, semiMajorAxisAu, (float)phaseDegrees);
        }

        /// <summary>
        /// Where along its orbit a body is at sky time t, in degrees and unwrapped (CelestialBody.Set): its
        /// epoch plus the planet's orbit period over its own, times t.
        /// </summary>
        public virtual double Anomaly(SkyOrbit orbit, double planetPeriodDays, double simulationTime)
        {
            return orbit.EpochDegrees + planetPeriodDays / orbit.PeriodDays * simulationTime;
        }

        /// <summary>
        /// A round orbit's place at this anomaly, from the body it circles (Orbit.GetLocalPosition with no
        /// eccentricity): the node and the periapsis in radians, the inclination in degrees.
        /// </summary>
        public virtual SkyVector OrbitOffset(SkyOrbit orbit, double anomalyDegrees)
        {
            double mean = anomalyDegrees * (Math.PI / 180.0);
            double trueAnomaly = 2.0 * Math.Atan2(Math.Sin(mean / 2.0), Math.Cos(mean / 2.0));
            double u = trueAnomaly + orbit.PeriapsisRadians;
            double node = orbit.AscendingNodeRadians;
            double inclination = orbit.InclinationDegrees * (Math.PI / 180.0);
            double r = orbit.SemiMajorAxisAu;
            return new SkyVector(
                r * (Math.Cos(node) * Math.Cos(u) - Math.Sin(node) * Math.Sin(u) * Math.Cos(inclination)),
                r * (Math.Sin(u) * Math.Sin(inclination)),
                r * (Math.Sin(node) * Math.Cos(u) + Math.Cos(node) * Math.Sin(u) * Math.Cos(inclination)));
        }

        /// <summary>The direction a comet circling the planet is seen in at sky time t.</summary>
        public SkyVector CometDirection(SkyFrame frame, SkyOrbit orbit, double simulationTime)
        {
            return SkyDirection(frame, simulationTime, -OrbitOffset(orbit, Anomaly(orbit, frame.PeriodDays, simulationTime)));
        }

        // ---- reading the sky ------------------------------------------------------------------------------

        /// <summary>
        /// A direction as a compass bearing, degrees clockwise from north (+z) through east (+x), 0 to 360, and
        /// a height in degrees above the horizon, negative below it. The game's own readout of the sun takes
        /// its horizontal angle the same way (OrbitalSimulation.PrintDebug).
        /// </summary>
        public virtual Bearing BearingOf(SkyVector direction)
        {
            double length = direction.Length;
            if (!(length > 0.0))
            {
                return new Bearing(0.0, 0.0);
            }
            double azimuth = Math.Atan2(direction.X, direction.Z) * 180.0 / Math.PI;
            double height = Math.Asin(Math.Max(-1.0, Math.Min(1.0, direction.Y / length))) * 180.0 / Math.PI;
            return new Bearing(azimuth < 0.0 ? azimuth + 360.0 : azimuth, height);
        }

        /// <summary>
        /// The Ground Telescope's Horizontal and Vertical, in degrees, that look along a bearing, for a
        /// telescope built facing north: Horizontal turns the dish from north through east, and Vertical tips it
        /// down from straight up, 0 overhead and 90 on the horizon (LargeRotatable.SetDishRotation). A dish
        /// cannot look below the horizon.
        /// </summary>
        public virtual TelescopeAim AimFor(Bearing bearing)
        {
            double vertical = 90.0 - bearing.Height;
            return new TelescopeAim(bearing.Azimuth, Math.Max(0.0, Math.Min(90.0, vertical)), bearing.Height >= 0.0);
        }

        /// <summary>
        /// Real seconds from sky time <paramref name="simulationTime"/> until the comet is next at its highest
        /// above the horizon, within <paramref name="withinSeconds"/>; NaN if it is not. Its height is followed
        /// a degree of its orbit at a time, and the top it passes is then found to a millisecond. A top it is
        /// at or has just passed is now, 0.
        /// </summary>
        public virtual double NextHighestSeconds(SkyFrame frame, SkyOrbit orbit, double simulationTime, double withinSeconds)
        {
            double orbitSeconds = CelestialRules.OrbitRealSeconds(orbit.PeriodDays, frame.PeriodDays, frame.TimeScale);
            if (!(orbitSeconds > 0.0) || !(frame.TimeScale > 0.0) || double.IsInfinity(orbitSeconds))
            {
                return double.NaN;
            }
            double step = orbitSeconds / 360.0;
            int steps = (int)Math.Min(100000.0, Math.Ceiling(withinSeconds / step));
            double HeightAt(double seconds) => BearingOf(CometDirection(frame, orbit, simulationTime + seconds * frame.TimeScale)).Height;
            double before = HeightAt(-step);
            double here = HeightAt(0.0);
            for (int k = 0; k <= steps; k++)
            {
                double next = HeightAt((k + 1) * step);
                if (here > before && here >= next && here > 0.0)
                {
                    double low = (k - 1) * step;
                    double high = (k + 1) * step;
                    while (high - low > 1e-3)
                    {
                        double a = low + (high - low) / 3.0;
                        double b = high - (high - low) / 3.0;
                        if (HeightAt(a) < HeightAt(b))
                        {
                            low = a;
                        }
                        else
                        {
                            high = b;
                        }
                    }
                    return Math.Max(0.0, 0.5 * (low + high));
                }
                before = here;
                here = next;
            }
            return double.NaN;
        }

        /// <summary>What the Ground Telescope reads as the True Anomaly of a body at this anomaly: the anomaly less whole turns, its sign kept (CelestialBody.GetWrappedTrueAnomaly).</summary>
        public static double TrueAnomalyReading(double anomalyDegrees) => anomalyDegrees % 360.0;

        /// <summary>What the Ground Telescope reads as the distance in km to a body this far away: the distance held as a float of AU, times the game's AU.</summary>
        public static double DistanceKmReading(double distanceAu) => (float)distanceAu * AuToKm;

        // ---- the check ------------------------------------------------------------------------------------

        private static readonly Lazy<string> ShippedVerdict = new Lazy<string>(() =>
        {
            try
            {
                return Check(Shipped);
            }
            catch (Exception e)
            {
                return "the check itself failed: " + e.Message;
            }
        });

        /// <summary>The shipped rules' own check, run once: null when they hold, else the first case that does not.</summary>
        public static string Verdict => ShippedVerdict.Value;

        /// <summary>
        /// The rules worked through on cases with known answers, worked out by hand from the game's code: the
        /// rotations Unity's way, the scene rotation, the day's angle, an orbit's place and pole, a bearing, a
        /// telescope's angles, and the time to a comet's highest on an orbit whose answer is exact. Returns the
        /// first case that does not hold, or null.
        /// </summary>
        public static string Check(CometSkyRules rules)
        {
            CultureInfo c = CultureInfo.InvariantCulture;

            // ---- Unity's rotations: about x by -90 takes north to up; about y by 90 takes north to east; an
            // axis of any length is a direction; and the right-hand factor of a product turns first.
            if (!Near(SkyRotation.AngleAxis(-90.0, SkyVector.Right).Rotate(SkyVector.Forward), SkyVector.Up, 1e-12)
                || !Near(SkyRotation.AngleAxis(90.0, SkyVector.Up).Rotate(SkyVector.Forward), SkyVector.Right, 1e-12)
                || !Near(SkyRotation.AngleAxis(90.0, new SkyVector(0.0, 2.0, 0.0)).Rotate(SkyVector.Forward), SkyVector.Right, 1e-12)
                || !Near((SkyRotation.AngleAxis(90.0, SkyVector.Up) * SkyRotation.AngleAxis(90.0, SkyVector.Right)).Rotate(SkyVector.Up), SkyVector.Right, 1e-12))
            {
                return "a rotation is not worked as Unity's Quaternion works it";
            }

            // ---- the day's angle: the planet's anomaly through its own day, backwards for a backward turn.
            SkyFrame day = new SkyFrame(SkyVector.Up, 0.0, 0.0, 10.0, 0.5, false, 687.0, 1.0);
            SkyFrame backward = new SkyFrame(SkyVector.Up, 0.0, 0.0, 10.0, 0.5, true, 687.0, 1.0);
            if (Math.Abs(rules.DayAngle(day, 20.0)) > 1e-4 || Math.Abs(rules.DayAngle(day, 20.25) - 180.0) > 1e-4
                || Math.Abs(rules.DayAngle(day, 20.125) - 90.0) > 1e-4 || Math.Abs(rules.DayAngle(backward, 20.125) - 270.0) > 1e-4)
            {
                return "the day's angle is not the planet's anomaly through its own day, counted down for a planet turning backwards";
            }

            // ---- the scene rotation. On the equator at the day's start, with the axis along the frame's y, the
            // rotation is the quarter turn back about x alone: (x, y, z) goes to (x, z, -y), so the frame's z is
            // overhead, its x east and its y to the south.
            SkyFrame equator = new SkyFrame(SkyVector.Up, 0.0, 0.0, 0.0, 1e12, false, 687.0, 1.0);
            SkyRotation still = rules.SceneRotation(equator, 0.0);
            if (!Near(still.Rotate(SkyVector.Forward), SkyVector.Up, 1e-9) || !Near(still.Rotate(SkyVector.Right), SkyVector.Right, 1e-9)
                || !Near(still.Rotate(SkyVector.Up), -SkyVector.Forward, 1e-9))
            {
                return "the scene rotation on the equator at the day's start does not take the frame's z overhead and its y to the south";
            }
            // As the day turns by d (plus the longitude), a body overhead at its start sinks toward the east:
            // its height is 90 - d.
            foreach (var (longitude, epoch, expected) in new[] { (0.0, 0.25 * 60.0 / 360.0, 30.0), (30.0, 0.25 * 30.0 / 360.0, 30.0), (0.0, 0.25 * 135.0 / 360.0, -45.0) })
            {
                SkyFrame turning = new SkyFrame(SkyVector.Up, 0.0, longitude, epoch, 0.25, false, 687.0, 1.0);
                Bearing seen = rules.BearingOf(rules.SkyDirection(turning, 0.0, SkyVector.Forward));
                if (Math.Abs(seen.Height - expected) > 1e-3 || Math.Abs(seen.Azimuth - 90.0) > 1e-3)
                {
                    return string.Format(c, "a body overhead at the day's start, the day {0} degrees on and the longitude {1}, is at height {2:0.###} bearing {3:0.###}, expected {4} in the east",
                        rules.DayAngle(turning, 0.0), longitude, seen.Height, seen.Azimuth, expected);
                }
            }
            // The planet's own axis stands still in the sky whatever the day's angle: an axis (ax, ay, 0) is
            // seen at (ax, ay sin L, -ay cos L), so the pole of the sky's turning stands asin(ay sin L) high, a
            // little under the latitude when the axis leans from the frame's y. Mars's axis and latitude, and
            // Mimas's latitude, north and south.
            SkyVector marsAxis = new SkyVector(0.436, 0.9, 0.0);
            foreach (double latitude in new[] { 13.5, -58.4, 58.4 })
            {
                SkyVector a = marsAxis.Normalized;
                SkyVector expected = new SkyVector(a.X, a.Y * Math.Sin(latitude * Math.PI / 180.0), -a.Y * Math.Cos(latitude * Math.PI / 180.0));
                foreach (double epoch in new[] { 0.0, 0.3, 0.71 })
                {
                    SkyFrame mars = new SkyFrame(marsAxis, latitude, -17.1, epoch, 1.0, false, 687.0, 1.0);
                    SkyVector seen = rules.SceneRotation(mars, 0.0).Rotate(a);
                    if (!Near(seen, expected, 1e-9))
                    {
                        return string.Format(c, "at latitude {0} the planet's axis is seen at ({1:0.####}, {2:0.####}, {3:0.####}), expected ({4:0.####}, {5:0.####}, {6:0.####}) whatever the hour",
                            latitude, seen.X, seen.Y, seen.Z, expected.X, expected.Y, expected.Z);
                    }
                }
            }
            // A body is seen from the planet: the direction is of the planet less the body, so a body at -z
            // from the planet is the one overhead in the case above.
            if (!Near(rules.SkyDirection(equator, 0.0, new SkyVector(0.0, 0.0, 3.0)), SkyVector.Up, 1e-9))
            {
                return "a body's direction is not of the planet less the body, made of unit length";
            }

            // ---- a round orbit's place: the node in radians, the inclination in degrees.
            SkyOrbit polar = new SkyOrbit(1.0, 90.0, Math.PI / 2.0, 0.0, 2.0, 0.0);
            SkyOrbit flat = new SkyOrbit(1.0, 0.0, 0.0, 0.0, 2.0, 0.0);
            if (!Near(rules.OrbitOffset(polar, 0.0), new SkyVector(0.0, 0.0, 2.0), 1e-12) || !Near(rules.OrbitOffset(polar, 90.0), new SkyVector(0.0, 2.0, 0.0), 1e-12)
                || !Near(rules.OrbitOffset(flat, 0.0), new SkyVector(2.0, 0.0, 0.0), 1e-12) || !Near(rules.OrbitOffset(flat, 90.0), new SkyVector(0.0, 0.0, 2.0), 1e-12)
                || !Near(rules.OrbitOffset(flat, 450.0), rules.OrbitOffset(flat, 90.0), 1e-12))
            {
                return "a round orbit's place is not the game's, with its node in radians and its inclination in degrees";
            }
            if (Math.Abs(rules.Anomaly(new SkyOrbit(0.3189, 0.0, 0.0, 0.0, 1.0, 30.0), 687.0, 2.0) - (30.0 + 687.0 / 0.3189 * 2.0)) > 1e-9)
            {
                return "a body's anomaly is not its epoch plus the planet's period over its own times the sky's time";
            }

            // ---- the comet's orbit: its pole the tilt from the planet's axis, whatever the axis.
            SkyFrame straight = new SkyFrame(SkyVector.Up, 13.5, 0.0, 0.0, 1.0, false, 687.0, 4.494e-4);
            SkyOrbit upright = rules.OrbitFor(straight, 372.0, 60.0, 0.0, 1.0);
            if (Math.Abs(upright.InclinationDegrees - 60.0) > 1e-4 || Math.Abs(upright.AscendingNodeRadians - Math.PI / 2.0) > 1e-9
                || Math.Abs(upright.PeriodDays - CelestialRules.Shipped.OrbitPeriodDays(372.0, 687.0, 4.494e-4)) > 1e-15)
            {
                return string.Format(c, "on a planet whose axis is the sky's own a 60 degree tilt is inclination {0:0.####} with its node at {1:0.####} rad, expected 60 and pi/2, and the period is not the one that turns in 372 s",
                    upright.InclinationDegrees, upright.AscendingNodeRadians);
            }
            foreach (SkyVector axis in new[] { marsAxis, new SkyVector(0.4, 1.0, 0.0), new SkyVector(0.045, -0.999, 0.0), new SkyVector(0.0, 1.0, 0.3) })
            {
                foreach (double tilt in new[] { 0.0, 25.0, 60.0, 120.0 })
                {
                    SkyFrame world = new SkyFrame(axis, 20.0, 0.0, 0.0, 1.0, false, 687.0, 4.494e-4);
                    SkyOrbit orbit = rules.OrbitFor(world, 372.0, tilt, 77.0, 1.0);
                    // The pole as the orbit's own places make it, and as the game's elements give it.
                    SkyVector made = rules.OrbitOffset(orbit, 0.0).Cross(rules.OrbitOffset(orbit, 90.0)).Normalized;
                    double i = orbit.InclinationDegrees * Math.PI / 180.0;
                    SkyVector elements = new SkyVector(-Math.Sin(orbit.AscendingNodeRadians) * Math.Sin(i), -Math.Cos(i), Math.Cos(orbit.AscendingNodeRadians) * Math.Sin(i));
                    double angle = SkyVector.AngleDegrees(made, -axis.Normalized);
                    if (!Near(made, elements, 1e-6) || Math.Abs(angle - tilt) > 1e-4 || orbit.EpochDegrees != 77.0)
                    {
                        return string.Format(c, "an orbit tilted {0} from an axis along ({1}, {2}, {3}) has its pole {4:0.####} from it",
                            tilt, axis.X, axis.Y, axis.Z, angle);
                    }
                }
            }
            if (Math.Abs(PhaseDegrees(1) - PhaseStepDegrees) > 1e-12 || Math.Abs(PhaseDegrees(3) - (3.0 * PhaseStepDegrees - 360.0)) > 1e-9)
            {
                return "comets parked together do not start the golden angle apart round their orbit";
            }

            // ---- bearings and the telescope.
            (SkyVector Direction, double Azimuth, double Height)[] bearings =
            {
                (new SkyVector(1.0, 0.0, 0.0), 90.0, 0.0), (new SkyVector(0.0, 0.0, -1.0), 180.0, 0.0), (new SkyVector(-1.0, -1.0, 0.0), 270.0, -45.0),
                (new SkyVector(0.0, 0.0, 3.0), 0.0, 0.0), (new SkyVector(1.0, Math.Sqrt(2.0), 1.0), 45.0, 45.0),
            };
            foreach (var (direction, azimuth, height) in bearings)
            {
                Bearing b = rules.BearingOf(direction);
                if (Math.Abs(b.Azimuth - azimuth) > 1e-9 || Math.Abs(b.Height - height) > 1e-9)
                {
                    return string.Format(c, "the direction ({0}, {1}, {2}) bears {3:0.###} at height {4:0.###}, expected {5} at {6}",
                        direction.X, direction.Y, direction.Z, b.Azimuth, b.Height, azimuth, height);
                }
            }
            if (Math.Abs(rules.BearingOf(SkyVector.Up).Height - 90.0) > 1e-9)
            {
                return "straight up is not at height 90";
            }
            TelescopeAim aim = rules.AimFor(new Bearing(135.0, 30.0));
            TelescopeAim low = rules.AimFor(new Bearing(10.0, -5.0));
            if (Math.Abs(aim.Horizontal - 135.0) > 1e-9 || Math.Abs(aim.Vertical - 60.0) > 1e-9 || !aim.Reachable || low.Reachable || low.Vertical != 90.0)
            {
                return "the telescope's Horizontal is not the bearing, its Vertical not the angle down from straight up, or it is said to look below the horizon";
            }

            // ---- the next highest. A sky that does not turn, with its axis along the frame's y at the pole, is
            // no rotation at all, so a polar orbit of node 0 is seen at -(cos u, sin u, 0): highest, straight up,
            // at u = 270. Once round in 372 s, so from anomaly a the top is (270 - a) / 360 x 372 s away, the next
            // round's once it has passed.
            SkyFrame pole = new SkyFrame(SkyVector.Up, 90.0, 0.0, 0.0, 1e12, false, 1.0, 1.0);
            foreach (var (start, seconds) in new[] { (0.0, 279.0), (200.0, 70.0 / 360.0 * 372.0), (300.0, 330.0 / 360.0 * 372.0), (100.0, 170.0 / 360.0 * 372.0), (270.0, 0.0) })
            {
                SkyOrbit circling = new SkyOrbit(372.0 / 360.0, 90.0, 0.0, 0.0, 1.0, start);
                double found = rules.NextHighestSeconds(pole, circling, 0.0, 1000.0);
                Bearing top = rules.BearingOf(rules.CometDirection(pole, circling, found));
                if (!(Math.Abs(found - seconds) < 0.01) || top.Height < 89.9)
                {
                    return string.Format(c, "a comet {0} degrees round its orbit is next at its highest in {1:0.###} s at height {2:0.###}, expected {3:0.###} s straight up",
                        start, found, top.Height, seconds);
                }
            }
            if (!double.IsNaN(rules.NextHighestSeconds(pole, new SkyOrbit(372.0 / 360.0, 90.0, 0.0, 0.0, 1.0, 0.0), 0.0, 100.0)))
            {
                return "a highest point further away than the search is reported";
            }
            return null;
        }

        private static bool Near(SkyVector a, SkyVector b, double tolerance) =>
            Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance && Math.Abs(a.Z - b.Z) <= tolerance;
    }

    /// <summary>A direction or a place in the game's sky or world frames, x, y and z as Unity's. A plain struct: net472 has no IsExternalInit.</summary>
    public readonly struct SkyVector
    {
        public static readonly SkyVector Right = new SkyVector(1.0, 0.0, 0.0);
        public static readonly SkyVector Up = new SkyVector(0.0, 1.0, 0.0);
        public static readonly SkyVector Forward = new SkyVector(0.0, 0.0, 1.0);

        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public SkyVector(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>Of unit length; a zero vector stays zero.</summary>
        public SkyVector Normalized
        {
            get
            {
                double length = Length;
                return length > 0.0 ? new SkyVector(X / length, Y / length, Z / length) : this;
            }
        }

        public double Dot(SkyVector other) => X * other.X + Y * other.Y + Z * other.Z;

        public SkyVector Cross(SkyVector other) =>
            new SkyVector(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);

        public static SkyVector operator -(SkyVector a) => new SkyVector(-a.X, -a.Y, -a.Z);

        public static SkyVector operator +(SkyVector a, SkyVector b) => new SkyVector(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static SkyVector operator -(SkyVector a, SkyVector b) => new SkyVector(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static SkyVector operator *(SkyVector a, double k) => new SkyVector(a.X * k, a.Y * k, a.Z * k);

        /// <summary>The angle between two directions, in degrees.</summary>
        public static double AngleDegrees(SkyVector a, SkyVector b)
        {
            double lengths = a.Length * b.Length;
            return lengths > 0.0 ? Math.Acos(Math.Max(-1.0, Math.Min(1.0, a.Dot(b) / lengths))) * 180.0 / Math.PI : double.NaN;
        }
    }

    /// <summary>A rotation held as Unity's Quaternion holds one, worked the same way, in doubles.</summary>
    public readonly struct SkyRotation
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;
        public readonly double W;

        public SkyRotation(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>A turn of this many degrees about an axis of any length (Quaternion.AngleAxis); no axis is no turn.</summary>
        public static SkyRotation AngleAxis(double degrees, SkyVector axis)
        {
            double length = axis.Length;
            if (!(length > 1e-6))
            {
                return new SkyRotation(0.0, 0.0, 0.0, 1.0);
            }
            double half = degrees * Math.PI / 360.0;
            double s = Math.Sin(half) / length;
            return new SkyRotation(axis.X * s, axis.Y * s, axis.Z * s, Math.Cos(half));
        }

        /// <summary>The right-hand rotation first, then the left (Quaternion * Quaternion).</summary>
        public static SkyRotation operator *(SkyRotation a, SkyRotation b) => new SkyRotation(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
            a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>A vector turned by this rotation (Quaternion * Vector3).</summary>
        public SkyVector Rotate(SkyVector v)
        {
            double x = X * 2.0, y = Y * 2.0, z = Z * 2.0;
            double xx = X * x, yy = Y * y, zz = Z * z, xy = X * y, xz = X * z, yz = Y * z, wx = W * x, wy = W * y, wz = W * z;
            return new SkyVector(
                (1.0 - (yy + zz)) * v.X + (xy - wz) * v.Y + (xz + wy) * v.Z,
                (xy + wz) * v.X + (1.0 - (xx + zz)) * v.Y + (yz - wx) * v.Z,
                (xz - wy) * v.X + (yz + wx) * v.Y + (1.0 - (xx + yy)) * v.Z);
        }
    }

    /// <summary>
    /// The planet being played, as the game's sky turns it: its axis in the sky's frame
    /// (RotatingCelestialBody.RotationAxis), the latitude and longitude the world puts the player at
    /// (OrbitalSimulation.Latitude, Longitude), its epoch and the share of its orbit a day of its own takes
    /// (LongitudeAtEpoch, SolDegreesPerDay), whether it turns backwards, its orbit's period in Earth days, and
    /// the sky's clock in degrees of that orbit a real second (OrbitalSimulation.TimeScale).
    /// </summary>
    public readonly struct SkyFrame
    {
        public readonly SkyVector Axis;
        public readonly double LatitudeDegrees;
        public readonly double LongitudeDegrees;
        public readonly double EpochDegrees;
        public readonly double SolDegreesPerDay;
        public readonly bool Retrograde;
        public readonly double PeriodDays;
        public readonly double TimeScale;

        public SkyFrame(SkyVector axis, double latitudeDegrees, double longitudeDegrees, double epochDegrees, double solDegreesPerDay, bool retrograde, double periodDays, double timeScale)
        {
            Axis = axis;
            LatitudeDegrees = latitudeDegrees;
            LongitudeDegrees = longitudeDegrees;
            EpochDegrees = epochDegrees;
            SolDegreesPerDay = solDegreesPerDay;
            Retrograde = retrograde;
            PeriodDays = periodDays;
            TimeScale = timeScale;
        }
    }

    /// <summary>
    /// An orbit as the game's Orbit holds one, with no eccentricity: its period in Earth days, its
    /// inclination in degrees and its node and periapsis in radians (as Orbit.GetLocalPosition uses them),
    /// its radius in AU, and where along it the body is at the sky's epoch (LongitudeAtEpoch, degrees).
    /// </summary>
    public readonly struct SkyOrbit
    {
        public readonly double PeriodDays;
        public readonly double InclinationDegrees;
        public readonly double AscendingNodeRadians;
        public readonly double PeriapsisRadians;
        public readonly double SemiMajorAxisAu;
        public readonly double EpochDegrees;

        public SkyOrbit(double periodDays, double inclinationDegrees, double ascendingNodeRadians, double periapsisRadians, double semiMajorAxisAu, double epochDegrees)
        {
            PeriodDays = periodDays;
            InclinationDegrees = inclinationDegrees;
            AscendingNodeRadians = ascendingNodeRadians;
            PeriapsisRadians = periapsisRadians;
            SemiMajorAxisAu = semiMajorAxisAu;
            EpochDegrees = epochDegrees;
        }

        public bool SameAs(SkyOrbit other) =>
            PeriodDays.Equals(other.PeriodDays) && InclinationDegrees.Equals(other.InclinationDegrees) && AscendingNodeRadians.Equals(other.AscendingNodeRadians)
            && PeriapsisRadians.Equals(other.PeriapsisRadians) && SemiMajorAxisAu.Equals(other.SemiMajorAxisAu) && EpochDegrees.Equals(other.EpochDegrees);
    }

    /// <summary>A compass bearing, degrees clockwise from north, and a height above the horizon, degrees.</summary>
    public readonly struct Bearing
    {
        public readonly double Azimuth;
        public readonly double Height;

        public Bearing(double azimuth, double height)
        {
            Azimuth = azimuth;
            Height = height;
        }
    }

    /// <summary>The Ground Telescope's Horizontal and Vertical, degrees, and whether the dish can look that way at all.</summary>
    public readonly struct TelescopeAim
    {
        public readonly double Horizontal;
        public readonly double Vertical;
        public readonly bool Reachable;

        public TelescopeAim(double horizontal, double vertical, bool reachable)
        {
            Horizontal = horizontal;
            Vertical = vertical;
            Reachable = reachable;
        }
    }
}
