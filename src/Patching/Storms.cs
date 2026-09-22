using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;
using Weather;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Two independent rules, either of which stops a world scheduling its own storm. They change
    /// whether a storm is scheduled and nothing else: a running storm, a storm's temperature, the
    /// dimming of the sun and rain and snow are all left alone. docs/STORMS.md is the design.
    ///
    /// Rule one, stripped: the planet holds less than a share of the air the world shipped with,
    /// counted across the tank's gas and liquid and all three reservoirs, in moles per outdoor cell.
    /// A world that ships with no air is exempt, and solar storms are never stopped by it.
    ///
    /// Rule two, mild: at the current point in the orbit, across a full day, the air stays between
    /// a temperature floor and ceiling, between a pressure floor and ceiling, and under a toxin
    /// ceiling. Seasonal on purpose, so a marginal world is calm in a good season and stormy in a bad
    /// one.
    ///
    /// Everything is worked out once per planet tick, in <see cref="Guards.Upkeep"/>, under the tank
    /// lock, and published as one immutable object. The scheduler asks about sixty times a second
    /// from the main thread and must never sum anything or take a lock, so it reads that one
    /// reference and plain fields off it.
    ///
    /// Nothing here is persisted: both rules are a pure function of live state, and the game already
    /// saves its own weather state.
    /// </summary>
    public static class Storms
    {
        /// <summary>The day sweep: 0 to 180 degrees in steps of 5, so 37 angles.</summary>
        private const int SweepAngles = 37;
        private const float SweepStepDegrees = 5f;

        /// <summary>
        /// The orbit has to move this far, in percentage points of the way from this world's
        /// furthest point from its sun to its nearest, before the day sweep is taken again.
        /// </summary>
        private const double OrbitStep = 1.0;

        /// <summary>
        /// The five gases the game counts as toxic to a human. There is no data source for this in
        /// the game: it is hand-written inside Atmosphere.PartialPressureHumanToxins, so it is
        /// hand-written here too and <see cref="SelfTest.CheckToxinList"/> checks the two still agree.
        /// Methane is Volatiles in the game's own text.
        /// </summary>
        internal static readonly Chemistry.GasType[] ToxinGases =
        {
            Chemistry.GasType.Pollutant,
            Chemistry.GasType.Methane,
            Chemistry.GasType.Hydrazine,
            Chemistry.GasType.Silanol,
            Chemistry.GasType.HydrochloricAcid,
        };

        /// <summary>
        /// What was true of the planet at the last tick. Built whole and published as one reference,
        /// so the scheduler and the readout can never see half of it. Public and plain because the
        /// readout, and tools/LiveCheck, both need every figure that went into the verdict.
        /// </summary>
        public sealed class Snapshot
        {
            public bool HasSolar;               // this world ships at least one event marked ActiveInOrbit
            public bool HasOther;               // ... and at least one that is not

            // Rule one.
            public bool Stripped;               // the rule fires right now
            public bool ShareKnown;
            public bool Airless;                // the world ships with no air, so the rule does not apply
            public string StripStoodDown;       // why the rule cannot be evaluated at all, or null
            public double Share;                // air now / air shipped, both per outdoor cell. 1.000 untouched
            public double ThresholdShare;

            // Rule two.
            public bool Mild;                   // the rule fires right now
            public bool MildKnown;
            public double Coldest;
            public double Hottest;
            public double PressureCold;
            public double PressureHot;
            public double Toxins;
            public double OrbitPercent;
            public List<string> MildFailures = new List<string>(0);
        }

        private static volatile Snapshot _now;
        private static int _faults;

        /// <summary>
        /// The verdict and every figure behind it, as of the last planet tick, or null while there
        /// is none: before the first tick of a world, on a client, and whenever the planet is not
        /// one the mod runs. Null is the open direction; nothing is suppressed without one.
        /// </summary>
        public static Snapshot Now => _now;

        // The day sweep, cached on the air. Written and read only from the planet tick, under the
        // tank lock, so these need no publication of their own.
        private static bool _haveSweep;
        private static double _sweepColdest;
        private static double _sweepHottest;
        private static object _keyData;
        private static float _keyGhgIndex;
        private static double _keyDensity;
        private static double _keyToxinMoles;
        private static double _keyOrbitPercent;
        private static bool _keyWeatherRunning;

        private static volatile string _toxinBoundOff;

        /// <summary>
        /// The toxin bound stands down rather than measure a list the game no longer uses. Called by
        /// <see cref="Patcher"/> when Atmosphere.PartialPressureHumanToxins no longer reads all five.
        /// </summary>
        internal static void StandDownToxinBound(string reason) => _toxinBoundOff = reason;

        /// <summary>Why the toxin bound is not being applied, or null while it is.</summary>
        public static string ToxinBoundStoodDown => _toxinBoundOff;

        /// <summary>Forget the day sweep and the verdict. Called at every world start.</summary>
        internal static void Invalidate()
        {
            _haveSweep = false;
            _keyData = null;
            _now = null;
        }

        // ---- the per-tick measure ---------------------------------------------------------------

        /// <summary>
        /// Last in <see cref="Guards.Upkeep"/>, after the phase change has been put back in
        /// proportion and after the pressure ceiling has scaled the tank, or it would forecast air
        /// that this same tick is about to change.
        ///
        /// It catches its own exceptions and leaves the verdict unknown, which is the open direction:
        /// nothing is suppressed while this cannot be worked out.
        /// </summary>
        internal static void Update()
        {
            try
            {
                _now = Measure();
            }
            catch (Exception e)
            {
                _now = null;
                if (Interlocked.Increment(ref _faults) <= 3)
                {
                    Log.Error("The storm rules could not be worked out and are not being applied: " + e);
                }
            }
        }

        private static Snapshot Measure()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            WorldSetting world = WorldSetting.Current;
            GlobalAtmosphereData data = world?.Data?.GlobalAtmosphereData;
            if (tank == null || data == null)
            {
                return null;
            }
            Snapshot snapshot = new Snapshot();
            CountWeatherEvents(world, snapshot);
            MeasureStripped(tank, data, snapshot);
            MeasureMild(tank, data, snapshot);
            return snapshot;
        }

        /// <summary>
        /// Whether this world ships a solar storm, an ordinary one, or both. A world's event list
        /// never changes while it is loaded, but it is read here rather than on the scheduler's
        /// thread so that the scheduler reads nothing but fields.
        /// </summary>
        private static void CountWeatherEvents(WorldSetting world, Snapshot snapshot)
        {
            List<WeatherEvent> events = world.WeatherEvents;
            if (events == null)
            {
                return;
            }
            for (int i = 0; i < events.Count; i++)
            {
                WeatherEvent weatherEvent = events[i];
                if (weatherEvent == null)
                {
                    continue;
                }
                if (weatherEvent.ActiveInOrbit)
                {
                    snapshot.HasSolar = true;
                }
                else
                {
                    snapshot.HasOther = true;
                }
            }
        }

        /// <summary>
        /// How much of the air the world shipped with is still on the planet, in moles per outdoor
        /// cell. All five stores count: gas leaving the sky for the tank's liquid, the clouds or the
        /// ice caps has not left the planet, and on Mars the whole of the carbon dioxide leaves the
        /// air in one tick every night (docs/TEMPERATURE.md). Real outdoor cells are deliberately
        /// left out; measured, they are four orders of magnitude below the threshold (docs/STORMS.md).
        ///
        /// The baseline is the world file's own figure, which is already per 8000 L cell and already
        /// free of planet size, so an untouched world sits at exactly 1.000 at any size.
        /// </summary>
        private static void MeasureStripped(GlobalGasMix tank, GlobalAtmosphereData data, Snapshot snapshot)
        {
            double shippedPerCell = data.TotalMolesGasses().ToDouble();
            // Ten percent of nothing is nothing, so without this an untouched airless world would
            // lose its weather on turn one.
            snapshot.Airless = !(shippedPerCell > 0.0);
            if (snapshot.Airless)
            {
                return;
            }
            // The clouds and the ice caps are most of the measure on a cold world: the Europa run
            // parked 19.9 of 20 mol per cell in the caps. With those fields unresolved the figure
            // would read low, which is the direction that switches weather off, so it stands down.
            if (!Planet.ReservoirsKnown)
            {
                snapshot.StripStoodDown = "this game build's clouds and ice caps could not be read, so the air left cannot be counted";
                return;
            }
            double cells = (tank.Volume / Chemistry.GridVolume).ToDouble();
            if (!(cells > 0.0) || double.IsInfinity(cells))
            {
                snapshot.StripStoodDown = "this world's planet has no usable volume";
                return;
            }
            double held = tank.TotalQuantity().ToDouble();
            foreach (GlobalGasMix reservoir in Planet.ReservoirMixes())
            {
                if (reservoir != null)
                {
                    held += reservoir.TotalQuantity().ToDouble();
                }
            }
            double perCell = held / cells;
            if (double.IsNaN(perCell) || double.IsInfinity(perCell))
            {
                snapshot.StripStoodDown = "the air on this planet could not be counted";
                return;
            }
            // A share of 0 means every last mole. GlobalGasMix has no minimum-quantity cleanup, so a
            // literal zero test would never fire; the game's own smallest quantity is the floor.
            double threshold = Math.Max(shippedPerCell * Settings.StrippedAtmosphereShare / 100.0,
                Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble());
            snapshot.ShareKnown = true;
            snapshot.Share = perCell / shippedPerCell;
            snapshot.ThresholdShare = threshold / shippedPerCell;
            snapshot.Stripped = Settings.StormsStopWhenStripped && perCell < threshold;
        }

        /// <summary>
        /// The day's coldest and hottest, and the pressure and toxin load at each, at the current
        /// point in the orbit.
        ///
        /// The sweep itself is cached on the air, because it costs 37 evaluations of the game's
        /// temperature formula. Three things move the forecast without the air moving, so the cache
        /// is keyed on them too: the orbit, which is the whole point of the rule being seasonal; a
        /// running storm, whose offset the formula adds; and the two heat offsets. The offsets are
        /// the clean case: they are the same at every angle, so they come out of the cached figures
        /// and go back in fresh every tick, which is free because Upkeep has just read both.
        ///
        /// The air is keyed on what the temperature actually consumes -- the greenhouse index and
        /// the gas density -- plus the toxin moles, and not on total moles: swapping 42 mol of
        /// methane for 42 mol of nitrogen leaves the total alone and changes all three.
        /// </summary>
        private static void MeasureMild(GlobalGasMix tank, GlobalAtmosphereData data, Snapshot snapshot)
        {
            OrbitalSimulation orbit = OrbitalSimulation.System;
            if (orbit == null)
            {
                return;
            }
            double percent = orbit.GetSolarEnergyPercentClamped(orbit.GetSolarEnergy(), orbit.CalculateSolarIrradiance());
            if (double.IsNaN(percent) || double.IsInfinity(percent))
            {
                return;
            }
            snapshot.OrbitPercent = percent;

            // The game adds each of these to every temperature it reports, and only when it is a
            // number. Read the same way, so what is taken out of the sweep is what went into it.
            double heat = Finite(PlanetaryAtmosphereSimulation.GetLatentTemperatureOffset().ToDouble())
                + Finite(PlanetaryAtmosphereSimulation.GetExternalInputEnergyOffset().ToDouble());

            float ghgIndex = TerraForming.GetGhgIndex(tank);
            MoleQuantity gas = tank.TotalQuantityGas();
            double density = IdealGas.GetMilliMolesPerLitre(tank.Volume, gas);
            MoleQuantity toxins = ToxinMoles(tank);
            bool weatherRunning = WeatherManager.IsWeatherEventRunning;

            if (!_haveSweep
                || !ReferenceEquals(_keyData, data)
                || ghgIndex != _keyGhgIndex
                || density != _keyDensity
                || toxins.ToDouble() != _keyToxinMoles
                || weatherRunning != _keyWeatherRunning
                || Math.Abs(percent - _keyOrbitPercent) >= OrbitStep)
            {
                if (!TakeSweep(tank, data, (float)percent, heat))
                {
                    _haveSweep = false;
                    return;
                }
                _keyData = data;
                _keyGhgIndex = ghgIndex;
                _keyDensity = density;
                _keyToxinMoles = toxins.ToDouble();
                _keyWeatherRunning = weatherRunning;
                _keyOrbitPercent = percent;
                _haveSweep = true;
            }

            double coldest = _sweepColdest + heat;
            double hottest = _sweepHottest + heat;
            if (double.IsNaN(coldest) || double.IsNaN(hottest))
            {
                return;
            }
            VolumeLitres gasVolume = tank.VolumeForGas();
            snapshot.Coldest = coldest;
            snapshot.Hottest = hottest;
            // Partial pressures at a forecast temperature are not on GlobalGasMix. The volume is the
            // volume left for gas, not a flat 8,000 L: the two separate as soon as the tank holds
            // liquid, which on a cold world is most of its air.
            snapshot.PressureCold = IdealGas.Pressure(gas, new TemperatureKelvin(Math.Max(0.0, coldest)), gasVolume).ToDouble();
            snapshot.PressureHot = IdealGas.Pressure(gas, new TemperatureKelvin(Math.Max(0.0, hottest)), gasVolume).ToDouble();
            snapshot.Toxins = IdealGas.Pressure(toxins, new TemperatureKelvin(Math.Max(0.0, hottest)), gasVolume).ToDouble();
            snapshot.MildKnown = true;

            CultureInfo c = CultureInfo.InvariantCulture;
            List<string> failures = new List<string>(5);
            // Written as the negation of the bound holding, so a figure that is not a number fails
            // rather than slips through: NaN loses every comparison.
            if (!(snapshot.Coldest >= Settings.MildAtmosphereColdestKelvin))
            {
                failures.Add(string.Format(c, "coldest {0:0.0} K is below the {1:0.##} K floor", snapshot.Coldest, Settings.MildAtmosphereColdestKelvin));
            }
            if (!(snapshot.Hottest <= Settings.MildAtmosphereHottestKelvin))
            {
                failures.Add(string.Format(c, "hottest {0:0.0} K is above the {1:0.##} K ceiling", snapshot.Hottest, Settings.MildAtmosphereHottestKelvin));
            }
            if (!(snapshot.PressureCold >= Settings.MildAtmosphereMinPressureKpa))
            {
                failures.Add(string.Format(c, "pressure {0:0.##} kPa at the coldest hour is below the {1:0.##} kPa minimum", snapshot.PressureCold, Settings.MildAtmosphereMinPressureKpa));
            }
            if (!(snapshot.PressureHot <= Settings.MildAtmosphereMaxPressureKpa))
            {
                failures.Add(string.Format(c, "pressure {0:0.##} kPa at the hottest hour is above the {1:0.##} kPa maximum", snapshot.PressureHot, Settings.MildAtmosphereMaxPressureKpa));
            }
            if (_toxinBoundOff == null && !(snapshot.Toxins <= Settings.MildAtmosphereMaxToxinsKpa))
            {
                failures.Add(string.Format(c, "toxins {0:0.###} kPa at the hottest hour are above the {1:0.##} kPa ceiling", snapshot.Toxins, Settings.MildAtmosphereMaxToxinsKpa));
            }
            snapshot.MildFailures = failures;
            snapshot.Mild = Settings.StormsStopWhenAtmosphereIsMild && failures.Count == 0;
        }

        /// <summary>
        /// The day's coldest and hottest with the two heat offsets taken back out, so a tick that
        /// only moved the heat can put the current ones back in without sweeping again.
        /// The three-argument overload is the one to call: the one-argument one recomputes the angle
        /// and the orbit percent from the sun on every call, which is wrong for a sweep.
        /// </summary>
        private static bool TakeSweep(GlobalGasMix tank, GlobalAtmosphereData data, float percent, double heat)
        {
            double coldest = double.MaxValue;
            double hottest = double.MinValue;
            for (int i = 0; i < SweepAngles; i++)
            {
                double kelvin = tank.GetGlobalGasMixTemperature(data, i * SweepStepDegrees, percent).ToDouble() - heat;
                if (double.IsNaN(kelvin) || double.IsInfinity(kelvin))
                {
                    return false;
                }
                if (kelvin < coldest)
                {
                    coldest = kelvin;
                }
                if (kelvin > hottest)
                {
                    hottest = kelvin;
                }
            }
            _sweepColdest = coldest;
            _sweepHottest = hottest;
            return true;
        }

        private static MoleQuantity ToxinMoles(GlobalGasMix tank)
        {
            MoleQuantity total = MoleQuantity.Zero;
            foreach (Chemistry.GasType type in ToxinGases)
            {
                // Get throws on Air and Fuel, which are mixtures rather than gases. None of the five
                // is one, but the guard is what keeps that true after a game update.
                if (Mole.MatterState(type) != AtmosphereHelper.MatterState.None)
                {
                    total += tank.Get(type);
                }
            }
            return total;
        }

        private static double Finite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
        }

        // ---- what the scheduler asks ------------------------------------------------------------

        /// <summary>
        /// Whether this particular event is one the world may not schedule right now. Read from the
        /// main thread, about sixty times a second while a world is suppressed, so it is one volatile
        /// read and then fields.
        ///
        /// Stripping never stops a solar storm: removing air cannot stop radiation. Mild air stops
        /// one only when the player has said it should, which is off by default because a solar
        /// storm is four times the normal solar power.
        /// </summary>
        public static bool Suppresses(WeatherEvent weatherEvent)
        {
            Snapshot snapshot = _now;
            if (snapshot == null || weatherEvent == null)
            {
                return false;
            }
            bool solar = weatherEvent.ActiveInOrbit;
            if (snapshot.Stripped && !solar)
            {
                return true;
            }
            return snapshot.Mild && (!solar || Settings.MildAtmosphereStopsSolarStorms);
        }

        /// <summary>
        /// True only when every event this world ships is suppressed, which is what lets the game be
        /// stopped before it picks one. A world with a solar storm and an ordinary one goes on
        /// picking while only the ordinary one is suppressed, and the pick is turned away instead;
        /// that costs a roll of the game's shared Random per frame, but it is the only way the solar
        /// storm still gets its turn.
        /// </summary>
        public static bool SuppressesEverything()
        {
            return SuppressesEverything(_now);
        }

        /// <summary>
        /// The same question of one snapshot. The readout asks it this way so that its verdict line
        /// and the figures printed under it are the same tick's; asking twice could straddle one.
        /// </summary>
        private static bool SuppressesEverything(Snapshot snapshot)
        {
            if (snapshot == null || (!snapshot.HasSolar && !snapshot.HasOther))
            {
                return false;
            }
            if (snapshot.HasOther && !(snapshot.Stripped || snapshot.Mild))
            {
                return false;
            }
            if (snapshot.HasSolar && !(snapshot.Mild && Settings.MildAtmosphereStopsSolarStorms))
            {
                return false;
            }
            return true;
        }

        // ---- the readout ------------------------------------------------------------------------

        /// <summary>
        /// Why weather is or is not happening. It never answers a bare no: every negative names the
        /// bound it failed with the measured value and the bound beside it, and it always says which
        /// point in the orbit the mild rule was evaluated at, because that rule is seasonal and a
        /// player reading "mild" today has to know it can lapse later in the year. Without that line
        /// an annual storm season reads as a bug.
        /// </summary>
        public static void Describe(StringBuilder text, CultureInfo c)
        {
            Snapshot snapshot = _now;
            if (snapshot == null)
            {
                // A client evaluates the temperature formula but never runs the planet tick, so it
                // has no forecast of its own, and one worked out from what it can see would disagree
                // with the host's. Say whose answer it is rather than invent one.
                string why = NetworkManager.IsClient
                    ? "the host decides this; a client is not asked"
                    : (Gate.Enabled() ? "not worked out yet" : Gate.Describe());
                text.AppendLine("  storms: " + why);
                return;
            }

            text.AppendLine("  storms: " + Verdict(snapshot, c));
            text.AppendLine("    stripped:  " + Stripped(snapshot, c));
            foreach (string line in Mild(snapshot, c))
            {
                text.AppendLine("    " + line);
            }
            text.AppendLine(string.Format(c,
                "    season:    {0:0}% of the way from this world's furthest point from its sun to its nearest; the mild rule is judged here and can lapse later in the year",
                snapshot.OrbitPercent));
            text.AppendLine("    solar:     " + Solar(snapshot));
            string rain = RainHeldBack(snapshot);
            if (rain != null)
            {
                text.AppendLine("    rain:      " + rain);
            }
        }

        private static string Verdict(Snapshot snapshot, CultureInfo c)
        {
            string running = WeatherManager.IsWeatherEventRunning && WeatherManager.CurrentWeatherEvent != null
                ? ", " + WeatherManager.CurrentWeatherEvent.Id + " is running now"
                : (WeatherManager.IsWeatherEventScheduled && WeatherManager.CurrentWeatherEvent != null
                    ? ", " + WeatherManager.CurrentWeatherEvent.Id + " is already scheduled and still arrives"
                    : "");
            if (!snapshot.HasSolar && !snapshot.HasOther)
            {
                return "this world ships no weather of its own" + running;
            }
            string because = Because(snapshot);
            if (because == null)
            {
                return "not suppressed, this world schedules its own storms" + running;
            }
            if (SuppressesEverything(snapshot))
            {
                return "suppressed, " + because + running;
            }
            return "partly suppressed, " + because + ", but this world's solar storms are not stopped by it" + running;
        }

        private static string Because(Snapshot snapshot)
        {
            if (snapshot.Stripped && snapshot.Mild)
            {
                return "the air has been stripped and what is left is mild";
            }
            if (snapshot.Stripped)
            {
                return "the air has been stripped";
            }
            return snapshot.Mild ? "the air is mild" : null;
        }

        private static string Stripped(Snapshot snapshot, CultureInfo c)
        {
            if (!Settings.StormsStopWhenStripped)
            {
                return "the rule is off in the config" + (snapshot.ShareKnown
                    ? string.Format(c, "; this planet holds {0:0.0}% of this world's starting air", snapshot.Share * 100.0)
                    : "");
            }
            if (snapshot.Airless)
            {
                return "this world ships with no air, so the rule does not apply to it";
            }
            if (snapshot.StripStoodDown != null)
            {
                return "not applied: " + snapshot.StripStoodDown;
            }
            if (!snapshot.ShareKnown)
            {
                return "not worked out yet";
            }
            return string.Format(c, "{0:0.0}% of this world's starting air, stops below {1:0.0}%{2}",
                snapshot.Share * 100.0, snapshot.ThresholdShare * 100.0,
                snapshot.Stripped ? "" : " (not stripped)");
        }

        private static IEnumerable<string> Mild(Snapshot snapshot, CultureInfo c)
        {
            if (!snapshot.MildKnown)
            {
                yield return "mild:      not worked out yet";
                yield break;
            }
            string headline;
            if (snapshot.MildFailures.Count > 0)
            {
                headline = "no, " + string.Join("; ", snapshot.MildFailures.ToArray());
            }
            else if (!Settings.StormsStopWhenAtmosphereIsMild)
            {
                headline = "yes at this point in the orbit, but the rule is off in the config";
            }
            else
            {
                headline = "yes, at this point in the orbit";
            }
            yield return "mild:      " + headline;
            yield return string.Format(c, "           coldest {0:0.0} K (floor {1:0.##})    hottest {2:0.0} K (ceiling {3:0.##})",
                snapshot.Coldest, Settings.MildAtmosphereColdestKelvin, snapshot.Hottest, Settings.MildAtmosphereHottestKelvin);
            yield return string.Format(c, "           pressure {0:0.##} to {1:0.##} kPa ({2:0.##} to {3:0.##})  toxins {4:0.###} kPa ({5})",
                snapshot.PressureCold, snapshot.PressureHot,
                Settings.MildAtmosphereMinPressureKpa, Settings.MildAtmosphereMaxPressureKpa, snapshot.Toxins,
                _toxinBoundOff != null
                    ? "bound off: " + _toxinBoundOff
                    : string.Format(c, "ceiling {0:0.##}", Settings.MildAtmosphereMaxToxinsKpa));
        }

        private static string Solar(Snapshot snapshot)
        {
            if (!snapshot.HasSolar)
            {
                return "this world has none";
            }
            if (!Settings.MildAtmosphereStopsSolarStorms)
            {
                return "not suppressed; stripping never stops one, and the setting that would let mild air stop one is off";
            }
            if (snapshot.Mild)
            {
                return "suppressed, the air is mild and the setting says mild air stops solar storms too";
            }
            return "not suppressed; mild air would stop one, but this air is not mild";
        }

        /// <summary>
        /// A cloud that has filled on a world with no weather of its own, while the setting that
        /// lets such a world rain is off. Without this the clouds fill and drain with no rain and no
        /// explanation. Read off the tick like the rest of the readout, so it can lag a tick.
        /// </summary>
        private static string RainHeldBack(Snapshot snapshot)
        {
            if (snapshot.HasSolar || snapshot.HasOther || Settings.WeatherOnWeatherlessWorlds || !Planet.ReservoirsKnown)
            {
                return null;
            }
            GlobalGasMix[] mixes = Planet.ReservoirMixes();
            // The two clouds only; the ice caps are compared against nothing, ever.
            for (int i = 0; i < 2 && i < mixes.Length; i++)
            {
                GlobalGasMix cloud = mixes[i];
                if (cloud != null && cloud.Volume.ToDouble() > 0.0 && cloud.VolumeOfLiquid().ToDouble() >= cloud.Volume.ToDouble())
                {
                    return "a cloud is full, but this world ships no weather of its own and \"Rain or snow on worlds with no weather\" is off, so nothing falls";
                }
            }
            return null;
        }
    }
}
