using System;

namespace TerraformingReloaded
{
    /// <summary>
    /// Plain values the patches read. The plugin binds BepInEx config entries onto these, so
    /// nothing under Patching/ depends on BepInEx and the patch set can be exercised outside
    /// the game by tools/PatchCheck.
    /// </summary>
    public static class Settings
    {
        public static bool Enabled = true;
        public static bool DynamicSky = true;

        /// <summary>Real-time minutes for stored external heat to halve. 0 disables decay.</summary>
        public static double ExternalHeatHalfLifeMinutes = 60.0;

        /// <summary>Largest shift, in kelvin, stored external heat may apply to the planet.</summary>
        public static double MaxExternalOffsetKelvin = 50.0;

        public static double GhgResponseScale = 1.0;
        public static double DensityResponseScale = 1.0;
        public static double AirlessAlbedo = 0.3;

        /// <summary>Planet pressure ceiling in kPa. 0 means no ceiling.</summary>
        public static double MaxPressureKPa = 0.0;

        /// <summary>
        /// Planet size as a share of what the game ships (40 billion litres, five million outdoor
        /// cells). It sets how long terraforming takes and nothing else: the starting air per cell,
        /// and so pressure, temperature and every reading, are identical at any size.
        /// Read when a planet is created: a new world, or terraform reset confirm.
        /// </summary>
        public static double PlanetSize = 0.05;

        public static double SyncIntervalSeconds = 5.0;
        public static bool WeatherOnWeatherlessWorlds = false;

        /// <summary>Write the terraform status to the log this often. 0 is off.</summary>
        public static double StatusLogSeconds = 0.0;
    }

    /// <summary>
    /// Named planet sizes. Hours are for reaching shirt-sleeve air on Mars, from tools/Balance, for a
    /// mega base making about 380,000 mol an hour; a base with one ice rocket is about four times slower.
    /// </summary>
    public enum PlanetSizePreset
    {
        /// <summary>1/100 of shipped. About 17 hours for a mega base, 70 for a one-rocket base.</summary>
        Short,
        /// <summary>1/20 of shipped. About 85 hours for a mega base, near the original mod's stated aim of 100.</summary>
        Standard,
        /// <summary>1/4 of shipped. About 410 hours for a mega base.</summary>
        Long,
        /// <summary>As the game ships it. About 1,650 hours for a mega base.</summary>
        UnmoddedBaseline,
        /// <summary>Use CustomPlanetSize.</summary>
        Custom,
    }

    public static class Log
    {
        public static Action<string> InfoSink = Console.WriteLine;
        public static Action<string> WarnSink = Console.WriteLine;
        public static Action<string> ErrorSink = Console.WriteLine;

        public static void Info(string message) => InfoSink(message);
        public static void Warn(string message) => WarnSink(message);
        public static void Error(string message) => ErrorSink(message);
    }
}
