using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.UI.ImGuiUi;
using HarmonyLib;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Same shape as a world file's GlobalAtmosphere block, so anyone who has edited a world can
    /// edit this. Public with public fields because XmlSerializer needs it.
    /// </summary>
    [XmlRoot("TerraformingCurves")]
    public class ResponseCurves
    {
        [XmlElement("GHGTemperatureOffset")]
        public GlobalTemperatureCurveOffset Ghg = new GlobalTemperatureCurveOffset();

        [XmlElement("DensityOffset")]
        public GlobalTemperatureCurveOffset Density = new GlobalTemperatureCurveOffset();
    }

    /// <summary>
    /// The game's planet temperature is a base curve plus offsets for sun distance, greenhouse gases
    /// and air density, but only Mars and three tutorials ship the greenhouse and density curves, and
    /// the Moon and Mimas ship no base curve at all. On every other world the air can change and the
    /// temperature cannot follow. One postfix on the game's temperature formula fills the gaps and
    /// leaves any part a world ships itself alone. docs/TEMPERATURE.md has the reasoning;
    /// tools/Balance/planet.py is the same arithmetic, and tools/LiveCheck -Observe compares the two.
    ///
    /// Greenhouse. The default response curve g(index) is measured from the world's starting air.
    /// A world that starts under a real greenhouse load (Venus, Vulcan) is "anchored": its shipped
    /// mean temperature is read as bare-rock equilibrium plus the warming its starting air causes,
    /// so the positive part of g is scaled by gain = warming / g(start index). Strip the greenhouse
    /// gases and the mean falls to equilibrium. Cooling (g below zero) is never amplified.
    ///
    /// Density. Thick air evens out day and night. The day-minus-night gap s(density) of the default
    /// density curve, relative to the gap at the starting air, scales the world's own swing about its
    /// mean; it is clamped so thinning the air can never make the swing larger than shipped.
    ///
    /// Every term is zero on untouched air, so a world nobody has touched reads exactly what it
    /// reads without the mod (checked at world start). The exception is deliberate: an airless world
    /// with no base curve reads 0 K in the shipped game and gets a bare-rock temperature here.
    ///
    /// Runs on atmosphere worker threads, several times per outdoor cell per tick.
    /// </summary>
    public static class Climate
    {
        private const string DefaultCurvesXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<TerraformingCurves>
  <!-- X is the greenhouse index summed from the game's Data/terraforming.xml. Y is kelvin. -->
  <GHGTemperatureOffset>
    <Day>
      <Key Time=""-100"" Value=""-50"" InTangent=""0"" OutTangent=""-0.2"" InWeight=""0"" OutWeight=""0.3"" WeightedMode=""Both"" />
      <Key Time=""0"" Value=""0"" InTangent=""1.5"" OutTangent=""0"" InWeight=""0"" OutWeight=""0.1"" WeightedMode=""Both"" />
      <Key Time=""100"" Value=""250"" InTangent=""0"" OutTangent=""0"" InWeight=""0.5"" OutWeight=""0"" WeightedMode=""Both"" />
    </Day>
    <Night>
      <Key Time=""-100"" Value=""-50"" InTangent=""0"" OutTangent=""-0.2"" InWeight=""0"" OutWeight=""0.3"" WeightedMode=""Both"" />
      <Key Time=""0"" Value=""0"" InTangent=""1.5"" OutTangent=""0"" InWeight=""0"" OutWeight=""0.1"" WeightedMode=""Both"" />
      <Key Time=""100"" Value=""250"" InTangent=""0"" OutTangent=""0"" InWeight=""0.5"" OutWeight=""0"" WeightedMode=""Both"" />
    </Night>
  </GHGTemperatureOffset>
  <!-- X is gas density in millimoles per litre. Thin air swings hard between day and night; thick air evens out. -->
  <DensityOffset>
    <Day>
      <Key Time=""0"" Value=""50"" InTangent=""0"" OutTangent=""0"" InWeight=""0"" OutWeight=""0.01"" WeightedMode=""Both"" />
      <Key Time=""1.2"" Value=""15"" InTangent=""0"" OutTangent=""-0.9"" InWeight=""0.3"" OutWeight=""0.1"" WeightedMode=""Both"" />
      <Key Time=""100"" Value=""-10"" InTangent=""0"" OutTangent=""0"" InWeight=""0.9"" OutWeight=""0"" WeightedMode=""Both"" />
    </Day>
    <Night>
      <Key Time=""0"" Value=""-60"" InTangent=""0"" OutTangent=""0"" InWeight=""0"" OutWeight=""0.01"" WeightedMode=""Both"" />
      <Key Time=""1.2"" Value=""-25"" InTangent=""0"" OutTangent=""1.1"" InWeight=""0.3"" OutWeight=""0.25"" WeightedMode=""Both"" />
      <Key Time=""100"" Value=""10"" InTangent=""0.2"" OutTangent=""0"" InWeight=""0.1"" OutWeight=""0"" WeightedMode=""Both"" />
    </Night>
  </DensityOffset>
</TerraformingCurves>
";

        /// <summary>The built-in curves, as the text a player would edit.</summary>
        public static string DefaultXml => DefaultCurvesXml;

        private static ResponseCurves _curves;

        // One rule for every world; none of these is a per-world number.
        private const double AnchorAlbedo = 0.3;        // fixed: the AirlessAlbedo setting must not move a hot world mid-save
        private const double AnchorIndexFrom = 5.0;     // below this starting index a world is not anchored at all
        private const double AnchorIndexFull = 10.0;    // from here up it is fully anchored; blended between, so there is no cliff
        private const double AnchorMaxGain = 10.0;      // bounds the gain when g(start index) is small

        /// <summary>What is known about one world's atmosphere data. Immutable, swapped whole.</summary>
        private sealed class Entry
        {
            public GlobalAtmosphereData Data;
            public ResponseCurves Curves;
            public bool Adjust;
            public bool FillGhg;
            public bool FillDensity;
            public bool FillBase;
            public float StartGhgIndex;
            public float StartDensity;
            public double Albedo;           // the settings this entry was worked out with
            public double DensityScale;
            public double GhgScale;
            public double AirlessBase;      // bare-rock kelvin for a FillBase world
            public double SunMid;           // W/m2 at the middle of the orbit's range
            public double Mean0;            // shipped mean of day and night at mid orbit
            public double Gain;
            public double G0Warm;
            public double G0Cool;
            public double Swing0;
            public double DensityMean0;
            public string Problem;
        }

        private static volatile Entry _entry;

        public static bool Ready => _curves != null;

        /// <summary>
        /// Loads the response curves, from <paramref name="overridePath"/> when that file exists.
        /// Builds Unity AnimationCurves, so it needs the Unity runtime.
        /// </summary>
        public static void Load(string overridePath)
        {
            ResponseCurves curves = null;
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            {
                try
                {
                    using (StreamReader reader = new StreamReader(overridePath))
                    {
                        curves = Parse(reader);
                    }
                    Build(curves.Ghg);
                    Build(curves.Density);
                    Log.Info("Using response curves from " + overridePath);
                }
                catch (Exception e)
                {
                    Log.Error("Could not read " + overridePath + ", using the built-in curves. " + e.Message);
                    curves = null;
                }
            }
            if (curves == null)
            {
                using (StringReader reader = new StringReader(DefaultCurvesXml))
                {
                    curves = Parse(reader);
                }
                Build(curves.Ghg);
                Build(curves.Density);
            }
            _curves = curves;
            _entry = null;
        }

        /// <summary>
        /// The game's own Init() also fills the text boxes of its in-game curve editor, through a
        /// string cache that does not exist yet when mods load. Only the curve is wanted, so build
        /// that alone and leave the game's day and night blending to evaluate it.
        /// </summary>
        private static void Build(GlobalTemperatureCurveOffset offset)
        {
            MethodInfo setCurve = AccessTools.PropertySetter(typeof(AnimationCurveData), nameof(AnimationCurveData.Curve))
                ?? throw new MissingMethodException(nameof(AnimationCurveData), "set_Curve");
            foreach (AnimationCurveData data in new[] { offset.DayCurveOffsetData, offset.NightCurveOffsetData })
            {
                if (data == null || data.Keys == null || data.Keys.Count == 0)
                {
                    throw new InvalidDataException("Every Day and Night block needs at least one Key.");
                }
                setCurve.Invoke(data, new object[] { AnimationCurveData.Create(data) });
            }
        }

        private static ResponseCurves Parse(TextReader reader)
        {
            ResponseCurves curves = (ResponseCurves)new XmlSerializer(typeof(ResponseCurves)).Deserialize(reader);
            if (curves?.Ghg == null || curves.Density == null)
            {
                throw new InvalidDataException("GHGTemperatureOffset and DensityOffset are both required.");
            }
            return curves;
        }

        /// <summary>Forget what was worked out for the current world. Called at world start.</summary>
        internal static void Invalidate() => _entry = null;

        private static Entry Lookup(GlobalAtmosphereData data)
        {
            Entry entry = _entry;
            ResponseCurves curves = _curves;
            if (entry != null && ReferenceEquals(entry.Data, data) && ReferenceEquals(entry.Curves, curves)
                && entry.Albedo == Settings.AirlessAlbedo && entry.DensityScale == Settings.DensityResponseScale
                && entry.GhgScale == Settings.GhgResponseScale)
            {
                return entry;
            }
            // Only the world being played is adjusted. The new-game menu evaluates these same
            // functions for worlds that are not loaded, and those should read as shipped.
            WorldSetting world = WorldSetting.Current;
            if (curves == null || world?.Data == null || !ReferenceEquals(world.Data.GlobalAtmosphereData, data))
            {
                return null;
            }
            // The greenhouse index reads these; before they load, a starting index would be wrong.
            if (TerraForming.TerraformingGasCurves.Count == 0)
            {
                return null;
            }

            lock (BuildLock)
            {
                entry = _entry;
                if (entry != null && ReferenceEquals(entry.Data, data) && ReferenceEquals(entry.Curves, curves)
                    && entry.Albedo == Settings.AirlessAlbedo && entry.DensityScale == Settings.DensityResponseScale
                    && entry.GhgScale == Settings.GhgResponseScale)
                {
                    return entry;
                }
                try
                {
                    entry = BuildEntry(data, curves, world);
                }
                catch (Exception e)
                {
                    // A world file the game itself can barely load. Say so once and stay out, rather
                    // than throw on every outdoor cell of every tick.
                    entry = new Entry
                    {
                        Data = data, Curves = curves, Adjust = false, Albedo = Settings.AirlessAlbedo,
                        DensityScale = Settings.DensityResponseScale, GhgScale = Settings.GhgResponseScale,
                        Problem = "this world's atmosphere data could not be read (" + e.Message + ")",
                    };
                    Log.Error("Temperature response is off for this world: " + entry.Problem);
                }
                _entry = entry;
                return entry;
            }
        }

        private static readonly object BuildLock = new object();

        private static Entry BuildEntry(GlobalAtmosphereData data, ResponseCurves curves, WorldSetting world)
        {
            GlobalGasMix start = GlobalGasMix.Create(data);
            bool airless = start.TotalQuantity() <= MoleQuantity.Zero;
            Entry entry = new Entry
            {
                Data = data,
                Curves = curves,
                Adjust = !world.IsTutorial,
                FillGhg = IsEmpty(data.GHGTemperatureOffset),
                FillDensity = IsEmpty(data.DensityOffset),
                FillBase = airless && (data.SolarAngleTemperatureCurveData?.Keys == null || data.SolarAngleTemperatureCurveData.Keys.Count == 0),
                StartGhgIndex = TerraForming.GetGhgIndex(start),
                StartDensity = (float)IdealGas.GetMilliMolesPerLitre(start.Volume, start.TotalQuantityGas()),
                Albedo = Settings.AirlessAlbedo,
                DensityScale = Settings.DensityResponseScale,
                GhgScale = Settings.GhgResponseScale,
            };
            entry.SunMid = MidOrbitIrradiance();
            entry.AirlessBase = EquilibriumKelvin(entry.SunMid, entry.Albedo);
            entry.Mean0 = 0.5 * (Shipped(entry, 0f, 50f) + Shipped(entry, 180f, 50f));
            double g0 = G(curves, entry.StartGhgIndex);
            entry.G0Warm = Math.Max(g0, 0.0);
            entry.G0Cool = Math.Min(g0, 0.0);
            double warming = entry.Mean0 - EquilibriumKelvin(entry.SunMid, AnchorAlbedo);
            double weight = Clamp01((entry.StartGhgIndex - AnchorIndexFrom) / (AnchorIndexFull - AnchorIndexFrom));
            // The strength setting is divided out of the gain, so on an anchored world it changes the
            // route and never the end point: stripped of its greenhouse air the world still lands on
            // equilibrium. Otherwise lowering the strength would make Venus impossible.
            entry.Gain = 1.0;
            if (entry.FillGhg && warming > 0.0 && g0 > 0.0 && weight > 0.0 && entry.GhgScale > 0.0)
            {
                entry.Gain = 1.0 + weight * (Math.Min(warming / g0, AnchorMaxGain) / entry.GhgScale - 1.0);
            }
            entry.Swing0 = Swing(curves, entry.StartDensity);
            entry.DensityMean0 = DensityMean(curves, entry.StartDensity);

            // Requirement: untouched air reads as shipped. Work it out once rather than trust it.
            double untouched = Math.Abs(Response(entry, 0f, 50f, entry.StartGhgIndex, entry.StartDensity))
                + Math.Abs(Response(entry, 180f, 50f, entry.StartGhgIndex, entry.StartDensity));
            if (double.IsNaN(untouched) || untouched > 1e-3)
            {
                entry.Adjust = false;
                entry.Problem = $"the temperature response is {untouched:0.####} K on untouched air, so it is off for this world";
                Log.Error("Self-check failed: " + entry.Problem + ". Please report this with the world's name.");
            }
            else if (!airless && entry.Mean0 <= 0.0)
            {
                Log.Warn("This world has air but no Temperature curve, so it reads near 0 K with or without the mod. Its author needs to add one (see WORLDS.md).");
            }
            return entry;
        }

        /// <summary>
        /// Sunlight at the middle of the orbit's range, from the game's own orbit, so a custom world
        /// needs no table. The anchor must not wobble with an eccentric orbit; the game's sun-distance
        /// term already carries the seasons.
        /// </summary>
        private static double MidOrbitIrradiance()
        {
            try
            {
                ValueRange sun = OrbitalSimulation.System?.GetSolarEnergy();
                if (sun != null)
                {
                    double mid = 0.5 * ((double)sun.Minimum + sun.Maximum);
                    if (mid > 0.0 && !double.IsNaN(mid) && !double.IsInfinity(mid))
                    {
                        return mid;
                    }
                }
            }
            catch (Exception)
            {
                // fall through to the live value
            }
            double live = OrbitalSimulation.SolarIrradiance;
            return live > 0.0 && !double.IsNaN(live) && !double.IsInfinity(live) ? live : 0.0;
        }

        private static bool IsEmpty(GlobalTemperatureCurveOffset offset)
        {
            if (offset == null)
            {
                return true;
            }
            int day = offset.DayCurveOffsetData?.Keys?.Count ?? 0;
            int night = offset.NightCurveOffsetData?.Keys?.Count ?? 0;
            return day == 0 && night == 0;
        }

        private static double Clamp01(double value) => value < 0.0 ? 0.0 : (value > 1.0 ? 1.0 : value);

        private static double G(ResponseCurves curves, float index)
        {
            return 0.5 * ((double)curves.Ghg.GetOffset(0f, index) + curves.Ghg.GetOffset(180f, index));
        }

        private static double Swing(ResponseCurves curves, float density)
        {
            return Math.Max(0.0, (double)curves.Density.GetOffset(0f, density) - curves.Density.GetOffset(180f, density));
        }

        private static double DensityMean(ResponseCurves curves, float density)
        {
            return 0.5 * ((double)curves.Density.GetOffset(0f, density) + curves.Density.GetOffset(180f, density));
        }

        /// <summary>The base an airless world lacks: bare rock plus the vacuum end of the density curve.</summary>
        private static double AirlessBaseAt(Entry entry, float solarAngle)
        {
            return entry.AirlessBase + entry.Curves.Density.GetOffset(solarAngle, 0f);
        }

        /// <summary>What the world reads untouched at this sun angle and point in its orbit.</summary>
        private static double Shipped(Entry entry, float solarAngle, float solarEnergyPercent)
        {
            double kelvin = entry.FillBase ? AirlessBaseAt(entry, solarAngle) : entry.Data.GetSolarAngleTemperature(solarAngle).ToDouble();
            double sun = entry.Data.GetSolarDistanceTemperatureOffset(solarAngle, solarEnergyPercent).ToDouble();
            return double.IsNaN(sun) ? kelvin : kelvin + sun;
        }

        /// <summary>Kelvin the air's change from its starting state adds. Zero on untouched air.</summary>
        private static double Response(Entry entry, float solarAngle, float solarEnergyPercent, float ghgIndex, float density)
        {
            double total = 0.0;
            if (entry.FillGhg)
            {
                double g = G(entry.Curves, ghgIndex);
                total += (entry.Gain * (Math.Max(g, 0.0) - entry.G0Warm) + (Math.Min(g, 0.0) - entry.G0Cool)) * entry.GhgScale;
            }
            if (entry.FillDensity)
            {
                double damp = entry.Swing0 > 0.0 ? Clamp01(Swing(entry.Curves, density) / entry.Swing0) : 1.0;
                // Strength as an exponent: 0 switches damping off, more makes it bite sooner, and air thick
                // enough to end the swing ends it at any strength, so no setting strands a world like Vulcan.
                damp = Math.Pow(damp, Math.Max(0.0, entry.DensityScale));
                total += (Shipped(entry, solarAngle, solarEnergyPercent) - entry.Mean0) * (damp - 1.0);
                total += (DensityMean(entry.Curves, density) - entry.DensityMean0) * entry.DensityScale;
            }
            return total;
        }

        // GlobalGasMix.GetGlobalGasMixTemperature(GlobalAtmosphereData data, float solarAngle, float solarEnergyPercent)
        // The one-argument overload calls this one, so patching only this covers both exactly once.
        // __instance is the mix being asked about: the planet, or a fresh starting mix when the
        // new-game menu works out a temperature range, which then reads as shipped.
        public static void TemperaturePostfix(GlobalGasMix __instance, GlobalAtmosphereData data, float solarAngle, float solarEnergyPercent, ref TemperatureKelvin __result)
        {
            if (!Settings.Enabled || __instance == null || data == null)
            {
                return;
            }
            Entry entry = Lookup(data);
            if (entry == null || !entry.Adjust || !(entry.FillGhg || entry.FillDensity || entry.FillBase))
            {
                return;
            }
            try
            {
                // The game asks the ice caps for "their" temperature when it decides whether they melt
                // (GlobalGasMix.MeltMoleQuantity evaluates this on the caps themselves). The caps hold
                // no gas, so left alone they would read the planet as if it had no air: a terraformed
                // Europa's caps would sit at 125 K for ever and never give back what froze into them.
                // The planet has one temperature; its reservoirs read the planet's air.
                GlobalGasMix air = IsReservoir(__instance) ? (PlanetaryAtmosphereSimulation.GetGlobalGasMix() ?? __instance) : __instance;
                float index = TerraForming.GetGhgIndex(air);
                float density = (float)IdealGas.GetMilliMolesPerLitre(air.Volume, air.TotalQuantityGas());
                double delta = Response(entry, solarAngle, solarEnergyPercent, index, float.IsNaN(density) ? 0f : density);
                if (entry.FillBase)
                {
                    delta += AirlessBaseAt(entry, solarAngle);
                }
                // Exactly nothing to add: leave the game's own value alone, bit for bit.
                if (delta == 0.0 || double.IsNaN(delta) || double.IsInfinity(delta))
                {
                    return;
                }
                __result = new TemperatureKelvin(Math.Max(0.0, __result.ToDouble() + delta));
            }
            catch (Exception e)
            {
                if (System.Threading.Interlocked.Increment(ref _faults) <= 3)
                {
                    Log.Error("Temperature response failed and was skipped: " + e);
                }
            }
        }

        private static int _faults;
        private static readonly AccessTools.FieldRef<GlobalGasMix> IceCaps = Reservoir("_iceCaps");
        private static readonly AccessTools.FieldRef<GlobalGasMix> IceClouds = Reservoir("_iceClouds");
        private static readonly AccessTools.FieldRef<GlobalGasMix> LiquidClouds = Reservoir("_liquidClouds");

        private static AccessTools.FieldRef<GlobalGasMix> Reservoir(string name)
        {
            try
            {
                return AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), name) == null
                    ? null
                    : AccessTools.StaticFieldRefAccess<GlobalGasMix>(AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), name));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True for the clouds and ice caps of the planet being played.</summary>
        private static bool IsReservoir(GlobalGasMix mix)
        {
            return (IceCaps != null && ReferenceEquals(mix, IceCaps()))
                || (IceClouds != null && ReferenceEquals(mix, IceClouds()))
                || (LiquidClouds != null && ReferenceEquals(mix, LiquidClouds()));
        }

        /// <summary>Whether the reservoir fields were found, for the status readout.</summary>
        public static bool ReservoirsKnown => IceCaps != null && IceClouds != null && LiquidClouds != null;

        /// <summary>
        /// The game's debug readout adds the parts up itself and so misses the postfix above. Make the
        /// total it shows, which `terraform` and the tools read, the temperature outdoor cells get.
        /// </summary>
        public static void ReadoutPostfix()
        {
            try
            {
                GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
                GlobalAtmosphereData data = WorldSetting.Current?.Data?.GlobalAtmosphereData;
                LastAdjustment = 0.0;
                if (tank == null || data == null || !Settings.Enabled)
                {
                    return;
                }
                double shown = PlanetaryAtmosphereSimulation.AggregateTemperature.ToDouble();
                double real = tank.GetGlobalGasMixTemperature(data).ToDouble();
                LastAdjustment = real - shown;
                PlanetaryAtmosphereSimulation.AggregateTemperature = new TemperatureKelvin(real);
            }
            catch (Exception)
            {
                // a readout, never worth a tick
            }
        }

        /// <summary>Kelvin the mod added at the last planet tick, for the status readout.</summary>
        public static double LastAdjustment { get; private set; }

        /// <summary>
        /// Radiative equilibrium of a rotating sphere: T = (S (1 - a) / 4 sigma)^(1/4), which is
        /// 278.6 K at Earth's 1367 W/m2 with no reflection.
        /// </summary>
        public static double EquilibriumKelvin(double irradiance, double albedo)
        {
            if (double.IsNaN(irradiance) || irradiance <= 0.0)
            {
                return 0.0;
            }
            double absorbed = Math.Max(0.0, Math.Min(1.0, 1.0 - albedo));
            return 278.6 * Math.Sqrt(Math.Sqrt(irradiance / 1367.0 * absorbed));
        }

        public static string Describe()
        {
            Entry entry = _entry;
            if (entry == null)
            {
                return "not evaluated yet";
            }
            if (entry.Problem != null)
            {
                return "off: " + entry.Problem;
            }
            if (!entry.Adjust)
            {
                return "tutorial world, left as shipped";
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "fills greenhouse={0}, density={1}, base={2}; start index {3:0.###}, start density {4:0.###} mmol/L; shipped mean {5:0.#} K, greenhouse gain {6:0.##}, start swing {7:0.#} K, mid-orbit sun {8:0.#} W/m2; adding {9:+0.##;-0.##;0} K now",
                entry.FillGhg, entry.FillDensity, entry.FillBase, entry.StartGhgIndex, entry.StartDensity, entry.Mean0, entry.Gain, entry.Swing0, entry.SunMid, LastAdjustment);
        }
    }
}
