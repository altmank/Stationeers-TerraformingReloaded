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
        /// The fuel a comet must carry to fly a route by the rocket rule, the fuel's own weight included:
        /// its dry mass (the comet and its engines) times (e^(Δv / exhaust speed) - 1).
        /// </summary>
        public virtual double FuelNeeded(double cometKg, int engines, double routeDeltaV, double fuelKgPerMole)
        {
            if (engines <= 0 || !(fuelKgPerMole > 0.0) || !(routeDeltaV > 0.0))
            {
                return 0.0;
            }
            double dry = Math.Max(0.0, cometKg) + EngineMassKg * engines;
            double exhaust = EngineThrustNewtons / (EngineMolesPerSecond * fuelKgPerMole);
            return dry * (Math.Exp(routeDeltaV / exhaust) - 1.0) / fuelKgPerMole;
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

        /// <summary>
        /// What one Cut cycle adds to a comet, in the site's own measure (moles of its ice, or units of
        /// its ore): the share of the site a cycle cuts, times the drill head's yield for the site's kind
        /// (IceYieldMultiplier or ReagentYieldMultiplier, as the game's mining uses them), times the size
        /// multiplier, and never past the size multiplier times the site. A head that yields nothing of
        /// the site's kind cuts nothing, as it mines nothing in the game.
        /// </summary>
        public virtual double CutAdds(double siteAmount, double cometAmount, double sharePerCycle, double sizeMultiplier, double headYield)
        {
            double cap = Math.Max(0.0, sizeMultiplier) * Math.Max(0.0, siteAmount);
            double step = Math.Max(0.0, sharePerCycle) * Math.Max(0.0, headYield) * cap;
            double room = cap - Math.Max(0.0, cometAmount);
            // A comet within rounding of the cap is full: summing cycles never lands on it exactly.
            return room > 1e-9 * cap ? Math.Min(step, room) : 0.0;
        }

        /// <summary>
        /// A comet's weight in flight: what its contents weigh by the game's figures, less the share the
        /// size multiplier added, which weighs nothing (the game leaves mined cargo weightless on a
        /// rocket), times the mass scale. So the comet weighs the ice or ore cut from its site, scaled,
        /// the same on every planet size.
        /// </summary>
        public virtual double FlightKg(double contentsKg, double massScale, double sizeMultiplierInForce)
        {
            return Math.Max(0.0, contentsKg) * Math.Max(0.0, massScale) / Math.Max(1.0, sizeMultiplierInForce);
        }

        /// <summary>
        /// What the Fuel action takes from one of the rocket's fuel stores in <paramref name="seconds"/>:
        /// of the methane and oxygen it holds, only what pairs at the engines' two to one counts as fuel,
        /// and of that the share a pump moving <paramref name="litresPerSecond"/> through all
        /// <paramref name="storesLitres"/> of the rocket's stores moves, never more than all of it.
        /// What is not premix stays behind.
        /// </summary>
        public virtual OwnFuel PremixTransfer(double methane, double oxygen, double storesLitres, double litresPerSecond, double seconds)
        {
            if (!(storesLitres > 0.0))
            {
                return new OwnFuel(0.0, 0.0);
            }
            double share = Math.Min(1.0, Math.Max(0.0, litresPerSecond) * Math.Max(0.0, seconds) / storesLitres);
            double oxygenInPairs = Math.Min(Math.Max(0.0, methane) / MethanePerOxygen, Math.Max(0.0, oxygen));
            double oxygenTaken = share * oxygenInPairs;
            return new OwnFuel(MethanePerOxygen * oxygenTaken, oxygenTaken);
        }

        /// <summary>
        /// The premix one Burn cycle asks of a comet: a Cut cycle's share of the comet as it stands,
        /// times the drill head's ice yield. What it gets is <see cref="BurnOwnFuel"/> of that.
        /// </summary>
        public virtual double BurnWanted(double cometMoles, double sharePerCycle, double headYield)
        {
            return Math.Max(0.0, cometMoles) * Math.Max(0.0, sharePerCycle) * Math.Max(0.0, headYield);
        }

        /// <summary>
        /// A comet's gas after one Burn: its methane and oxygen fall by what <see cref="BurnOwnFuel"/>
        /// burns, every other gas stays, and the fuel it gains is exactly the moles that left.
        /// </summary>
        public virtual double[] AfterBurn(double[] moles, int methane, int oxygen, double wanted, out double fuelGained)
        {
            double[] after = (double[])moles.Clone();
            fuelGained = 0.0;
            if (methane < 0 || oxygen < 0 || methane >= moles.Length || oxygen >= moles.Length || methane == oxygen)
            {
                return after;
            }
            OwnFuel burnt = BurnOwnFuel(moles[methane], moles[oxygen], wanted);
            after[methane] = Math.Max(0.0, moles[methane] - burnt.Methane);
            after[oxygen] = Math.Max(0.0, moles[oxygen] - burnt.Oxygen);
            fuelGained = (moles[methane] - after[methane]) + (moles[oxygen] - after[oxygen]);
            return after;
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

        /// <summary>The most a site on any shipped map holds: Europa's map's biggest, D8 R3 S7, 24 mol a unit, before the game rounds it.</summary>
        public const double LargestShippedSiteMoles = 3048381.0;

        /// <summary>
        /// The disc in force on a planet of this size. The setting is stated at Standard size, where half
        /// the disc is the biggest rocket comet's safe distance; on a bigger planet K makes the biggest
        /// comet bigger, and the disc grows by twice what that adds to its safe distance (by the world's
        /// zones, <paramref name="zones"/>), so a landing tracked while the comet is in sight can still
        /// reach a safe distance. It never shrinks below the setting. Then the curvature, as
        /// <see cref="DiscRadius"/>.
        /// </summary>
        public virtual double DiscRadiusAt(double settingMetres, double curvature, double standardMultiplier, double planetSize, CelestialTuning zones)
        {
            CometRules wave = CometRules.Shipped;
            double inForce = SizeMultiplier(standardMultiplier, planetSize) * LargestShippedSiteMoles;
            double atStandard = SizeMultiplier(standardMultiplier, StandardPlanetSize) * LargestShippedSiteMoles;
            double grown = 2.0 * (wave.SafeDistance(inForce, zones) - wave.SafeDistance(atStandard, zones));
            return DiscRadius(Math.Max(0.0, settingMetres) + Math.Max(0.0, grown), curvature);
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

        /// <summary>
        /// The moles of fuel a deorbit burn spends to move where a comet lands by <paramref name="metres"/>:
        /// the Δv that is, at <paramref name="metresPerDeltaV"/> metres for each metre a second, burnt by the
        /// rocket equation from the comet's mass with all its fuel aboard, and never more than the engines can
        /// burn in <paramref name="windowSeconds"/> or than it has. The inverse of <see cref="ReachMetres"/>:
        /// steering the whole reach spends exactly what the reach was worked out from.
        /// </summary>
        public virtual double SteerFuelMoles(double cometKg, int engines, double fuelMoles, double metres, double metresPerDeltaV, double windowSeconds, double fuelKgPerMole)
        {
            if (engines <= 0 || !(fuelMoles > 0.0) || !(fuelKgPerMole > 0.0) || !(metres > 0.0) || !(metresPerDeltaV > 0.0))
            {
                return 0.0;
            }
            double dry = Math.Max(0.0, cometKg) + EngineMassKg * engines;
            double fuelKg = fuelMoles * fuelKgPerMole;
            double flow = engines * EngineMolesPerSecond * fuelKgPerMole;
            double exhaust = engines * EngineThrustNewtons / flow;
            double start = dry + fuelKg;
            double most = Math.Min(fuelKg, flow * Math.Max(0.0, windowSeconds));
            double burnt = start * (1.0 - Math.Exp(-(metres / metresPerDeltaV) / exhaust));
            return Math.Min(most, burnt) / fuelKgPerMole;
        }

        /// <summary>
        /// A comet brought down from orbit (docs/COMET-ROCKETS.md, "Bring it down"): where it lands and the fuel
        /// the burn spends. With a target, the sky point moves toward it by the reach or the whole gap
        /// (<see cref="Landing"/>), and the burn spends what that steer takes (<see cref="SteerFuelMoles"/>);
        /// with none, the burn is untracked: it lands at the sky point and spends nothing on steering.
        /// </summary>
        public virtual Descent Descend(GroundPoint sky, GroundPoint? target, double padX, double padZ, double radius,
            double cometKg, int engines, double fuelMoles, double metresPerDeltaV, double windowSeconds, double fuelKgPerMole)
        {
            double fuel = Math.Max(0.0, fuelMoles);
            double reach = ReachMetres(cometKg, engines, fuel, metresPerDeltaV, windowSeconds, fuelKgPerMole);
            if (target == null)
            {
                return new Descent(Landing(sky, sky, 0.0, padX, padZ, radius), reach, 0.0, 0.0, fuel);
            }
            GroundPoint aim = target.Value;
            double dx = aim.X - sky.X;
            double dz = aim.Z - sky.Z;
            double steered = Math.Min(Math.Sqrt(dx * dx + dz * dz), reach);
            double spent = SteerFuelMoles(cometKg, engines, fuel, steered, metresPerDeltaV, windowSeconds, fuelKgPerMole);
            return new Descent(Landing(sky, aim, reach, padX, padZ, radius), reach, steered, spent, Math.Max(0.0, fuel - spent));
        }

        /// <summary>The moles of each gas of an ice comet that reach the planet: <see cref="Arriving"/> of each.</summary>
        public virtual double[] ArrivingGas(double[] moles, double share)
        {
            double[] arriving = new double[moles.Length];
            for (int i = 0; i < moles.Length; i++)
            {
                arriving[i] = Arriving(moles[i], share);
            }
            return arriving;
        }

        /// <summary>
        /// The moles of each gas of the planet's air a mineral comet takes away: <see cref="MineralRemovedMoles"/>
        /// over the air's mean molar mass by the game's figures, taken from each gas in its share of the air, so
        /// the mix that is left is the mix that was there.
        /// </summary>
        public virtual double[] MineralRemoval(double[] airMoles, double[] gramsPerMole, double cometKg, double airKgPerKg)
        {
            double[] removed = new double[airMoles.Length];
            double moles = 0.0;
            double grams = 0.0;
            for (int i = 0; i < airMoles.Length; i++)
            {
                double held = Math.Max(0.0, airMoles[i]);
                moles += held;
                grams += held * gramsPerMole[i];
            }
            if (!(moles > 0.0))
            {
                return removed;
            }
            double total = MineralRemovedMoles(cometKg, airKgPerKg, moles, grams / moles);
            double share = Math.Min(1.0, total / moles);
            for (int i = 0; i < airMoles.Length; i++)
            {
                double held = Math.Max(0.0, airMoles[i]);
                removed[i] = share >= 1.0 ? held : held * share;
            }
            return removed;
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
        private const double LargestSiteMoles = LargestShippedSiteMoles;
        private const double LargestMarsOrMoonSiteMoles = 2785190.0;
        private const double ShippedCells = 5000000.0;           // 40 billion litres in 8,000 L cells
        private const double StandardCells = ShippedCells * StandardPlanetSize;
        private const double LeastBreathableNeedPerCell = 121.0; // the Moon, docs/BALANCE.md (Mars 125)
        private const double BasicHeadCycles = 1000.0;           // 100 quantity at a 0.1 chance of wear a cycle

        // The fuel rocket and the comet CometMassScale is derived from (docs/COMET-ROCKETS.md, stage 4).
        private const double HaulerDryKg = 1120.0;               // LU's tanker hull, by the game's part masses (engine mount 100, engine 500, doors 250, fuselage 250, battery 20)
        private const double HaulerLitres = 8120.0;              // its 2 large, 1 medium and 2 small gas capsule tanks (3,000, 1,500, 250 L) and lines
        internal const double RefuelKPa = 30000.0;               // RocketCommand.Refuel: the game's own full rocket, 30 MPa
        internal const double RefuelKelvin = 215.0;              // at 215 K
        private const double GasConstant = 8.3144;               // Chemistry.R
        private const double LandingReserveMoles = 6250.0;       // the 100 kg the tanker keeps back to land on
        private const double LostMoonSiteDeltaV = 2058.5;        // Mars Orbit to Lost Moon Fragments 1,858.5, and a site's 200
        private const double LargestMarsSiteUnits = 100000.0;    // a D8 R3 S7 site's TotalOreAtLocation, as the game rounds it
        private const double LargestMarsSiteDensity = 8.0;
        private const double SilanolIceGramsPerUnit = 22.0 * 166.0 + 2.0 * 36.0; // LostMoonFragments05: silanol 22, hydrochloric acid 2 a unit
        private const double IceMolesPerUnit = 24.0;             // every shipped ice site
        private const double WaterGramsPerMole = 108.0;          // Mole.MolarMass(Water)
        private const double TurboPumpLitresPerTick = 100.0;     // a Turbo Volume Pump at its Maximum

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

            // ---- bringing it down ----
            string landing = CheckLanding(rules);
            if (landing != null)
            {
                return landing;
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

            // ---- the rocket actions at a site ----
            string actions = CheckActions(rules);
            if (actions != null)
            {
                return actions;
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
            double safe = CometRules.Shipped.SafeDistance(tuning.CometSizeMultiplier * LargestSiteMoles, tuning);
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
                || tuning.FireballHeatFactor != 1.0
                || Math.Abs(tuning.DebrisRadiusFactor * tuning.GroundZeroMetresAt1e6 / WaveReach(wave, 1e6, CometRules.ThrownHardKPa) - 1.0) > 0.01)
            {
                return "the zones are not where the wave passes the strongest wall, as far as the fireball is drawn, and where the wave breaks ordinary windows";
            }
            if (tuning.MineralRemovalPerKg != tuning.GasArrivingShare)
            {
                return "a mineral comet does not take away what an ice comet of its weight brings";
            }
            if (Math.Abs(tuning.MineralKgPerUnit - IceMolesPerUnit * WaterGramsPerMole / 1000.0) > 1e-12)
            {
                return "a unit of ore does not weigh what a unit of water ice does, 24 mol at 108 g/mol";
            }
            if (Math.Abs(tuning.FuelLitresPerSecond - TurboPumpLitresPerTick / FuelTickSeconds) > 1e-9)
            {
                return "the Fuel action does not move what a Turbo Volume Pump moves, 100 litres a half-second tick";
            }
            string scale = CheckMassScale(tuning);
            if (scale != null)
            {
                return scale;
            }
            // The disc follows the planet's size where K makes the biggest comet bigger, and keeps its own
            // derivation there: half the disc is the biggest rocket comet's safe distance, rounded up. Where
            // K is no bigger than at Standard it stays the setting, so every landing stays as easy to see.
            foreach (double size in new[] { 0.01, StandardPlanetSize, 0.25, 1.0 })
            {
                double disc = rules.DiscRadiusAt(tuning.DiscRadiusMetres, ReferenceCurvature, tuning.CometSizeMultiplier, size, tuning);
                double biggest = rules.SizeMultiplier(tuning.CometSizeMultiplier, size) * LargestSiteMoles;
                double safeThere = CometRules.Shipped.SafeDistance(biggest, tuning);
                bool grows = rules.SizeMultiplier(tuning.CometSizeMultiplier, size) > rules.SizeMultiplier(tuning.CometSizeMultiplier, StandardPlanetSize);
                if (grows ? disc / 2.0 < safeThere || disc / 2.0 > safeThere + 100.0 : Math.Abs(disc - tuning.DiscRadiusMetres) > 1e-9)
                {
                    return string.Format(c, "at planet size {0} the disc is {1:0} m: half of it is not the biggest rocket comet's safe distance there ({2:0} m) rounded up, or it is not the setting where K is no bigger than at Standard",
                        size, disc, safeThere);
                }
            }
            if (!(rules.DiscRadiusAt(tuning.DiscRadiusMetres, ReferenceCurvature, tuning.CometSizeMultiplier, 0.25, tuning) > tuning.DiscRadiusMetres + 300.0))
            {
                return "the disc does not grow on a Long planet, where K makes the biggest comet's safe distance pass half the setting";
            }
            if (Math.Abs(rules.DiscRadiusAt(tuning.DiscRadiusMetres, 0.45, tuning.CometSizeMultiplier, 0.25, tuning)
                    - rules.DiscRadiusAt(tuning.DiscRadiusMetres, ReferenceCurvature, tuning.CometSizeMultiplier, 0.25, tuning) * ReferenceCurvature / 0.45) > 1e-9)
            {
                return "the disc that follows the planet's size does not then shrink with the world's curvature";
            }
            return null;
        }

        /// <summary>
        /// A comet brought down from orbit, on cases with known answers: the fuel a steer spends is the
        /// rocket equation's for its Δv and the inverse of the reach; a target in reach is landed on, one out
        /// of it is fallen short of along the line, an untracked burn lands at the sky point and spends
        /// nothing; what an ice comet brings and what a mineral comet takes from the air.
        /// </summary>
        private static string CheckLanding(CelestialRules rules)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            const double kg = TestFuelKgPerMole;
            const double R = 5100.0;
            const double comet = 18000.0;
            const int engines = 2;
            const double plenty = 20000.0;
            const double perDv = 25.0;
            const double window = 50.0;
            double exhaust = EngineThrustNewtons / (EngineMolesPerSecond * kg);
            double start = comet + EngineMassKg * engines + plenty * kg;
            double windowFuel = engines * EngineMolesPerSecond * window;

            // The steer's fuel: the whole reach spends what the reach came from; half of it, the rocket
            // equation's for half the Δv, a little more than half the fuel, since the last fuel burnt pushes
            // the least mass.
            double reach = rules.ReachMetres(comet, engines, plenty, perDv, window, kg);
            double whole = rules.SteerFuelMoles(comet, engines, plenty, reach, perDv, window, kg);
            double half = rules.SteerFuelMoles(comet, engines, plenty, reach / 2.0, perDv, window, kg);
            double halfBack = perDv * exhaust * Math.Log(start / (start - half * kg));
            if (Math.Abs(whole - windowFuel) > 1e-6 * windowFuel || Math.Abs(halfBack - reach / 2.0) > 1e-9 * reach
                || !(half > windowFuel / 2.0) || !(half < 0.51 * windowFuel))
            {
                return string.Format(c, "steering an 18 t comet with two engines its whole reach of {0:0.#} m spends {1:0.###} mol, expected the {2:0} mol its engines burn in the fall, or half the reach spends {3:0.###} mol, which is not the rocket equation's for half the delta-v",
                    reach, whole, windowFuel, half);
            }
            double little = rules.ReachMetres(comet, engines, 360.0, perDv, window, kg);
            if (Math.Abs(rules.SteerFuelMoles(comet, engines, 360.0, little, perDv, window, kg) - 360.0) > 1e-6
                || rules.SteerFuelMoles(comet, engines, 360.0, 0.0, perDv, window, kg) != 0.0 || rules.SteerFuelMoles(comet, 0, 360.0, 100.0, perDv, window, kg) != 0.0
                || rules.SteerFuelMoles(comet, engines, 360.0, 1e9, perDv, window, kg) > 360.0)
            {
                return "a reserve smaller than the engines burn in the fall is not all spent steering its whole reach, or a steer of nothing, or with no engines, spends fuel, or a steer spends more than there is";
            }

            // Where it lands: on a target in reach, short of one out of it along the line, at the sky point untracked.
            GroundPoint sky = rules.SkyPoint(0.0, 0.0, 1.0, 0.0, 0.0, R);
            GroundPoint near = new GroundPoint(sky.X + 0.6 * reach * 0.8, sky.Z + 0.6 * reach * 0.6);
            GroundPoint far = new GroundPoint(sky.X, sky.Z + 10000.0);
            Descent onTarget = rules.Descend(sky, near, 0.0, 0.0, R, comet, engines, plenty, perDv, window, kg);
            Descent shortOf = rules.Descend(sky, far, 0.0, 0.0, R, comet, engines, plenty, perDv, window, kg);
            Descent untracked = rules.Descend(sky, null, 0.0, 0.0, R, comet, engines, plenty, perDv, window, kg);
            Descent engineless = rules.Descend(sky, near, 0.0, 0.0, R, comet, 0, plenty, perDv, window, kg);
            double nearSpent = rules.SteerFuelMoles(comet, engines, plenty, 0.6 * reach, perDv, window, kg);
            if (Math.Abs(onTarget.Land.X - near.X) > 1e-9 || Math.Abs(onTarget.Land.Z - near.Z) > 1e-9 || Math.Abs(onTarget.Steered - 0.6 * reach) > 1e-9
                || Math.Abs(onTarget.FuelSpent - nearSpent) > 1e-9 || Math.Abs(onTarget.FuelLeft - (plenty - nearSpent)) > 1e-9 || Math.Abs(onTarget.Reach - reach) > 1e-9)
            {
                return string.Format(c, "a target {0:0.#} m from the sky point, in reach, is not landed on with the fuel that steer takes: lands ({1:0.###}, {2:0.###}), steered {3:0.###} m on {4:0.###} mol",
                    0.6 * reach, onTarget.Land.X, onTarget.Land.Z, onTarget.Steered, onTarget.FuelSpent);
            }
            if (Math.Abs(shortOf.Land.X - sky.X) > 1e-9 || Math.Abs(shortOf.Land.Z - (sky.Z + reach)) > 1e-9 || Math.Abs(shortOf.Steered - reach) > 1e-9
                || Math.Abs(shortOf.FuelSpent - windowFuel) > 1e-6 * windowFuel)
            {
                return "a target out of reach is not fallen short of by the reach along the line from the sky point, spending all the engines burn in the fall";
            }
            if (untracked.Land.X != sky.X || untracked.Land.Z != sky.Z || untracked.Steered != 0.0 || untracked.FuelSpent != 0.0 || untracked.FuelLeft != plenty
                || Math.Abs(untracked.Reach - reach) > 1e-9 || engineless.Land.X != sky.X || engineless.Land.Z != sky.Z || engineless.FuelSpent != 0.0)
            {
                return "an untracked burn, or a comet with no engines, does not land at the sky point with all its fuel";
            }
            Descent edge = rules.Descend(new GroundPoint(5000.0, 0.0), new GroundPoint(9000.0, 0.0), 0.0, 0.0, R, comet, engines, plenty, perDv, window, kg);
            if (Math.Abs(edge.Land.X - R) > 1e-9 || Math.Abs(edge.Land.Z) > 1e-9 || Math.Abs(edge.Steered - reach) > 1e-9)
            {
                return "a steer toward a target past the disc does not stop at the disc's edge, having burnt for its whole reach";
            }

            // What reaches the planet.
            double[] gas = { 1000.0, 500.0, 0.0 };
            double[] arriving = rules.ArrivingGas(gas, 0.7);
            double[] all = rules.ArrivingGas(gas, 1.2);
            if (arriving.Length != 3 || Math.Abs(arriving[0] - 700.0) > 1e-9 || Math.Abs(arriving[1] - 350.0) > 1e-9 || arriving[2] != 0.0
                || all[0] != 1000.0 || all[1] != 500.0 || gas[0] != 1000.0)
            {
                return "an ice comet's gas does not arrive at its share, gas by gas";
            }
            // Venus's air by the game's molar masses: carbon dioxide 44, hydrochloric acid 36, nitrogen 64 g/mol.
            double[] air = { 1e9, 4.5e8, 1.075e8 };
            double[] grams = { 44.0, 36.0, 64.0 };
            double[] taken = rules.MineralRemoval(air, grams, 1e6, 0.7);
            double airMoles = air[0] + air[1] + air[2];
            double mean = (air[0] * grams[0] + air[1] * grams[1] + air[2] * grams[2]) / airMoles;
            double takenMoles = taken[0] + taken[1] + taken[2];
            double takenKg = (taken[0] * grams[0] + taken[1] * grams[1] + taken[2] * grams[2]) / 1000.0;
            bool proportional = Math.Abs(taken[1] / air[1] - taken[0] / air[0]) <= 1e-12 * taken[0] / air[0] && Math.Abs(taken[2] / air[2] - taken[0] / air[0]) <= 1e-12 * taken[0] / air[0];
            if (Math.Abs(takenMoles - rules.MineralRemovedMoles(1e6, 0.7, airMoles, mean)) > 1e-9 * takenMoles || Math.Abs(takenKg - 7e5) > 1e-6 * 7e5 || !proportional)
            {
                return string.Format(c, "a 1,000 t mineral comet on Venus takes {0:N3} mol weighing {1:N3} kg, expected 700,000 kg of the air in its own proportions", takenMoles, takenKg);
            }
            double[] everything = rules.MineralRemoval(air, grams, 1e15, 0.7);
            double[] none = rules.MineralRemoval(new[] { 0.0, 0.0 }, new[] { 44.0, 64.0 }, 1e6, 0.7);
            if (everything[0] != air[0] || everything[1] != air[1] || everything[2] != air[2] || none[0] != 0.0 || none[1] != 0.0
                || rules.MineralRemoval(air, grams, 0.0, 0.7)[0] != 0.0)
            {
                return "a mineral comet bigger than the air does not take all of it, gas by gas, or one takes air that is not there, or a massless one takes any";
            }
            return null;
        }

        /// <summary>
        /// Cut, Fuel and Burn worked through on cases with known answers, and a comet's weight in flight.
        /// </summary>
        private static string CheckActions(CelestialRules rules)
        {
            CultureInfo c = CultureInfo.InvariantCulture;

            // Cut: a cycle's share of K times the site, by the head's yield, never past K times the site.
            if (Math.Abs(rules.CutAdds(2.4e6, 0.0, 0.001, 5.0, 1.0) - 12000.0) > 1e-6 || Math.Abs(rules.CutAdds(2.4e6, 0.0, 0.001, 5.0, 1.2) - 14400.0) > 1e-6
                || rules.CutAdds(2.4e6, 0.0, 0.001, 5.0, 0.0) != 0.0 || Math.Abs(rules.CutAdds(2.4e6, 1.2e7 - 5000.0, 0.001, 5.0, 1.0) - 5000.0) > 1e-6
                || rules.CutAdds(2.4e6, 1.2e7, 0.001, 5.0, 1.0) != 0.0 || rules.CutAdds(2.4e6, 2e7, 0.001, 5.0, 1.0) != 0.0)
            {
                return "a Cut cycle does not add its share of K times the site by the head's yield, stopping at K times the site";
            }
            foreach (var (yield, whole) in new[] { (1.0, 1000), (1.2, 834), (0.5, 2000) })
            {
                double comet = 0.0;
                int cycles = 0;
                while (cycles < 5000)
                {
                    double added = rules.CutAdds(2.4e6, comet, 0.001, 5.0, yield);
                    if (!(added > 0.0))
                    {
                        break;
                    }
                    comet += added;
                    cycles++;
                    if (cycles == 500 && Math.Abs(comet - rules.CometMoles(2.4e6, 500.0 * yield, 0.001, 5.0)) > 1e-9 * comet)
                    {
                        return string.Format(c, "500 Cut cycles with a head of yield {0} give {1:R} mol, where the comet's whole rule gives {2:R}", yield, comet, rules.CometMoles(2.4e6, 500.0 * yield, 0.001, 5.0));
                    }
                }
                if (cycles != whole || Math.Abs(comet - 1.2e7) > 1e-3)
                {
                    return string.Format(c, "a whole comet takes {0} Cut cycles with a head of yield {1} and holds {2:R} mol, expected {3} cycles and 12,000,000", cycles, yield, comet, whole);
                }
            }

            // A comet weighs what was cut from its site, scaled; K's extra gas weighs nothing.
            if (Math.Abs(rules.FlightKg(1000.0, 0.39, 1.0) - 390.0) > 1e-9 || Math.Abs(rules.FlightKg(25000.0, 0.39, 25.0) - rules.FlightKg(5000.0, 0.39, 5.0)) > 1e-9
                || Math.Abs(rules.FlightKg(5000.0, 0.39, 5.0) - 390.0) > 1e-9 || Math.Abs(rules.FlightKg(1000.0, 0.39, 0.5) - 390.0) > 1e-9 || rules.FlightKg(1000.0, 0.0, 1.0) != 0.0)
            {
                return "a comet does not weigh what was cut from its site times the mass scale, the same on every planet size";
            }

            // What a route takes: carried exactly, the comet arrives on it; a thousandth less and it does not.
            double needed = rules.FuelNeeded(18000.0, 2, 583.3, TestFuelKgPerMole);
            Flight onNeeded = rules.Fly(18000.0, 2, needed * (1.0 + 1e-9), 583.3, TestFuelKgPerMole);
            if (!onNeeded.Arrives || Math.Abs(onNeeded.FuelMoles - needed) > 1e-6 * needed || rules.Fly(18000.0, 2, 0.999 * needed, 583.3, TestFuelKgPerMole).Arrives
                || rules.FuelNeeded(18000.0, 0, 583.3, TestFuelKgPerMole) != 0.0)
            {
                return string.Format(c, "the fuel an 18 t comet with two engines needs for 583.3 delta-v, {0:N1} mol, is not exactly what the rocket rule burns arriving", needed);
            }

            // Fuel: only what pairs two to one moves, the pump's share of all the stores' volume a second.
            OwnFuel even = rules.PremixTransfer(2000.0, 1000.0, 1500.0, 200.0, 0.5);
            OwnFuel rich = rules.PremixTransfer(3000.0, 1000.0, 1500.0, 200.0, 0.5);
            OwnFuel lean = rules.PremixTransfer(1000.0, 5000.0, 1500.0, 200.0, 0.5);
            OwnFuel all = rules.PremixTransfer(2000.0, 1000.0, 1500.0, 1e6, 0.5);
            if (Math.Abs(even.Methane - 2000.0 / 15.0) > 1e-9 || Math.Abs(even.Oxygen - 1000.0 / 15.0) > 1e-9
                || Math.Abs(rich.Methane - even.Methane) > 1e-9 || Math.Abs(rich.Oxygen - even.Oxygen) > 1e-9
                || Math.Abs(lean.Methane - 1000.0 / 15.0) > 1e-9 || Math.Abs(lean.Oxygen - 500.0 / 15.0) > 1e-9
                || Math.Abs(all.Methane - 2000.0) > 1e-9 || Math.Abs(all.Oxygen - 1000.0) > 1e-9)
            {
                return "the Fuel action does not move the pump's share of the stores' two to one premix, leaving what is not premix behind";
            }
            if (rules.PremixTransfer(2000.0, 0.0, 1500.0, 200.0, 0.5).Methane != 0.0 || rules.PremixTransfer(2000.0, 1000.0, 0.0, 200.0, 0.5).Oxygen != 0.0
                || Math.Abs(rules.PremixTransfer(2000.0, 1000.0, 3000.0, 200.0, 0.5).Oxygen - 1000.0 / 30.0) > 1e-9)
            {
                return "the Fuel action moves methane with no oxygen, moves from no stores, or moves as much from twice the stores' volume";
            }

            // Burn: a Cut cycle's share of the comet, two of methane to one of oxygen, fuel exactly what left.
            if (Math.Abs(rules.BurnWanted(1e6, 0.001, 1.2) - 1200.0) > 1e-9 || rules.BurnWanted(1e6, 0.001, 0.0) != 0.0)
            {
                return "a Burn cycle does not ask a Cut cycle's share of the comet by the head's ice yield";
            }
            double[] before = { 1000.0, 1000.0, 500.0 };
            double[] plenty = rules.AfterBurn(before, 0, 1, 900.0, out double gained);
            double[] scarce = rules.AfterBurn(new[] { 1000.0, 100.0, 500.0 }, 0, 1, 900.0, out double scarceGained);
            double[] none = rules.AfterBurn(before, -1, 1, 900.0, out double noneGained);
            if (Math.Abs(plenty[0] - 400.0) > 1e-9 || Math.Abs(plenty[1] - 700.0) > 1e-9 || plenty[2] != 500.0 || Math.Abs(gained - 900.0) > 1e-9
                || Math.Abs(plenty[0] + plenty[1] + plenty[2] + gained - 2500.0) > 1e-9
                || Math.Abs(scarce[0] - 800.0) > 1e-9 || Math.Abs(scarce[1]) > 1e-9 || Math.Abs(scarceGained - 300.0) > 1e-9
                || noneGained != 0.0 || none[0] != 1000.0 || before[0] != 1000.0)
            {
                return "a Burn does not take its fuel two to one from the comet's own methane and oxygen, leave every other gas, and credit exactly the moles that left";
            }
            return null;
        }

        /// <summary>
        /// CometMassScale held to its derivation: the largest hundredth at which the heaviest comet a Mars
        /// map can make, from its farthest site, takes no more time hauling its fuel than cutting it took.
        /// </summary>
        private static string CheckMassScale(CelestialTuning tuning)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            HaulerTrip(LostMoonSiteDeltaV, tuning.FuelLitresPerSecond, out double handOver, out double tripSeconds);
            if (Math.Abs(handOver - 100302.0) > 100.0 || Math.Abs(tripSeconds - 994.0) > 2.0)
            {
                return string.Format(c, "a fuel rocket filled the game's way hands over {0:N0} mol at a Lost Moon site in a {1:N0} s trip, where the derivation took 100,302 mol in 994 s", handOver, tripSeconds);
            }
            double cutSeconds = CycleSeconds(LargestMarsSiteDensity, 1.0) / tuning.CutSharePerCycle;
            double exhaust = EngineThrustNewtons / (EngineMolesPerSecond * TestFuelKgPerMole);
            double Hauling(double scale)
            {
                double dry = scale * LargestMarsSiteUnits * SilanolIceGramsPerUnit / 1000.0 + 2.0 * EngineMassKg;
                double fuel = dry * (Math.Exp(LostMoonSiteDeltaV / exhaust) - 1.0) / TestFuelKgPerMole;
                return fuel / handOver * tripSeconds;
            }
            if (Hauling(tuning.CometMassScale) > cutSeconds || Hauling(tuning.CometMassScale + 0.01) <= cutSeconds)
            {
                return string.Format(c, "at a comet weight of {0} the heaviest Mars comet takes {1:N0} s hauling its fuel against {2:N0} s cutting it, and the next hundredth up {3:N0} s: the default is not the largest that keeps hauling within cutting",
                    tuning.CometMassScale, Hauling(tuning.CometMassScale), cutSeconds, Hauling(tuning.CometMassScale + 0.01));
            }
            return null;
        }

        /// <summary>
        /// One trip of the fuel rocket the mass scale is derived from: filled as the game fills a rocket,
        /// up from Mars at full throttle against its gravity, out to a site by the rocket rule, the pump
        /// emptying what it can spare into the comet, back to Mars Orbit with the landing reserve aboard,
        /// and down from the game's re-entry height. Seconds exclude refilling on the ground.
        /// </summary>
        private static void HaulerTrip(double routeDeltaV, double litresPerSecond, out double handOver, out double seconds)
        {
            const double step = 0.02;
            double kg = TestFuelKgPerMole;
            double flowKg = EngineMolesPerSecond * kg;
            double exhaust = EngineThrustNewtons / flowKg;
            double full = HaulerLitres * RefuelKPa / (GasConstant * RefuelKelvin);
            double mass = HaulerDryKg + full * kg;
            double gained = 0.0;
            double climb = 0.0;
            while (gained < EntrySpeed && climb < 3600.0)
            {
                gained += (EngineThrustNewtons / mass - MarsGravity) * step;
                mass -= flowKg * step;
                climb += step;
            }
            double orbit = full - EngineMolesPerSecond * climb;
            double outbound = (HaulerDryKg + orbit * kg) * (1.0 - Math.Exp(-routeDeltaV / exhaust)) / kg;
            double atSite = orbit - outbound;
            double landingKg = HaulerDryKg + LandingReserveMoles * kg;
            double back = landingKg * (Math.Exp(routeDeltaV / exhaust) - 1.0) / kg;
            handOver = atSite - back - LandingReserveMoles;
            double transfer = HaulerLitres / litresPerSecond * Math.Log(atSite / (atSite - handOver));
            seconds = climb + outbound / EngineMolesPerSecond + transfer + back / EngineMolesPerSecond + LandingSeconds(landingKg);
        }

        /// <summary>Seconds from re-entry to the ground: falling freely, then full throttle timed to stop at the ground.</summary>
        private static double LandingSeconds(double massKg)
        {
            double low = 0.0;
            double high = ReEntryMetres;
            for (int i = 0; i < 60; i++)
            {
                double middle = 0.5 * (low + high);
                if (Descent(massKg, middle, out _) > 0.0)
                {
                    high = middle;
                }
                else
                {
                    low = middle;
                }
            }
            Descent(massKg, high, out double seconds);
            return seconds;
        }

        /// <summary>A fall from re-entry with the engine lit below <paramref name="burnFrom"/> metres; the height it stops at, 0 or less if it reaches the ground still falling.</summary>
        private static double Descent(double massKg, double burnFrom, out double seconds)
        {
            const double step = 0.02;
            double height = ReEntryMetres;
            double speed = -EntrySpeed;
            double mass = massKg;
            bool burning = false;
            seconds = 0.0;
            while (height > 0.0 && seconds < 3600.0)
            {
                burning |= height <= burnFrom;
                speed += (-MarsGravity + (burning ? EngineThrustNewtons / mass : 0.0)) * step;
                height += speed * step;
                seconds += step;
                if (burning)
                {
                    mass -= EngineMolesPerSecond * TestFuelKgPerMole * step;
                    if (speed >= 0.0)
                    {
                        return height;
                    }
                }
            }
            return height;
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

    /// <summary>A comet brought down, by <see cref="CelestialRules.Descend"/>. A plain struct: net472 has no IsExternalInit.</summary>
    public readonly struct Descent
    {
        /// <summary>Where it lands.</summary>
        public readonly GroundPoint Land;

        /// <summary>The most its fuel could have moved the landing, in metres.</summary>
        public readonly double Reach;

        /// <summary>How far the burn moved it toward the target, in metres: the reach or the whole gap, 0 untracked.</summary>
        public readonly double Steered;

        /// <summary>Moles of fuel the steering burnt.</summary>
        public readonly double FuelSpent;

        /// <summary>Moles of fuel still aboard as it falls.</summary>
        public readonly double FuelLeft;

        public Descent(GroundPoint land, double reach, double steered, double fuelSpent, double fuelLeft)
        {
            Land = land;
            Reach = reach;
            Steered = steered;
            FuelSpent = fuelSpent;
            FuelLeft = fuelLeft;
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
