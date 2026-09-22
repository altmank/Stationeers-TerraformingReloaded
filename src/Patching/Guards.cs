using System;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
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
            double capacity = PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble();

            double external = PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset.ToDouble();
            double settled = Settle(external, capacity);
            if (settled != external)
            {
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset = new MoleEnergy(settled);
            }

            // The game's phase change books heat unevenly: freezing a gas adds its heat of vaporisation
            // and of fusion, but evaporating it again takes back only the first, and melting the ice caps
            // takes back more than freezing into them gave. Measured live: 20 mol per cell of CO2 frozen
            // and returned left 17 % of its heat behind for good. Every cycle adds to a counter that is
            // saved and never drained, so it gets the same fade and the same bound as outside heat.
            double latent = PlanetaryAtmosphereSimulation.LatentEnergyOffset.ToDouble();
            settled = Settle(latent, capacity);
            if (settled != latent)
            {
                PlanetaryAtmosphereSimulation.LatentEnergyOffset = new MoleEnergy(settled);
            }

            Planet.KeepPhaseChangeInProportion();

            // Per-world, not the config: this one deletes air for good and saves the loss, so it is
            // the setting a world must never inherit from whatever the config says today. No ceiling
            // is null here and never 0; the config's sentinel died at the Sidecar boundary.
            if (Effective.MaxPressureKPa.HasValue)
            {
                double cap = Effective.MaxPressureKPa.Value;
                double pressure = PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble();
                if (pressure > cap)
                {
                    PlanetaryAtmosphereSimulation.GetGlobalGasMix()?.Scale(cap / pressure);
                }
            }

            // Last, after the phase change has been put back in proportion and after the ceiling has
            // scaled the tank, or it would forecast air this same tick is about to change.
            Storms.Update();
        }

        /// <summary>One tick of fading toward zero, then the bound on the kelvin it may apply.</summary>
        private static double Settle(double energy, double capacity)
        {
            if (double.IsNaN(energy) || double.IsInfinity(energy))
            {
                return 0.0;
            }
            // Both per-world: they write back into counters the save carries, so a shorter half-life
            // or a lower limit deletes banked heat for good. Null is "never fades".
            double? halfLifeMinutes = Effective.ExternalHeatHalfLifeMinutes;
            if (halfLifeMinutes.HasValue && halfLifeMinutes.Value > 0.0)
            {
                energy *= Math.Pow(0.5, GameManager.GameTickSpeedSeconds / (halfLifeMinutes.Value * 60.0));
            }
            double limit = Math.Max(0.0, Effective.MaxExternalOffsetKelvin) * Math.Max(0.0, capacity);
            if (double.IsNaN(limit))
            {
                limit = 0.0;
            }
            return Math.Max(-limit, Math.Min(limit, energy));
        }

        // ---- WallVent.OnAtmosphericTick ------------------------------------------------------------

        /// <summary>
        /// Outdoor air at a grid is the real cell there if one exists, otherwise a shared read-only copy
        /// of the planet's air: writes to the copy are ignored and removals from it cost the planet
        /// nothing. Every vent that moves real amounts builds a real cell first; the wall vent does not.
        /// It averages the room with the copy every tick, so a wall vent to outdoors was a bottomless
        /// source for a thin room and a bottomless sink for a thick one, and the ordinary way to open a
        /// base to outside skipped the finite planet altogether. Give it a real cell to mix with: building
        /// one draws its air from the planet, and what the vent pushes into it drains back.
        /// tools/census lists every other place the game touches the copy; the rest are too small to matter.
        /// </summary>
        public static void WallVentPrefix(WorldGrid ____facingGrid, WorldGrid ____rearGrid)
        {
            if (!Gate.Enabled())
            {
                return;
            }
            try
            {
                bool facing = AtmosphericsManager.Find(____facingGrid) != null;
                bool rear = AtmosphericsManager.Find(____rearGrid) != null;
                if (facing == rear)
                {
                    return;             // both real already, or nothing on either side to exchange
                }
                AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(facing ? ____rearGrid : ____facingGrid);
            }
            catch (Exception e)
            {
                if (Interlocked.Increment(ref _ventFaults) <= 3)
                {
                    Log.Error("Wall vent fix failed and was skipped: " + e.Message);
                }
            }
        }

        private static int _ventFaults;

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
        /// Two unrelated jobs on one method, told apart by which thread called it.
        ///
        /// In the tick (D6): when a cloud bucket fills, the tick schedules rain or snow without
        /// checking whether other weather is already scheduled or running, which overwrites the
        /// current event mid-flight. The clouds have already been returned to the planet by then, so
        /// skipping the event loses nothing.
        ///
        /// Out of the tick, which is the game's own scheduler on the main thread: turn away an event
        /// a suppressed world may not have (docs/STORMS.md). The flag above is thread static and the
        /// planet tick runs on a worker, so the D6 branch never sees an ordinary storm being
        /// scheduled and the two cannot be merged. This branch checks the gate; the one above does
        /// not need to, because being in the tick already means it is open.
        /// </summary>
        public static bool ScheduleWeatherPrefix(WeatherEvent weatherEvent)
        {
            if (_inTick)
            {
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
            if (!Gate.Enabled())
            {
                return true;
            }
            return !Storms.Suppresses(weatherEvent);
        }

        // ---- WeatherManager.CanScheduleWeatherEvent ------------------------------------------------

        /// <summary>
        /// The game evaluates GetNextWeatherEvent() as the argument to ScheduleWeatherEvent, so
        /// turning the pick away in the prefix above still rolls the game's shared static Random
        /// about sixty times a second, for ever, on a world that will never be allowed a storm.
        /// This predicate is the one caller's own guard and has no side effects, so answering it
        /// stops the pick being evaluated at all.
        ///
        /// Only answered when every event this world ships is suppressed. A world with a solar storm
        /// beside an ordinary one goes on picking, and the prefix above turns away the picks it must.
        /// </summary>
        public static bool CanScheduleWeatherPrefix(ref bool __result)
        {
            if (!Gate.Enabled() || !Storms.SuppressesEverything())
            {
                return true;
            }
            __result = false;
            return false;
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
