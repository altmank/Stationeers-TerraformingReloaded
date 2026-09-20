using System;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using UnityEngine;
using Weather;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Fixes for what the game's planetary code does wrong once takes and gives are real.
    /// Every method here is a Harmony patch body; Patcher wires them up.
    /// </summary>
    public static class Guards
    {
        internal static object TankLock;

        [ThreadStatic] private static bool _inTick;
        [ThreadStatic] private static bool _inDivide;

        private static int _rejectedGives;
        private static int _tickFaults;
        private static int _sawTick;

        // ---- PlanetaryAtmosphereSimulation.TickPlanetarySimulation --------------------------------

        /// <summary>
        /// The tick rewrites the tank (phase change, cloud dumps) without the lock the takes and gives
        /// use, and a main-thread OnDestroy can clone a cell while it runs. Hold the lock for the tick.
        /// Upkeep runs first so the tick rebuilds the planet's air from the corrected values.
        /// </summary>
        public static void TickPrefix(out bool __state)
        {
            __state = false;
            // Before the gate check on purpose: proof in the log that the patch is reached at all.
            if (Interlocked.Exchange(ref _sawTick, 1) == 0)
            {
                Log.Info("Planet tick is running. " + Gate.Describe());
            }
            if (!Gate.Enabled() || TankLock == null)
            {
                return;
            }
            Monitor.Enter(TankLock);
            __state = true;
            _inTick = true;
            try
            {
                SelfTest.RunIfPending();
                Upkeep();
            }
            catch (Exception e)
            {
                if (Interlocked.Increment(ref _tickFaults) <= 3)
                {
                    Log.Error("Upkeep failed: " + e);
                }
            }
        }

        public static void TickFinalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }
            _inTick = false;
            Monitor.Exit(TankLock);
        }

        /// <summary>
        /// The game adds every joule outdoor cells and objects shed to a counter that nothing ever
        /// drains, then divides it by the planet's heat capacity to get a temperature shift. Left
        /// alone that drifts without bound, and on an airless world it divides banked energy by the
        /// heat capacity of the first puff of vented gas. Drain it like a planet radiating to space,
        /// and bound the shift it can apply.
        /// </summary>
        private static void Upkeep()
        {
            double energy = PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset.ToDouble();
            double original = energy;

            if (double.IsNaN(energy) || double.IsInfinity(energy))
            {
                energy = 0.0;
            }

            double halfLife = Settings.ExternalHeatHalfLifeMinutes * 60.0;
            if (halfLife > 0.0)
            {
                energy *= Math.Pow(0.5, GameManager.GameTickSpeedSeconds / halfLife);
            }

            double capacity = PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble();
            double limit = Math.Max(0.0, Settings.MaxExternalOffsetKelvin) * Math.Max(0.0, capacity);
            if (double.IsNaN(limit))
            {
                limit = 0.0;
            }
            energy = Math.Max(-limit, Math.Min(limit, energy));

            if (energy != original)
            {
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset = new MoleEnergy(energy);
            }

            double cap = Settings.MaxPressureKPa;
            if (cap > 0.0)
            {
                double pressure = PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble();
                if (pressure > cap)
                {
                    PlanetaryAtmosphereSimulation.GetGlobalGasMix()?.Scale(cap / pressure);
                }
            }
        }

        // ---- PlanetaryAtmosphereSimulation.GiveToGlobal -------------------------------------------

        /// <summary>
        /// The tank adds whatever it is handed with no checks, and it is saved, so one cell that has
        /// gone NaN would poison the planet for the life of the save. Turn bad mixtures away.
        /// Taken by ref only to avoid copying a 28 species struct per call; it is never written.
        /// </summary>
        public static bool GivePrefix(ref GasMixture gasMixture)
        {
            if (!Gate.Enabled())
            {
                return true;
            }
            double moles = gasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
            if (moles == 0.0)
            {
                return false;
            }
            double energy = gasMixture.TotalEnergy.ToDouble();
            if (moles < 0.0 || double.IsNaN(moles) || double.IsInfinity(moles) || double.IsNaN(energy) || double.IsInfinity(energy))
            {
                if (Interlocked.Increment(ref _rejectedGives) <= 3)
                {
                    Log.Warn($"Refused a gas mixture bound for the planet: moles={moles}, energy={energy}.");
                }
                return false;
            }
            return true;
        }

        public static int RejectedGives => _rejectedGives;

        // ---- AtmosphericEventInstance.DivideWorldAtmosphere / AtmosphericsManager.Deregister ------

        /// <summary>
        /// When a structure fills an outdoor cell the game copies the cell's gas into its open
        /// neighbours, then removes the cell, and removal hands the still-full cell to the planet as
        /// well. Empty the cell first when its gas has already been passed on. With no open neighbours
        /// nothing was passed on, and the planet is the right place for it.
        /// </summary>
        public static void DividePrefix() => _inDivide = true;

        public static void DivideFinalizer() => _inDivide = false;

        public static void DeregisterPrefix(Atmosphere atmosphere)
        {
            if (!_inDivide || atmosphere == null || !Gate.Enabled())
            {
                return;
            }
            if (atmosphere.OpenNeighbors.Count > 0)
            {
                atmosphere.GasMixture.Reset();
            }
        }

        // ---- XmlSaveLoad.GetWorldData --------------------------------------------------------------

        /// <summary>
        /// Off the simulation thread a mole reports a cached quantity, refreshed at the start of each
        /// tick, before that tick's mixing. The save collects cells on the main thread, so it records
        /// every cell as it was before the last mix, next to a tank that is current. Whatever flowed
        /// between cells and the tank in that tick is then in the file twice, or not at all.
        /// Found by tools/LiveCheck -SaveLoad: 5,800 mol appeared across one save and load.
        /// The game has paused the tick and waited for it to come to rest before this runs, so
        /// refreshing the caches here, with the same call it makes every tick, is safe.
        /// </summary>
        public static void SavePrefix()
        {
            if (!Gate.Enabled())
            {
                return;
            }
            try
            {
                AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(atmosphere => atmosphere?.GasMixture.UpdateCache()));
            }
            catch (Exception e)
            {
                Log.Error("Could not bring outdoor cells up to date before saving; this save may be off by one tick of gas flow. " + e.Message);
            }
        }

        // ---- WeatherManager.ScheduleWeatherEvent --------------------------------------------------

        /// <summary>
        /// When a cloud bucket fills, the tick schedules rain or snow without checking whether other
        /// weather is already scheduled or running, which overwrites the current event mid-flight.
        /// The clouds have already been returned to the planet by then, so skipping the event loses
        /// nothing. Only calls made from inside the tick are filtered.
        /// </summary>
        public static bool ScheduleWeatherPrefix(WeatherEvent weatherEvent)
        {
            if (!_inTick)
            {
                return true;
            }
            if (weatherEvent == null)
            {
                return false;
            }
            if (WeatherManager.IsWeatherEventRunning || WeatherManager.IsWeatherEventScheduled)
            {
                return false;
            }
            return WeatherManager.WorldHasWeather || Settings.WeatherOnWeatherlessWorlds;
        }

        // ---- AtmosphericScattering.UpdateAtmosphericScatteringToGlobalAtmosphere ------------------

        private static uint _skyTick = uint.MaxValue;
        private static bool _skyRan;
        private static float _fogScale = 1f;

        /// <summary>
        /// The sky blend is rebuilt from the planet's air, which only changes once per game tick, but
        /// the game rebuilds it every frame and allocates a few dozen objects each time. Let it run
        /// once per tick and repeat its one per-frame effect, the fog scaling, in between.
        /// Main thread only.
        /// </summary>
        public static bool SkyPrefix(ref float desiredFogIntensity)
        {
            uint tick = GameManager.GameTickCount;
            if (tick != _skyTick)
            {
                _skyTick = tick;
                _skyRan = true;
                return true;
            }
            _skyRan = false;
            desiredFogIntensity = Mathf.Lerp(0f, desiredFogIntensity, _fogScale);
            return false;
        }

        public static void SkyPostfix()
        {
            if (_skyRan)
            {
                _fogScale = Mathf.Clamp01(PlanetaryAtmosphereSimulation.GlobalPressure.ToFloat() / 2f);
            }
        }
    }
}
