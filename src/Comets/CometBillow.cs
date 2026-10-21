using System;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The cloud's puff, as a sheet of frames: a soft mass of cloud whose density falls off over most
    /// of its radius and is eaten into by billowing noise, so its edge is ragged and wispy rather than
    /// a disc, and lit by the light it lets through from above, so a puff has a bright top, a shadowed
    /// underside and the billows inside it read, with no line anywhere where one part of it changes to
    /// another. The frames run on into each other and the last into the first, so a puff stepping
    /// through them from any frame never jumps.
    ///
    /// Plain arithmetic with its own noise and no game or Unity calls, so tools/PatchCheck and the
    /// load check measure the very texture the game draws (<see cref="Check(CometBillow)"/>). The
    /// parts are virtual so PatchCheck can hand the check wrong puffs and see each caught.
    /// </summary>
    public class CometBillow
    {
        public static readonly CometBillow Shipped = new CometBillow();

        /// <summary>Frames across and down; a particle system steps through them top-left first.</summary>
        public const int Tiles = 4;

        public const int Frames = Tiles * Tiles;

        /// <summary>The frame size the game draws: 768 by 768 for the sheet.</summary>
        public const int GameFrame = 192;

        /// <summary>Where the light comes from, across and up in the frame: above, a little to the left.</summary>
        public virtual double LightX => -0.38;

        public virtual double LightY => 0.92;

        /// <summary>How the puff thins from its middle to its edge: 0 holds it full to the rim, higher thins it from nearer the middle.</summary>
        public virtual double Softness => 1.4;

        /// <summary>How hard the noise eats into the thin edge, 0 to 1: the higher, the more ragged and wispy.</summary>
        public virtual double Erosion => 0.5;

        private const int Seed = 4127;

        /// <summary>The frame's circle as a share of the puff's own coordinates, so the thinning edge has room inside the quad.</summary>
        private const double Zoom = 0.78;

        /// <summary>How far the noise pushes the outline about, as a share of the radius.</summary>
        private const double Warp = 0.1;

        /// <summary>How quickly overlapping billows add up to a full mass.</summary>
        private const double MassGain = 1.5;

        /// <summary>How much the noise thickens and thins the body, and its lowest frequency and spread.</summary>
        private const double BodyVariation = 0.9;

        private const double NoiseFrequency = 3.0;

        private const double NoiseSpread = 1.6;

        /// <summary>How smoothly thick cloud saturates, and how opaque a full density is drawn.</summary>
        private const double Saturate = 1.5;

        private const double Opacity = 1.5;

        /// <summary>The light: how much the cloud toward it darkens a point, the shade it never falls below, and how strongly a billow's face turned to it catches it.</summary>
        private const double Absorb = 3.2;

        private const double Ambient = 0.3;

        private const double Relief = 2.0;

        private const double ReliefStep = 0.06;

        /// <summary>The middle billows' smallest radius, and how far out the small ones round them sit.</summary>
        private const double BigRadius = 0.44;

        private const double Spread = 0.66;

        /// <summary>
        /// How far each frame moves on from the last: the noise's circle, each billow's swelling and its
        /// drift. A particle steps from one frame to the next without blending them, so each step must be small.
        /// </summary>
        private const double Roll = 0.4;

        private const double Swell = 0.05;

        private const double Wander = 0.015;

        /// <summary>
        /// One frame, <paramref name="size"/> texels square, rows from the bottom up as a texture
        /// holds them: the alpha and the grey shade of each texel, both 0 to 1.
        /// </summary>
        public virtual void Frame(int frame, int size, float[] alpha, float[] shade)
        {
            if (alpha.Length < size * size || shade.Length < size * size)
            {
                throw new ArgumentException("the arrays are smaller than the frame");
            }
            Layout layout = Layout.Make(Seed);
            Noise noise = Noise.Make(Seed);
            double phase = 2.0 * Math.PI * frame / Frames;
            double cos = Math.Cos(phase), sin = Math.Sin(phase);
            // The noise turns round circles in its own plane, two layers against each other, so the
            // edge frays and changes, and the last frame runs on into the first.
            double ax = Roll * cos, ay = Roll * sin, bx = 31.7 - 0.85 * Roll * sin, by = 12.9 + 0.85 * Roll * cos;
            double mix = 0.5 + 0.25 * Math.Sin(phase + 0.7);
            double erosion = Erosion;
            double soft = Softness;
            float[] density = new float[size * size];
            float[] heap = new float[size * size];
            int blobs = layout.X.Length;
            double[] bx0 = new double[blobs], by0 = new double[blobs], br2 = new double[blobs];
            for (int i = 0; i < blobs; i++)
            {
                // Each billow swells and drifts round a small circle of its own, so the cluster rolls.
                double r = layout.Radius[i] * (1.0 + Swell * Math.Sin(phase + layout.Pulse[i]));
                bx0[i] = layout.X[i] + layout.Orbit[i] * Math.Cos(phase + layout.Drift[i]);
                by0[i] = layout.Y[i] + layout.Orbit[i] * Math.Sin(phase + layout.Drift[i]);
                br2[i] = r * r;
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double px = (x + 0.5) / size * 2.0 - 1.0;
                    double py = (y + 0.5) / size * 2.0 - 1.0;
                    // Ragged outlines: the frame pushed about by a slow noise before the mass is read.
                    double zx = px * Zoom, zy = py * Zoom;
                    double wx = zx + Warp * noise.Smooth(zx * 2.1 + ax * 0.5 + 5.1, zy * 2.1 + ay * 0.5 + 1.7);
                    double wy = zy + Warp * noise.Smooth(zx * 2.1 + ay * 0.5 + 9.3, zy * 2.1 - ax * 0.5 + 4.4);
                    double mass = 0.0;
                    for (int i = 0; i < blobs; i++)
                    {
                        double dx = wx - bx0[i];
                        double dy = wy - by0[i];
                        double q = (dx * dx + dy * dy) / br2[i];
                        if (q < 1.0)
                        {
                            double k = 1.0 - q;
                            mass += layout.Weight[i] * k * k;
                        }
                    }
                    // Overlapping billows merge into one mass rather than meeting at a line.
                    double cover = 1.0 - Math.Exp(-MassGain * mass);
                    heap[y * size + x] = (float)cover;
                    // Thinning out over most of the radius, as a puff of smoke does, rather than at its rim.
                    double outward = Math.Sqrt(wx * wx + wy * wy);
                    double edge = Clamp01(outward / 0.95);
                    cover *= Math.Pow(1.0 - edge * edge * (3.0 - 2.0 * edge), soft);
                    double fray = Fbm(noise, wx, wy, ax, ay, bx, by, mix);
                    // The noise thickens and thins the body a little and eats the thin edge away.
                    double d = cover * (1.0 + BodyVariation * (fray - 0.5)) - erosion * (1.0 - fray) * (1.0 - cover);
                    // Thick cloud saturates smoothly rather than flattening into a plateau with an edge.
                    density[y * size + x] = (float)(1.0 - Math.Exp(-Saturate * Math.Max(0.0, d)));
                }
            }

            double lx = LightX, ly = LightY;
            double ll = Math.Sqrt(lx * lx + ly * ly);
            lx /= ll;
            ly /= ll;
            const int steps = 12;
            const double reach = 0.6;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double px = (x + 0.5) / size * 2.0 - 1.0;
                    double py = (y + 0.5) / size * 2.0 - 1.0;
                    double d = density[y * size + x];
                    // The cloud between this point and the light: the more of it, the darker.
                    double depth = 0.0;
                    for (int k = 1; k <= steps; k++)
                    {
                        double t = reach * k / steps;
                        depth += Sample(density, size, px + lx * t, py + ly * t);
                    }
                    depth *= reach / steps;
                    double through = Math.Exp(-Absorb * depth);
                    // A billow's face turned to the light catches it; one turned away is in its own shade.
                    double facing = Sample(heap, size, px, py) - Sample(heap, size, px + lx * ReliefStep, py + ly * ReliefStep);
                    double lit = Ambient + (1.0 - Ambient) * through + Relief * facing;
                    lit *= 0.88 + 0.12 * (py + 1.0) * 0.5;
                    shade[y * size + x] = (float)Clamp01(lit);
                    double r = Math.Sqrt(px * px + py * py);
                    double window = Clamp01((1.0 - r) / 0.25);
                    double a = 1.0 - Math.Exp(-Opacity * d);
                    alpha[y * size + x] = (float)(a / (1.0 - Math.Exp(-Opacity)) * window * window);
                }
            }
        }

        /// <summary>
        /// The whole sheet, <see cref="Tiles"/> by <see cref="Tiles"/> frames of <paramref name="frame"/>
        /// texels, as RGBA bytes, rows from the bottom up, frame 0 top-left.
        /// </summary>
        public byte[] Sheet(int frame)
        {
            int size = frame * Tiles;
            byte[] pixels = new byte[size * size * 4];
            float[] alpha = new float[frame * frame];
            float[] shade = new float[frame * frame];
            for (int f = 0; f < Frames; f++)
            {
                Frame(f, frame, alpha, shade);
                int column = f % Tiles;
                int row = Tiles - 1 - f / Tiles;
                for (int y = 0; y < frame; y++)
                {
                    for (int x = 0; x < frame; x++)
                    {
                        int at = ((row * frame + y) * size + column * frame + x) * 4;
                        byte grey = (byte)Math.Round(shade[y * frame + x] * 255.0);
                        pixels[at] = grey;
                        pixels[at + 1] = grey;
                        pixels[at + 2] = grey;
                        pixels[at + 3] = (byte)Math.Round(alpha[y * frame + x] * 255.0);
                    }
                }
            }
            return pixels;
        }

        private static double Fbm(Noise noise, double x, double y, double ax, double ay, double bx, double by, double mix)
        {
            // Smooth noise, octave on octave: no creases, so nothing in the puff reads as a line.
            double sum = 0.0, amplitude = 0.5, frequency = NoiseFrequency, total = 0.0;
            for (int octave = 0; octave < 4; octave++)
            {
                double a = noise.Smooth(x * frequency + ax, y * frequency + ay);
                double b = noise.Smooth(x * frequency + bx, y * frequency + by);
                sum += amplitude * (a + (b - a) * mix);
                total += amplitude;
                amplitude *= 0.5;
                frequency *= 2.03;
            }
            return Clamp01(0.5 + 0.5 * sum / total * NoiseSpread);
        }

        /// <summary>The density at a point of the frame, between texels; nothing outside it.</summary>
        private static double Sample(float[] field, int size, double px, double py)
        {
            double fx = (px + 1.0) * 0.5 * size - 0.5;
            double fy = (py + 1.0) * 0.5 * size - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            double v00 = At(field, size, x0, y0), v10 = At(field, size, x0 + 1, y0);
            double v01 = At(field, size, x0, y0 + 1), v11 = At(field, size, x0 + 1, y0 + 1);
            return (v00 + (v10 - v00) * tx) * (1.0 - ty) + (v01 + (v11 - v01) * tx) * ty;
        }

        private static double At(float[] field, int size, int x, int y) => x < 0 || y < 0 || x >= size || y >= size ? 0.0 : field[y * size + x];

        private static double Clamp01(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;

        // ------------------------------------------------------------------ the check

        /// <summary>The frame size the check measures at. The puff is drawn from continuous noise, so it is the same puff at any size.</summary>
        public const int CheckFrame = 64;

        /// <summary>The middle of the puff (inside a fifth of its radius) is at least this opaque, on average: a body, not a haze.</summary>
        public const double CoreAlphaAtLeast = 0.75;

        /// <summary>The edge, from 90 % of the middle's opacity to 10 % of it on average round the puff, spans at least this share of the radius.</summary>
        public const double EdgeShareAtLeast = 0.33;

        /// <summary>
        /// Opacity changes by at most this much per unit of radius in all but the steepest 1 % of
        /// texels: an edge sharper than that is a crisp outline at the size a near cloud is drawn.
        /// </summary>
        public const double AlphaSlopeAtMost = 11.0;

        /// <summary>The shade changes by at most this much per unit of radius in all but the steepest 1 % of the texels that show: no seam inside the puff.</summary>
        public const double ShadeSlopeAtMost = 6.0;

        /// <summary>The top half of the puff is at least this much lighter than the bottom half, opacity-weighted.</summary>
        public const double TopLighterBy = 0.06;

        /// <summary>
        /// The mean change in opacity from one frame to the next is at most this. A particle steps
        /// through the frames without blending them, so a bigger step shows as the puff flickering.
        /// </summary>
        public const double StepAtMost = 0.02;

        /// <summary>What the check measures of a puff, over all its frames.</summary>
        public readonly struct Measures
        {
            /// <summary>Mean opacity inside a fifth of the radius.</summary>
            public readonly double Core;

            /// <summary>Where the mean opacity round the puff falls to 90 % and to 10 % of the core's, as shares of the radius.</summary>
            public readonly double EdgeFrom;

            public readonly double EdgeTo;

            /// <summary>The mean opacity in the outermost ring inside the radius, and the most anywhere outside it.</summary>
            public readonly double Rim;

            public readonly double Corner;

            /// <summary>The 99th percentile of the change in opacity, and in shade where it shows, per unit of radius.</summary>
            public readonly double AlphaSlope;

            public readonly double ShadeSlope;

            /// <summary>How much lighter the top half is than the bottom half, opacity-weighted.</summary>
            public readonly double TopLighter;

            /// <summary>The largest mean change in opacity from one frame to the next, and from the last to the first.</summary>
            public readonly double Step;

            public readonly double Wrap;

            /// <summary>The opacity-weighted mean shade.</summary>
            public readonly double Shade;

            public Measures(double core, double edgeFrom, double edgeTo, double rim, double corner, double alphaSlope, double shadeSlope, double topLighter, double step, double wrap, double shade)
            {
                Core = core;
                EdgeFrom = edgeFrom;
                EdgeTo = edgeTo;
                Rim = rim;
                Corner = corner;
                AlphaSlope = alphaSlope;
                ShadeSlope = shadeSlope;
                TopLighter = topLighter;
                Step = step;
                Wrap = wrap;
                Shade = shade;
            }

            public override string ToString()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "core {0:0.00} opaque, edge from {1:0.00} to {2:0.00} of the radius, rim {3:0.000}, corner {4:0.000}, steepest opacity {5:0.0} and shade {6:0.0} per radius, top lighter by {7:0.00}, mean shade {8:0.00}, frame step {9:0.000}, wrap {10:0.000}",
                    Core, EdgeFrom, EdgeTo, Rim, Corner, AlphaSlope, ShadeSlope, TopLighter, Shade, Step, Wrap);
            }
        }

        /// <summary>The puff measured at <see cref="CheckFrame"/>, over all its frames.</summary>
        public static Measures Measure(CometBillow billow)
        {
            int size = CheckFrame;
            int rings = size / 2;
            double[] ringAlpha = new double[rings];
            double[] ringCount = new double[rings];
            float[][] alphas = new float[Frames][];
            double top = 0.0, topWeight = 0.0, bottom = 0.0, bottomWeight = 0.0, corner = 0.0;
            var alphaSlopes = new System.Collections.Generic.List<double>();
            var shadeSlopes = new System.Collections.Generic.List<double>();
            double perRadius = size / 2.0;
            for (int f = 0; f < Frames; f++)
            {
                float[] alpha = new float[size * size];
                float[] shade = new float[size * size];
                billow.Frame(f, size, alpha, shade);
                alphas[f] = alpha;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int at = y * size + x;
                        double a = alpha[at], s = shade[at];
                        if (!(a >= 0.0 && a <= 1.0 && s >= 0.0 && s <= 1.0))
                        {
                            return new Measures(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
                        }
                        double px = (x + 0.5) / size * 2.0 - 1.0;
                        double py = (y + 0.5) / size * 2.0 - 1.0;
                        int ring = (int)(Math.Sqrt(px * px + py * py) * rings);
                        if (ring < rings)
                        {
                            ringAlpha[ring] += a;
                            ringCount[ring] += 1.0;
                        }
                        else
                        {
                            corner = Math.Max(corner, a);
                        }
                        if (py > 0.0)
                        {
                            top += a * s;
                            topWeight += a;
                        }
                        else
                        {
                            bottom += a * s;
                            bottomWeight += a;
                        }
                        if (x + 1 < size && y + 1 < size)
                        {
                            double ax = alpha[at + 1] - a, ay = alpha[at + size] - a;
                            double sx = shade[at + 1] - s, sy = shade[at + size] - s;
                            alphaSlopes.Add(Math.Sqrt(ax * ax + ay * ay) * perRadius);
                            // A change of shade only shows as far as the puff does.
                            double shows = Math.Min(a, Math.Min(alpha[at + 1], alpha[at + size]));
                            if (shows > 0.02)
                            {
                                shadeSlopes.Add(Math.Sqrt(sx * sx + sy * sy) * perRadius * shows);
                            }
                        }
                    }
                }
            }

            double[] profile = new double[rings];
            for (int i = 0; i < rings; i++)
            {
                profile[i] = ringCount[i] > 0.0 ? ringAlpha[i] / ringCount[i] : 0.0;
            }
            double core = 0.0, coreCount = 0.0;
            for (int i = 0; i < rings / 5; i++)
            {
                core += ringAlpha[i];
                coreCount += ringCount[i];
            }
            core /= coreCount;
            double step = 0.0, wrap = 0.0;
            for (int f = 0; f < Frames; f++)
            {
                double change = Change(alphas[f], alphas[(f + 1) % Frames]);
                if (f + 1 < Frames)
                {
                    step = Math.Max(step, change);
                }
                else
                {
                    wrap = change;
                }
            }
            return new Measures(core, Crossing(profile, 0.9 * core), Crossing(profile, 0.1 * core), profile[rings - 1], corner,
                Percentile(alphaSlopes, 0.99), Percentile(shadeSlopes, 0.99), top / topWeight - bottom / bottomWeight, step, wrap,
                (top + bottom) / (topWeight + bottomWeight));
        }

        /// <summary>
        /// The puff against what a cloud needs: a body with a soft, wide edge, nothing at the quad's
        /// border, no crisp outline and no seam inside, lit from above, and frames that run on into each
        /// other, the last into the first. Returns the first case that does not hold, or null.
        /// </summary>
        public static string Check(CometBillow billow) => Judge(Measure(billow));

        /// <summary>The puff's measures against what a cloud needs (<see cref="Check(CometBillow)"/>): the first case that does not hold, or null.</summary>
        public static string Judge(Measures m)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (double.IsNaN(m.Core))
            {
                return "the cloud's puff has a texel out of range";
            }
            if (m.Core < CoreAlphaAtLeast)
            {
                return string.Format(c, "the cloud's puff is {0:0.###} opaque in its middle, under {1}: a haze, not a cloud", m.Core, CoreAlphaAtLeast);
            }
            if (m.EdgeTo - m.EdgeFrom < EdgeShareAtLeast)
            {
                return string.Format(c, "the cloud's puff falls from 90 % to 10 % of its opacity between {0:0.###} and {1:0.###} of its radius, over {2:0.###} of it against at least {3}: a disc with a hard edge",
                    m.EdgeFrom, m.EdgeTo, m.EdgeTo - m.EdgeFrom, EdgeShareAtLeast);
            }
            if (m.Rim > 0.01 || m.Corner > 0.004)
            {
                return string.Format(c, "the cloud's puff is still {0:0.###} opaque at the rim of its circle and {1:0.###} outside it: its quad would show", m.Rim, m.Corner);
            }
            if (m.AlphaSlope > AlphaSlopeAtMost)
            {
                return string.Format(c, "the cloud's puff changes opacity by {0:0.#} per radius in its steepest 1 % against at most {1}: a crisp outline", m.AlphaSlope, AlphaSlopeAtMost);
            }
            if (m.ShadeSlope > ShadeSlopeAtMost)
            {
                return string.Format(c, "the cloud's puff changes shade by {0:0.#} per radius in its steepest 1 % against at most {1}: a seam inside it", m.ShadeSlope, ShadeSlopeAtMost);
            }
            if (m.TopLighter < TopLighterBy)
            {
                return string.Format(c, "the cloud's puff is {0:0.###} lighter on top than below, under {1}: it is not lit from above", m.TopLighter, TopLighterBy);
            }
            if (m.Step > StepAtMost)
            {
                return string.Format(c, "the cloud's puff changes {0:0.###} from one frame to the next against at most {1}: it flickers as it steps through them", m.Step, StepAtMost);
            }
            if (m.Wrap > 1.5 * m.Step + 1e-9)
            {
                return string.Format(c, "the cloud's puff changes {0:0.###} from its last frame to its first against at most {1:0.###} between any two others: it jumps when its frames wrap", m.Wrap, m.Step);
            }
            return null;
        }

        private static double Crossing(double[] profile, double level)
        {
            for (int i = 0; i < profile.Length; i++)
            {
                if (profile[i] < level)
                {
                    if (i == 0)
                    {
                        return 0.0;
                    }
                    double t = (profile[i - 1] - level) / (profile[i - 1] - profile[i]);
                    return (i - 0.5 + t) / profile.Length;
                }
            }
            return 1.0;
        }

        private static double Percentile(System.Collections.Generic.List<double> values, double share)
        {
            values.Sort();
            return values.Count == 0 ? 0.0 : values[Math.Min(values.Count - 1, (int)(share * values.Count))];
        }

        private static double Change(float[] a, float[] b)
        {
            double sum = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                sum += Math.Abs(a[i] - b[i]);
            }
            return sum / a.Length;
        }

        /// <summary>
        /// Where the billows of the mass sit: a few large ones in the middle and many small ones round
        /// them, more toward the middle and heaped up on top, as cauliflower cloud is.
        /// </summary>
        private sealed class Layout
        {
            public double[] X, Y, Radius, Weight, Pulse, Drift, Orbit;

            public static Layout Make(int seed)
            {
                const int large = 4, small = 34;
                int count = large + small;
                Layout layout = new Layout
                {
                    X = new double[count], Y = new double[count], Radius = new double[count], Weight = new double[count],
                    Pulse = new double[count], Drift = new double[count], Orbit = new double[count],
                };
                Random random = new Random(seed);
                for (int i = 0; i < count; i++)
                {
                    bool big = i < large;
                    double angle = random.NextDouble() * 2.0 * Math.PI;
                    double distance = big ? 0.16 * Math.Sqrt(random.NextDouble()) : 0.22 + (Spread - 0.22) * Math.Sqrt(random.NextDouble());
                    double x = Math.Cos(angle) * distance;
                    double y = Math.Sin(angle) * distance;
                    // Heaped up: flattened in underneath.
                    y = (y > 0.0 ? y : 0.65 * y) - 0.03;
                    double radius = big ? BigRadius + 0.12 * random.NextDouble() : 0.13 + 0.14 * random.NextDouble();
                    layout.X[i] = x;
                    layout.Y[i] = y;
                    layout.Radius[i] = Math.Min(radius, Math.Max(0.06, 0.97 - Math.Sqrt(x * x + y * y)));
                    layout.Weight[i] = big ? 0.9 : 0.45 + 0.45 * random.NextDouble();
                    layout.Pulse[i] = random.NextDouble() * 2.0 * Math.PI;
                    layout.Drift[i] = random.NextDouble() * 2.0 * Math.PI;
                    layout.Orbit[i] = Wander * (big ? 0.5 : 1.0 + random.NextDouble());
                }
                return layout;
            }
        }

        /// <summary>Gradient noise in the plane, -1 to 1, smooth everywhere, from a seed: the same on every machine.</summary>
        private sealed class Noise
        {
            private readonly int[] _perm = new int[512];
            private readonly double[] _gx = new double[256];
            private readonly double[] _gy = new double[256];

            public static Noise Make(int seed)
            {
                Noise noise = new Noise();
                Random random = new Random(seed * 31 + 7);
                int[] p = new int[256];
                for (int i = 0; i < 256; i++)
                {
                    p[i] = i;
                    double angle = 2.0 * Math.PI * i / 256.0;
                    noise._gx[i] = Math.Cos(angle);
                    noise._gy[i] = Math.Sin(angle);
                }
                for (int i = 255; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (p[i], p[j]) = (p[j], p[i]);
                }
                for (int i = 0; i < 512; i++)
                {
                    noise._perm[i] = p[i & 255];
                }
                return noise;
            }

            public double Smooth(double x, double y)
            {
                double fx = Math.Floor(x), fy = Math.Floor(y);
                int ix = (int)fx & 255, iy = (int)fy & 255;
                double tx = x - fx, ty = y - fy;
                double u = tx * tx * tx * (tx * (tx * 6.0 - 15.0) + 10.0);
                double v = ty * ty * ty * (ty * (ty * 6.0 - 15.0) + 10.0);
                double n00 = Dot(_perm[_perm[ix] + iy], tx, ty);
                double n10 = Dot(_perm[_perm[ix + 1] + iy], tx - 1.0, ty);
                double n01 = Dot(_perm[_perm[ix] + iy + 1], tx, ty - 1.0);
                double n11 = Dot(_perm[_perm[ix + 1] + iy + 1], tx - 1.0, ty - 1.0);
                double a = n00 + (n10 - n00) * u;
                double b = n01 + (n11 - n01) * u;
                return 1.4142 * (a + (b - a) * v);
            }

            private double Dot(int hash, double x, double y) => _gx[hash] * x + _gy[hash] * y;
        }
    }
}
