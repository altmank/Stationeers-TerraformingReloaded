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
