using System;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Everything a comet is drawn and heard with, made in code: textures, materials, meshes and
    /// sounds. Nothing is loaded from disk, so there is no asset bundle to keep in step with the
    /// game's Unity version. Made once, on first use, and kept for the session.
    /// </summary>
    internal static class CometArt
    {
        private static Texture2D _glow;
        private static Texture2D _smoke;
        private static Texture2D _smokeSheet;
        private static Texture2D _ring;
        private static Texture2D _billowSheet;
        private static Texture2D _fireballSheet;
        private static Texture2D _streak;
        private static Mesh _quad;
        private static AudioClip _roar;
        private static AudioClip _boom;
        private static AudioClip _rumble;
        private static AudioClip _thump;

        public static string ShaderReport { get; private set; } = "";

        /// <summary>A soft round glow, white, the falloff in alpha.</summary>
        public static Texture2D Glow => _glow ? _glow : (_glow = Radial(128, r => Mathf.Exp(-r * r * 9f) * 0.85f + Mathf.Exp(-r * r * 60f) * 0.15f));

        /// <summary>A lumpy puff of smoke: a round falloff broken up by noise.</summary>
        public static Texture2D Smoke => _smoke ? _smoke : (_smoke = MakeSmoke(128));

        /// <summary>Frames across and down in <see cref="SmokeSheet"/>.</summary>
        public const int SheetTiles = 4;

        /// <summary>
        /// The same kind of puff, sixteen frames of it rolling over and spreading, four by four in one
        /// texture, for a particle system to step through over each puff's life, so smoke billows
        /// instead of sliding.
        /// </summary>
        public static Texture2D SmokeSheet => _smokeSheet ? _smokeSheet : (_smokeSheet = MakeSmokeSheet(128));

        /// <summary>
        /// Sixteen frames, four by four, of a cauliflower of cloud: a cluster of round billows, each lit
        /// from above as a ball is, so a puff has a bright top, a shadowed underside and dark seams
        /// where billows meet, with wispy edges. The billows swell and roll across the frames. Grey, so
        /// a particle's colour tints it. Drawn upright: a system using it turns its puffs very little,
        /// so the light stays on top.
        /// </summary>
        public static Texture2D BillowSheet => _billowSheet ? _billowSheet : (_billowSheet = MakeBillowSheet(128));

        /// <summary>
        /// Sixteen frames, four by four, of a fireball's face: a disc darker and redder toward its limb,
        /// as a glowing ball is, broken into boiling bright cells with dark lanes between them, the
        /// cells churning from frame to frame, and a lumpy edge. A white-hot centre to a red limb in
        /// colour, which the particle's or quad's colour then cools.
        /// </summary>
        public static Texture2D FireballSheet => _fireballSheet ? _fireballSheet : (_fireballSheet = MakeFireballSheet(128));

        /// <summary>The fireball's own face, drawn before the glow and the smoke, so both cover it.</summary>
        public const int BallQueue = 3040;

        /// <summary>The bulges boiling off the fireball's face, drawn over it.</summary>
        public const int BoilQueue = 3045;

        /// <summary>A thin bright ring with a soft inside, for a shock front.</summary>
        public static Texture2D Ring => _ring ? _ring : (_ring = Radial(256, r =>
            Mathf.Exp(-Mathf.Pow((r - 0.86f) / 0.05f, 2f)) + 0.25f * Mathf.Exp(-Mathf.Pow((r - 0.74f) / 0.12f, 2f))));

        /// <summary>Bright down the middle, fading to both edges: across a trail's width.</summary>
        public static Texture2D Streak => _streak ? _streak : (_streak = MakeStreak(64));

        public static Mesh Quad => _quad ? _quad : (_quad = MakeQuad());

        /// <summary>Glow drawn before the smoke, so the smoke around it covers it from every side: fire inside a cloud.</summary>
        public const int InnerQueue = 3050;

        /// <summary>Smoke and dust.</summary>
        public const int SmokeQueue = 3100;

        /// <summary>Glow drawn over everything else: the comet, the flash, sparks and debris out in the open.</summary>
        public const int OuterQueue = 3150;

        /// <summary>
        /// How far, in metres, a particle fades out where it meets the ground or a wall, rather than
        /// cutting a hard line into it. The shaders take its inverse.
        /// </summary>
        private const float SoftMetres = 30f;

        /// <summary>Additive: light added to what is behind it. Fire, plasma, flashes.</summary>
        public static Material Additive(Texture texture, Color tint, int queue = OuterQueue)
        {
            return Make(texture, tint, queue, "Legacy Shaders/Particles/Additive", "Mobile/Particles/Additive", "Particles/Standard Unlit", "Sprites/Default");
        }

        /// <summary>Alpha blended: covers what is behind it. Smoke, dust.</summary>
        public static Material Blended(Texture texture, Color tint, int queue = SmokeQueue)
        {
            return Make(texture, tint, queue, "Legacy Shaders/Particles/Alpha Blended", "Mobile/Particles/Alpha Blended", "Particles/Standard Unlit", "Sprites/Default");
        }

        private static Material Make(Texture texture, Color tint, int queue, params string[] shaders)
        {
            foreach (string name in shaders)
            {
                Shader shader = Shader.Find(name);
                if (shader == null)
                {
                    continue;
                }
                Material material = new Material(shader) { mainTexture = texture, renderQueue = queue };
                if (material.HasProperty("_TintColor"))
                {
                    material.SetColor("_TintColor", tint);
                }
                else
                {
                    material.color = tint;
                }
                // The legacy particle shaders fade against the depth buffer when this keyword is on.
                bool soft = material.HasProperty("_InvFade");
                if (soft)
                {
                    material.SetFloat("_InvFade", 1f / SoftMetres);
                    material.EnableKeyword("SOFTPARTICLES_ON");
                }
                string entry = name + (soft ? " (soft)" : "");
                if (!ShaderReport.Contains(entry))
                {
                    ShaderReport += (ShaderReport.Length > 0 ? ", " : "") + entry;
                }
                return material;
            }
            throw new InvalidOperationException("None of the particle shaders " + string.Join(", ", shaders) + " is in this build of the game.");
        }

        private static Texture2D Radial(int size, Func<float, float> alpha)
        {
            Texture2D texture = NewTexture(size);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = r >= 1f ? 0f : Mathf.Clamp01(alpha(r)) * Mathf.Clamp01((1f - r) * 8f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D MakeSmoke(int size)
        {
            Texture2D texture = NewTexture(size);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;
                    float dx = u * 2f - 1f;
                    float dy = v * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float noise = 0f;
                    float amplitude = 0.5f;
                    float frequency = 3f;
                    for (int octave = 0; octave < 4; octave++)
                    {
                        noise += amplitude * Mathf.PerlinNoise(u * frequency + 17.3f, v * frequency + 4.1f);
                        amplitude *= 0.5f;
                        frequency *= 2.1f;
                    }
                    float falloff = Mathf.Clamp01(1f - r);
                    float a = Mathf.Clamp01(Mathf.Pow(falloff, 1.3f) * (0.35f + 1.1f * noise) * 1.3f);
                    byte shade = (byte)(200 + 55 * Mathf.Clamp01(noise));
                    pixels[y * size + x] = new Color32(shade, shade, shade, (byte)(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D MakeSmokeSheet(int frame)
        {
            int size = frame * SheetTiles;
            Texture2D texture = NewTexture(size);
            Color32[] pixels = new Color32[size * size];
            int frames = SheetTiles * SheetTiles;
            for (int f = 0; f < frames; f++)
            {
                // Top-left first, the order a particle system steps through a sheet.
                int column = f % SheetTiles;
                int row = SheetTiles - 1 - f / SheetTiles;
                float age = f / (float)(frames - 1);
                // Two layers of noise drift past each other, so the puff turns over, and it swells a little.
                float roll = age * 1.6f;
                float swell = 0.86f + 0.14f * age;
                for (int y = 0; y < frame; y++)
                {
                    for (int x = 0; x < frame; x++)
                    {
                        float u = (x + 0.5f) / frame;
                        float v = (y + 0.5f) / frame;
                        float dx = (u * 2f - 1f) / swell;
                        float dy = (v * 2f - 1f) / swell;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float noise = 0f;
                        float amplitude = 0.5f;
                        float frequency = 3f;
                        for (int octave = 0; octave < 4; octave++)
                        {
                            float a = Mathf.PerlinNoise(u * frequency + 17.3f + roll, v * frequency + 4.1f - roll * 0.6f);
                            float b = Mathf.PerlinNoise(u * frequency + 41.7f - roll * 0.8f, v * frequency + 23.9f + roll);
                            noise += amplitude * Mathf.Lerp(a, b, 0.5f + 0.5f * Mathf.Sin(age * Mathf.PI + octave));
                            amplitude *= 0.5f;
                            frequency *= 2.1f;
                        }
                        float falloff = Mathf.Clamp01(1f - r);
                        float alpha = Mathf.Clamp01(Mathf.Pow(falloff, 1.3f) * (0.35f + 1.1f * noise) * 1.3f);
                        byte shade = (byte)(190 + 65 * Mathf.Clamp01(noise));
                        pixels[(row * frame + y) * size + column * frame + x] = new Color32(shade, shade, shade, (byte)(alpha * 255f));
                    }
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D MakeBillowSheet(int frame)
        {
            int size = frame * SheetTiles;
            Texture2D texture = NewTexture(size);
            Color32[] pixels = new Color32[size * size];
            int frames = SheetTiles * SheetTiles;

            // The billows, the same in every frame: a big one in the middle, the rest around and above it.
            const int count = 9;
            float[] cx = new float[count];
            float[] cy = new float[count];
            float[] radius = new float[count];
            float[] driftX = new float[count];
            float[] driftY = new float[count];
            float[] depth = new float[count];
            System.Random random = new System.Random(4127);
            for (int i = 0; i < count; i++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float distance = i == 0 ? 0f : Mathf.Sqrt((float)random.NextDouble()) * 0.42f;
                cx[i] = Mathf.Cos(angle) * distance;
                cy[i] = Mathf.Sin(angle) * distance * 0.8f + (i == 0 ? -0.05f : 0.06f);
                radius[i] = i == 0 ? 0.42f : 0.22f + 0.2f * (float)random.NextDouble();
                float roll = (float)random.NextDouble() * Mathf.PI * 2f;
                driftX[i] = Mathf.Cos(roll);
                driftY[i] = Mathf.Sin(roll);
                depth[i] = (float)random.NextDouble() * 0.25f;
            }
            Vector3 light = new Vector3(-0.35f, 0.8f, 0.5f).normalized;

            for (int f = 0; f < frames; f++)
            {
                int column = f % SheetTiles;
                int row = SheetTiles - 1 - f / SheetTiles;
                float age = f / (float)(frames - 1);
                float swell = 0.88f + 0.16f * age;
                float turn = age * 1.4f;
                for (int y = 0; y < frame; y++)
                {
                    for (int x = 0; x < frame; x++)
                    {
                        float u = (x + 0.5f) / frame;
                        float v = (y + 0.5f) / frame;
                        float px = u * 2f - 1f;
                        float py = v * 2f - 1f;
                        // Ragged outlines: the distance into each billow is pushed about by noise.
                        float warp = Mathf.PerlinNoise(u * 5f + 3.1f + turn * 0.5f, v * 5f + 7.7f) - 0.5f;
                        float front = float.NegativeInfinity;
                        float shade = 0f;
                        float cover = 0f;
                        for (int i = 0; i < count; i++)
                        {
                            float rb = radius[i] * swell;
                            float dx = (px - cx[i] - driftX[i] * 0.06f * age) / rb;
                            float dy = (py - cy[i] - driftY[i] * 0.06f * age - 0.04f * age) / rb;
                            float d2 = dx * dx + dy * dy + 0.35f * warp;
                            if (d2 >= 1f)
                            {
                                continue;
                            }
                            cover = Mathf.Max(cover, Mathf.Clamp01((1f - d2) * 4f));
                            float bulge = Mathf.Sqrt(1f - Mathf.Max(0f, d2));
                            float z = depth[i] + bulge * rb;
                            if (z > front)
                            {
                                front = z;
                                Vector3 normal = new Vector3(dx, dy, bulge).normalized;
                                float lambert = Mathf.Clamp01((Vector3.Dot(normal, light) + 0.15f) / 1.15f);
                                shade = 0.3f + 0.7f * Mathf.Pow(lambert, 0.8f);
                            }
                        }
                        if (cover <= 0f)
                        {
                            pixels[(row * frame + y) * size + column * frame + x] = new Color32(0, 0, 0, 0);
                            continue;
                        }
                        // Billows sitting further back are in the shade of those in front of them.
                        shade *= 0.72f + 0.28f * Mathf.Clamp01(front / 0.55f);
                        float detail = Mathf.PerlinNoise(u * 9f + turn, v * 9f - turn * 0.5f);
                        shade = Mathf.Clamp01(shade * (0.85f + 0.3f * detail));
                        float border = Mathf.Clamp01((1f - Mathf.Max(Mathf.Abs(px), Mathf.Abs(py))) * 10f);
                        float alpha = cover * (0.82f + 0.18f * detail) * border;
                        byte grey = (byte)(shade * 255f);
                        pixels[(row * frame + y) * size + column * frame + x] = new Color32(grey, grey, grey, (byte)(Mathf.Clamp01(alpha) * 255f));
                    }
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D MakeFireballSheet(int frame)
        {
            int size = frame * SheetTiles;
            Texture2D texture = NewTexture(size);
            Color32[] pixels = new Color32[size * size];
            int frames = SheetTiles * SheetTiles;
            for (int f = 0; f < frames; f++)
            {
                int column = f % SheetTiles;
                int row = SheetTiles - 1 - f / SheetTiles;
                // The noise is moved round a circle, so the last frame runs on into the first.
                float phase = f / (float)frames * Mathf.PI * 2f;
                float ox = Mathf.Cos(phase) * 0.9f;
                float oy = Mathf.Sin(phase) * 0.9f;
                for (int y = 0; y < frame; y++)
                {
                    for (int x = 0; x < frame; x++)
                    {
                        float px = (x + 0.5f) / frame * 2f - 1f;
                        float py = (y + 0.5f) / frame * 2f - 1f;
                        float r = Mathf.Sqrt(px * px + py * py) + 0.06f * (Mathf.PerlinNoise(px * 3f + ox + 5f, py * 3f + oy + 9f) - 0.5f);
                        int at = (row * frame + y) * size + column * frame + x;
                        if (r >= 0.97f)
                        {
                            pixels[at] = new Color32(0, 0, 0, 0);
                            continue;
                        }
                        float mu = Mathf.Sqrt(Mathf.Max(0f, 1f - r * r));
                        // Cells crowd toward the limb, as they would on a ball seen edge-on there.
                        float k = 1f / (0.35f + 0.65f * mu);
                        float sx = px * k;
                        float sy = py * k;
                        float cells = 0f;
                        float amplitude = 0.6f;
                        float frequency = 3.2f;
                        for (int octave = 0; octave < 3; octave++)
                        {
                            float n = Mathf.PerlinNoise(sx * frequency + ox * frequency * 0.3f + 13f, sy * frequency + oy * frequency * 0.3f + 29f);
                            float lane = 1f - Mathf.Abs(2f * n - 1f);
                            cells += amplitude * (1f - lane * lane * lane * lane);
                            amplitude *= 0.5f;
                            frequency *= 2.05f;
                        }
                        cells /= 1.05f;
                        float limb = 0.35f + 0.65f * Mathf.Pow(mu, 0.55f);
                        float b = Mathf.Clamp01(limb * (0.45f + 0.75f * cells));
                        float alpha = Mathf.Clamp01((0.97f - r) / 0.06f);
                        pixels[at] = new Color32((byte)(Mathf.Pow(b, 0.55f) * 255f), (byte)(Mathf.Pow(b, 1.25f) * 255f), (byte)(Mathf.Pow(b, 2.6f) * 255f), (byte)(alpha * 255f));
                    }
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D MakeStreak(int size)
        {
            Texture2D texture = NewTexture(size);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float across = (y + 0.5f) / size * 2f - 1f;
                float a = Mathf.Exp(-across * across * 5f) * 0.7f + Mathf.Exp(-across * across * 40f) * 0.3f;
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Texture2D NewTexture(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
        }

        /// <summary>A unit square in X and Y, centred, facing -Z. The particle shaders draw both sides.</summary>
        private static Mesh MakeQuad()
        {
            Mesh mesh = new Mesh { name = "CometQuad", hideFlags = HideFlags.DontUnloadUnusedAsset };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return mesh;
        }

        // ---- Sound. Synthesised, mono, 44.1 kHz. ----

        private const int Rate = 44100;

        /// <summary>The comet tearing through the air: a rising roar, low rumble under a hiss.</summary>
        public static AudioClip Roar => _roar ? _roar : (_roar = Synth("CometRoar", 7f, 11, (t, n) =>
        {
            float rise = Mathf.Pow(Mathf.Clamp01(t / 7f), 2.2f);
            float flutter = 0.75f + 0.25f * Mathf.Sin(t * 23f + Mathf.Sin(t * 5.3f) * 3f);
            return rise * (n.Low * 0.85f + n.High * 0.22f * rise) * flutter;
        }));

        /// <summary>
        /// The impact arriving: a crack, a chest-deep thump falling in pitch, and a long tail of
        /// rumble. It comes seconds after the flash, as far behind it as sound is from the light.
        /// </summary>
        public static AudioClip Boom => _boom ? _boom : (_boom = Synth("CometBoom", 11f, 29, (t, n) =>
        {
            float crack = n.White * Mathf.Exp(-t / 0.018f) * 0.9f + n.Mid * Mathf.Exp(-t / 0.12f) * 0.6f;
            float pitch = Mathf.Lerp(58f, 24f, Mathf.Clamp01(t / 1.6f));
            float thump = Mathf.Sin(2f * Mathf.PI * pitch * t) * Mathf.Exp(-t / 1.1f) * (1f - Mathf.Exp(-t / 0.004f));
            float tail = n.Low * (1f - Mathf.Exp(-t / 0.08f)) * Mathf.Exp(-t / 3.8f) * 1.15f;
            float aftershock = n.Low * 0.45f * Mathf.Exp(-Mathf.Pow((t - 3.2f) / 0.9f, 2f));
            return crack + thump * 0.95f + tail + aftershock;
        }));

        /// <summary>
        /// The impact through the ground: a deep thud felt through the feet, with no crack in it. It
        /// arrives in a vacuum too, so an airless world gets this and no boom.
        /// </summary>
        public static AudioClip Thump => _thump ? _thump : (_thump = Synth("CometThump", 3.5f, 71, (t, n) =>
        {
            float pitch = Mathf.Lerp(42f, 22f, Mathf.Clamp01(t / 1.2f));
            float body = Mathf.Sin(2f * Mathf.PI * pitch * t) * Mathf.Exp(-t / 0.7f) * (1f - Mathf.Exp(-t / 0.012f));
            float shudder = n.Low * Mathf.Exp(-t / 1.4f) * (1f - Mathf.Exp(-t / 0.05f));
            return body * 1.1f + shudder * 0.8f;
        }));

        /// <summary>A low ground rumble for the aftermath.</summary>
        public static AudioClip Rumble => _rumble ? _rumble : (_rumble = Synth("CometRumble", 18f, 53, (t, n) =>
        {
            float swell = 0.6f + 0.4f * Mathf.Sin(t * 0.9f) * Mathf.Sin(t * 0.37f + 1f);
            return n.Low * 0.7f * swell * Mathf.Clamp01(t / 1.5f) * Mathf.Clamp01((18f - t) / 6f);
        }));

        private struct Noise
        {
            public float White;
            public float Mid;
            public float Low;
            public float High;
        }

        private static AudioClip Synth(string name, float seconds, int seed, Func<float, Noise, float> sample)
        {
            int count = (int)(seconds * Rate);
            float[] data = new float[count];
            System.Random random = new System.Random(seed);
            float brown = 0f;
            float low = 0f;
            float low2 = 0f;
            float mid = 0f;
            float previousWhite = 0f;
            float peak = 0f;
            for (int i = 0; i < count; i++)
            {
                float white = (float)(random.NextDouble() * 2.0 - 1.0);
                brown = Mathf.Clamp((brown + 0.02f * white) / 1.02f, -1f, 1f);
                low += 0.02f * (brown * 3.5f - low);
                low2 += 0.03f * (low - low2);
                mid += 0.15f * (white - mid);
                Noise n = new Noise { White = white, Mid = mid * 2f, Low = low2 * 3f, High = (white - previousWhite) * 0.5f };
                previousWhite = white;
                float value = sample(i / (float)Rate, n);
                data[i] = value;
                peak = Mathf.Max(peak, Mathf.Abs(value));
            }
            float gain = peak > 0f ? 0.95f / peak : 1f;
            for (int i = 0; i < count; i++)
            {
                data[i] = (float)Math.Tanh(data[i] * gain * 1.4f) / (float)Math.Tanh(1.4f);
            }
            AudioClip clip = AudioClip.Create(name, count, 1, Rate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return clip;
        }
    }
}
