using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using Objects.Rockets;
using TerraformingReloaded.Patching;
using UnityEngine;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// Bringing a comet down (docs/COMET-ROCKETS.md, "Bring it down", stage 6). A rocket at Entry runs Deorbit:
    /// the burn fires for the comet its payload bay aims, at the landing the bay's aim gave that frame
    /// (<see cref="CometAim"/>), spending the fuel that steer takes (<see cref="CelestialRules.Descend"/>). The
    /// comet leaves the map and the sky at once and falls for <see cref="CelestialTuning.EntryWindowSeconds"/>;
    /// every player is warned with the time to impact and their own distance from it, and the impact event is
    /// the console comet's own (<see cref="CometDirector"/>), timed to land at the end of the fall. At the
    /// impact, on the host, under the tank lock, an ice comet's arriving gas goes into the planet and a mineral
    /// comet takes the planet's air away in its own proportions, both with the stored heat kept in kelvin
    /// (<see cref="Planet.AddGas"/>, <see cref="Planet.RemoveGas"/>). The gas is in exactly when the falling
    /// comet is gone, both done in one call on the main thread, so a save holds one or the other.
    ///
    /// Nothing keeps it off a base: the console comet's safe distance is the console's alone.
    /// </summary>
    public static class CometLand
    {
        private const float DescentDegrees = 24f;

        private static volatile bool _installed;
        private static readonly HashSet<string> Warned = new HashSet<string>();
        private static readonly List<LandingDelivery> Delivered = new List<LandingDelivery>();
        private static int _faults;

        /// <summary>Deorbit is installed (Patcher's "comet deorbit").</summary>
        public static bool Installed => _installed;

        /// <summary>
        /// Patcher's "comet deorbit": nothing of its own is patched, but every part it rests on must be in: the
        /// rocket actions (the mode, its button and its save), the comets' save, the sky body and the moment it
        /// may leave the sky, and the bays' aim.
        /// </summary>
        public static void Apply()
        {
            string warning = CheckWarning();
            if (warning != null)
            {
                throw new InvalidOperationException("the warning a comet's fall gives did not pass its own check: " + warning);
            }
            if (!CometActions.Installed || !CometMap.SaveInstalled || !CometSkyBodies.Installed || !CometSkyBodies.SafePointInstalled)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "a part a comet's deorbit needs is off: rocket actions {0}, comet save {1}, sky body {2}, sky safe point {3}",
                    CometActions.Installed, CometMap.SaveInstalled, CometSkyBodies.Installed, CometSkyBodies.SafePointInstalled));
            }
            _installed = true;
        }

        /// <summary>Every world start: nothing of the last world's warnings or landings is left.</summary>
        internal static void WorldStart()
        {
            lock (Warned)
            {
                Warned.Clear();
            }
            lock (Delivered)
            {
                Delivered.Clear();
            }
        }

        /// <summary>What every landing this session did to the planet, in order; for tools/LiveCheck.</summary>
        public static LandingDelivery[] Deliveries()
        {
            lock (Delivered)
            {
                return Delivered.ToArray();
            }
        }

        /// <summary>Every comet falling now, as this machine sees it.</summary>
        public static LandingView[] Falling()
        {
            CometLanding[] landings = CometMap.Landings;
            LandingView[] views = new LandingView[landings.Length];
            for (int i = 0; i < landings.Length; i++)
            {
                views[i] = new LandingView(landings[i]);
            }
            return views;
        }

        /// <summary>
        /// The impact event in force on this machine is the one of comet <paramref name="cometId"/> brought down by
        /// rocket, and when and where it lands; for tools/LiveCheck. A joining player's game cannot tell, since
        /// which comet an event belongs to is never sent: false there.
        /// </summary>
        public static bool EventFor(int cometId, out double impactClock, out Vector3 impact)
        {
            CometEvent current = CometDirector.Current;
            bool ours = current != null && current.RocketComet == cometId && cometId != 0;
            impactClock = ours ? current.ImpactClock : double.NaN;
            impact = ours ? current.Impact : Vector3.zero;
            return ours;
        }

        /// <summary>A comet is coming down, or the show of the last one is still playing: one at a time.</summary>
        public static bool Busy()
        {
            CometEvent current = CometDirector.Current;
            return CometMap.Landings.Length > 0 || (current != null && CometEvent.Clock < current.StartClock + CometEvent.EndSeconds);
        }

        // ---- the burn ------------------------------------------------------------------------------------------

        /// <summary>
        /// Host, main thread (the rocket's server tick, Deorbit): the burn for the comet the rocket's payload bay
        /// aims, at the landing the bay's aim gives now. Null on success, else why not.
        /// </summary>
        internal static string Deorbit(Rocket rocket)
        {
            if (NetworkManager.IsClient)
            {
                return "only the host brings a comet down";
            }
            if (Busy())
            {
                return "another comet is coming down";
            }
            CometAim.Aim? found = null;
            List<RocketPayloadBay> bays = new List<RocketPayloadBay>(rocket?.RocketNetwork?.RocketPayloadBays ?? new List<RocketPayloadBay>());
            bays.Sort((a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
            foreach (RocketPayloadBay bay in bays)
            {
                found = bay == null ? null : CometAim.ForBay(bay.ReferenceId);
                if (found != null)
                {
                    break;
                }
            }
            if (found == null)
            {
                return "no payload bay on this rocket aims a comet in the sky";
            }
            CometAim.Aim aim = found.Value;
            MapComet comet = CometMap.Find(aim.CometId);
            if (comet == null || comet.State != MapCometState.Arrived)
            {
                return "the comet its bay aims is no longer in orbit";
            }
            CelestialTuning tuning = Effective.Celestial;
            CultureInfo c = CultureInfo.InvariantCulture;
            double now = CometEvent.Clock;

            Vector3 impact = CometTerrain.Ground(new Vector3((float)aim.LandX, 0f, (float)aim.LandZ), (float)aim.PadY, out string ground);
            Vector3 travel = Travel(aim.Direction);
            Chemistry.GasType[] gases;
            double[] moles;
            double mineralKg = 0.0;
            double airKgPerKg = 0.0;
            double show;
            if (comet.Kind == CometKind.Ice)
            {
                gases = (Chemistry.GasType[])comet.Gases.Clone();
                moles = CelestialRules.Shipped.ArrivingGas(comet.Moles, tuning.GasArrivingShare);
                show = 0.0;
                foreach (double one in moles)
                {
                    show += one;
                }
            }
            else
            {
                mineralKg = comet.OreUnits * tuning.MineralKgPerUnit;
                airKgPerKg = tuning.MineralRemovalPerKg;
                Air(out gases, out double[] air, out double[] grams);
                moles = CelestialRules.Shipped.MineralRemoval(air, grams, mineralKg, airKgPerKg);
                show = 0.0;
                foreach (double one in moles)
                {
                    show += one;
                }
                if (!(show > 0.0))
                {
                    // A world with no air: sized as the carbon dioxide its mass would be.
                    show = mineralKg * airKgPerKg * 1000.0 / Mole.MolarMass(Chemistry.GasType.CarbonDioxide);
                }
            }
            show = Math.Max(1.0, Math.Min(CometMap.MaxMoles, show));
            LandingAim how = new LandingAim(rocket.ReferenceId, aim.BayId, aim.Tracked, aim.SkyX, aim.SkyZ, aim.TargetX, aim.TargetZ, aim.PadX, aim.PadZ, aim.Radius, aim.Reach,
                aim.Steered, aim.FuelSpent, Math.Max(0.0, aim.Fuel - aim.FuelSpent), aim.Frame, aim.Clock, aim.Fuel);
            CometLanding landing = new CometLanding(comet.Id, comet.Kind, gases, moles, mineralKg, airKgPerKg, show, impact.x, impact.y, impact.z, travel.x, travel.y, travel.z,
                new System.Random().Next(), tuning.EntryWindowSeconds, now, 0.0, how);
            string wrong = landing.Refusal();
            if (wrong != null)
            {
                return "the landing would have " + wrong;
            }
            string refused = CometMap.Deorbited(comet.Id, landing);
            if (refused != null)
            {
                return refused;
            }
            Log.Info(string.Format(c,
                "[TR comet land] comet {0} brought down by rocket {1} bay {2} at clock {3:R}, the aim of frame {4} (clock {5:R}): {6} | sky point ({7:R}, {8:R}) | target ({9:R}, {10:R}) | pad ({11:R}, {12:R}) disc {13:R} m | reach {14:R} m, steered {15:R} m | fuel {16:R} mol, spent {17:R}, left {18:R} | lands ({19:R}, {20:R}, {21:R}) at clock {22:R}, after {23:R} s | {24} | ground: {25}",
                comet.Id, rocket.ReferenceId, aim.BayId, now, aim.Frame, aim.Clock, aim.Tracked ? "tracked" : "untracked", aim.SkyX, aim.SkyZ, aim.TargetX, aim.TargetZ, aim.PadX, aim.PadZ,
                aim.Radius, aim.Reach, aim.Steered, aim.Fuel, aim.FuelSpent, how.FuelLeft, landing.ImpactX, landing.ImpactY, landing.ImpactZ, landing.ImpactClock, landing.FallSeconds,
                What(landing), ground));
            CometDirector.Land(landing);
            return null;
        }

        /// <summary>The way in: outward along the comet's bearing from the pad, coming down at the console comet's angle.</summary>
        private static Vector3 Travel(SkyVector direction)
        {
            Vector3 flat = new Vector3((float)direction.X, 0f, (float)direction.Z);
            flat = flat.sqrMagnitude > 1e-8f ? flat.normalized : Vector3.forward;
            return (flat * Mathf.Cos(DescentDegrees * Mathf.Deg2Rad) - Vector3.up * Mathf.Sin(DescentDegrees * Mathf.Deg2Rad)).normalized;
        }

        private static string What(CometLanding landing)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (landing.Kind == CometKind.Mineral)
            {
                return string.Format(c, "mineral, {0:R} kg, takes {1:R} kg of air per kg, about {2:R} mol of the air as it is now", landing.MineralKg, landing.AirKgPerKg, landing.ShowMoles);
            }
            StringBuilder text = new StringBuilder("ice, brings");
            for (int i = 0; i < landing.Gases.Length; i++)
            {
                text.Append(i == 0 ? " " : ", ").Append(landing.Gases[i]).Append(' ').Append(landing.Moles[i].ToString("R", c));
            }
            return text.Append(" mol").ToString();
        }

        /// <summary>The planet's air: each gas the tank holds as a gas, its moles and the game's molar mass. Read under the tank lock.</summary>
        private static void Air(out Chemistry.GasType[] gases, out double[] moles, out double[] grams)
        {
            List<Chemistry.GasType> types = new List<Chemistry.GasType>();
            List<double> held = new List<double>();
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tank != null)
            {
                Planet.UnderTankLock(() =>
                {
                    foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
                    {
                        if (Mole.MatterState(type) != AtmosphereHelper.MatterState.Gas)
                        {
                            continue;
                        }
                        double amount = tank.Get(type).ToDouble();
                        if (amount > 0.0)
                        {
                            types.Add(type);
                            held.Add(amount);
                        }
                    }
                });
            }
            gases = types.ToArray();
            moles = held.ToArray();
            grams = Array.ConvertAll(gases, g => (double)Mole.MolarMass(g));
        }

        // ---- every frame -----------------------------------------------------------------------------------------

        /// <summary>
        /// Main thread, every frame, on every machine: a fall that has gone on the clock warns this machine's
        /// player once; on the host, a fall whose time is up lands.
        /// </summary>
        internal static void Step()
        {
            if (GameManager.GameState != GameState.Running)
            {
                return;
            }
            CometLanding[] landings = CometMap.Landings;
            if (landings.Length == 0)
            {
                return;
            }
            double now = CometEvent.Clock;
            foreach (CometLanding landing in landings)
            {
                if (!landing.OnClock)
                {
                    continue;
                }
                Warn(landing, now);
                if (!NetworkManager.IsClient && now >= landing.ImpactClock)
                {
                    Land(landing, now);
                }
            }
        }

        /// <summary>
        /// The warning, once for each fall this machine sees go on the clock: the time to impact and this
        /// machine's player's own distance from it, printed as the game prints a chat message, to the console of
        /// every player. The host also logs each player it knows of and their distance.
        /// </summary>
        private static void Warn(CometLanding landing, double now)
        {
            string key = landing.CometId.ToString(CultureInfo.InvariantCulture) + "@" + landing.ImpactClock.ToString("R", CultureInfo.InvariantCulture);
            lock (Warned)
            {
                if (!Warned.Add(key))
                {
                    return;
                }
            }
            double seconds = landing.ImpactClock - now;
            if (!(seconds > 0.0))
            {
                return;
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            Human local = InventoryManager.ParentHuman;
            string text = WarningText(landing.CometId, landing.Kind, seconds, landing.ShowMoles, landing.ImpactX, landing.ImpactZ,
                local != null ? local.transform.position.x : double.NaN, local != null ? local.transform.position.z : double.NaN);
            StringBuilder players = new StringBuilder();
            int count = 0;
            foreach (Human human in Human.AllHumans)
            {
                if (human == null)
                {
                    continue;
                }
                Vector3 at = human.transform.position;
                double dx = landing.ImpactX - at.x;
                double dz = landing.ImpactZ - at.z;
                players.Append(count++ == 0 ? "" : "; ").Append(human.DisplayName).Append(' ').Append(Math.Sqrt(dx * dx + dz * dz).ToString("0.###", c)).Append(" m");
            }
            Log.Info(string.Format(c, "[TR comet land] warning | comet {0} | lands in {1:R} s, at clock {2:R} | at ({3:R}, {4:R}, {5:R}) | this machine's player: {6} | players: {7} | {8}",
                landing.CometId, seconds, landing.ImpactClock, landing.ImpactX, landing.ImpactY, landing.ImpactZ, local != null ? local.DisplayName : "none",
                count > 0 ? players.ToString() : "none", text));
            try
            {
                ConsoleWindow.Print(text, ConsoleColor.Yellow, clearLine: false, aged: false);
            }
            catch (Exception e)
            {
                Fault("the warning could not be printed: " + e.Message);
            }
        }

        /// <summary>
        /// The warning a player reads: the comet, the seconds to impact, and with a player there their distance
        /// and the compass direction from them; with none, where it lands. NaN for the player's position when
        /// this machine has none.
        /// </summary>
        public static string WarningText(int cometId, CometKind kind, double seconds, double moles, double impactX, double impactZ, double playerX, double playerZ)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder text = new StringBuilder(string.Format(c, "Comet {0} is coming down in {1:N0} s", cometId, Math.Max(0.0, Math.Ceiling(seconds))));
            if (!double.IsNaN(playerX) && !double.IsNaN(playerZ))
            {
                double dx = impactX - playerX;
                double dz = impactZ - playerZ;
                double distance = Math.Sqrt(dx * dx + dz * dz);
                text.Append(string.Format(c, ", {0:N0} m from you", distance));
                if (distance >= 1.0)
                {
                    text.Append(", to the ").Append(Compass(Math.Atan2(dx, dz) * 180.0 / Math.PI));
                }
            }
            else
            {
                text.Append(string.Format(c, " at ({0:N0}, {1:N0})", impactX, impactZ));
            }
            text.Append(kind == CometKind.Mineral
                ? string.Format(c, ". It will blast about {0:N0} mol of the air away.", moles)
                : string.Format(c, ". It brings {0:N0} mol.", moles));
            return text.ToString();
        }

        /// <summary>
        /// The warning on cases with known answers: the seconds rounded up, the player's distance and the compass
        /// direction from them, or where it lands with no player, and what it brings or takes. Null when it holds.
        /// </summary>
        public static string CheckWarning()
        {
            string near = WarningText(3, CometKind.Ice, 49.2, 70000.4, 300.0, 400.0, 0.0, 0.0);
            string far = WarningText(4, CometKind.Mineral, 10.0, 1234.4, -100.0, 50.0, double.NaN, double.NaN);
            if (near != "Comet 3 is coming down in 50 s, 500 m from you, to the north-east. It brings 70,000 mol.")
            {
                return "a player 500 m south-west of the impact reads: " + near;
            }
            if (far != "Comet 4 is coming down in 10 s at (-100, 50). It will blast about 1,234 mol of the air away.")
            {
                return "a machine with no player reads: " + far;
            }
            foreach (var (bearing, point) in new[] { (0.0, "north"), (89.0, "east"), (180.0, "south"), (225.0, "south-west"), (-45.0, "north-west"), (337.4, "north-west"), (337.6, "north"), (359.0, "north") })
            {
                if (Compass(bearing) != point)
                {
                    return string.Format(CultureInfo.InvariantCulture, "a bearing of {0} degrees is called {1}, expected {2}", bearing, Compass(bearing), point);
                }
            }
            return null;
        }

        /// <summary>One of the eight compass points for a bearing in degrees clockwise from north.</summary>
        public static string Compass(double bearing)
        {
            string[] points = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            double turned = ((bearing % 360.0) + 360.0) % 360.0;
            return points[(int)Math.Floor((turned + 22.5) / 45.0) % 8];
        }

        /// <summary>
        /// Host, main thread: the fall is over. The gas goes in or the air comes out, under the tank lock, and the
        /// falling comet is gone, in this one call; then everyone is told.
        /// </summary>
        private static void Land(CometLanding landing, double now)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tank == null || WorldSetting.Current?.Data?.GlobalAtmosphereData == null)
            {
                CometMap.Landed(landing.CometId);
                Fault("comet " + landing.CometId + " landed with no planet loaded to take it; nothing reached the planet");
                return;
            }
            LandingDelivery delivery = null;
            try
            {
                Planet.UnderTankLock(() => delivery = Deliver(tank, landing, now));
            }
            catch (Exception e)
            {
                Fault("comet " + landing.CometId + "'s gas could not be delivered: " + e.Message);
            }
            CometMap.Landed(landing.CometId);
            if (delivery == null)
            {
                return;
            }
            lock (Delivered)
            {
                Delivered.Add(delivery);
            }
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < delivery.Types.Length; i++)
            {
                double change = delivery.After[i] - delivery.Before[i];
                if (change != 0.0 || delivery.Rule[i] != 0.0)
                {
                    text.Append(text.Length == 0 ? "" : ", ").Append(string.Format(c, "{0} {1:R} -> {2:R} (rule {3}{4:R})", delivery.Types[i], delivery.Before[i], delivery.After[i],
                        landing.Kind == CometKind.Mineral ? "-" : "+", delivery.Rule[i]));
                }
            }
            Log.Info(string.Format(c, "[TR comet land] comet {0} landed at clock {1:R} at ({2:R}, {3:R}, {4:R}): {5} | stored heat {6:R} -> {7:R} K latent, {8:R} -> {9:R} K outside",
                landing.CometId, now, landing.ImpactX, landing.ImpactY, landing.ImpactZ, text.Length == 0 ? "nothing changed" : text.ToString(),
                delivery.LatentKelvinBefore, delivery.LatentKelvinAfter, delivery.ExternalKelvinBefore, delivery.ExternalKelvinAfter));
            string said = landing.Kind == CometKind.Mineral
                ? string.Format(c, "Comet {0} landed and blasted {1:N0} mol of the air away.", landing.CometId, Sum(delivery.Rule))
                : string.Format(c, "Comet {0} landed: {1:N0} mol reached the planet.", landing.CometId, Sum(delivery.Rule));
            try
            {
                ConsoleWindow.Print(said);
            }
            catch (Exception e)
            {
                Fault("the landing could not be printed: " + e.Message);
            }
        }

        /// <summary>Under the tank lock: the gas in, or the air out, and what that did.</summary>
        private static LandingDelivery Deliver(GlobalGasMix tank, CometLanding landing, double now)
        {
            Chemistry.GasType[] types = Array.FindAll((Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)), t => Mole.MatterState(t) != AtmosphereHelper.MatterState.None);
            LandingDelivery delivery = new LandingDelivery
            {
                CometId = landing.CometId,
                Kind = landing.Kind,
                Clock = now,
                Types = types,
                Before = Array.ConvertAll(types, t => tank.Get(t).ToDouble()),
                Rule = new double[types.Length],
            };
            StoredKelvin(out delivery.LatentKelvinBefore, out delivery.ExternalKelvinBefore);
            if (landing.Kind == CometKind.Ice)
            {
                for (int i = 0; i < landing.Gases.Length; i++)
                {
                    int at = Array.IndexOf(types, landing.Gases[i]);
                    if (at >= 0)
                    {
                        delivery.Rule[at] += landing.Moles[i];
                    }
                    if (landing.Moles[i] > 0.0)
                    {
                        Planet.AddGas(tank, landing.Gases[i], landing.Moles[i], null);
                    }
                }
            }
            else
            {
                List<int> air = new List<int>();
                for (int i = 0; i < types.Length; i++)
                {
                    if (Mole.MatterState(types[i]) == AtmosphereHelper.MatterState.Gas && delivery.Before[i] > 0.0)
                    {
                        air.Add(i);
                    }
                }
                double[] held = air.ConvertAll(i => delivery.Before[i]).ToArray();
                double[] grams = air.ConvertAll(i => (double)Mole.MolarMass(types[i])).ToArray();
                double[] taken = CelestialRules.Shipped.MineralRemoval(held, grams, landing.MineralKg, landing.AirKgPerKg);
                for (int k = 0; k < air.Count; k++)
                {
                    delivery.Rule[air[k]] = taken[k];
                    if (taken[k] > 0.0)
                    {
                        Planet.RemoveGas(tank, types[air[k]], taken[k]);
                    }
                }
            }
            delivery.After = Array.ConvertAll(types, t => tank.Get(t).ToDouble());
            StoredKelvin(out delivery.LatentKelvinAfter, out delivery.ExternalKelvinAfter);
            return delivery;
        }

        /// <summary>Both stored heats in kelvin: each energy over the heat capacity of the planet and its reservoirs.</summary>
        private static void StoredKelvin(out double latent, out double external)
        {
            double capacity = PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble();
            latent = capacity > 0.0 ? PlanetaryAtmosphereSimulation.LatentEnergyOffset.ToDouble() / capacity : double.NaN;
            external = capacity > 0.0 ? PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset.ToDouble() / capacity : double.NaN;
        }

        private static double Sum(double[] values)
        {
            double total = 0.0;
            foreach (double value in values)
            {
                total += value;
            }
            return total;
        }

        /// <summary>
        /// Host, a fall read from a save is back on the clock (CometMap.ClockRestored): its impact event is made
        /// again, timed to the same clock time, since a save keeps no event.
        /// </summary>
        internal static void BackOnClock(CometLanding landing)
        {
            if (NetworkManager.IsClient)
            {
                return;
            }
            try
            {
                CometDirector.Land(landing);
            }
            catch (Exception e)
            {
                Fault("comet " + landing.CometId + "'s impact could not be shown again after the load; it still lands: " + e.Message);
            }
        }

        internal static void Fault(string message)
        {
            if (++_faults <= 5)
            {
                Log.Warn("[TR comet land] " + message);
            }
        }
    }
}
