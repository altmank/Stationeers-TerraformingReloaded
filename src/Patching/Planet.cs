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
        [ThreadStatic] private static int _buildingPlanet;

        // Prefix and finalizer on the two methods that build the planet being played. GlobalGasMix.Create
        // is also called by the new-game menu, which divides the moles by the world's unscaled volume to
        // show a pressure: resized there, every world read twenty times too thin at the default size.
        public static void BuildPrefix() => _buildingPlanet++;

        public static void BuildFinalizer() => _buildingPlanet = Math.Max(0, _buildingPlanet - 1);

        public static void CreatePostfix(GlobalGasMix __result)
        {
            double size = Settings.PlanetSize;
            if (_buildingPlanet <= 0)
            {
                return;
            }
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

        // ---- phase change in proportion to the planet ------------------------------------------------
        // The game's clouds and ice caps have fixed volumes and its melt and freeze rates are fixed moles
        // per tick, all sized for its full planet. On a planet a twentieth the size they would run twenty
        // times faster per outdoor cell: more rain and snow, caps that fill and empty in minutes. Planet
        // size is meant to change how long terraforming takes and nothing else, so they follow the size.
        // The shipped values are read from the game, not written here.

        private static readonly AccessTools.FieldRef<GlobalGasMix>[] ReservoirRefs = ResolveReservoirs();
        private static readonly double ShippedMelt = PlanetaryAtmosphereSimulation.MINMeltQuantity.ToDouble();
        private static readonly double ShippedFreeze = PlanetaryAtmosphereSimulation.MINLiquidFreezeQuantity.ToDouble();
        private static readonly double[] ShippedReservoirLitres = new double[3];
        private static volatile bool _reservoirsSeen;

        private static AccessTools.FieldRef<GlobalGasMix>[] ResolveReservoirs()
        {
            var refs = new AccessTools.FieldRef<GlobalGasMix>[Reservoirs.Length];
            for (int i = 0; i < Reservoirs.Length; i++)
            {
                try
                {
                    FieldInfo field = AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), Reservoirs[i]);
                    refs[i] = field == null ? null : AccessTools.StaticFieldRefAccess<GlobalGasMix>(field);
                }
                catch (Exception)
                {
                    refs[i] = null;
                }
            }
            return refs;
        }

        /// <summary>True for the clouds and ice caps of the planet being played.</summary>
        public static bool IsReservoir(GlobalGasMix mix)
        {
            foreach (AccessTools.FieldRef<GlobalGasMix> reservoir in ReservoirRefs)
            {
                if (reservoir != null && ReferenceEquals(mix, reservoir()))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool ReservoirsKnown => ReservoirRefs[0] != null && ReservoirRefs[1] != null && ReservoirRefs[2] != null;

        /// <summary>World start, straight after the game has built fresh reservoirs and before a save is loaded over them.</summary>
        internal static void NoteShippedReservoirs()
        {
            for (int i = 0; i < ReservoirRefs.Length; i++)
            {
                GlobalGasMix reservoir = ReservoirRefs[i]?.Invoke();
                if (reservoir == null)
                {
                    return;
                }
                ShippedReservoirLitres[i] = reservoir.Volume.ToDouble();
            }
            _reservoirsSeen = true;
        }

        /// <summary>
        /// Every planet tick, under the tank lock. The share is measured from the planet itself, so a
        /// save made at another size, or before the mod, behaves at the size it actually has.
        /// </summary>
        internal static void KeepPhaseChangeInProportion()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            double shipped = WorldSetting.Current?.Data?.GlobalAtmosphereData?.Volume?.Value ?? 0.0;
            if (tank == null || SetVolume == null || !(shipped > 0.0))
            {
                return;
            }
            double share = tank.Volume.ToDouble() / shipped;
            if (!(share > 0.0) || double.IsInfinity(share))
            {
                return;
            }
            PlanetaryAtmosphereSimulation.MINMeltQuantity = new MoleQuantity(ShippedMelt * share);
            PlanetaryAtmosphereSimulation.MINLiquidFreezeQuantity = new MoleQuantity(ShippedFreeze * share);
            if (!_reservoirsSeen)
            {
                return;
            }
            for (int i = 0; i < ReservoirRefs.Length; i++)
            {
                GlobalGasMix reservoir = ReservoirRefs[i]?.Invoke();
                double wanted = ShippedReservoirLitres[i] * share;
                if (reservoir != null && wanted > 0.0 && Math.Abs(reservoir.Volume.ToDouble() - wanted) > wanted * 1e-9)
                {
                    SetVolume.Invoke(reservoir, new object[] { new VolumeLitres(wanted) });
                }
            }
        }

        /// <summary>
        /// Rescales the planet being played by <paramref name="factor"/>: the tank's volume and every
        /// gas and liquid in it, the clouds and the ice caps with their contents, and both stored
        /// heats, all by the same factor. Everything measured per litre is therefore exactly where it
        /// was - pressure, the mix, the greenhouse index, the density term - and the only thing that
        /// moves is how much air a base has to make to change the planet.
        ///
        /// The reservoirs' contents scale with the rest on purpose. Their volumes and the melt and
        /// freeze rates already follow the planet every tick (KeepPhaseChangeInProportion), but what
        /// is in them does not, so a planet shrunk tenfold would keep ten times its share of ice in
        /// the caps and then melt that back into a tenth of the air. What is frozen out is part of
        /// what is left to terraform, so it moves with the planet like the rest of it.
        ///
        /// The two heat stores are energies, not temperatures: the kelvin each applies is the energy
        /// divided by the heat capacity of the planet and its reservoirs, which has just moved by the
        /// factor. Scaling them is what keeps the temperature where it was.
        ///
        /// The game's one-cell read-only copy of the planet's air is rebuilt from the tank every tick
        /// and holds air per litre, which has not changed, so it is already right.
        ///
        /// Returns null on success, or what stopped it.
        /// </summary>
        public static string Rescale(double factor)
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tank == null)
            {
                return "No planet loaded.";
            }
            if (double.IsNaN(factor) || double.IsInfinity(factor) || !(factor > 0.0))
            {
                return "That is not a size a planet can be; nothing was changed.";
            }
            if (SetVolume == null)
            {
                return "This game build's planet volume cannot be set; nothing was changed.";
            }
            // Resolved before anything is changed: scaling the tank without its clouds and ice caps
            // would leave them out of proportion, which is worse than refusing.
            if (!ReservoirsKnown)
            {
                return "This game build's cloud and ice cap fields were not found, so a rescale would leave them out of proportion; nothing was changed.";
            }

            // The planet tick and every take and give hold this lock, so nothing sees half a rescale.
            object tankLock = Guards.TankLock;
            bool locked = false;
            try
            {
                if (tankLock != null)
                {
                    Monitor.Enter(tankLock, ref locked);
                }
                SetVolume.Invoke(tank, new object[] { tank.Volume * factor });
                tank.Scale(factor);
                foreach (AccessTools.FieldRef<GlobalGasMix> reservoir in ReservoirRefs)
                {
                    reservoir?.Invoke()?.Scale(factor);
                }
                PlanetaryAtmosphereSimulation.LatentEnergyOffset =
                    new MoleEnergy(PlanetaryAtmosphereSimulation.LatentEnergyOffset.ToDouble() * factor);
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset =
                    new MoleEnergy(PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset.ToDouble() * factor);
                // Cloud and ice cap volumes and the phase rates, now rather than a tick later.
                KeepPhaseChangeInProportion();
            }
            catch (Exception e)
            {
                Log.Error("Rescaling the planet failed. " + e);
                return "The planet could not be rescaled: " + e.Message;
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
