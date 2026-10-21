using System;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Every number an impact is judged by, in one place (docs/COMET.md sections 2, 3, 5 and 8): how
    /// big a comet is drawn, how far from anyone it must land, the pressure wave and what it does to
    /// structures and to a player outdoors, and how dark the sky goes afterwards. Plain arithmetic with
    /// no game or Unity calls, so tools/PatchCheck runs <see cref="Check(CometRules)"/> outside the
    /// game. The parts are virtual so PatchCheck can hand the check wrong rules and see each caught.
    ///
    /// This is a game rule, not a physical model: the comet's size sets the damage, and the planet's
    /// air does not enter into it.
    /// </summary>
    public class CometRules
    {
        public static readonly CometRules Shipped = new CometRules();

        /// <summary>Metres a second, for the sound and the wave alike.</summary>
        public const double SpeedOfSound = 340.0;

        public const double SafeBaseMetres = 1500.0;
        public const double SafeMetresPerScale = 500.0;

        /// <summary>The wave of a comet this size, at this distance, peaks at this overpressure.</summary>
        public const double ReferenceMoles = 1e7;
        public const double ReferenceMetres = 2000.0;
        public const double ReferencePeakKPa = 400.0;

        /// <summary>
        /// The wave stops growing where the show stops growing: a comet past this many moles is drawn,
        /// placed and felt as one of exactly this many, so the safe distance still holds every
        /// reinforced window and wall in one piece.
        /// </summary>
        public const double WaveMolesCap = 1e9;

        /// <summary>The wave is worked out no closer than this, so a point at the impact is finite.</summary>
        public const double NearestMetres = 100.0;

        /// <summary>The game's own share of a limit past which a structure is stressed (Thing.StressedRatio).</summary>
        public const double StressRatio = 0.8;

        /// <summary>A player outdoors is thrown a bit above this peak, and hard above <see cref="ThrownHardKPa"/>.</summary>
        public const double ThrownKPa = 50.0;
        public const double ThrownHardKPa = 200.0;

        /// <summary>Where the hardest push reaches its full strength.</summary>
        public const double FullForceKPa = 2000.0;

        /// <summary>The game caps a player's speed here without a jetpack (DynamicThing.MaxMovementSpeed).</summary>
        public const double MaxPlayerSpeed = 20.0;

        /// <summary>
        /// The impulse handed to the game's own collision damage for a hard push. The game scales it by
        /// the suit (3 for every suit built on SuitBase) and passes up to 6 times that through as stun
        /// (the space suit), so the worst case is 18 stun per unit; 100 stun knocks a player out.
        /// </summary>
        public const double SuitImpulseFrom = 2.0;
        public const double SuitImpulseTo = 4.0;
        public const double WorstStunPerImpulse = 18.0;
        public const double KnockoutStun = 100.0;

        public const double SkyDimSmallest = 0.15;
        public const double SkyDimLargest = 0.55;
        public const double SkyFallSeconds = 30.0;
        public const double SkyRecoverSeconds = 300.0;

        /// <summary>The camera shake the game accepts at most (CameraController.SetCameraShake).</summary>
        public const double MaxShake = 2.5;

        /// <summary>
        /// How big the show is drawn, from how much the comet brings, on a log scale: 0.5 at 10,000
        /// mol, 1 at 100,000, 1.5 at a million, 2 at ten million, 3 from a billion up, never below 0.4.
        /// </summary>
        public virtual double Scale(double moles)
        {
            return Math.Max(0.4, Math.Min(3.0, (Math.Log10(Math.Max(1.0, moles)) - 3.0) / 2.0));
        }

        /// <summary>How close to a structure or a player a comet of this scale may land, in metres.</summary>
        public virtual double SafeDistance(double scale)
        {
            return SafeBaseMetres + SafeMetresPerScale * scale;
        }

        /// <summary>
        /// The wave's peak overpressure in kPa <paramref name="metres"/> from the impact:
        /// 400 x (moles / 1e7)^(2/3) x (2000 / d)^2.
        /// </summary>
        public virtual double PeakKPa(double moles, double metres)
        {
            double felt = Math.Min(Math.Max(0.0, moles), WaveMolesCap);
            double d = Math.Max(NearestMetres, metres);
            double ratio = ReferenceMetres / d;
            return ReferencePeakKPa * Math.Pow(felt / ReferenceMoles, 2.0 / 3.0) * ratio * ratio;
        }

        /// <summary>Seconds from the impact until the sound and the wave reach a point this far away.</summary>
        public double ArrivalSeconds(double metres) => Math.Max(0.0, metres) / SpeedOfSound;

        /// <summary>Whether a structure with this limit, in kPa, breaks under this peak.</summary>
        public bool Breaks(double peakKPa, double limitKPa) => limitKPa > 0.0 && peakKPa > limitKPa;

        /// <summary>Whether it holds but creaks: past the game's stressed share of its limit.</summary>
        public bool Creaks(double peakKPa, double limitKPa) => limitKPa > 0.0 && peakKPa > StressRatio * limitKPa && peakKPa <= limitKPa;

        /// <summary>
        /// What the wave does to a player outdoors: a velocity change away from the impact and upward,
        /// and for the hardest band an impulse for the game's own suit damage. The upward part is what
        /// gets a standing player off the ground, where the game halves their speed every step.
        /// </summary>
        public virtual Push PushFor(double peakKPa)
        {
            if (!(peakKPa > 0.0))
            {
                return new Push(0.0, 0.0, 0.0, 0);
            }
            if (peakKPa < ThrownKPa)
            {
                double t = peakKPa / ThrownKPa;
                return new Push(Lerp(1.0, 4.0, t), Lerp(0.5, 1.5, t), 0.0, 1);
            }
            if (peakKPa <= ThrownHardKPa)
            {
                double t = (peakKPa - ThrownKPa) / (ThrownHardKPa - ThrownKPa);
                return new Push(Lerp(6.0, 12.0, t), Lerp(3.0, 5.0, t), 0.0, 2);
            }
            double hard = Clamp01(Math.Log10(peakKPa / ThrownHardKPa) / Math.Log10(FullForceKPa / ThrownHardKPa));
            double up = Lerp(6.0, 8.0, hard);
            // Held where the most upward push still leaves the whole within the game's cap.
            double away = Math.Min(Lerp(14.0, 20.0, hard), Math.Sqrt(MaxPlayerSpeed * MaxPlayerSpeed - 8.0 * 8.0));
            return new Push(away, up, Lerp(SuitImpulseFrom, SuitImpulseTo, hard), 3);
        }

        /// <summary>The camera shake when the wave reaches the player, indoors or out.</summary>
        public virtual double Shake(double peakKPa)
        {
            if (!(peakKPa > 0.0))
            {
                return 0.0;
            }
            return Math.Min(MaxShake, 0.4 + 0.7 * Math.Log10(1.0 + peakKPa / 5.0));
        }

        /// <summary>
        /// How much dimmer the sky, the ambient light and the sun are, 0 to 1, this long after the
        /// impact: down to a depth set by the comet's size over the first 30 s, back over 5 minutes.
        /// </summary>
        public virtual double SkyDim(double scale, double secondsSinceImpact)
        {
            if (!(secondsSinceImpact > 0.0))
            {
                return 0.0;
            }
            double depth = Lerp(SkyDimSmallest, SkyDimLargest, Clamp01((scale - 1.0) / 2.0));
            if (secondsSinceImpact <= SkyFallSeconds)
            {
                return depth * SmoothStep(secondsSinceImpact / SkyFallSeconds);
            }
            return depth * (1.0 - SmoothStep((secondsSinceImpact - SkyFallSeconds) / SkyRecoverSeconds));
        }

        /// <summary>Seconds after the impact when the sky is back to what it was.</summary>
        public static double SkyClearSeconds => SkyFallSeconds + SkyRecoverSeconds;

        private static double Lerp(double from, double to, double t) => from + (to - from) * Clamp01(t);

        private static double Clamp01(double t) => t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;

        private static double SmoothStep(double t)
        {
            t = Clamp01(t);
            return t * t * (3.0 - 2.0 * t);
        }

        private static readonly Lazy<string> ShippedVerdict = new Lazy<string>(() =>
        {
            try
            {
                return Check(Shipped) ?? CometYield.Verdict;
            }
            catch (Exception e)
            {
                return "the check itself failed: " + e.Message;
            }
        });

        /// <summary>
        /// The shipped rules' own check, and the show's sizes from the yield the wave implies
        /// (<see cref="CometYield"/>), run once: null when they hold, else the first case that does not.
        /// </summary>
        public static string Verdict => ShippedVerdict.Value;

        /// <summary>
        /// docs/COMET.md's tables worked through, and the rules swept for the shape they must have.
        /// Returns the first case that does not hold, or null.
        /// </summary>
        public static string Check(CometRules rules)
        {
            CultureInfo c = CultureInfo.InvariantCulture;

            (double Moles, double Scale)[] scales = { (1.0, 0.4), (1e4, 0.5), (1e5, 1.0), (1e6, 1.5), (1e7, 2.0), (1e8, 2.5), (1e9, 3.0), (1e12, 3.0) };
            foreach (var (moles, scale) in scales)
            {
                double got = rules.Scale(moles);
                if (Math.Abs(got - scale) > 1e-9)
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol is drawn at scale {1}, expected {2}", moles, got, scale);
                }
            }

            // Section 3's table: the safe distance, the peak there, and the weakest and strongest
            // limits (kPa) that break and hold there. 0 is "nothing breaks".
            (double Moles, double Safe, double Peak, double Breaks, double Holds)[] table =
            {
                (1e4, 1750.0, double.NaN, 0.0, 200.0),
                (1e5, 2000.0, 19.0, 0.0, 200.0),
                (1e6, 2250.0, 68.0, 0.0, 200.0),
                (1e7, 2500.0, 256.0, 200.0, 300.0),
                (1e8, 2750.0, 982.0, 500.0, 1000.0),
                (1e9, 3000.0, 3830.0, 1000.0, 8000.0),
                (1e12, 3000.0, 3830.0, 1000.0, 8000.0),
            };
            foreach (var (moles, safe, peak, breaks, holds) in table)
            {
                double distance = rules.SafeDistance(rules.Scale(moles));
                if (Math.Abs(distance - safe) > 1e-9)
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol must land {1} m from anyone, expected {2} m", moles, distance, safe);
                }
                double at = rules.PeakKPa(moles, distance);
                if (!double.IsNaN(peak) && Math.Abs(at - peak) > 0.5)
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol peaks at {1:0.###} kPa at {2} m, expected {3} kPa", moles, at, distance, peak);
                }
                if (breaks > 0.0 && !rules.Breaks(at, breaks))
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol does not break a {1} kPa structure at {2} m", moles, breaks, distance);
                }
                if (rules.Breaks(at, holds))
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol breaks a {1} kPa structure at {2} m, which must hold", moles, holds, distance);
                }
            }
            if (Math.Abs(rules.PeakKPa(1e7, 4000.0) * 4.0 - rules.PeakKPa(1e7, 2000.0)) > 1e-9)
            {
                return "the peak does not fall with the square of the distance";
            }
            if (double.IsInfinity(rules.PeakKPa(1e9, 0.0)) || double.IsNaN(rules.PeakKPa(1e9, 0.0)))
            {
                return "the peak at the impact itself is not finite";
            }
            if (Math.Abs(rules.ArrivalSeconds(3400.0) - 10.0) > 1e-9)
            {
                return "the wave does not travel at 340 m/s";
            }
            if (!rules.Creaks(170.0, 200.0) || rules.Creaks(150.0, 200.0) || rules.Creaks(210.0, 200.0) || rules.Breaks(200.0, 200.0) || rules.Breaks(1e6, -1.0))
            {
                return "a structure creaks past 80 % of its limit and breaks past all of it, and one with no limit never breaks";
            }

            // Section 5's bands, swept.
            Push previous = rules.PushFor(0.01);
            for (double kpa = 0.01; kpa < 1e5; kpa *= 1.05)
            {
                Push push = rules.PushFor(kpa);
                double total = Math.Sqrt(push.Away * push.Away + push.Up * push.Up);
                string band = kpa < ThrownKPa ? "below 50 kPa" : kpa <= ThrownHardKPa ? "50 to 200 kPa" : "above 200 kPa";
                bool fits = kpa < ThrownKPa
                    ? push.Band == 1 && push.Away >= 1.0 && push.Away <= 4.0 && push.Up > 0.0 && push.Up < push.Away && push.SuitImpulse == 0.0
                    : kpa <= ThrownHardKPa
                        ? push.Band == 2 && push.Away >= 6.0 && push.Away <= 12.0 && push.Up >= 3.0 && push.Up <= 5.0 && push.SuitImpulse == 0.0
                        : push.Band == 3 && push.Away >= 14.0 && push.Up >= 6.0 && push.Up <= 8.0 && push.SuitImpulse >= SuitImpulseFrom && push.SuitImpulse <= SuitImpulseTo;
                if (!fits)
                {
                    return string.Format(c, "at {0:0.###} kPa ({1}) the push is {2:0.###} m/s away, {3:0.###} up, suit impulse {4:0.###}, band {5}",
                        kpa, band, push.Away, push.Up, push.SuitImpulse, push.Band);
                }
                if (total > MaxPlayerSpeed + 1e-9)
                {
                    return string.Format(c, "at {0:0.###} kPa a player is pushed at {1:0.###} m/s, past the game's cap of 20", kpa, total);
                }
                if (push.Band == previous.Band && (push.Away < previous.Away - 1e-9 || push.Up < previous.Up - 1e-9 || push.SuitImpulse < previous.SuitImpulse - 1e-9))
                {
                    return string.Format(c, "within a band the push must grow with the peak; at {0:0.###} kPa it shrank", kpa);
                }
                if (push.SuitImpulse * WorstStunPerImpulse >= KnockoutStun)
                {
                    return string.Format(c, "at {0:0.###} kPa the suit damage can knock a player out", kpa);
                }
                double shake = rules.Shake(kpa);
                if (shake < 0.0 || shake > MaxShake || shake < rules.Shake(kpa / 1.05) - 1e-9)
                {
                    return string.Format(c, "at {0:0.###} kPa the camera shake is {1:0.###}", kpa, shake);
                }
                previous = push;
            }
            if (rules.PushFor(0.0).Away != 0.0 || rules.PushFor(double.NaN).Away != 0.0)
            {
                return "no wave pushes nobody";
            }

            // Section 8: the darker sky.
            foreach (var (scale, depth) in new[] { (0.4, SkyDimSmallest), (1.0, SkyDimSmallest), (3.0, SkyDimLargest) })
            {
                double deepest = rules.SkyDim(scale, SkyFallSeconds);
                if (Math.Abs(deepest - depth) > 1e-9)
                {
                    return string.Format(c, "at scale {0} the sky dims to {1:0.###}, expected {2}", scale, deepest, depth);
                }
                if (rules.SkyDim(scale, 0.0) != 0.0 || Math.Abs(rules.SkyDim(scale, SkyClearSeconds)) > 1e-9 || rules.SkyDim(scale, SkyClearSeconds + 60.0) != 0.0)
                {
                    return string.Format(c, "at scale {0} the sky is dimmed before the impact or after it has cleared", scale);
                }
                double last = 0.0;
                for (double s = 0.5; s <= SkyClearSeconds + 1.0; s += 0.5)
                {
                    double dim = rules.SkyDim(scale, s);
                    bool falling = s <= SkyFallSeconds;
                    if (dim < -1e-12 || dim > SkyDimLargest + 1e-12 || (falling ? dim < last - 1e-12 : dim > last + 1e-12))
                    {
                        return string.Format(c, "at scale {0}, {1} s after the impact the sky dims {2:0.####}", scale, s, dim);
                    }
                    last = dim;
                }
            }
            return null;
        }

        /// <summary>What the wave does to one player. A plain struct: net472 has no IsExternalInit.</summary>
        public readonly struct Push
        {
            /// <summary>Velocity change away from the impact, metres a second.</summary>
            public readonly double Away;

            /// <summary>Velocity change upward, metres a second.</summary>
            public readonly double Up;

            /// <summary>The impulse for the game's own collision damage; 0 for none.</summary>
            public readonly double SuitImpulse;

            /// <summary>0 for no wave, then 1 to 3 from the gentlest band.</summary>
            public readonly int Band;

            public Push(double away, double up, double suitImpulse, int band)
            {
                Away = away;
                Up = up;
                SuitImpulse = suitImpulse;
                Band = band;
            }
        }
    }
}
