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
        public static bool WeatherOnWeatherlessWorlds = true;

        // Storms. Two independent rules, either of which stops a world scheduling its own storm.
        // Every threshold is a setting, so there is no hidden rule (docs/STORMS.md).

        /// <summary>Once most of a world's starting air is gone, it stops scheduling its own storm.</summary>
        public static bool StormsStopWhenStripped = true;

        /// <summary>
        /// Share of the air the world started with, as a percentage, below which storms stop.
        /// 0 means every last mole.
        /// </summary>
        public static double StrippedAtmosphereShare = 5.0;

        /// <summary>A world whose air is temperate, thick and clean stops scheduling its own storm.</summary>
        public static bool StormsStopWhenAtmosphereIsMild = true;

        /// <summary>Coldest the air may get across a day, in kelvin, and still count as mild.</summary>
        public static double MildAtmosphereColdestKelvin = 263.15;

        /// <summary>Hottest the air may get across a day, in kelvin, and still count as mild.</summary>
        public static double MildAtmosphereHottestKelvin = 323.15;

        /// <summary>Least air pressure that counts as mild. The game's own PressureMinimumSafe.</summary>
        public static double MildAtmosphereMinPressureKpa = 20.0;

        /// <summary>Most air pressure that counts as mild. The game's own PressureMaximumSafe.</summary>
        public static double MildAtmosphereMaxPressureKpa = 607.95;

        /// <summary>Most toxic gas allowed, in kPa, measured at the hottest point of the day.</summary>
        public static double MildAtmosphereMaxToxinsKpa = 1.0;

        /// <summary>Air shields radiation, so a mild world stops solar storms as well.</summary>
        public static bool MildAtmosphereStopsSolarStorms = false;

        /// <summary>Write the terraform status to the log this often. 0 is off.</summary>
        public static double StatusLogSeconds = 0.0;
    }

    /// <summary>
    /// Named planet sizes. Hours are for reaching air you can breathe without a suit on Mars, from
    /// tools/Balance, for a mega base of four ice rockets mining 60 % of the time; a base with one ice
    /// rocket is about five times slower.
    /// </summary>
    public enum PlanetSizePreset
    {
        /// <summary>1/100 of shipped. About 15 hours for a mega base, 72 for a one-rocket base.</summary>
        Short,
        /// <summary>1/20 of shipped. About 75 hours for a mega base, near the original mod's stated aim of 100.</summary>
        Standard,
        /// <summary>1/4 of shipped. About 375 hours for a mega base.</summary>
        Long,
        /// <summary>As the game ships it. About 1,500 hours for a mega base.</summary>
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
