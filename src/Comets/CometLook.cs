using System;
using System.Collections.Generic;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// How an impact's cloud is drawn, layer by layer, as shares of the sizes its yield gives
    /// (<see cref="CometSizes"/>), and how long its flash may hold the view (docs/COMET.md, "Size from
    /// yield", "The look"). Every length here is a share of the fireball, the cap or the dust's reach,
    /// so a comet of any size keeps the same proportions; the counts are fixed, so a bigger comet is
    /// drawn with bigger puffs, not more of them.
    ///
    /// Plain arithmetic with no game or Unity calls, so tools/PatchCheck runs
    /// <see cref="Check(CometLook)"/> outside the game. The parts are virtual so PatchCheck can hand
    /// the check wrong looks and see each caught.
    /// </summary>
    public class CometLook
    {
        public static readonly CometLook Shipped = new CometLook();

        /// <summary>No more particles than this across the whole show (CometShow's own cap).</summary>
        public const int MaxParticles = 6000;

        /// <summary>The particles the head's tail, sparks and smoke hold at most, alive past the impact.</summary>
        public const int HeadParticles = 500 + 600 + 700;

        /// <summary>Particles that are not the cloud's layers: the thrown rocks, their rain, and each reaction's own layer at its largest.</summary>
        public const int OtherParticles = 220 + 180 + 90 + 200 + 160 + 200;

        /// <summary>An overlay this opaque or more is a white frame: the ground behind it no longer reads.</summary>
        public const double WhiteOverlay = 0.6;

        /// <summary>The longest the view may be a white frame, at any distance, for any comet.</summary>
        public const double WhiteSecondsAtMost = 0.3;

        /// <summary>A glare quad this bright or more washes out what is behind it.</summary>
        public const double WhiteGain = 1.0;

        /// <summary>A flash light this strong or more lights the ground to white.</summary>
        public const double WhiteLight = 6.0;

        // ------------------------------------------------------------------ the flash

        /// <summary>Seconds the flash takes to reach its peak.</summary>
        public virtual double FlashRiseSeconds => 0.03;

        /// <summary>Seconds the first flash takes to fall to 1/e after its peak.</summary>
        public virtual double FlashFadeSeconds => 0.08;

        /// <summary>The overlay's opacity at the flash's peak, facing the impact from close by.</summary>
        public const double FlashPeak = 0.9;

        /// <summary>The thermal pulse after the first flash never makes the overlay more opaque than this.</summary>
        public virtual double ThermalOverlayMax => 0.3;

        /// <summary>The first flash's shape over time: up in a frame or two, gone in a few tenths of a second.</summary>
        public double FlashFirst(double seconds)
        {
            if (!(seconds > 0.0))
            {
                return 0.0;
            }
            return seconds < FlashRiseSeconds ? seconds / FlashRiseSeconds : Math.Exp(-(seconds - FlashRiseSeconds) / FlashFadeSeconds);
        }

        /// <summary>How much of the flash reaches this eye: <paramref name="seen"/> is the thermal pulse's irradiance there saturated, I / (1 + I).</summary>
        public static double Seen(double irradiance) => irradiance > 0.0 ? irradiance / (1.0 + irradiance) : 0.0;

        /// <summary>
        /// The overlay's opacity this long after the impact: the first flash, as strong as the eye
        /// is turned to it (<paramref name="view"/>, 0.35 to 1) and as near as it is, then the thermal
        /// pulse, warm and held to <see cref="ThermalOverlayMax"/> so the ground always reads through it.
        /// </summary>
        public double FlashOverlay(double seconds, double peakSeconds, double irradiance, double view)
        {
            double seen = Seen(irradiance);
            double first = FlashPeak * Clamp01(view) * (0.4 + 0.6 * seen) * FlashFirst(seconds);
            return Math.Max(first, Thermal(seconds, peakSeconds, irradiance));
        }

        /// <summary>The thermal pulse's share of the overlay, this long after the impact.</summary>
        public double Thermal(double seconds, double peakSeconds, double irradiance)
        {
            return ThermalOverlayMax * Seen(irradiance) * CometYield.Pulse(seconds / Math.Max(0.01, peakSeconds));
        }

        /// <summary>The flash core's brightness (a glow quad's gain): the first flash, then a fraction of the thermal pulse.</summary>
        public virtual double CoreGain(double seconds, double peakSeconds, double irradiance)
        {
            return 1.3 * Math.Exp(-Math.Max(0.0, seconds) / 0.15) + 0.6 * Seen(irradiance) * CometYield.Pulse(seconds / Math.Max(0.01, peakSeconds));
        }

        /// <summary>
        /// The flash light's intensity: the first flash, the thermal pulse, and the fire's glow as it
        /// cools over <paramref name="fireSeconds"/>, stronger when the comet burns.
        /// </summary>
        public virtual double FlashLight(double seconds, double peakSeconds, double irradiance, double fireSeconds, double burning)
        {
            double s = Math.Max(0.0, seconds);
            return (8.0 * Math.Exp(-s / 0.15) + 1.8 * Seen(irradiance) * CometYield.Pulse(s / Math.Max(0.01, peakSeconds)) + 1.2 * Math.Exp(-s / Math.Max(0.5, fireSeconds)))
                * (1.0 + 0.5 * Clamp01(burning));
        }

        // ------------------------------------------------------------------ the fireball

        /// <summary>The fireball's radius at the impact, as a share of its largest.</summary>
        public const double BallStartShare = 0.2;

        /// <summary>The fireball's radius this long after the impact: from a fifth of R, 90 % grown by the time the thermal pulse is over.</summary>
        public virtual double BallRadius(CometSizes sizes, double seconds)
        {
            double grown = 1.0 - Math.Exp(-Math.Max(0.0, seconds) * Math.Log(10.0) / GrowSeconds(sizes));
            return sizes.FireballMetres * (BallStartShare + (1.0 - BallStartShare) * grown);
        }

        /// <summary>
        /// How hot the fireball still glows, 1 to 0: white-hot while it grows, then cooling through
        /// orange to a dull red, more slowly the bigger it is.
        /// </summary>
        public virtual double BallHeat(CometSizes sizes, double seconds)
        {
            double grow = GrowSeconds(sizes);
            double hot = 0.6 * grow;
            return seconds <= hot ? 1.0 : Math.Exp(-(seconds - hot) / (0.9 * grow + 4.0));
        }

        public static double GrowSeconds(CometSizes sizes) => Math.Max(0.6, sizes.FireballSeconds);

        // ------------------------------------------------------------------ the stem

        /// <summary>The stem's radius at the ground, as a share of the fireball's.</summary>
        public const double StemRootShare = 0.35;

        /// <summary>The stem's radius this high above the ground: widening as a buoyant plume does (<see cref="CometYield.StemSpread"/>).</summary>
        public static double StemRadius(CometSizes sizes, double height) => StemRootShare * sizes.FireballMetres + CometYield.StemSpread * Math.Max(0.0, height);

        /// <summary>A stem puff's diameter, as a share of the stem's own diameter where it is.</summary>
        public virtual double StemPuffMin => 0.7;

        public virtual double StemPuffMax => 1.05;

        /// <summary>Seconds a stem puff takes to climb from the ground to the cap, and lives.</summary>
        public const double StemSeconds = 60.0;

        /// <summary>A stem puff lives from this share of its life (<see cref="StemLife"/>) to all of it.</summary>
        public const double StemLifeFrom = 0.85;

        // ------------------------------------------------------------------ the cap

        /// <summary>The cap is a ring of cloud: its middle this far out, and its tube this thick, as shares of the cap's radius.</summary>
        public const double CapRingShare = 0.5;

        public const double CapTubeShare = 0.32;

        /// <summary>A cap puff's diameter, as a share of the cap's radius.</summary>
        public virtual double CapPuffMin => 0.3;

        public virtual double CapPuffMax => 0.48;

        /// <summary>The sunlit top sits this far above the cap's middle, the shadowed underside this far below, as shares of its radius.</summary>
        public const double CapTopLift = 0.1;

        public const double CapUnderDrop = 0.14;

        /// <summary>Seconds a cap puff lives at most, and the share of that it lives at least.</summary>
        public const double CapSeconds = 80.0;

        public const double CapLifeFrom = 0.6;

        // ------------------------------------------------------------------ how the cloud is fed

        /// <summary>The longest any puff lives in a show this many seconds after the impact: nothing outlives the show.</summary>
        public static double LongestLife(double showSeconds) => 0.9 * showSeconds;

        public static double StemLife(double showSeconds) => Math.Min(LongestLife(showSeconds), StemSeconds);

        public static double CapLife(double showSeconds) => Math.Min(LongestLife(showSeconds), CapSeconds);

        /// <summary>
        /// Puffs a second for a layer the show keeps fed (the stem, the cap's two rings), this long after the
        /// impact and fed at <paramref name="feed"/> (<see cref="CloudFeed"/>): as many as its limit holds over its
        /// longest life. The game stops a particle system emitting while it holds its limit and lets it emit again
        /// only as its particles die, so a layer fed faster fills at once and is then renewed only as its first
        /// puffs die, one generation of one age after another: the stem's puffs climb and fade together, and
        /// between generations the column is missing from the view.
        /// </summary>
        public virtual double FedRate(int limit, double longestLife, double seconds, double feed) => limit / longestLife * Clamp01(feed);

        /// <summary>
        /// The puffs a fed layer opens with all at once: as many as leave room, beside its feed until the first of
        /// them die, for it never to reach its limit.
        /// </summary>
        public virtual int OpeningBurst(int limit, double shortestLife, double longestLife) => (int)Math.Floor(limit * (1.0 - shortestLife / longestLife));

        // ------------------------------------------------------------------ the base surge

        /// <summary>A surge puff's diameter at birth, as a share of the fireball's radius; it grows by <see cref="SurgeGrowth"/> as it rolls out.</summary>
        public virtual double SurgePuffMin => 0.2;

        public virtual double SurgePuffMax => 0.38;

        public const double SurgeGrowth = 2.0;

        /// <summary>The most of the screen one surge puff may cover, so the sky shows past the wall as it rolls over the eye.</summary>
        public virtual double SurgeScreenShare => 0.45;

        // ------------------------------------------------------------------ the cloud's puffs

        /// <summary>The puff every layer of the cloud is drawn with (<see cref="CometBillow"/>).</summary>
        public virtual CometBillow Billow => CometBillow.Shipped;

        /// <summary>
        /// A cloud puff's opacity over the middle of its life, before its shade's own (the darker
        /// masses up to <see cref="ShadeAlphaMax"/>): low enough that puffs crossing each other blend
        /// into one mass, and the cloud is dense where many lie behind each other.
        /// </summary>
        public virtual double PuffAlpha => 0.55;

        /// <summary>The most opaque of the shades a cloud layer picks its puffs' colours from.</summary>
        public const double ShadeAlphaMax = 0.95;

        /// <summary>The share of its life a puff takes to appear, and the share from which it fades to nothing at its end.</summary>
        public const double PuffFadeIn = 0.08;

        public const double PuffFadeFrom = 0.6;

        /// <summary>Smoke is drawn this much more opaque in full day than at night, so it reads against a bright sky.</summary>
        public const double DaySmoke = 1.28;

        /// <summary>The legacy particle shader doubles the tint and vertex colour it is given; the show tints its smoke at half.</summary>
        public const double ShaderGain = 2.0;

        public const double SmokeTint = 0.5;

        /// <summary>
        /// One puff at its middle is at most this opaque in full day, so the one behind it still shows
        /// through and their outlines do not cut each other, and at least this opaque at night, so a
        /// few of them make a cloud.
        /// </summary>
        public const double PuffOpacityAtMost = 0.7;

        public const double PuffOpacityAtLeast = 0.3;

        /// <summary>
        /// One puff's opacity at its middle in the middle of its life: the shader's gain, the tint by
        /// night or day, the darkest shade, the puff's own, and <paramref name="textureCore"/>, its
        /// texture's at the middle (<see cref="CometBillow.Measures.Core"/>).
        /// </summary>
        public double PuffOpacity(bool day, double textureCore)
        {
            return ShaderGain * SmokeTint * (day ? DaySmoke : 1.0) * ShadeAlphaMax * PuffAlpha * textureCore;
        }

        /// <summary>
        /// The cloud stops being fed <see cref="CloudFeedEndsSeconds"/> before the show ends, tapering
        /// over the <see cref="CloudFadeSeconds"/> before that, and the whole of it fades out over the
        /// show's last <see cref="CloudFadeSeconds"/>, so it stands near its full height until then and
        /// is gone when the show ends.
        /// </summary>
        public const double CloudFadeSeconds = 30.0;

        public const double CloudFeedEndsSeconds = 30.0;

        /// <summary>How much of the cloud is drawn this long after the impact, 1 to 0, in a show <paramref name="showSeconds"/> long.</summary>
        public virtual double CloudFade(double seconds, double showSeconds) => Clamp01((showSeconds - seconds) / CloudFadeSeconds);

        /// <summary>How strongly the cloud is fed with new puffs this long after the impact, 1 to 0.</summary>
        public virtual double CloudFeed(double seconds, double showSeconds) => Clamp01((showSeconds - CloudFeedEndsSeconds - seconds) / CloudFadeSeconds);

        // ------------------------------------------------------------------ the layers

        /// <summary>The fireball's surface and every layer of the cloud, at its full size.</summary>
        public virtual IList<CometLayer> Layers(CometSizes sizes)
        {
            double r = sizes.FireballMetres;
            double cap = sizes.CapRadiusMetres;
            double capHigh = 0.75 * sizes.CloudTopMetres;
            double rootDiameter = 2.0 * StemRadius(sizes, 0.0);
            double topDiameter = 2.0 * StemRadius(sizes, capHigh);
            return new[]
            {
                new CometLayer("fireball", 1, 2.0 * r, 2.0 * r, 1.0, 4.0, r, "R", "R"),
                new CometLayer("boils", 40, 0.3 * r, 0.55 * r, 1.2, 1.0, 0.85 * r, "R", "R"),
                new CometLayer("stem", 260, StemPuffMin * rootDiameter, StemPuffMax * rootDiameter, topDiameter / rootDiameter, 0.8, 0.5 * rootDiameter, "R", "R"),
                new CometLayer("stem glow", 80, 0.5 * r, 0.9 * r, 1.6, 1.0, 0.25 * r, "R", "R"),
                new CometLayer("cap top", 200, CapPuffMin * cap, CapPuffMax * cap, 1.25, 0.8, (CapRingShare + CapTubeShare) * cap, "cap", "cap"),
                new CometLayer("cap underside", 150, CapPuffMin * cap, CapPuffMax * cap, 1.25, 0.8, (CapRingShare + CapTubeShare) * cap, "cap", "cap"),
                new CometLayer("cap glow", 80, 1.0 * r, 1.8 * r, 1.8, 1.0, 0.5 * r, "R", "R"),
                new CometLayer("base surge", 220, SurgePuffMin * r, SurgePuffMax * r, SurgeGrowth, SurgeScreenShare, Math.Max(r, sizes.SurgeMetres), "R", "dust"),
            };
        }

        /// <summary>The layer of this name, from <see cref="Layers"/>.</summary>
        public CometLayer Layer(CometSizes sizes, string name)
        {
            foreach (CometLayer layer in Layers(sizes))
            {
                if (layer.Name == name)
                {
                    return layer;
                }
            }
            throw new ArgumentException("no layer '" + name + "'");
        }

        private static double Clamp01(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;

        private static readonly Lazy<string> ShippedVerdict = new Lazy<string>(() =>
        {
            try
            {
                return Check(Shipped);
            }
            catch (Exception e)
            {
                return "the look check itself failed: " + e.Message;
            }
        });

        /// <summary>The shipped look's own check, run once: null when it holds, else the first case that does not.</summary>
        public static string Verdict => ShippedVerdict.Value;

        /// <summary>
        /// The flash against the view, the layers against the cloud they draw and against each other,
        /// and the counts against the cap. Returns the first case that does not hold, or null.
        /// </summary>
        public static string Check(CometLook look)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            CometYield yield = CometYield.Shipped;
            const double step = 0.002;

            // ---- the puff: a soft, lit mass, and thin enough that puffs blend where they cross.
            CometBillow.Measures puff = CometBillow.Measure(look.Billow);
            string billow = CometBillow.Judge(puff);
            if (billow != null)
            {
                return billow;
            }
            double byDay = look.PuffOpacity(true, puff.Core), byNight = look.PuffOpacity(false, puff.Core);
            if (byDay > PuffOpacityAtMost)
            {
                return string.Format(c, "a cloud puff is {0:0.##} opaque at its middle in full day, over {1}: puffs crossing each other cover rather than blend, and their outlines cut each other", byDay, PuffOpacityAtMost);
            }
            if (byNight < PuffOpacityAtLeast)
            {
                return string.Format(c, "a cloud puff is {0:0.##} opaque at its middle at night, under {1}: the cloud is a haze", byNight, PuffOpacityAtLeast);
            }
            foreach (double moles in new[] { 1e3, 1e5, 1e6, 1e7, 3.6e7, 1e8, 1e9 })
            {
                CometSizes sizes = yield.For(moles, CelestialTuning.Defaults);
                double grow = GrowSeconds(sizes);
                double fire = 3.0 + sizes.FireballSeconds;

                // ---- the flash: white for a few tenths of a second at most, from anywhere, and still a flash.
                foreach (double metres in new[] { 100.0, 500.0, 2500.0, 5000.0, 20000.0 })
                {
                    double irradiance = yield.FlashIrradiance(sizes.Kilotons, metres);
                    double white = 0.0, glare = 0.0, lit = 0.0, thermal = 0.0, peak = 0.0;
                    for (double s = 0.0; s <= 340.0; s += s < 30.0 ? step : 0.05)
                    {
                        double dt = s < 30.0 ? step : 0.05;
                        double overlay = look.FlashOverlay(s, sizes.FlashPeakSeconds, irradiance, 1.0);
                        peak = Math.Max(peak, overlay);
                        thermal = Math.Max(thermal, look.Thermal(s, sizes.FlashPeakSeconds, irradiance));
                        white += overlay >= WhiteOverlay ? dt : 0.0;
                        glare += look.CoreGain(s, sizes.FlashPeakSeconds, irradiance) >= WhiteGain ? dt : 0.0;
                        lit += look.FlashLight(s, sizes.FlashPeakSeconds, irradiance, fire, 1.0) >= WhiteLight ? dt : 0.0;
                    }
                    if (white > WhiteSecondsAtMost || glare > WhiteSecondsAtMost || lit > WhiteSecondsAtMost)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol seen from {1:N0} m whites out the view for {2:0.##} s (overlay), {3:0.##} s (glare), {4:0.##} s (light): at most {5} s",
                            moles, metres, white, glare, lit, WhiteSecondsAtMost);
                    }
                    if (thermal >= WhiteOverlay)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol seen from {1:N0} m: its thermal pulse holds the overlay at {2:0.##}, a white frame", moles, metres, thermal);
                    }
                    if (peak < 0.3)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol seen from {1:N0} m, facing it, flashes only to {2:0.##}", moles, metres, peak);
                    }
                }

                // ---- the fireball: grown to R by the end of the pulse, hot while it grows, cool by the show's end.
                if (Math.Abs(look.BallRadius(sizes, 50.0 * grow) / sizes.FireballMetres - 1.0) > 0.01
                    || look.BallRadius(sizes, grow) < 0.9 * sizes.FireballMetres || look.BallRadius(sizes, 0.0) > 0.25 * sizes.FireballMetres)
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol draws its fireball growing to {1:N0} m against its R of {2:N0} m", moles, look.BallRadius(sizes, 50.0 * grow), sizes.FireballMetres);
                }
                double previous = 2.0;
                for (double s = 0.0; s <= sizes.ShowSeconds; s += 0.5)
                {
                    double heat = look.BallHeat(sizes, s);
                    if (heat > previous + 1e-12 || heat > 1.0 || heat < 0.0 || (s < 0.5 * grow && heat < 1.0))
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol: its fireball's heat goes {1:0.###} -> {2:0.###} at {3:0.#} s", moles, previous, heat, s);
                    }
                    previous = heat;
                }
                if (previous > 0.05)
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol still glows at {1:0.###} when its show ends", moles, previous);
                }

                // ---- the cloud: standing in full for most of the show, and gone when it ends.
                double show = sizes.ShowSeconds;
                double fadePrevious = 2.0;
                for (double s = 0.0; s <= show + 1e-9; s += 0.5)
                {
                    double fade = look.CloudFade(s, show), feed = look.CloudFeed(s, show);
                    if (fade > fadePrevious + 1e-12 || fade < 0.0 || fade > 1.0 || feed < 0.0 || feed > 1.0
                        || (s <= 0.8 * show && (fade < 1.0 || feed < 1.0)) || (s >= show - CloudFeedEndsSeconds && feed > 0.0))
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol draws {1:0.###} of its cloud, fed at {2:0.###}, {3:0.#} s into a show of {4:0.#} s: the cloud must stand in full for the first 80 % of it, stop being fed before it fades, and fade only once",
                            moles, fade, feed, s, show);
                    }
                    fadePrevious = fade;
                }
                if (look.CloudFade(show, show) > 0.0)
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol still draws {1:0.###} of its cloud when its show ends", moles, look.CloudFade(show, show));
                }

                // ---- the cloud is fed no faster than its limits hold: a system at its limit emits nothing until its
                // puffs die, so a layer fed faster is renewed in generations of one age that climb and fade together.
                foreach ((string name, double longestLife, double shortestLife, bool opens) in new[]
                {
                    ("stem", StemLife(show), StemLifeFrom * StemLife(show), false),
                    ("cap top", CapLife(show), CapLifeFrom * CapLife(show), true),
                    ("cap underside", CapLife(show), CapLifeFrom * CapLife(show), true),
                })
                {
                    int limit = look.Layer(sizes, name).Count;
                    for (double s = 0.0; s <= show + 1e-9; s += 0.5)
                    {
                        double feed = look.CloudFeed(s, show);
                        double held = look.FedRate(limit, longestLife, s, feed) * longestLife;
                        if (!(held >= 0.0) || held > limit * (1.0 + 1e-9) || (feed >= 1.0 && held < 0.99 * limit))
                        {
                            return string.Format(c, "a comet of {0:0.#E+0} mol feeds its {1} {2:0.#} s after the impact, fed at {3:0.###}, with {4:0.#} puffs over their longest life of {5:0.#} s against its limit of {6}: it must be fed in full and never past its limit, or it is renewed in generations of one age that leave the view together",
                                moles, name, s, feed, held, longestLife, limit);
                        }
                    }
                    int burst = opens ? look.OpeningBurst(limit, shortestLife, longestLife) : 0;
                    double first = burst + look.FedRate(limit, longestLife, 0.0, 1.0) * shortestLife;
                    if (burst < 0 || first > limit * (1.0 + 1e-9))
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol opens its {1} with {2} puffs, and with its feed holds {3:0.#} before the first of them die, against its limit of {4}",
                            moles, name, burst, first, limit);
                    }
                }

                // ---- the layers: each sized to the part of the cloud it draws.
                IList<CometLayer> layers = look.Layers(sizes);
                int count = 0;
                foreach (CometLayer layer in layers)
                {
                    count += layer.Count;
                    if (!(layer.PuffMinMetres > 0.0) || layer.PuffMaxMetres < layer.PuffMinMetres || !(layer.ScreenShare > 0.0) || !(layer.GrowsTo >= 1.0))
                    {
                        return "the layer '" + layer.Name + "' has no size, a smallest puff over its largest, or puffs that shrink";
                    }
                }
                CometLayer stem = look.Layer(sizes, "stem");
                double rootDiameter = 2.0 * StemRadius(sizes, 0.0);
                if (stem.PuffMaxMetres > 1.1 * rootDiameter || stem.PuffMaxMetres * stem.GrowsTo > 1.1 * 2.0 * StemRadius(sizes, 0.75 * sizes.CloudTopMetres) + 1e-6)
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol draws stem puffs up to {1:N0} m across a stem {2:N0} m wide at its root: a puff wider than the stem turns the column into a wall",
                        moles, stem.PuffMaxMetres, rootDiameter);
                }
                CometLayer surge = look.Layer(sizes, "base surge");
                if (surge.PuffMaxMetres * surge.GrowsTo > sizes.FireballMetres || surge.ScreenShare > 0.5)
                {
                    return string.Format(c, "a comet of {0:0.#E+0} mol rolls its surge out in puffs {1:N0} m high against a fireball of {2:N0} m, each up to {3:0.##} of the screen: the surge must stay below the fireball and let the sky through",
                        moles, surge.PuffMaxMetres * surge.GrowsTo, sizes.FireballMetres, surge.ScreenShare);
                }
                foreach (string name in new[] { "cap top", "cap underside" })
                {
                    CometLayer cap = look.Layer(sizes, name);
                    if (cap.ReachMetres + 0.5 * cap.PuffMaxMetres > 1.1 * sizes.CapRadiusMetres || cap.PuffMaxMetres > sizes.CapRadiusMetres)
                    {
                        return string.Format(c, "a comet of {0:0.#E+0} mol draws its {1} out to {2:N0} m against a cap {3:N0} m in radius", moles, name, cap.ReachMetres + 0.5 * cap.PuffMaxMetres, sizes.CapRadiusMetres);
                    }
                }
                foreach (CometLayer layer in layers)
                {
                    if (layer.Name != "fireball" && layer.ScreenShare > 1.0)
                    {
                        return "a puff of '" + layer.Name + "' may cover more than the whole screen";
                    }
                }
                if (count + HeadParticles + OtherParticles > MaxParticles)
                {
                    return string.Format(c, "the layers want {0:N0} particles beside the head's {1:N0} and the rest's {2:N0}: more than the show's cap of {3:N0}", count, HeadParticles, OtherParticles, MaxParticles);
                }

                // ---- every size keeps its proportions: each layer the same share of the size it is drawn from.
                CometSizes other = yield.For(moles * 7.0, CelestialTuning.Defaults);
                IList<CometLayer> theirs = look.Layers(other);
                for (int i = 0; i < layers.Count; i++)
                {
                    double here = layers[i].PuffMaxMetres / Basis(sizes, layers[i].PuffBasis);
                    double there = theirs[i].PuffMaxMetres / Basis(other, theirs[i].PuffBasis);
                    double reachHere = layers[i].ReachMetres / Basis(sizes, layers[i].ReachBasis);
                    double reachThere = theirs[i].ReachMetres / Basis(other, theirs[i].ReachBasis);
                    if (Math.Abs(here / there - 1.0) > 1e-9 || Math.Abs(reachHere / reachThere - 1.0) > 1e-9)
                    {
                        return string.Format(c, "the layer '{0}' is {1:0.####} of its {2} at {3:0.#E+0} mol and {4:0.####} at seven times that: a size must keep its proportions",
                            layers[i].Name, here, layers[i].PuffBasis, moles, there);
                    }
                }
            }
            return null;
        }

        private static double Basis(CometSizes sizes, string name)
        {
            switch (name)
            {
                case "R": return sizes.FireballMetres;
                case "cap": return sizes.CapRadiusMetres;
                case "dust": return Math.Max(sizes.FireballMetres, sizes.SurgeMetres);
                default: throw new ArgumentException("no size '" + name + "'");
            }
        }
    }

    /// <summary>One layer of the cloud as drawn at its full size. A plain struct: net472 has no IsExternalInit.</summary>
    public readonly struct CometLayer
    {
        public readonly string Name;

        /// <summary>Particles at most; 1 for a single quad.</summary>
        public readonly int Count;

        /// <summary>A puff's diameter at birth, metres.</summary>
        public readonly double PuffMinMetres;

        public readonly double PuffMaxMetres;

        /// <summary>How much larger a puff is at the end of its life.</summary>
        public readonly double GrowsTo;

        /// <summary>The most of the screen's height one puff may cover.</summary>
        public readonly double ScreenShare;

        /// <summary>How far out from the layer's middle its puffs are made.</summary>
        public readonly double ReachMetres;

        /// <summary>The yield size the puffs and the reach are shares of: R the fireball, cap the cap's radius, dust the surge's reach.</summary>
        public readonly string PuffBasis;

        public readonly string ReachBasis;

        public CometLayer(string name, int count, double puffMinMetres, double puffMaxMetres, double growsTo, double screenShare, double reachMetres, string puffBasis, string reachBasis)
        {
            Name = name;
            Count = count;
            PuffMinMetres = puffMinMetres;
            PuffMaxMetres = puffMaxMetres;
            GrowsTo = growsTo;
            ScreenShare = screenShare;
            ReachMetres = reachMetres;
            PuffBasis = puffBasis;
            ReachBasis = reachBasis;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} x {2:N0}-{3:N0} m growing x{4:0.##}, out to {5:N0} m, at most {6:0.##} of the screen",
                Name, Count, PuffMinMetres, PuffMaxMetres, GrowsTo, ReachMetres, ScreenShare);
        }
    }
}
