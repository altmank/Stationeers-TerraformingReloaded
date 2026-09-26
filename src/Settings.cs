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

        /// <summary>
        /// Experimental, off by default: trace gases gather where they are consumed.
        /// </summary>
        public static bool TraceGasGatheringEnabled = false;

        /// <summary>
        /// How many times its normal share of a trace gas an outdoor cell draws from the planet, just
        /// under the trace line; it doubles for every factor of ten further below.
        /// 1 is the game's own share, which turns gathering off.
        /// </summary>
        public static double TraceGasGathering = 100.0;

        /// <summary>
        /// Below this many moles per 8000 L outdoor cell of planet air, a gas is a trace. A hundred
        /// times the least the game keeps in a cell (Chemistry.MINIMUM_QUANTITY_MOLES), and a tenth of
        /// the thinnest gas any shipped world starts with. 0 turns gathering off.
        /// </summary>
        public static double TraceGasLine = 1e-3;

        /// <summary>Write the terraform status to the log this often. 0 is off.</summary>
        public static double StatusLogSeconds = 0.0;
    }

    /// <summary>
    /// What a setting is allowed to be. One declaration per setting, used both to build the config
    /// entry's AcceptableValueRange and to check what comes out of a world's settings file, so a
    /// hand-edited file cannot carry a value the config editor would refuse and the two cannot
    /// drift apart when a range is changed.
    /// </summary>
    public readonly struct Range
    {
        public readonly double Min;
        public readonly double Max;

        /// <summary>True when Min itself is out, not in. See <see cref="AboveMin"/>.</summary>
        public readonly bool MinExcluded;

        public Range(double min, double max, bool minExcluded = false)
        {
            Min = min;
            Max = max;
            MinExcluded = minExcluded;
        }

        /// <summary>
        /// The same range with its lower end excluded. For the two settings the config flags with 0:
        /// in the config 0 is a real choice meaning "no rule", in a world's settings file that state
        /// is an absent element, so a 0 in the file is the config's sentinel leaking through a hand
        /// edit and is out of range exactly like a negative number would be.
        /// </summary>
        public Range AboveMin => new Range(Min, Max, true);

        /// <summary>
        /// Whether a value is one this setting may take. Not a number and not finite are both out:
        /// NaN fails every comparison, so it would otherwise slip through a naive bounds test, and
        /// an infinity poisons every figure derived from it.
        /// </summary>
        public bool Holds(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return false;
            }
            return (MinExcluded ? value > Min : value >= Min) && value <= Max;
        }

        public override string ToString()
        {
            return (MinExcluded ? "above " : "") + Min.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " to " + Max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The range of every setting that can come out of a world's settings file. Plain BCL, because
    /// <see cref="Patching.Sidecar"/> validates against these and nothing under Patching/ may
    /// reference BepInEx; Plugin.BindConfig builds each AcceptableValueRange from the same numbers.
    /// </summary>
    public static class Limits
    {
        /// <summary>In the config 0 means no ceiling. In a file use <see cref="Range.AboveMin"/>.</summary>
        public static readonly Range MaxPressureKPa = new Range(0.0, 10000.0);

        /// <summary>In the config 0 means never fades. In a file use <see cref="Range.AboveMin"/>.</summary>
        public static readonly Range ExternalHeatHalfLifeMinutes = new Range(0.0, 10000.0);

        public static readonly Range MaxExternalOffsetKelvin = new Range(0.0, 500.0);
        public static readonly Range GhgResponseScale = new Range(0.0, 5.0);
        public static readonly Range DensityResponseScale = new Range(0.0, 5.0);
        public static readonly Range AirlessAlbedo = new Range(0.0, 0.95);

        public static readonly Range StrippedAtmosphereShare = new Range(0.0, 100.0);
        public static readonly Range MildAtmosphereColdestKelvin = new Range(0.0, 1000.0);
        public static readonly Range MildAtmosphereHottestKelvin = new Range(0.0, 1000.0);
        public static readonly Range MildAtmosphereMinPressureKpa = new Range(0.0, 10000.0);
        public static readonly Range MildAtmosphereMaxPressureKpa = new Range(0.0, 10000.0);
        public static readonly Range MildAtmosphereMaxToxinsKpa = new Range(0.0, 1000.0);

        public static readonly Range TraceGasGathering = new Range(1.0, 100.0);
        public static readonly Range TraceGasLine = new Range(0.0, 0.01);
    }

    /// <summary>
    /// The world-scoped values in force for the world being played. Every setting that decides how
    /// a particular world behaves is here, so tuning one for a new save cannot change or damage an
    /// older one; <see cref="Patching.Sidecar"/> records them per world and assigns these whole at
    /// every world start. The config only decides what a new world starts with, and what a world
    /// that recorded nothing falls back to. Changing a world's own values is the console's job
    /// (terraform set). The patches read these, never <see cref="Settings"/>, which stays the
    /// config and is never written back: BepInEx saves it whenever the config editor moves a slider,
    /// so overwriting it would show numbers that are not in force and would need restoring on
    /// leaving a world. Assigned whole every time, so there is nothing to restore.
    ///
    /// The config cannot express a nullable double and uses 0 as a flag; these do not. Null here
    /// means the rule is off, so a consumer asks whether there is a ceiling rather than whether the
    /// ceiling is zero, and the sentinel dies at the boundary instead of leaking into the rules.
    /// The starting values are the config's own defaults, so out of the box the two agree.
    /// </summary>
    public static class Effective
    {
        // ---- the pressure ceiling ----------------------------------------------------------------
        //
        // The one setting that deletes a player's air for good and saves the loss, so it is the one
        // that must never be acquired by accident. It is a private field with no public setter, and
        // the only three ways it can become anything other than null are the three named methods
        // below, each of which says where the value came from. Every other path through the mod can
        // only turn it off, because turning it off is all the code it can reach does.
        //
        // The rule those three enforce between them: a LOADED world's ceiling comes only from that
        // world's own settings file, or from a deliberate console command on that world. A brand new
        // world takes the config's, which is safe because the player chose it for the world they are
        // creating and there is no history to damage. Anything else at all -- no file, an unreadable
        // file, a file from a later version, the read throwing, the self-test having switched the
        // per-world file off, a station name whose folder did not resolve, a network client -- is no
        // ceiling. Every future defect in the read path then degrades to "no ceiling" rather than to
        // "some ceiling", and no defect anywhere in it can delete a player's air.
        //
        // tools/ci/check_repo.py holds the call sites of all three to this list, so a later edit
        // cannot quietly add a fourth.
        private static double? _maxPressureKPa;

        /// <summary>Planet pressure ceiling in kPa. Null means no ceiling. Read-only by design.</summary>
        public static double? MaxPressureKPa => _maxPressureKPa;

        /// <summary>No ceiling. The state every path that is not one of the three below leaves it in.</summary>
        public static void NoCeiling()
        {
            _maxPressureKPa = null;
        }

        /// <summary>Recorded in the settings file of the world now loading, and read back from it.</summary>
        public static void CeilingFromWorldFile(double? kpa)
        {
            _maxPressureKPa = InRange(kpa);
        }

        /// <summary>
        /// A world being created, which takes the settings the player chose for it. Null when the
        /// config's 0 said no ceiling.
        /// </summary>
        public static void CeilingForNewWorld(double? kpa)
        {
            _maxPressureKPa = InRange(kpa);
        }

        /// <summary>terraform set MaxPressureKPa &lt;kPa&gt; confirm, on the world being played.</summary>
        public static void CeilingByConsoleCommand(double? kpa)
        {
            _maxPressureKPa = InRange(kpa);
        }

        /// <summary>
        /// Belt as well as braces: even the three ways in cannot install a ceiling the config editor
        /// would refuse, or one that is not a finite number.
        /// </summary>
        private static double? InRange(double? kpa)
        {
            return kpa.HasValue && Limits.MaxPressureKPa.AboveMin.Holds(kpa.Value) ? kpa : null;
        }

        // ---- everything else ----------------------------------------------------------------------
        // Plain fields, falling back to the config. The two heat settings do write into counters the
        // save carries, which is why terraform set asks before lowering them, but a wrong fallback
        // for them costs a world some banked heat, not its air.

        /// <summary>Real-time minutes for stored external heat to halve. Null means it never fades.</summary>
        public static double? ExternalHeatHalfLifeMinutes;

        /// <summary>Largest shift, in kelvin, stored external heat may apply to the planet.</summary>
        public static double MaxExternalOffsetKelvin = 50.0;

        public static double GhgResponseScale = 1.0;
        public static double DensityResponseScale = 1.0;
        public static double AirlessAlbedo = 0.3;

        public static bool DynamicSky = true;
        public static bool WeatherOnWeatherlessWorlds = true;

        public static bool StormsStopWhenStripped = true;
        public static double StrippedAtmosphereShare = 5.0;
        public static bool StormsStopWhenAtmosphereIsMild = true;
        public static double MildAtmosphereColdestKelvin = 263.15;
        public static double MildAtmosphereHottestKelvin = 323.15;
        public static double MildAtmosphereMinPressureKpa = 20.0;
        public static double MildAtmosphereMaxPressureKpa = 607.95;
        public static double MildAtmosphereMaxToxinsKpa = 1.0;
        public static bool MildAtmosphereStopsSolarStorms = false;

        public static bool TraceGasGatheringEnabled = false;
        public static double TraceGasGathering = 100.0;
        public static double TraceGasLine = 1e-3;
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
