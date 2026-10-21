using System;
using System.Globalization;

namespace TerraformingReloaded.Comets
{
    /// <summary>
    /// The rules that decide a comet made and brought down by rocket (docs/COMET-ROCKETS.md): how it
    /// flies by the game's rocket travel rule, what Cut makes of a site, where the sky rule and the
    /// reserve put it down, how far each impact zone reaches, and what is lost on the way. Plain
    /// arithmetic with no game or Unity calls: whatever the game defines (a gas's molar mass, a world's
    /// curvature, the sky's clock) comes in as an argument, so tools/PatchCheck runs
    /// <see cref="Check(CelestialRules)"/> outside the game. The parts are virtual so PatchCheck can hand
    /// the check wrong rules and see each caught.
    /// </summary>
    public class CelestialRules
    {
        public static readonly CelestialRules Shipped = new CelestialRules();

        // ---- the game's rocket, which a comet flies as ----------------------------------------------

        /// <summary>A Pumped Gas engine at full throttle on a 2:1 methane and oxygen premix, as measured in the game.</summary>
        public const double EngineThrustNewtons = 16390.0;

        /// <summary>What a Pumped Gas engine draws at full throttle: 18 mol every 0.5 s atmospherics tick (GovernedGasEngine).</summary>
        public const double EngineMolesPerSecond = 36.0;

        /// <summary>The atmospherics tick the engines draw their fuel on, and the rocket's mass is recounted on.</summary>
        public const double FuelTickSeconds = 0.5;

        /// <summary>What one engine adds to a rocket's mass (RocketEngineBase).</summary>
        public const double EngineMassKg = 500.0;

        /// <summary>Moles of methane the game burns with each mole of oxygen (Combustion.ResultMethaneOxygen).</summary>
        public const double MethanePerOxygen = 2.0;

        /// <summary>
        /// The kilograms in one mole of the engines' premix: <see cref="MethanePerOxygen"/> methane to one
        /// oxygen, from the game's molar masses for the two, which are handed in.
        /// </summary>
        public static double PremixKgPerMole(double methaneGramsPerMole, double oxygenGramsPerMole)
        {
            return (MethanePerOxygen * methaneGramsPerMole + oxygenGramsPerMole) / (MethanePerOxygen + 1.0) / 1000.0;
        }

        /// <summary>
        /// A hop's Δv: twice its map distance (NodeTransit), times the difficulty's space map multiplier
        /// for a standard connection only; an orbit connection (a pad to its orbit) is never multiplied.
        /// </summary>
        public virtual double HopDeltaV(double mapDistance, bool standardConnection, double difficultyMultiplier)
        {
            return 2.0 * Math.Max(0.0, mapDistance) * (standardConnection ? difficultyMultiplier : 1.0);
        }

        /// <summary>
        /// A comet flying a route of <paramref name="routeDeltaV"/> with its engines at full throttle, as
        /// the game flies a rocket: speed gained is thrust over the whole mass (the comet, its engines and
        /// the fuel still aboard) over time, it moves only while the engines burn, and it stops for good
        /// where the fuel runs out. The mass falls as the fuel burns, so this is the rocket equation, not
        /// thrust over a fixed mass: Δv = (thrust / fuel flow) x ln(mass before / mass after).
        /// </summary>
        public virtual Flight Fly(double cometKg, int engines, double fuelMoles, double routeDeltaV, double fuelKgPerMole)
        {
            if (!(routeDeltaV > 0.0))
            {
                return new Flight(0.0, 0.0, 0.0, 1.0, true);
            }
            if (engines <= 0 || !(fuelMoles > 0.0) || !(fuelKgPerMole > 0.0))
            {
                return new Flight(0.0, 0.0, 0.0, 0.0, false);
            }
            double dry = Math.Max(0.0, cometKg) + EngineMassKg * engines;
            double fuelKg = fuelMoles * fuelKgPerMole;
            double flow = engines * EngineMolesPerSecond * fuelKgPerMole;
            double exhaust = engines * EngineThrustNewtons / flow;
            double start = dry + fuelKg;
            double neededKg = start * (1.0 - Math.Exp(-routeDeltaV / exhaust));
            if (neededKg <= fuelKg)
            {
                return new Flight(neededKg / flow, neededKg / fuelKgPerMole, routeDeltaV, 1.0, true);
            }
            double reached = exhaust * Math.Log(start / dry);
            return new Flight(fuelKg / flow, fuelMoles, reached, reached / routeDeltaV, false);
        }

        /// <summary>
        /// The Δv a comet has gained once its engines have burnt for <paramref name="seconds"/> at full
        /// throttle, by the rocket equation, and no more once the fuel is gone: Δv = (thrust / fuel flow)
        /// x ln(mass at the start / mass now).
        /// </summary>
        public virtual double DeltaVAfter(double cometKg, int engines, double fuelMoles, double seconds, double fuelKgPerMole)
        {
            if (engines <= 0 || !(fuelMoles > 0.0) || !(fuelKgPerMole > 0.0) || !(seconds > 0.0))
            {
                return 0.0;
            }
            double dry = Math.Max(0.0, cometKg) + EngineMassKg * engines;
            double fuelKg = fuelMoles * fuelKgPerMole;
            double flow = engines * EngineMolesPerSecond * fuelKgPerMole;
            double exhaust = engines * EngineThrustNewtons / flow;
            double start = dry + fuelKg;
            return exhaust * Math.Log(start / (start - Math.Min(fuelKg, flow * seconds)));
        }

        /// <summary>
        /// Where a comet flying a route of these hops stands <paramref name="seconds"/> after it set off,
        /// as the game's rocket travel rule puts it: its Δv so far, counted through the hops in order,
        /// with the mass falling as the fuel burns. It moves only while it burns, so once the route is
        /// flown it stands at the end, and once the fuel is gone it stands where that left it, for good.
        /// A pure function of the time flown, so a save, a load and every client agree on it.
        /// </summary>
        public virtual RoutePosition PositionAt(double[] hopDeltaVs, double cometKg, int engines, double fuelMoles, double seconds, double fuelKgPerMole)
        {
            double route = 0.0;
            foreach (double hop in hopDeltaVs)
            {
                route += Math.Max(0.0, hop);
            }
            Flight whole = Fly(cometKg, engines, fuelMoles, route, fuelKgPerMole);
            double flown = Math.Max(0.0, seconds);
            if (flown >= whole.Seconds)
            {
                // Arrived is the end of the last hop exactly, whatever the rounding in summing the hops.
                Stop end = whole.Arrives ? new Stop(hopDeltaVs.Length - 1, 1.0) : StopAt(hopDeltaVs, whole.DeltaV);
                return new RoutePosition(end, whole.DeltaV, whole.Arrives, true, whole.Seconds, Math.Max(0.0, fuelMoles - whole.FuelMoles));
            }
            double gained = Math.Min(route, DeltaVAfter(cometKg, engines, fuelMoles, flown, fuelKgPerMole));
            return new RoutePosition(StopAt(hopDeltaVs, gained), gained, false, false, flown,
                Math.Max(0.0, fuelMoles - engines * EngineMolesPerSecond * flown));
        }

        /// <summary>Planet size the size multiplier setting is stated for: Standard, 1/20 of the shipped planet.</summary>
        public const double StandardPlanetSize = 0.05;

        /// <summary>
        /// The size multiplier K in force on a planet of this size, as a share of the shipped planet: the
        /// setting at Standard size, times the planet's size over Standard, never under 1. What a planet
        /// needs grows with its size, so a third of it does too, and one site keeps to that third on every
        /// size.
        /// </summary>
        public virtual double SizeMultiplier(double standardMultiplier, double planetSize)
        {
            return Math.Max(1.0, Math.Max(0.0, standardMultiplier) * Math.Max(0.0, planetSize) / StandardPlanetSize);
        }

        /// <summary>
        /// Where along a route of these hops a comet stops that has gained <paramref name="reachedDeltaV"/>:
        /// the hop it is in and how far through it, as the game's Progress. One that has the whole route
        /// stands at the end of the last hop.
        /// </summary>
        public static Stop StopAt(double[] hopDeltaVs, double reachedDeltaV)
        {
            double left = Math.Max(0.0, reachedDeltaV);
            for (int i = 0; i < hopDeltaVs.Length; i++)
            {
                double hop = hopDeltaVs[i];
                if (left < hop)
                {
                    return new Stop(i, left / hop);
                }
                left -= Math.Max(0.0, hop);
            }
            return new Stop(hopDeltaVs.Length - 1, 1.0);
        }

        // ---- what Cut makes of a site -----------------------------------------------------------------

        /// <summary>The game's mining cycle at a site of this density with a head of this speed, in seconds.</summary>
        public static double CycleSeconds(double density, double headSpeed)
        {
            return (10.0 - 0.4 * density) / headSpeed;
        }

        /// <summary>
        /// The moles a comet holds after this many Cut cycles: each cycle adds its share of the site, and
        /// the comet stops growing at <paramref name="sizeMultiplier"/> times the site's gas.
        /// </summary>
        public virtual double CometMoles(double siteMoles, double cycles, double sharePerCycle, double sizeMultiplier)
        {
            double cut = Math.Min(1.0, Math.Max(0.0, cycles) * Math.Max(0.0, sharePerCycle));
            return Math.Max(0.0, sizeMultiplier) * Math.Max(0.0, siteMoles) * cut;
        }

        /// <summary>A comet's mass in kilograms: each gas's moles times its molar mass in grams, as the game defines it.</summary>
        public virtual double MassKg(double[] moles, double[] gramsPerMole)
        {
            double grams = 0.0;
            for (int i = 0; i < moles.Length; i++)
            {
                grams += Math.Max(0.0, moles[i]) * gramsPerMole[i];
            }
            return grams / 1000.0;
        }

        // ---- the sky rule and the reserve ---------------------------------------------------------------

        /// <summary>The curvature the disc radius setting is stated for: Mars2's and Venus's.</summary>
        public const double ReferenceCurvature = 0.3;

        /// <summary>
        /// The disc a comet lands in on a world drawn with this curvature. The terrain shader lowers
        /// ground by 0.001 x (distance x curvature)^2, so how far a column stays in sight goes as one over
        /// the curvature, and the disc shrinks with it. A world with no curvature takes the setting.
        /// </summary>
        public virtual double DiscRadius(double settingMetres, double curvature)
        {
            return curvature > 0.0 ? settingMetres * ReferenceCurvature / curvature : settingMetres;
        }

        /// <summary>
        /// The point under the comet: in the comet's compass direction from the pad, at the disc radius
        /// times (90 degrees less its height in the sky) over 180. Straight overhead is the pad, the
        /// horizon half the radius, straight below the edge of the disc. The direction is the one the
        /// sky shows it in (Celestial.WorldVector), x and z across the ground and y up; it need not be
        /// of unit length. Straight below has no compass direction and comes down north of the pad.
        /// </summary>
        public virtual GroundPoint SkyPoint(double padX, double padZ, double dirX, double dirY, double dirZ, double radius)
        {
            double length = Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
            if (!(length > 0.0))
            {
                return new GroundPoint(padX, padZ);
            }
            double height = Math.Asin(Math.Max(-1.0, Math.Min(1.0, dirY / length))) * 180.0 / Math.PI;
            double distance = radius * (90.0 - height) / 180.0;
            double flat = Math.Sqrt(dirX * dirX + dirZ * dirZ);
            double east = flat > 1e-12 * length ? dirX / flat : 0.0;
            double north = flat > 1e-12 * length ? dirZ / flat : 1.0;
            return new GroundPoint(padX + east * distance, padZ + north * distance);
        }

        /// <summary>
        /// The sky rule from a bearing: azimuth in degrees clockwise from north (+z) through east (+x),
        /// height in degrees above the horizon, negative below it.
        /// </summary>
        public GroundPoint SkyPointAt(double padX, double padZ, double azimuthDegrees, double heightDegrees, double radius)
        {
            double azimuth = azimuthDegrees * Math.PI / 180.0;
            double height = heightDegrees * Math.PI / 180.0;
            return SkyPoint(padX, padZ, Math.Sin(azimuth) * Math.Cos(height), Math.Sin(height), Math.Cos(azimuth) * Math.Cos(height), radius);
        }

        /// <summary>
        /// How far the fuel left at Entry can move where a comet lands: the Δv that fuel gives,
        /// <paramref name="metresPerDeltaV"/> metres for each metre a second, but no more Δv than the
        /// engines can give while it falls: they burn for <paramref name="windowSeconds"/> at most, so a
        /// heavy comet with few engines steers little however much fuel it has.
        /// </summary>
        public virtual double ReachMetres(double cometKg, int engines, double fuelMoles, double metresPerDeltaV, double windowSeconds, double fuelKgPerMole)
        {
            if (engines <= 0 || !(fuelMoles > 0.0) || !(fuelKgPerMole > 0.0))
            {
                return 0.0;
            }
            double dry = Math.Max(0.0, cometKg) + EngineMassKg * engines;
            double fuelKg = fuelMoles * fuelKgPerMole;
            double flow = engines * EngineMolesPerSecond * fuelKgPerMole;
            double exhaust = engines * EngineThrustNewtons / flow;
            double burnt = Math.Min(fuelKg, flow * Math.Max(0.0, windowSeconds));
            double start = dry + fuelKg;
            return Math.Max(0.0, metresPerDeltaV) * exhaust * Math.Log(start / (start - burnt));
        }

        /// <summary>
        /// Where a comet lands: the sky point moved straight toward the target by the reach or the whole
        /// gap, whichever is less, and kept inside the disc around the pad. A burn with no target passes
        /// the sky point as the target and lands there.
        /// </summary>
        public virtual GroundPoint Landing(GroundPoint sky, GroundPoint target, double reachMetres, double padX, double padZ, double radius)
        {
            double dx = target.X - sky.X;
            double dz = target.Z - sky.Z;
            double gap = Math.Sqrt(dx * dx + dz * dz);
            double move = Math.Min(gap, Math.Max(0.0, reachMetres));
            double x = gap > 0.0 ? sky.X + dx / gap * move : sky.X;
            double z = gap > 0.0 ? sky.Z + dz / gap * move : sky.Z;
            double ox = x - padX;
            double oz = z - padZ;
            double out_ = Math.Sqrt(ox * ox + oz * oz);
            if (out_ > radius && out_ > 0.0)
            {
                return new GroundPoint(padX + ox / out_ * radius, padZ + oz / out_ * radius);
            }
            return new GroundPoint(x, z);
        }

        // ---- the orbit ------------------------------------------------------------------------------------

        /// <summary>
        /// The orbit period, in the Earth days the game's sky takes, that goes round once in
        /// <paramref name="orbitSeconds"/> real seconds on a world whose own orbit is
        /// <paramref name="playerPeriodDays"/> and whose sky runs at <paramref name="timeScale"/> degrees of
        /// that orbit a second (OrbitalSimulation.TimeScale). A body's anomaly runs player period over its
        /// period times the sky's clock, so this is the period that makes one turn take that long.
        /// </summary>
        public virtual double OrbitPeriodDays(double orbitSeconds, double playerPeriodDays, double timeScale)
        {
            return orbitSeconds * playerPeriodDays * timeScale / 360.0;
        }

        /// <summary>The other way round: real seconds for one turn of an orbit of this period.</summary>
        public static double OrbitRealSeconds(double periodDays, double playerPeriodDays, double timeScale)
        {
            return 360.0 * periodDays / (playerPeriodDays * timeScale);
        }

        /// <summary>How far the ground turns under the orbit from one pass to the next, in degrees of the day.</summary>
        public virtual double TurnPerOrbitDegrees(double orbitSeconds, double solarDaySeconds)
        {
            return 360.0 * orbitSeconds / solarDaySeconds;
        }

        /// <summary>
        /// How near the pad a comet's path comes, over a day of passes, as a share of the disc radius: the
        /// nearest and the farthest of the passes' closest points. The sky turns the pad's zenith round the
        /// planet's axis once a day; the zenith sits (90 - latitude) from the axis and the orbit's pole
        /// <paramref name="inclinationDegrees"/> from it, so the angle between them sweeps from their
        /// difference to their sum, and a path's highest point is that angle's distance from 90 degrees
        /// off the zenith. 0 nearest means some passes go straight overhead.
        /// </summary>
        public virtual Pass ClosestPass(double latitudeDegrees, double inclinationDegrees)
        {
            double toRadians = Math.PI / 180.0;
            double zenith = (90.0 - Math.Abs(latitudeDegrees)) * toRadians;
            double pole = inclinationDegrees * toRadians;
            double least = Math.Acos(Math.Cos(zenith - pole)) / toRadians;
            double most = Math.Acos(Math.Cos(zenith + pole)) / toRadians;
            double nearest = least <= 90.0 && most >= 90.0 ? 0.0 : Math.Min(Math.Abs(90.0 - least), Math.Abs(90.0 - most));
            double farthest = Math.Max(Math.Abs(90.0 - least), Math.Abs(90.0 - most));
            return new Pass(nearest / 180.0, farthest / 180.0);
        }

        /// <summary>
        /// Seconds to fall from <paramref name="heightMetres"/> starting down at <paramref name="startSpeed"/>
        /// under <paramref name="gravity"/>, with nothing slowing it: how a landing rocket's descent runs in
        /// the game before its engines brake it.
        /// </summary>
        public static double FallSeconds(double startSpeed, double heightMetres, double gravity)
        {
            return (-startSpeed + Math.Sqrt(startSpeed * startSpeed + 2.0 * gravity * heightMetres)) / gravity;
        }

        // ---- the impact zones -------------------------------------------------------------------------------

        public const double ZoneReferenceMoles = 1e6;

        /// <summary>
        /// Ground zero's radius: the setting at a million moles, growing with the cube root of the moles,
        /// and no further past <see cref="CometRules.WaveMolesCap"/>, where the wave and the show stop growing.
        /// </summary>
        public virtual double GroundZeroMetres(double moles, double metresAt1e6)
        {
            double felt = Math.Min(Math.Max(0.0, moles), CometRules.WaveMolesCap);
            return Math.Max(0.0, metresAt1e6) * Math.Pow(felt / ZoneReferenceMoles, 1.0 / 3.0);
        }

        /// <summary>
        /// The one zone a point this far from an impact is in, innermost first. Past the falling rocks only
        /// the pressure wave reaches, by its own rule (docs/COMET.md section 3).
        /// </summary>
        public virtual CometZone ZoneAt(double metres, double moles, CelestialTuning tuning)
        {
            double groundZero = GroundZeroMetres(moles, tuning.GroundZeroMetresAt1e6);
            double d = Math.Max(0.0, metres);
            if (d <= groundZero)
            {
                return CometZone.GroundZero;
            }
            if (d <= groundZero * tuning.FireballRadiusFactor)
            {
                return CometZone.Fireball;
            }
            if (d <= groundZero * tuning.DebrisRadiusFactor)
            {
                return CometZone.Debris;
            }
            return CometZone.Wave;
        }

        // ---- what is lost -------------------------------------------------------------------------------------

        /// <summary>The moles of one gas that reach the planet's air, of what was left after the flight.</summary>
        public virtual double Arriving(double moles, double share)
        {
            return Math.Max(0.0, moles) * Math.Max(0.0, Math.Min(1.0, share));
        }

        /// <summary>
        /// A comet feeding its own engines <paramref name="fuelWanted"/> moles of premix from its own
        /// methane and oxygen, at the engines' <see cref="MethanePerOxygen"/> to one, or as much of it as
        /// the scarcer of the two allows. What it returns is gone from the comet.
        /// </summary>
        public virtual OwnFuel BurnOwnFuel(double methane, double oxygen, double fuelWanted)
        {
            double share = MethanePerOxygen + 1.0;
            double most = Math.Min(Math.Max(0.0, methane) * share / MethanePerOxygen, Math.Max(0.0, oxygen) * share);
            double burnt = Math.Min(Math.Max(0.0, fuelWanted), most);
            return new OwnFuel(burnt * MethanePerOxygen / share, burnt / share);
        }

        /// <summary>
        /// The moles of the planet's air a mineral comet takes away: its mass times the kilograms of air
        /// per kilogram, over the air's mean molar mass, and never more than the planet holds. The caller
        /// takes them from each gas in its share of the air.
        /// </summary>
        public virtual double MineralRemovedMoles(double cometKg, double airKgPerKg, double planetMoles, double planetGramsPerMole)
        {
            if (!(planetGramsPerMole > 0.0))
            {
                return 0.0;
            }
            double moles = Math.Max(0.0, cometKg) * Math.Max(0.0, airKgPerKg) * 1000.0 / planetGramsPerMole;
            return Math.Min(Math.Max(0.0, planetMoles), moles);
        }

        // ---- the check ---------------------------------------------------------------------------------------

        // Game facts the defaults are derived from (docs/COMET-ROCKETS.md, the config table). Numbers, not
        // code: each is a figure read from the game's data or worked from its rules.
        private const double TestFuelKgPerMole = 0.016;          // methane 16 and oxygen 16 g/mol in Mole.MolarMass
        private const double MarsGravity = 3.7;                  // Mars2.xml
        private const double ReEntryMetres = 25000.0;            // the Low re-entry profile, the game's default
        private const double EntrySpeed = 400.0;                 // a landing starts down at its leg's Δv, 2 x OrbitDistance 200
        private const double MarsPeriodDays = 687.0;             // celestialbodies.xml
        private const double MarsTimeScale = 4.494e-4;           // OrbitalSimulation.CalculateTimeScale on Mars2
        private const double PhobosPeriodDays = 0.3189;          // celestialbodies.xml
        private const double MostShippedLatitude = 58.4;         // Mimas (MimasHerschel.xml), the farthest from any equator
        private const double LargestSiteMoles = 3048381.0;       // Europa's map, D8 R3 S7 x 24 mol, before the game rounds it
        private const double LargestMarsOrMoonSiteMoles = 2785190.0;
        private const double ShippedCells = 5000000.0;           // 40 billion litres in 8,000 L cells
        private const double StandardCells = ShippedCells * StandardPlanetSize;
        private const double LeastBreathableNeedPerCell = 121.0; // the Moon, docs/BALANCE.md (Mars 125)
        private const double BasicHeadCycles = 1000.0;           // 100 quantity at a 0.1 chance of wear a cycle

        private static readonly Lazy<string> ShippedVerdict = new Lazy<string>(() =>
        {
            try
            {
                return CelestialSettings.CheckTable() ?? Check(Shipped);
            }
            catch (Exception e)
            {
                return "the check itself failed: " + e.Message;
            }
        });

        /// <summary>The shipped rules' own check, run once: null when they hold, else the first case that does not.</summary>
        public static string Verdict => ShippedVerdict.Value;

        /// <summary>
        /// The rules worked through on cases with known answers, against a step by step flight the way the
        /// game ticks one, and the defaults held to their derivation. Returns the first case that does not
        /// hold, or null.
        /// </summary>
        public static string Check(CelestialRules rules)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            CelestialTuning tuning = CelestialTuning.Defaults;

            // ---- travel ----
            if (Math.Abs(rules.HopDeltaV(151.33, true, 1.0) - 302.66) > 1e-9 || Math.Abs(rules.HopDeltaV(151.33, true, 1.5) - 453.99) > 1e-9
                || Math.Abs(rules.HopDeltaV(200.0, false, 1.5) - 400.0) > 1e-9)
            {
                return "a hop's delta-v is not twice its distance, with the difficulty on standard connections only";
            }
            // docs/COMET-ROCKETS.md's worked example: an 18 t comet, two engines, Mars Orbit to Frozen Vapors,
            // carrying the fuel it needs and a little over.
            Flight example = rules.Fly(18000.0, 2, 16092.0, 383.0, TestFuelKgPerMole);
            if (!example.Arrives || Math.Abs(example.Seconds - 223.50) > 0.05 || Math.Abs(example.FuelMoles - 16091.7) > 1.0)
            {
                return string.Format(c, "an 18 t comet with two engines flies 383 delta-v in {0:0.00} s on {1:0.0} mol, expected 223.50 s and 16,091.7 mol", example.Seconds, example.FuelMoles);
            }
            foreach (var (comet, engines, fuel, route) in new[] { (18000.0, 2, 20000.0, 383.0), (1000.0, 1, 62500.0, 40000.0), (200000.0, 4, 300000.0, 583.3) })
            {
                Flight flown = rules.Fly(comet, engines, fuel, route, TestFuelKgPerMole);
                Flight ticked = Ticked(comet, engines, fuel, route, TestFuelKgPerMole);
                if (flown.Arrives != ticked.Arrives || Math.Abs(flown.Seconds - ticked.Seconds) > 0.002 * ticked.Seconds + 0.05
                    || Math.Abs(flown.DeltaV - ticked.DeltaV) > 0.002 * ticked.DeltaV + 0.05)
                {
                    return string.Format(c, "a comet of {0:N0} kg with {1} engines and {2:N0} mol for {3} delta-v: the rule says {4:0.0} s to {5:0.0} delta-v, the game's ticks {6:0.0} s to {7:0.0} delta-v",
                        comet, engines, fuel, route, flown.Seconds, flown.DeltaV, ticked.Seconds, ticked.DeltaV);
                }
            }
            Flight short_ = rules.Fly(18000.0, 2, 16092.0 * 0.99, 383.0, TestFuelKgPerMole);
            if (short_.Arrives || !(short_.Share < 1.0) || Math.Abs(short_.Share - short_.DeltaV / 383.0) > 1e-12
                || Math.Abs(short_.FuelMoles - 16092.0 * 0.99) > 1e-6)
            {
                return "a comet with a little less fuel than its route needs does not stop short, burning all it has, where its delta-v runs out";
            }
            Flight twice = rules.Fly(36000.0 + 1000.0, 2, 2.0 * 16092.0, 383.0, TestFuelKgPerMole);
            if (Math.Abs(twice.FuelMoles / example.FuelMoles - 2.0) > 0.01)
            {
                return "doubling a comet does not double its fuel";
            }
            if (rules.Fly(18000.0, 0, 20000.0, 383.0, TestFuelKgPerMole).DeltaV != 0.0 || rules.Fly(18000.0, 2, 0.0, 383.0, TestFuelKgPerMole).Arrives)
            {
                return "a comet without engines or fuel moves";
            }
            Stop stop = StopAt(new[] { 302.7, 80.6, 200.0 }, 350.0);
            if (stop.Hop != 1 || Math.Abs(stop.Progress - 47.3 / 80.6) > 1e-9 || StopAt(new[] { 302.7, 80.6 }, 500.0).Progress != 1.0)
            {
                return "a comet does not stop in the hop where its delta-v runs out";
            }
            string flight = CheckRoute(rules);
            if (flight != null)
            {
                return flight;
            }

            // ---- what Cut makes ----
            if (Math.Abs(rules.CometMoles(2.4e6, 1.0, 0.001, 5.0) - 12000.0) > 1e-6 || Math.Abs(rules.CometMoles(2.4e6, 1000.0, 0.001, 5.0) - 1.2e7) > 1e-3
                || Math.Abs(rules.CometMoles(2.4e6, 5000.0, 0.001, 5.0) - 1.2e7) > 1e-3 || rules.CometMoles(2.4e6, 0.0, 0.001, 5.0) != 0.0)
            {
                return "a comet does not grow by its share of the site each cycle up to the size multiplier times the site";
            }
            if (Math.Abs(rules.MassKg(new[] { 2.3e6 }, new[] { 108.0 }) - 248400.0) > 1e-6
                || Math.Abs(rules.MassKg(new[] { 20.0, 4.0 }, new[] { 16.0, 64.0 }) - 0.576) > 1e-12)
            {
                return "a comet's mass is not its moles times each gas's molar mass";
            }

            // ---- the sky rule ----
            const double R = 5100.0;
            (double X, double Y, double Z, double ExpectX, double ExpectZ)[] sky =
            {
                (0.0, 1.0, 0.0, 0.0, 0.0),                          // straight overhead: the pad
                (1.0, 0.0, 0.0, R / 2.0, 0.0),                      // on the horizon to the east: half out
                (0.0, -1.0, 0.0, 0.0, R),                           // straight below: the edge, north
                (0.0, Math.Sqrt(0.5), Math.Sqrt(0.5), 0.0, R / 4.0),  // halfway up to the north: a quarter out
                (-Math.Cos(Math.PI / 6.0), -0.5, 0.0, -R * 2.0 / 3.0, 0.0), // 30 degrees below the horizon to the west
                (0.0, 3.0, 0.0, 0.0, 0.0),                          // a direction that is not of unit length
            };
            foreach (var (x, y, z, ex, ez) in sky)
            {
                GroundPoint point = rules.SkyPoint(100.0, -200.0, x, y, z, R);
                if (Math.Abs(point.X - (100.0 + ex)) > 1e-6 || Math.Abs(point.Z - (-200.0 + ez)) > 1e-6)
                {
                    return string.Format(c, "a comet in the direction ({0:0.###}, {1:0.###}, {2:0.###}) comes down at ({3:0.#}, {4:0.#}) from a pad at (100, -200), expected ({5:0.#}, {6:0.#})",
                        x, y, z, point.X, point.Z, 100.0 + ex, -200.0 + ez);
                }
            }
            GroundPoint east = rules.SkyPointAt(0.0, 0.0, 90.0, 0.0, R);
            GroundPoint south = rules.SkyPointAt(0.0, 0.0, 180.0, 30.0, R);
            if (Math.Abs(east.X - R / 2.0) > 1e-6 || Math.Abs(east.Z) > 1e-6 || Math.Abs(south.Z + R / 3.0) > 1e-6 || Math.Abs(south.X) > 1e-6)
            {
                return "a bearing is not clockwise from north through east";
            }
            if (Math.Abs(rules.DiscRadius(5100.0, 0.3) - 5100.0) > 1e-9 || Math.Abs(rules.DiscRadius(5100.0, 0.45) - 3400.0) > 1e-9
                || Math.Abs(rules.DiscRadius(5100.0, 0.6) - 2550.0) > 1e-9 || rules.DiscRadius(5100.0, 0.0) != 5100.0)
            {
                return "the disc does not shrink as the world is drawn more curved";
            }

            // ---- the reserve ----
            // Plenty of fuel: no more than the engines give in the fall, which a steady burn over the whole
            // fall turns into half a T squared sideways.
            double reach = rules.ReachMetres(18000.0, 2, 20000.0, 25.0, 50.0, TestFuelKgPerMole);
            double accel = 2.0 * EngineThrustNewtons / (19000.0 + 20000.0 * TestFuelKgPerMole);
            if (Math.Abs(reach - 0.5 * accel * 50.0 * 50.0) > 0.01 * reach)
            {
                return string.Format(c, "an 18 t comet with two engines and plenty of fuel steers {0:0.#} m in a 50 s fall, expected about {1:0.#} m", reach, 0.5 * accel * 2500.0);
            }
            double little = rules.ReachMetres(18000.0, 2, 360.0, 25.0, 50.0, TestFuelKgPerMole);
            double littleDv = EngineThrustNewtons / (EngineMolesPerSecond * TestFuelKgPerMole) * Math.Log((19000.0 + 5.76) / 19000.0);
            if (Math.Abs(little - 25.0 * littleDv) > 1e-6 || rules.ReachMetres(18000.0, 2, 0.0, 25.0, 50.0, TestFuelKgPerMole) != 0.0
                || rules.ReachMetres(18000.0, 0, 360.0, 25.0, 50.0, TestFuelKgPerMole) != 0.0)
            {
                return "a reserve smaller than the engines can burn in the fall does not steer by its own delta-v, or no fuel or no engines steers";
            }
            GroundPoint from = new GroundPoint(1000.0, 0.0);
            GroundPoint onTarget = rules.Landing(from, new GroundPoint(1300.0, 400.0), 600.0, 0.0, 0.0, R);
            GroundPoint shortOf = rules.Landing(from, new GroundPoint(1300.0, 400.0), 250.0, 0.0, 0.0, R);
            GroundPoint edge = rules.Landing(new GroundPoint(5000.0, 0.0), new GroundPoint(9000.0, 0.0), 2000.0, 0.0, 0.0, R);
            GroundPoint untracked = rules.Landing(from, from, 600.0, 0.0, 0.0, R);
            if (Math.Abs(onTarget.X - 1300.0) > 1e-9 || Math.Abs(onTarget.Z - 400.0) > 1e-9
                || Math.Abs(shortOf.X - 1150.0) > 1e-9 || Math.Abs(shortOf.Z - 200.0) > 1e-9
                || Math.Abs(edge.X - R) > 1e-9 || Math.Abs(edge.Z) > 1e-9 || untracked.X != 1000.0 || untracked.Z != 0.0)
            {
                return "a landing does not reach a target in reach, stop the reach short of one out of it, stay in the disc, or land where the sky puts an untracked burn";
            }

            // ---- the orbit ----
            if (Math.Abs(rules.OrbitPeriodDays(OrbitRealSeconds(PhobosPeriodDays, MarsPeriodDays, MarsTimeScale), MarsPeriodDays, MarsTimeScale) - PhobosPeriodDays) > 1e-9
                || Math.Abs(rules.OrbitPeriodDays(97.17, MarsPeriodDays, MarsTimeScale) - 2.0 / 24.0) > 0.001)
            {
                return "the orbit period is not what turns once in the asked real time under the world's sky clock";
            }
            if (Math.Abs(rules.TurnPerOrbitDegrees(372.0, 1200.0) - 111.6) > 1e-9)
            {
                return "the ground does not turn by the orbit's share of the day between passes";
            }
            (double Latitude, double Tilt, double Nearest, double Farthest)[] passes =
            {
                (13.5, 60.0, 0.0, 73.5), (58.4, 60.0, 0.0, 61.6), (58.4, 50.0, 8.4, 71.6), (45.0, 0.0, 45.0, 45.0), (0.0, 0.0, 0.0, 0.0),
            };
            foreach (var (latitude, tilt, nearest, farthest) in passes)
            {
                Pass pass = rules.ClosestPass(latitude, tilt);
                if (Math.Abs(pass.Nearest * 180.0 - nearest) > 1e-6 || Math.Abs(pass.Farthest * 180.0 - farthest) > 1e-6)
                {
                    return string.Format(c, "at latitude {0} under an orbit tilted {1} the passes come within {2:0.###} to {3:0.###} degrees of overhead, expected {4} to {5}",
                        latitude, tilt, pass.Nearest * 180.0, pass.Farthest * 180.0, nearest, farthest);
                }
            }

            // ---- the zones ----
            if (Math.Abs(rules.GroundZeroMetres(1e6, 186.0) - 186.0) > 1e-9 || Math.Abs(rules.GroundZeroMetres(8e6, 186.0) - 372.0) > 1e-9
                || Math.Abs(rules.GroundZeroMetres(1e12, 186.0) - 1860.0) > 1e-6 || rules.GroundZeroMetres(0.0, 186.0) != 0.0)
            {
                return "ground zero does not grow with the cube root of the moles, up to where the wave stops growing";
            }
            foreach (var (metres, zone) in new[] { (0.0, CometZone.GroundZero), (186.0, CometZone.GroundZero), (300.0, CometZone.Fireball), (416.0, CometZone.Fireball), (1000.0, CometZone.Debris), (1315.0, CometZone.Debris), (1316.0, CometZone.Wave), (2000.0, CometZone.Wave) })
            {
                CometZone got = rules.ZoneAt(metres, 1e6, tuning);
                if (got != zone)
                {
                    return string.Format(c, "{0} m from a comet of a million moles is {1}, expected {2}", metres, got, zone);
                }
            }

            // ---- the losses ----
            if (Math.Abs(rules.Arriving(1000.0, 0.7) - 700.0) > 1e-9 || rules.Arriving(1000.0, 0.0) != 0.0 || rules.Arriving(1000.0, 1.5) != 1000.0)
            {
                return "the gas arriving is not its share of what is left";
            }
            OwnFuel plenty = rules.BurnOwnFuel(1000.0, 1000.0, 900.0);
            OwnFuel scarce = rules.BurnOwnFuel(100.0, 1000.0, 900.0);
            if (Math.Abs(plenty.Methane - 600.0) > 1e-9 || Math.Abs(plenty.Oxygen - 300.0) > 1e-9
                || Math.Abs(scarce.Methane - 100.0) > 1e-9 || Math.Abs(scarce.Oxygen - 50.0) > 1e-9 || rules.BurnOwnFuel(1000.0, 0.0, 900.0).Methane != 0.0)
            {
                return "a comet feeding its own engines does not burn two of methane to one of oxygen, as far as the scarcer allows";
            }
            if (Math.Abs(rules.MineralRemovedMoles(1000.0, 0.7, 1e9, 43.0) - 700000.0 / 43.0) > 1e-6 || rules.MineralRemovedMoles(1000.0, 0.7, 10.0, 43.0) != 10.0
                || rules.MineralRemovedMoles(0.0, 0.7, 1e9, 43.0) != 0.0)
            {
                return "a mineral comet does not take its mass times the air per kilogram, in moles of the planet's air, up to what the planet holds";
            }

            // ---- the defaults, held to their derivation ----
            if (Math.Abs(tuning.CutSharePerCycle * BasicHeadCycles - 1.0) > 1e-9)
            {
                return "a whole comet is not the working life of one basic drill head";
            }
            if (tuning.CometSizeMultiplier * tuning.GasArrivingShare * LargestMarsOrMoonSiteMoles >= LeastBreathableNeedPerCell * StandardCells / 3.0
                || (tuning.CometSizeMultiplier + 1.0) * tuning.GasArrivingShare * LargestMarsOrMoonSiteMoles < LeastBreathableNeedPerCell * StandardCells / 3.0)
            {
                return "the size multiplier is not the largest whole one that keeps the biggest site's comet under a third of what a Standard Moon or Mars needs";
            }
            // Following the planet's size, it keeps to the third on Short and Long too.
            foreach (double size in new[] { 0.01, StandardPlanetSize, 0.25 })
            {
                double k = rules.SizeMultiplier(tuning.CometSizeMultiplier, size);
                double third = LeastBreathableNeedPerCell * ShippedCells * size / 3.0;
                if (k * tuning.GasArrivingShare * LargestMarsOrMoonSiteMoles >= third)
                {
                    return string.Format(c, "at planet size {0} the biggest site's comet brings {1:N0} mol at K = {2}, a third of what the Moon needs there is {3:N0}",
                        size, k * tuning.GasArrivingShare * LargestMarsOrMoonSiteMoles, k, third);
                }
            }
            double safe = CometRules.Shipped.SafeDistance(CometRules.Shipped.Scale(tuning.CometSizeMultiplier * LargestSiteMoles));
            if (tuning.DiscRadiusMetres / 2.0 < safe || tuning.DiscRadiusMetres / 2.0 > safe + 100.0)
            {
                return string.Format(c, "half the disc, {0} m, is not the biggest rocket comet's safe distance, {1:0} m, rounded up to the next 100 m", tuning.DiscRadiusMetres / 2.0, safe);
            }
            if (Math.Abs(tuning.OrbitSeconds - OrbitRealSeconds(PhobosPeriodDays, MarsPeriodDays, MarsTimeScale)) > 1.0)
            {
                return "the orbit is not as long as Phobos's on Mars";
            }
            if (rules.ClosestPass(MostShippedLatitude, tuning.OrbitInclinationDegrees).Nearest != 0.0)
            {
                return "the orbit is not tilted enough to pass overhead on every shipped world";
            }
            double fall = FallSeconds(EntrySpeed, ReEntryMetres, MarsGravity);
            if (Math.Abs(tuning.EntryWindowSeconds - fall) > 1.0 || Math.Abs(tuning.CorrectionMetresPerDeltaV - tuning.EntryWindowSeconds / 2.0) > 1e-9)
            {
                return string.Format(c, "the fall is not a rocket's from re-entry on Mars ({0:0.0} s), or the steering per m/s is not half of it", fall);
            }
            CometRules wave = CometRules.Shipped;
            double strongest = WaveReach(wave, 1e6, 10000.0);
            if (Math.Abs(tuning.GroundZeroMetresAt1e6 - strongest) > 1.0
                || Math.Abs(tuning.FireballRadiusFactor * tuning.GroundZeroMetresAt1e6 / WaveReach(wave, 1e6, CometRules.FullForceKPa) - 1.0) > 0.01
                || Math.Abs(tuning.DebrisRadiusFactor * tuning.GroundZeroMetresAt1e6 / WaveReach(wave, 1e6, CometRules.ThrownHardKPa) - 1.0) > 0.01)
            {
                return "the zones are not where the wave passes the strongest wall, throws with full force, and breaks ordinary windows";
            }
            if (tuning.MineralRemovalPerKg != tuning.GasArrivingShare)
            {
                return "a mineral comet does not take away what an ice comet of its weight brings";
            }
            return null;
        }

        /// <summary>
        /// A comet's place on a route over the time it flies: a site under Frozen Vapors back to Mars
        /// Orbit (the site's 200, Frozen Vapors to Phobos 80.6, Phobos to the orbit 302.7), held to a
        /// flight stepped as the game steps one, through every hop, to the end, and short of fuel to where
        /// it runs dry and no further. Then the size multiplier against the planet's size.
        /// </summary>
        private static string CheckRoute(CelestialRules rules)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            const double kg = TestFuelKgPerMole;
            double[] hops = { 200.0, 80.6, 302.7 };
            double route = hops[0] + hops[1] + hops[2];
            const double comet = 18000.0;
            const int engines = 2;
            const double plenty = 30000.0;

            // The rocket equation itself: a light comet whose mass falls from 2,500 to 1,500 kg as it burns.
            double exhaust = EngineThrustNewtons / (EngineMolesPerSecond * kg);
            double burnout = 62500.0 / EngineMolesPerSecond;
            double half = rules.DeltaVAfter(1000.0, 1, 62500.0, burnout / 2.0, kg);
            double full = rules.DeltaVAfter(1000.0, 1, 62500.0, burnout, kg);
            if (Math.Abs(half - exhaust * Math.Log(2500.0 / 2000.0)) > 1e-6 * half || Math.Abs(full - exhaust * Math.Log(2500.0 / 1500.0)) > 1e-6 * full)
            {
                return string.Format(c, "a comet of 1,000 kg, one engine and 1,000 kg of fuel gains {0:0.0} and {1:0.0} m/s at half and all of its burn, expected {2:0.0} and {3:0.0} as its mass falls",
                    half, full, exhaust * Math.Log(2500.0 / 2000.0), exhaust * Math.Log(2500.0 / 1500.0));
            }
            if (Math.Abs(rules.DeltaVAfter(1000.0, 1, 62500.0, 2.0 * burnout, kg) - full) > 1e-9 || rules.DeltaVAfter(1000.0, 1, 62500.0, 0.0, kg) != 0.0
                || rules.DeltaVAfter(1000.0, 0, 62500.0, burnout, kg) != 0.0)
            {
                return "a comet gains speed after its fuel is gone, before it burns, or without engines";
            }

            RoutePosition start = rules.PositionAt(hops, comet, engines, plenty, 0.0, kg);
            if (start.Settled || start.Stop.Hop != 0 || start.Stop.Progress != 0.0 || start.FuelLeft != plenty)
            {
                return "a comet that has not flown yet is not at the start of its route with all its fuel";
            }
            // Through each hop, at the moments a flight stepped as the game steps one reaches those points.
            foreach (double at in new[] { 150.0, 240.0, 500.0 })
            {
                Flight ticked = Ticked(comet, engines, plenty, at, kg);
                RoutePosition p = rules.PositionAt(hops, comet, engines, plenty, ticked.Seconds, kg);
                Stop expected = StopAt(hops, at);
                Stop own = StopAt(hops, p.DeltaV);
                if (p.Settled || p.Stop.Hop != expected.Hop || p.Stop.Hop != own.Hop || Math.Abs(p.Stop.Progress - own.Progress) > 1e-12
                    || Math.Abs(p.DeltaV - at) > 0.002 * at + 0.05)
                {
                    return string.Format(c, "{0:0.0} s into a flight the game's ticks take to {1} delta-v, the rule has the comet at {2:0.0} delta-v, hop {3} at {4:0.0000}, expected hop {5} at {6:0.0000}",
                        ticked.Seconds, at, p.DeltaV, p.Stop.Hop, p.Stop.Progress, expected.Hop, expected.Progress);
                }
            }
            Flight whole = rules.Fly(comet, engines, plenty, route, kg);
            RoutePosition nearly = rules.PositionAt(hops, comet, engines, plenty, whole.Seconds - 0.5, kg);
            RoutePosition there = rules.PositionAt(hops, comet, engines, plenty, whole.Seconds, kg);
            RoutePosition after = rules.PositionAt(hops, comet, engines, plenty, whole.Seconds + 1e6, kg);
            if (nearly.Settled || nearly.Stop.Hop != 2 || !(nearly.Stop.Progress < 1.0)
                || !there.Arrived || !there.Settled || there.Stop.Hop != 2 || there.Stop.Progress != 1.0 || Math.Abs(there.FuelLeft - (plenty - whole.FuelMoles)) > 1e-6
                || !after.Arrived || after.Stop.Hop != 2 || after.Stop.Progress != 1.0 || after.FuelLeft != there.FuelLeft || Math.Abs(after.BurnSeconds - whole.Seconds) > 1e-9)
            {
                return string.Format(c, "a comet does not reach the end of its route at the rule's {0:0.0} s with the fuel it did not burn, and stay there", whole.Seconds);
            }
            double previous = -1.0;
            for (int i = 0; i <= 100; i++)
            {
                double gained = rules.PositionAt(hops, comet, engines, plenty, whole.Seconds * i / 100.0, kg).DeltaV;
                if (gained < previous)
                {
                    return "a comet goes backwards along its route";
                }
                previous = gained;
            }
            double scarce = 0.6 * whole.FuelMoles;
            Flight dry = rules.Fly(comet, engines, scarce, route, kg);
            RoutePosition stranded = rules.PositionAt(hops, comet, engines, scarce, 1e7, kg);
            RoutePosition runningDry = rules.PositionAt(hops, comet, engines, scarce, dry.Seconds, kg);
            RoutePosition halfway = rules.PositionAt(hops, comet, engines, scarce, dry.Seconds / 2.0, kg);
            Stop where = StopAt(hops, dry.DeltaV);
            if (dry.Arrives || !stranded.Settled || stranded.Arrived || Math.Abs(stranded.DeltaV - dry.DeltaV) > 1e-9 || stranded.Stop.Hop != where.Hop
                || Math.Abs(stranded.Stop.Progress - where.Progress) > 1e-12 || stranded.FuelLeft > 1e-9 || Math.Abs(stranded.BurnSeconds - dry.Seconds) > 1e-9
                || !runningDry.Settled || runningDry.DeltaV != stranded.DeltaV
                || halfway.Settled || !(halfway.DeltaV < dry.DeltaV) || Math.Abs(halfway.FuelLeft - (scarce - engines * EngineMolesPerSecond * dry.Seconds / 2.0)) > 1e-6)
            {
                return string.Format(c, "a comet with 60 % of the fuel its route needs does not stop for good where its delta-v runs out ({0:0.0} of {1}, hop {2} at {3:0.0000}), with none left",
                    dry.DeltaV, route, where.Hop, where.Progress);
            }

            // The size multiplier follows the planet's size, and never falls under 1.
            foreach (var (size, expected) in new[] { (0.05, 5.0), (0.01, 1.0), (0.25, 25.0), (1.0, 100.0), (0.002, 1.0) })
            {
                double k = rules.SizeMultiplier(5.0, size);
                if (Math.Abs(k - expected) > 1e-9)
                {
                    return string.Format(c, "the size multiplier at planet size {0} is {1}, expected {2}: 5 at Standard, times the size over 1/20, never under 1", size, k, expected);
                }
            }
            return null;
        }

        /// <summary>The distance at which the wave of a comet this size falls to this peak.</summary>
        private static double WaveReach(CometRules wave, double moles, double kpa)
        {
            double near = CometRules.NearestMetres;
            double far = 1e6;
            for (int i = 0; i < 200; i++)
            {
                double middle = 0.5 * (near + far);
                if (wave.PeakKPa(moles, middle) > kpa)
                {
                    near = middle;
                }
                else
                {
                    far = middle;
                }
            }
            return 0.5 * (near + far);
        }

        /// <summary>
        /// A flight the way the game runs one: the engines draw 18 mol each every 0.5 s atmospherics tick,
        /// the mass is recounted then, and the physics step (0.02 s) adds thrust over that mass to the
        /// hop's progress. Independent of <see cref="Fly"/>, so it can judge it.
        /// </summary>
        private static Flight Ticked(double cometKg, int engines, double fuelMoles, double routeDeltaV, double fuelKgPerMole)
        {
            const double step = 0.02;
            double dry = cometKg + EngineMassKg * engines;
            double fuel = fuelMoles;
            double gained = 0.0;
            double seconds = 0.0;
            double burnt = 0.0;
            double mass = dry + fuel * fuelKgPerMole;
            double nextTick = 0.0;
            while (gained < routeDeltaV)
            {
                if (seconds >= nextTick - 1e-9)
                {
                    double draw = Math.Min(fuel, engines * EngineMolesPerSecond * FuelTickSeconds);
                    if (!(draw > 0.0))
                    {
                        break;
                    }
                    fuel -= draw;
                    burnt += draw;
                    mass = dry + fuel * fuelKgPerMole;
                    nextTick += FuelTickSeconds * draw / (engines * EngineMolesPerSecond * FuelTickSeconds);
                }
                gained += engines * EngineThrustNewtons / mass * step;
                seconds += step;
            }
            bool arrived = gained >= routeDeltaV;
            return new Flight(seconds, burnt, Math.Min(gained, routeDeltaV), Math.Min(1.0, gained / routeDeltaV), arrived);
        }
    }

    /// <summary>The zone an impact puts a point in, innermost first (docs/COMET-ROCKETS.md, "Near the impact").</summary>
    public enum CometZone
    {
        /// <summary>Every structure destroyed whatever its limit; anyone there dies.</summary>
        GroundZero,

        /// <summary>Heat damage to structures outside and to players.</summary>
        Fireball,

        /// <summary>Falling rocks may hit a structure or a player.</summary>
        Debris,

        /// <summary>Only the pressure wave, by its own rule.</summary>
        Wave,
    }

    /// <summary>A flight by the rocket rule. A plain struct: net472 has no IsExternalInit.</summary>
    public readonly struct Flight
    {
        /// <summary>Seconds the engines burn.</summary>
        public readonly double Seconds;

        /// <summary>Moles of fuel burnt.</summary>
        public readonly double FuelMoles;

        /// <summary>The Δv gained, which is how far along the route it got.</summary>
        public readonly double DeltaV;

        /// <summary>The share of the route flown, 1 when it arrives.</summary>
        public readonly double Share;

        public readonly bool Arrives;

        public Flight(double seconds, double fuelMoles, double deltaV, double share, bool arrives)
        {
            Seconds = seconds;
            FuelMoles = fuelMoles;
            DeltaV = deltaV;
            Share = share;
            Arrives = arrives;
        }
    }

    /// <summary>Where on a route a comet stands: the hop, and the game's Progress through it, 0 to 1.</summary>
    public readonly struct Stop
    {
        public readonly int Hop;
        public readonly double Progress;

        public Stop(int hop, double progress)
        {
            Hop = hop;
            Progress = progress;
        }
    }

    /// <summary>Where a comet on its route stands at a moment of its flight, by <see cref="CelestialRules.PositionAt"/>.</summary>
    public readonly struct RoutePosition
    {
        /// <summary>The hop it is in and the game's Progress through it.</summary>
        public readonly Stop Stop;

        /// <summary>The Δv gained so far, which is how far along the route it is.</summary>
        public readonly double DeltaV;

        /// <summary>It has flown the whole route.</summary>
        public readonly bool Arrived;

        /// <summary>It will not move again: arrived, or out of fuel short of the end.</summary>
        public readonly bool Settled;

        /// <summary>Seconds its engines have burnt: the time flown, up to when it arrived or ran dry.</summary>
        public readonly double BurnSeconds;

        /// <summary>Moles of fuel still aboard.</summary>
        public readonly double FuelLeft;

        public RoutePosition(Stop stop, double deltaV, bool arrived, bool settled, double burnSeconds, double fuelLeft)
        {
            Stop = stop;
            DeltaV = deltaV;
            Arrived = arrived;
            Settled = settled;
            BurnSeconds = burnSeconds;
            FuelLeft = fuelLeft;
        }
    }

    /// <summary>A point on the ground, world x and z in metres.</summary>
    public readonly struct GroundPoint
    {
        public readonly double X;
        public readonly double Z;

        public GroundPoint(double x, double z)
        {
            X = x;
            Z = z;
        }
    }

    /// <summary>How near the pad a day of passes comes, nearest and farthest, as shares of the disc radius.</summary>
    public readonly struct Pass
    {
        public readonly double Nearest;
        public readonly double Farthest;

        public Pass(double nearest, double farthest)
        {
            Nearest = nearest;
            Farthest = farthest;
        }
    }

    /// <summary>What a comet burns of its own gas to feed its engines.</summary>
    public readonly struct OwnFuel
    {
        public readonly double Methane;
        public readonly double Oxygen;

        public OwnFuel(double methane, double oxygen)
        {
            Methane = methane;
            Oxygen = oxygen;
        }
    }
}
