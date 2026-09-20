using System;
using System.Reflection;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using HarmonyLib;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// Whole-planet operations that are not patches.
    /// </summary>
    public static class Planet
    {
        private static readonly MethodInfo SetVolume = AccessTools.PropertySetter(typeof(GlobalGasMix), nameof(GlobalGasMix.Volume));

        public static bool CanResize => SetVolume != null;

        /// <summary>
        /// Postfix on GlobalGasMix.Create: the one place the game builds a planet from a world file.
        /// Volume and every quantity shrink together, so each outdoor cell holds exactly the air the
        /// world ships with and only the number of cells behind it changes. Hooked here rather than on
        /// GlobalAtmosphereData.GetVolume because that is a one-line getter, an inlining candidate.
        /// A planet loaded from a save keeps the size it was saved with.
        /// </summary>
        public static void CreatePostfix(GlobalGasMix __result)
        {
            double size = Settings.PlanetSize;
            if (!Settings.Enabled || __result == null || SetVolume == null || size == 1.0 || !(size > 0.0) || double.IsInfinity(size))
            {
                return;
            }
            try
            {
                SetVolume.Invoke(__result, new object[] { __result.Volume * size });
                __result.Scale(size);
            }
            catch (Exception e)
            {
                Log.Error("Could not resize the planet; it stays at the shipped size. " + e.Message);
            }
        }

        private static readonly string[] Reservoirs = { "_liquidClouds", "_iceClouds", "_iceCaps" };

        /// <summary>
        /// Puts the planet back exactly as the world ships: starting air, empty clouds, empty ice caps,
        /// no stored heat. This is the way out of the mod. The game saves and loads the planet with or
        /// without the mod installed, so removing the mod alone leaves the changed planet in the save,
        /// frozen, and on a world that ships with no base temperature (Moon, Mimas) leaves its new air
        /// at 0 K. Reset, save, then remove.
        ///
        /// Outdoor cells and everything the player built are untouched; outdoor cells relax toward
        /// the restored planet on their own, as they do toward any planet.
        ///
        /// Returns null on success, or what stopped it.
        /// </summary>
        public static string ResetToShipped()
        {
            WorldSetting world = WorldSetting.Current;
            GlobalAtmosphereData data = world?.Data?.GlobalAtmosphereData;
            if (data == null || PlanetaryAtmosphereSimulation.GetGlobalGasMix() == null)
            {
                return "No world loaded.";
            }

            // Resolve everything before changing anything, so a game update that renames a field
            // leaves the planet alone rather than half reset.
            Type simulation = typeof(PlanetaryAtmosphereSimulation);
            GlobalGasMix[] reservoirs = new GlobalGasMix[Reservoirs.Length];
            for (int i = 0; i < Reservoirs.Length; i++)
            {
                FieldInfo field = AccessTools.Field(simulation, Reservoirs[i]);
                if (field == null)
                {
                    return "This game build has no " + Reservoirs[i] + "; nothing was changed.";
                }
                reservoirs[i] = field.GetValue(null) as GlobalGasMix;
            }

            // The planet tick and every take and give hold this lock, so nothing sees a half reset.
            object tankLock = Guards.TankLock;
            bool locked = false;
            try
            {
                if (tankLock != null)
                {
                    Monitor.Enter(tankLock, ref locked);
                }
                PlanetaryAtmosphereSimulation.RegenerateGlobalFromData(data);
                foreach (GlobalGasMix reservoir in reservoirs)
                {
                    reservoir?.ClearQuantities(AtmosphereHelper.MatterState.All);
                }
                PlanetaryAtmosphereSimulation.LatentEnergyOffset = MoleEnergy.Zero;
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset = MoleEnergy.Zero;
            }
            finally
            {
                if (locked)
                {
                    Monitor.Exit(tankLock);
                }
            }
            return null;
        }
    }
}
