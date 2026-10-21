using System;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// How big an impact is drawn, from the explosion its pressure wave implies (docs/COMET.md, "Size
    /// from yield"). The wave rule (<see cref="CometRules.PeakKPa"/>) depends on the moles and the
    /// distance only through d / n^(1/3), which is cube-root blast scaling with a yield proportional
    /// to the moles; matched to the Kinney and Graham blast curve it is a yield of
    /// <see cref="KilotonsPerMole"/> a mole. Every size and time the show draws then follows from that
    /// yield by the nuclear-test scaling laws, or from the damage zones and the wave themselves, so
    /// nothing drawn disagrees with what the impact does.
    ///
    /// Plain arithmetic with no game or Unity calls, so tools/PatchCheck runs
    /// <see cref="Check(CometYield)"/> outside the game. The parts are virtual so PatchCheck can hand
    /// the check wrong rules and see each caught.
    ///
    /// Like the wave, the yield ignores the planet's air: the same comet is drawn the same on Mars and
    /// on Venus.
    /// </summary>
    public class CometYield
    {
        public static readonly CometYield Shipped = new CometYield();

        /// <summary>
        /// Kilotons of yield a mole: the least-squares match, in the logarithm of the peak, of the wave
        /// rule to <see cref="ReferencePeakKPa"/> wherever the rule is between the weakest limit that
        /// breaks (200 kPa) and the strongest wall (10,000 kPa), from 500 m to 20 km, for 1e5 to 1e9
        /// mol. A million moles is 219 kt; ten million, 2.19 Mt.
        /// </summary>
        public const double KilotonsPerMole = 2.19e-4;

        /// <summary>Sea-level air, the pressure the reference blast curve is written at.</summary>
        public const double SeaLevelKPa = 101.325;

        public const double MetresPerFoot = 0.3048;

        /// <summary>The maximum fireball radius of a 1 kt burst in free air, Glasstone and Dolan: 180 W^0.4 ft.</summary>
        public const double FireballFeetAt1Kt = 180.0;

        public const double FireballExponent = 0.4;

        /// <summary>The time of the thermal pulse's peak, Glasstone and Dolan: 0.0417 W^0.44 s.</summary>
        public const double ThermalPeakSecondsAt1Kt = 0.0417;

        public const double ThermalPeakExponent = 0.44;

        /// <summary>The fireball is at its full size, and the thermal pulse over, by this many pulse peaks.</summary>
        public const double FireballGrowthPeaks = 10.0;

        /// <summary>Cloud top at 1 Mt, fitted through Ivy Mike, Castle Bravo and Tsar Bomba.</summary>
        public const double CloudTopMetresAt1Mt = 22500.0;

        /// <summary>A buoyant thermal in a stratified atmosphere rises as its energy to the 1/4 (Morton, Taylor and Turner 1956).</summary>
        public const double CloudTopExponent = 0.25;

        /// <summary>
        /// The cloud's top approaches its height as 1 - e^(-t / this): 33 % at a minute (Castle Bravo
        /// was at 14 of 40 km, 35 %), 45 % at 90 s (Ivy Mike at 17 of 41 km, 41 %). The same for every yield, as the
        /// stratified thermal's rise time depends on the air's stability, not on the energy.
        /// </summary>
        public const double RiseSeconds = 150.0;

        /// <summary>The cap's width over the cloud's height: Tsar Bomba's 95 km across a cloud about 65 km high.</summary>
        public const double CapWidthPerTop = 1.5;

        /// <summary>
        /// The stem widens with height as a buoyant plume does, b = (6/5) alpha z with the entrainment
        /// constant alpha = 0.1 (Morton, Taylor and Turner 1956): a half-angle of 6.8 degrees.
        /// </summary>
        public const double StemSpread = 0.12;

        /// <summary>The comet whose head and flash the 0.16 show drew as they stay: ten million moles.</summary>
        public const double HeadReferenceMoles = 1e7;

        /// <summary>The 0.16 show's head scale at <see cref="HeadReferenceMoles"/>.</summary>
        public const double HeadReferenceScale = 2.0;

        /// <summary>The flash's brightness is stated against its reference comet seen from its safe distance.</summary>
        public const double FlashReferenceMetres = 2500.0;

        /// <summary>
        /// The yield the wave of a comet this size implies, in kilotons: proportional to the moles, so a
        /// tenth of the moles is a tenth of the yield; no further past <see cref="CometRules.WaveMolesCap"/>,
        /// where the wave stops growing.
        /// </summary>
        public virtual double Kilotons(double moles)
        {
            return KilotonsPerMole * Math.Min(Math.Max(0.0, moles), CometRules.WaveMolesCap);
        }

        /// <summary>
        /// The reference blast curve: the peak overpressure, kPa, of a surface burst of this many
        /// kilotons at this distance. Kinney and Graham's free-air TNT curve (Explosive Shocks in Air,
        /// 1985) at Z = d / (W kg)^(1/3); a nuclear surface burst of W is a free-air TNT burst of W
        /// (half of a nuclear yield goes to blast, Glasstone and Dolan 1.22, and the ground reflects it,
        /// doubling it, 3.34).
        /// </summary>
        public static double ReferencePeakKPa(double kilotons, double metres)
        {
            if (!(kilotons > 0.0) || !(metres > 0.0))
            {
                return kilotons > 0.0 ? double.PositiveInfinity : 0.0;
            }
            double z = metres / Math.Pow(kilotons * 1e6, 1.0 / 3.0);
            double numerator = 808.0 * (1.0 + Sq(z / 4.5));
            double denominator = Math.Sqrt((1.0 + Sq(z / 0.048)) * (1.0 + Sq(z / 0.32)) * (1.0 + Sq(z / 1.35)));
            return SeaLevelKPa * numerator / denominator;
        }

        /// <summary>
        /// The fireball's maximum radius by Glasstone and Dolan's 180 W^0.4 ft in free air, with a
        /// surface burst as twice the yield (its hemisphere holds the energy of a sphere of twice it).
        /// </summary>
        public virtual double FireballLawMetres(double kilotons)
        {
            return FireballFeetAt1Kt * MetresPerFoot * Math.Pow(2.0 * Math.Max(0.0, kilotons), FireballExponent);
        }

        /// <summary>
        /// The fireball drawn: the law's, and never smaller than the damage's fireball zone, so no
        /// heat damage is done outside what is drawn burning.
        /// </summary>
        public virtual double DrawnFireballMetres(double kilotons, double fireballZoneMetres)
        {
            return Math.Max(FireballLawMetres(kilotons), Math.Max(0.0, fireballZoneMetres));
        }

        /// <summary>Seconds to the thermal pulse's peak, Glasstone and Dolan's t_max = 0.0417 W^0.44.</summary>
        public virtual double ThermalPeakSeconds(double kilotons)
        {
            return ThermalPeakSecondsAt1Kt * Math.Pow(Math.Max(0.0, kilotons), ThermalPeakExponent);
        }

        /// <summary>
        /// The thermal pulse's power over its peak, at <paramref name="peaks"/> times t_max:
        /// 2 x^2 / (1 + x^4), 1 at the peak; about 90 % of its energy is out by 10 t_max.
        /// </summary>
        public static double Pulse(double peaks)
        {
            if (!(peaks > 0.0))
            {
                return 0.0;
            }
            double x2 = peaks * peaks;
            return 2.0 * x2 / (1.0 + x2 * x2);
        }

        /// <summary>
        /// The thermal pulse's peak irradiance this far away, against the reference comet's at
        /// <see cref="FlashReferenceMetres"/>: the energy over the pulse's length, W / t_max, which goes
        /// as W^0.56, over the square of the distance.
        /// </summary>
        public virtual double FlashIrradiance(double kilotons, double metres)
        {
            double reference = Kilotons(HeadReferenceMoles);
            double peak = ThermalPeakSeconds(kilotons);
            if (!(kilotons > 0.0) || !(peak > 0.0))
            {
                return 0.0;
            }
            double d = Math.Max(CometRules.NearestMetres, metres);
            return kilotons / peak / (reference / ThermalPeakSeconds(reference)) * Sq(FlashReferenceMetres / d);
        }

        /// <summary>The height the cloud's top reaches, in metres: 22.5 km x (W / 1 Mt)^(1/4).</summary>
        public virtual double CloudTopMetres(double kilotons)
        {
            return CloudTopMetresAt1Mt * Math.Pow(Math.Max(0.0, kilotons) / 1000.0, CloudTopExponent);
        }

        /// <summary>The share of its height the cloud's top has risen to this long after the impact.</summary>
        public static double RiseShare(double seconds)
        {
            return seconds > 0.0 ? 1.0 - Math.Exp(-seconds / RiseSeconds) : 0.0;
        }

        /// <summary>
        /// Seconds after the impact the show lasts: until the cloud is at 90 % of its height (the
        /// same for every yield), and never past the darker sky's clearing, which the event lasts
        /// for already.
        /// </summary>
        public virtual double ShowSeconds()
        {
            return Math.Min(RiseSeconds * Math.Log(10.0), CometRules.SkyClearSeconds);
        }

        /// <summary>
        /// How far the ground dust is thrown out along the ground: as far as the wave throws a player
        /// (<see cref="CometRules.ThrownKPa"/>), the wave rule solved for the distance.
        /// </summary>
        public virtual double SurgeMetres(double moles)
        {
            double felt = Math.Min(Math.Max(0.0, moles), CometRules.WaveMolesCap);
            return CometRules.ReferenceMetres * Math.Sqrt(CometRules.ReferencePeakKPa * Math.Pow(felt / CometRules.ReferenceMoles, 2.0 / 3.0) / CometRules.ThrownKPa);
        }

        /// <summary>
        /// The launch speed that throws a rock exactly <paramref name="rangeMetres"/> at 45 degrees
        /// under this gravity, v = sqrt(g R), no air drag: no faster throw lands outside the range.
        /// </summary>
        public static double LaunchSpeed(double rangeMetres, double gravity)
        {
            return Math.Sqrt(Math.Max(0.0, gravity) * Math.Max(0.0, rangeMetres));
        }

        /// <summary>
        /// The head's size, as the 0.16 show's scale: 2 at ten million moles, growing with the body's
        /// diameter, the cube root of its mass.
        /// </summary>
        public virtual double HeadScale(double moles)
        {
            double felt = Math.Min(Math.Max(0.0, moles), CometRules.WaveMolesCap);
            return HeadReferenceScale * Math.Pow(felt / HeadReferenceMoles, 1.0 / 3.0);
        }

        /// <summary>
        /// The head's light against the reference comet's: a meteor's light is its kinetic energy
        /// shed per second, which goes as the area it ablates over, the 2/3 power of its mass.
        /// </summary>
        public virtual double HeadLight(double moles)
        {
            double felt = Math.Min(Math.Max(0.0, moles), CometRules.WaveMolesCap);
            return Math.Pow(felt / HeadReferenceMoles, 2.0 / 3.0);
        }

        /// <summary>Every dimension of one impact's show, from its moles and the damage zones in force.</summary>
        public CometSizes For(double moles, double fireballZoneMetres, double debrisZoneMetres)
        {
            double kt = Kilotons(moles);
            double fireball = DrawnFireballMetres(kt, fireballZoneMetres);
            double top = Math.Max(fireball, CloudTopMetres(kt));
            return new CometSizes(moles, kt, FireballLawMetres(kt), Math.Max(0.0, fireballZoneMetres), fireball,
                FireballGrowthPeaks * ThermalPeakSeconds(kt), ThermalPeakSeconds(kt), top,
                CapWidthPerTop * top / 2.0, SurgeMetres(moles), Math.Max(0.0, debrisZoneMetres), ShowSeconds(),
                HeadScale(moles), HeadLight(moles));
        }

        /// <summary>The zones in force for a comet this size, by the world's Celestial terraforming settings.</summary>
        public CometSizes For(double moles, CelestialTuning tuning)
        {
            double groundZero = CelestialRules.Shipped.GroundZeroMetres(moles, tuning.GroundZeroMetresAt1e6);
            return For(moles, groundZero * tuning.FireballRadiusFactor, groundZero * tuning.DebrisRadiusFactor);
        }

        /// <summary>How far out from the impact anything of the show is drawn, at most.</summary>
        public static double BoundsRadius(CometSizes sizes)
        {
            return Math.Max(1.6 * sizes.CapRadiusMetres, Math.Max(1.2 * sizes.SurgeMetres, 1.05 * sizes.DebrisMetres));
        }

        /// <summary>How high above the impact anything of the show is drawn, at most.</summary>
        public static double BoundsHeight(CometSizes sizes)
        {
            return sizes.CloudTopMetres + 0.6 * sizes.CapRadiusMetres;
        }

        /// <summary>
        /// The camera's far clip that keeps the whole show in view from <paramref name="eyeMetres"/>
        /// from the impact across the ground, with the impact drawn <paramref name="dropMetres"/> lower
        /// for that camera: past the farthest corner of the show's bounds, with 5 % to spare.
        /// </summary>
        public virtual double FarClipMetres(double eyeMetres, double dropMetres, CometSizes sizes)
        {
            double across = Math.Max(0.0, eyeMetres) + BoundsRadius(sizes);
            double up = BoundsHeight(sizes) + Math.Abs(dropMetres);
            return 1.05 * Math.Sqrt(across * across + up * up);
        }

        private static double Sq(double x) => x * x;

        private static readonly Lazy<string> ShippedVerdict = new Lazy<string>(() =>
        {
            try
            {
                return Check(Shipped);
            }
            catch (Exception e)
            {
                return "the yield check itself failed: " + e.Message;
            }
        });

        /// <summary>The shipped rule's own check, run once: null when it holds, else the first case that does not.</summary>
        public static string Verdict => ShippedVerdict.Value;

        /// <summary>
        /// The yield refitted to the wave and checked against the one in force, the laws against their
        /// sources, and the show against the damage and the camera. Returns the first case that does
        /// not hold, or null.
        /// </summary>
        public static string Check(CometYield yield)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            CometRules wave = CometRules.Shipped;

            // ---- the yield: proportional to the moles, as the wave's cube-root form demands, and capped with it.
            foreach (var (from, to) in new[] { (1e5, 1e6), (1e6, 4e6), (1e7, 1e8), (3e7, 9e7), (1.0, 1e9) })
            {
                double ratio = yield.Kilotons(to) / yield.Kilotons(from);
                if (!(Math.Abs(ratio / (to / from) - 1.0) < 1e-9))
                {
                    return string.Format(c, "the yield of {0:0.#E+0} mol is {1:0.####} times that of {2:0.#E+0} mol, not {3:0.####}: the wave depends on d / n^(1/3), which makes the yield proportional to the moles",
                        to, ratio, from, to / from);
                }
            }
            if (yield.Kilotons(1e12) != yield.Kilotons(CometRules.WaveMolesCap) || yield.Kilotons(0.0) != 0.0)
            {
                return "the yield does not stop growing where the wave does, or a comet of nothing has a yield";
            }

            // ---- the fit: the least-squares yield a mole over the wave's damaging range, refitted here.
            double fitted = FitKilotonsPerMole(wave);
            foreach (double moles in new[] { 1e5, 1e6, 1e7, 3.6e7, 1e8, 1e9 })
            {
                double got = yield.Kilotons(moles) / moles;
                if (!(Math.Abs(got / fitted - 1.0) < 0.01))
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol is given {1:0.####E+0} kt a mole, but the wave fits {2:0.####E+0}", moles, got, fitted);
                }
                double worst = 0.0;
                double sum = 0.0;
                int n = 0;
                foreach (double d in Distances())
                {
                    double peak = wave.PeakKPa(moles, d);
                    if (peak < FitLowKPa || peak > FitHighKPa)
                    {
                        continue;
                    }
                    double e = Math.Log(peak / ReferencePeakKPa(yield.Kilotons(moles), d));
                    worst = Math.Max(worst, Math.Abs(e));
                    sum += e * e;
                    n++;
                }
                if (n == 0 || Math.Exp(Math.Sqrt(sum / n)) > 1.15 || Math.Exp(worst) > 1.2)
                {
                    return string.Format(c, "at {0:0.#E+0} mol the wave and the blast curve of its yield differ by x{1:0.###} on average and x{2:0.###} at worst over 200 to 10,000 kPa",
                        moles, n == 0 ? double.NaN : Math.Exp(Math.Sqrt(sum / n)), Math.Exp(worst));
                }
            }

            // ---- the laws, against their sources.
            double airMegaton = yield.FireballLawMetres(500.0) / MetresPerFoot;
            if (Math.Abs(airMegaton / 2850.0 - 1.0) > 0.01)
            {
                return string.Format(c, "a 1 Mt free-air fireball is {0:N0} ft across in radius, not Glasstone and Dolan's 2,850 (5,700 ft across)", airMegaton);
            }
            if (Math.Abs(yield.FireballLawMetres(2000.0) / yield.FireballLawMetres(1000.0) - Math.Pow(2.0, FireballExponent)) > 1e-9)
            {
                return "the fireball does not grow as the 0.4 power of the yield";
            }
            double megatonPeak = yield.ThermalPeakSeconds(1000.0);
            if (Math.Abs(megatonPeak - 0.87) > 0.01 || Math.Abs(FireballGrowthPeaks * megatonPeak / 10.0 - 1.0) > 0.15
                || Math.Abs(yield.ThermalPeakSeconds(2000.0) / megatonPeak - Math.Pow(2.0, ThermalPeakExponent)) > 1e-9)
            {
                return string.Format(c, "a 1 Mt thermal pulse peaks at {0:0.###} s; Glasstone and Dolan: 0.87 s as 0.0417 W^0.44, and the fireball full grown about 10 s after", megatonPeak);
            }
            foreach (var (name, megatons, topKm) in new[] { ("Ivy Mike", 10.4, 41.0), ("Castle Bravo", 15.0, 40.0), ("Tsar Bomba", 50.0, 65.0) })
            {
                double got = yield.CloudTopMetres(megatons * 1000.0) / 1000.0;
                if (Math.Abs(got / topKm - 1.0) > 0.12)
                {
                    return string.Format(c, "{0}'s {1} Mt gives a cloud {2:0.#} km high, not within 12 % of {3}", name, megatons, got, topKm);
                }
            }
            if (Math.Abs(yield.CloudTopMetres(16000.0) / yield.CloudTopMetres(1000.0) - 2.0) > 1e-9)
            {
                return "the cloud's top does not rise as the 1/4 power of the yield";
            }
            if (Math.Abs(RiseShare(60.0) - 14.0 / 40.0) > 0.05 || Math.Abs(RiseShare(90.0) - 17.0 / 41.0) > 0.05 || RiseShare(0.0) != 0.0)
            {
                return "the cloud does not rise as Castle Bravo's and Ivy Mike's did: 35 % of its height at a minute, 41 % at 90 s";
            }
            if (Math.Abs(Pulse(1.0) - 1.0) > 1e-12 || Pulse(0.0) != 0.0 || Pulse(0.5) >= 1.0 || Pulse(2.0) >= 1.0)
            {
                return "the thermal pulse does not peak at t_max";
            }

            // ---- the show against the damage, the camera and the event.
            double show = yield.ShowSeconds();
            if (!(show > 0.0) || show > CometRules.SkyClearSeconds + 1e-9 || RiseShare(show) < 0.85)
            {
                return string.Format(c, "the show lasts {0:0.#} s after the impact: it must end with the cloud near its height and not outlast the darker sky ({1} s)", show, CometRules.SkyClearSeconds);
            }
            CelestialTuning shipped = CelestialTuning.Defaults;
            CelestialTuning wide = CelestialTuning.Defaults
                .With(CelestialSettings.GroundZeroMetresAt1e6, CelestialSettings.GroundZeroMetresAt1e6.Range.Max)
                .With(CelestialSettings.FireballRadiusFactor, CelestialSettings.FireballRadiusFactor.Range.Max)
                .With(CelestialSettings.DebrisRadiusFactor, CelestialSettings.DebrisRadiusFactor.Range.Max);
            foreach (CelestialTuning tuning in new[] { shipped, wide })
            {
                for (double moles = 1.0; moles <= 1e12; moles *= 3.0)
                {
                    CometSizes sizes = yield.For(moles, tuning);
                    double groundZero = CelestialRules.Shipped.GroundZeroMetres(moles, tuning.GroundZeroMetresAt1e6);
                    if (sizes.FireballMetres < groundZero * tuning.FireballRadiusFactor - 1e-6 || sizes.FireballMetres < groundZero)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol is drawn with a fireball of {1:N0} m, smaller than its fireball zone of {2:N0} m", moles, sizes.FireballMetres, groundZero * tuning.FireballRadiusFactor);
                    }
                    if (Math.Abs(sizes.DebrisMetres - groundZero * tuning.DebrisRadiusFactor) > 1e-6)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol throws its rocks {1:N0} m, not to the edge of its falling rocks zone at {2:N0} m", moles, sizes.DebrisMetres, groundZero * tuning.DebrisRadiusFactor);
                    }
                    if (moles >= 1.0 && Math.Abs(wave.PeakKPa(moles, sizes.SurgeMetres) / CometRules.ThrownKPa - 1.0) > 1e-9 && sizes.SurgeMetres > CometRules.NearestMetres)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol throws its dust to {1:N0} m, where the wave is not the {2} kPa that throws a player", moles, sizes.SurgeMetres, CometRules.ThrownKPa);
                    }
                    if (sizes.CloudTopMetres < sizes.FireballMetres || sizes.CapRadiusMetres < 0.0)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol tops its cloud below its own fireball", moles);
                    }
                    foreach (double eye in new[] { wave.SafeDistance(wave.Scale(moles)), 5100.0, 20000.0 })
                    {
                        foreach (double drop in new[] { 0.0, 0.001 * Sq((eye - 144.0) * 0.3) })
                        {
                            double far = yield.FarClipMetres(eye, drop, sizes);
                            double across = eye + BoundsRadius(sizes);
                            double cloud = Math.Sqrt(across * across + Sq(BoundsHeight(sizes) + drop));
                            if (!(far >= cloud))
                            {
                                return string.Format(c, "a comet of {0:0.#E+0} mol seen from {1:N0} m: the far clip of {2:N0} m stops short of its cloud, {3:N0} m away at its farthest", moles, eye, far, cloud);
                            }
                        }
                    }
                }
            }
            foreach (double gravity in new[] { 0.6, 1.6, 3.7, 9.81 })
            {
                double range = 2830.0;
                double speed = LaunchSpeed(range, gravity);
                if (Math.Abs(speed * speed / gravity - range) > 1e-6)
                {
                    return "a rock thrown at the launch speed does not land at the edge of its range";
                }
            }

            // ---- the head: 0.16's at ten million moles, growing with the body.
            if (Math.Abs(yield.HeadScale(1e7) - HeadReferenceScale) > 1e-9 || Math.Abs(yield.HeadScale(8e7) / yield.HeadScale(1e7) - 2.0) > 1e-9
                || Math.Abs(yield.HeadLight(8e7) / yield.HeadLight(1e7) - 4.0) > 1e-9 || yield.HeadScale(1e12) != yield.HeadScale(CometRules.WaveMolesCap))
            {
                return "the head does not grow with the body's diameter, its light with its area, and both stop with the wave";
            }
            double flash = yield.FlashIrradiance(yield.Kilotons(1e7), FlashReferenceMetres);
            if (Math.Abs(flash - 1.0) > 1e-9 || Math.Abs(yield.FlashIrradiance(yield.Kilotons(1e7), 2.0 * FlashReferenceMetres) - 0.25) > 1e-9)
            {
                return "the flash is not 1 for the reference comet at its distance, falling with the square of the distance";
            }
            return null;
        }

        /// <summary>The band of the wave the yield is fitted over: windows (200 kPa) to the strongest wall (10,000 kPa).</summary>
        public const double FitLowKPa = 200.0;

        public const double FitHighKPa = 10000.0;

        /// <summary>The distances the show is seen over, 500 m to 20 km, 41 of them evenly on a log scale.</summary>
        public static double[] Distances()
        {
            double[] d = new double[41];
            for (int i = 0; i < d.Length; i++)
            {
                d[i] = 500.0 * Math.Pow(40.0, i / (double)(d.Length - 1));
            }
            return d;
        }

        /// <summary>
        /// The kilotons a mole that best match the wave to the reference blast curve, least squares in
        /// the logarithm of the peak, over <see cref="FitLowKPa"/> to <see cref="FitHighKPa"/>, at
        /// <see cref="Distances"/>, for 1e5 to 1e9 mol in quarter decades.
        /// </summary>
        public static double FitKilotonsPerMole(CometRules wave)
        {
            double lo = Math.Log(1e-6);
            double hi = Math.Log(1e-2);
            for (int i = 0; i < 80; i++)
            {
                double a = lo + (hi - lo) * 0.381966;
                double b = lo + (hi - lo) * 0.618034;
                if (FitCost(wave, Math.Exp(a)) < FitCost(wave, Math.Exp(b)))
                {
                    hi = b;
                }
                else
                {
                    lo = a;
                }
            }
            return Math.Exp((lo + hi) / 2.0);
        }

        private static double FitCost(CometRules wave, double kilotonsPerMole)
        {
            double sum = 0.0;
            for (int q = 0; q <= 16; q++)
            {
                double moles = 1e5 * Math.Pow(10.0, q / 4.0);
                foreach (double d in Distances())
                {
                    double peak = wave.PeakKPa(moles, d);
                    if (peak < FitLowKPa || peak > FitHighKPa)
                    {
                        continue;
                    }
                    double e = Math.Log(peak / ReferencePeakKPa(kilotonsPerMole * moles, d));
                    sum += e * e;
                }
            }
            return sum;
        }
    }

    /// <summary>One impact's dimensions, metres and seconds. A plain struct: net472 has no IsExternalInit.</summary>
    public readonly struct CometSizes
    {
        public readonly double Moles;

        /// <summary>The yield, kilotons.</summary>
        public readonly double Kilotons;

        /// <summary>The fireball by the law alone, and the damage's fireball zone it is never drawn smaller than.</summary>
        public readonly double FireballLawMetres;

        public readonly double FireballZoneMetres;

        /// <summary>The fireball's maximum radius as drawn.</summary>
        public readonly double FireballMetres;

        /// <summary>Seconds from the impact until the fireball is full grown and the thermal pulse is over.</summary>
        public readonly double FireballSeconds;

        /// <summary>Seconds from the impact to the thermal pulse's peak.</summary>
        public readonly double FlashPeakSeconds;

        public readonly double CloudTopMetres;

        /// <summary>Half the cap's width when the cloud is at its height.</summary>
        public readonly double CapRadiusMetres;

        /// <summary>How far the ground dust is thrown.</summary>
        public readonly double SurgeMetres;

        /// <summary>How far rocks are thrown: the edge of the falling rocks zone.</summary>
        public readonly double DebrisMetres;

        /// <summary>Seconds the show lasts after the impact.</summary>
        public readonly double ShowSeconds;

        /// <summary>The head's size, as the 0.16 show's scale, and its light against the reference comet's.</summary>
        public readonly double HeadScale;

        public readonly double HeadLight;

        public CometSizes(double moles, double kilotons, double fireballLawMetres, double fireballZoneMetres, double fireballMetres, double fireballSeconds,
            double flashPeakSeconds, double cloudTopMetres, double capRadiusMetres, double surgeMetres, double debrisMetres, double showSeconds,
            double headScale, double headLight)
        {
            Moles = moles;
            Kilotons = kilotons;
            FireballLawMetres = fireballLawMetres;
            FireballZoneMetres = fireballZoneMetres;
            FireballMetres = fireballMetres;
            FireballSeconds = fireballSeconds;
            FlashPeakSeconds = flashPeakSeconds;
            CloudTopMetres = cloudTopMetres;
            CapRadiusMetres = capRadiusMetres;
            SurgeMetres = surgeMetres;
            DebrisMetres = debrisMetres;
            ShowSeconds = showSeconds;
            HeadScale = headScale;
            HeadLight = headLight;
        }

        /// <summary>The cloud's top this long after the impact: from the fireball's top up to its height.</summary>
        public double TopAt(double seconds) => FireballMetres + (CloudTopMetres - FireballMetres) * CometYield.RiseShare(seconds);

        /// <summary>Half the cap's width this long after the impact, never narrower than the fireball.</summary>
        public double CapRadiusAt(double seconds) => Math.Max(FireballMetres, CometYield.CapWidthPerTop / 2.0 * TopAt(seconds));

        public override string ToString()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return string.Format(c, "yield {0} ({1:0.####E+0} kt), fireball {2:N0} m (law {3:N0}, zone {4:N0}) full grown at {5:0.0} s, flash peak {6:0.00} s, cloud top {7:N0} m, cap {8:N0} m across, dust {9:N0} m, rocks {10:N0} m, show {11:0} s after the impact, head scale {12:0.00}",
                Kilotons >= 1000.0 ? (Kilotons / 1000.0).ToString("0.00", c) + " Mt" : Kilotons.ToString("0.###", c) + " kt", Kilotons,
                FireballMetres, FireballLawMetres, FireballZoneMetres, FireballSeconds, FlashPeakSeconds, CloudTopMetres, 2.0 * CapRadiusMetres,
                SurgeMetres, DebrisMetres, ShowSeconds, HeadScale);
        }
    }
}
