using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;

namespace TerraformingReloaded.Patching
{
    /// <summary>
    /// The planet fire's own check (docs/PLANET-COMBUSTION.md, Appendix H, cases 1 to 20), run on
    /// the game's own types at load before the rule is installed, and by tools/PatchCheck, which also
    /// hands it rules with one part broken and needs every one of them to fail.
    ///
    /// Every expected figure is worked out here independently of the rule: one cell's worth by its
    /// own scaling, the game's rate by the formula written out, the products and the heat from the
    /// combustion table and enthalpies in the specification. A check that asked the rule for its own
    /// answer would prove nothing.
    /// </summary>
    public static class PlanetCombustionCheck
    {
        /// <summary>The planet the specification measures with: 250,000 outdoor cells.</summary>
        private const double Litres = 2.0e9;

        private const double Cells = Litres / 8000.0;

        private const double MethaneKiloJoules = 286.0;
        private const double HydrogenKiloJoules = 306.0;

        private sealed class CheckFailed : Exception
        {
            public CheckFailed(string message) : base(message)
            {
            }
        }

        /// <summary>Runs every case on <paramref name="rule"/> and the shipped trace hold. Returns the first problem, or null.</summary>
        public static string CheckArithmetic(FireRule rule) => CheckArithmetic(rule, TraceHoldRule.Shipped);

        /// <summary>Runs every case on <paramref name="rule"/> and <paramref name="traces"/>. Returns the first problem, or null.</summary>
        public static string CheckArithmetic(FireRule rule, TraceHoldRule traces)
        {
            try
            {
                GateIsTheGames(rule);
                RateIsTheGames(rule);
                VulcanAndOxygen(rule);
                TwoOxidisers(rule);
                FuelScarce(rule);
                HoldBackConserves(rule);
                Flapping(rule);
                Spark(rule);
                SelfSustain(rule);
                HeatBooking(rule);
                NoCap(rule);
                Hydrazine(rule);
                NitrousOxideOffset(rule);
                LiquidsUntouched(rule);
                SelfTestUnheld(rule);
                Gathering(rule, traces);
                ArmedHoldBack(rule);
                HoldSettings(rule);
                TraceHold(traces);
                SelfTestCountsWhatWasHanded(rule, traces);
                return null;
            }
            catch (CheckFailed e)
            {
                return e.Message;
            }
            catch (Exception e)
            {
                return "it threw: " + (e.InnerException ?? e).Message;
            }
        }

        /// <summary>The temperature above which pure hydrazine lights itself in this game build, for tools/PatchCheck to print.</summary>
        public static double HydrazineIgnitionKelvin => Chemistry.AutoIgnitionHydrazine.ToDouble();

        // ---- the cases -------------------------------------------------------------------------------

        /// <summary>1. Lit by heat exactly when the game's IsAutoIgnition says one cell's worth lights at the peak.</summary>
        private static void GateIsTheGames(FireRule rule)
        {
            Func<GlobalGasMix>[] planets =
            {
                () => Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0)),
                () => Plus(Vulcan(), (Chemistry.GasType.NitrousOxide, 26.0)),
                () => Plus(Vulcan(), (Chemistry.GasType.NitrousOxide, 300000.0)),
                () => Plus(Europa(), (Chemistry.GasType.Methane, 300000.0)),
                () => Plus(Mars(), (Chemistry.GasType.Hydrazine, 100.0)),
                () => Plus(Mars(), (Chemistry.GasType.Hydrazine, 300000.0)),
            };
            foreach (Func<GlobalGasMix> planet in planets)
            {
                foreach (double peak in new[] { 250.0, 330.0, 500.0, 560.0, 580.0, 975.0 })
                {
                    GlobalGasMix tank = planet();
                    bool game = OneCell(tank, peak).IsAutoIgnition();
                    FireTick tick = Step(rule, tank, peak, sparked: false);
                    Need(IsBurning(tick, FireCause.Heat) == game, string.Format(CultureInfo.InvariantCulture,
                        "at a peak of {0} K the planet's lit-by-heat answer was {1}, where the game's own test on one cell's worth says {2}",
                        peak, !game, game));
                }
            }
        }

        /// <summary>2. The share a tick is the game's curve at the peak, its second branch with nitrous oxide, and all of it under 0.0003 mol a cell.</summary>
        private static void RateIsTheGames(FireRule rule)
        {
            double[] peaks = { 975.0, 1166.0, 1725.0 };
            double[] stated = { 0.0563, 0.0468, 0.0318 };
            for (int i = 0; i < peaks.Length; i++)
            {
                Burning burning = Burns(Step(rule, Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0)), peaks[i], sparked: false), "Vulcan with 5,000 mol of oxygen");
                Near(burning.Rate, stated[i], 1e-4, "the share a tick at a peak of " + peaks[i] + " K");
                Near(burning.Rate, CurvedShare(peaks[i], nitrous: false), 1e-12, "the share a tick against the game's formula at " + peaks[i] + " K");
            }
            Burning both = Burns(Step(rule, Plus(Vulcan(), (Chemistry.GasType.Oxygen, 1000.0), (Chemistry.GasType.NitrousOxide, 1000.0)), 1000.0, sparked: false), "Vulcan with two oxidisers");
            Near(both.Rate, CurvedShare(1000.0, nitrous: true), 1e-12, "the share a tick with nitrous oxide over a tenth of the oxidiser");
            Burning last = Burns(Step(rule, Plus(Vulcan(), (Chemistry.GasType.Oxygen, 50.0)), 1000.0, sparked: false), "Vulcan with 50 mol of oxygen");
            Need(last.Rate == 1.0, "under 0.0003 mol of oxidiser a cell the share a tick is " + last.Rate.ToString("R", CultureInfo.InvariantCulture) + ", not all of it");
        }

        /// <summary>3. 5,000 mol of oxygen on Vulcan burns at the game's share each tick to the products of Appendix C, and its last 75 mol go at once.</summary>
        private static void VulcanAndOxygen(FireRule rule)
        {
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0));
            Dictionary<Chemistry.GasType, double> start = Totals(tank);
            bool sawLast = false;
            RunToTheEnd(rule, tank, 1000.0, (before, burning) =>
            {
                double oxygen = tank.Get(Chemistry.GasType.Oxygen).ToDouble();
                double expected = before[Chemistry.GasType.Oxygen] < 0.0003 * Cells ? 0.0 : before[Chemistry.GasType.Oxygen] * (1.0 - burning.Rate);
                Need(Math.Abs(oxygen - expected) <= 1e-9 * before[Chemistry.GasType.Oxygen] + 1e-9,
                    "a tick left " + oxygen.ToString("R", CultureInfo.InvariantCulture) + " mol of oxygen where the game's share leaves " + expected.ToString("R", CultureInfo.InvariantCulture));
                Need(Holds(rule.HeldBack(burning), Chemistry.GasType.Oxygen, Chemistry.GasType.NitrousOxide, Chemistry.GasType.Ozone)
                    && !rule.HeldBack(burning).Contains(Chemistry.GasType.Methane) && !rule.HeldBack(burning).Contains(Chemistry.GasType.Hydrogen),
                    "while oxygen burns on Vulcan, the oxidisers are not all held back, or a fuel is");
                sawLast |= before[Chemistry.GasType.Oxygen] < 75.0;
            });
            Need(sawLast, "the oxygen never went at once under 75 mol");
            Changed(start, Totals(tank), "5,000 mol of oxygen burnt on Vulcan",
                (Chemistry.GasType.Oxygen, -5000.0), (Chemistry.GasType.Methane, -9000.0), (Chemistry.GasType.Hydrogen, -1000.0),
                (Chemistry.GasType.Pollutant, 13500.0), (Chemistry.GasType.CarbonDioxide, 27000.0), (Chemistry.GasType.Steam, 1500.0));
        }

        /// <summary>4. Oxygen beside nitrous oxide: both held back, the fuels shared, the products and the heat of Appendix C.</summary>
        private static void TwoOxidisers(FireRule rule)
        {
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 1000.0), (Chemistry.GasType.NitrousOxide, 1000.0));
            Dictionary<Chemistry.GasType, double> start = Totals(tank);
            double energy = 0.0;
            RunToTheEnd(rule, tank, 1000.0, (before, burning) =>
            {
                HeldGases held = rule.HeldBack(burning);
                Need(Holds(held, Chemistry.GasType.Oxygen, Chemistry.GasType.NitrousOxide), "with two oxidisers, both are not held back");
                Need(!held.Contains(Chemistry.GasType.Methane) && !held.Contains(Chemistry.GasType.Hydrogen), "with two oxidisers, a fuel is held back");
                energy += burning.Burnt.Energy;
            });
            Changed(start, Totals(tank), "1,000 mol of oxygen and 1,000 of nitrous oxide burnt on Vulcan",
                (Chemistry.GasType.Oxygen, -1000.0), (Chemistry.GasType.NitrousOxide, -1000.0), (Chemistry.GasType.Methane, -2700.0),
                (Chemistry.GasType.Hydrogen, -300.0), (Chemistry.GasType.Pollutant, 2700.0), (Chemistry.GasType.CarbonDioxide, 7200.0),
                (Chemistry.GasType.Steam, 400.0), (Chemistry.GasType.Nitrogen, 1900.0));
            double stated = 1e3 * (MethaneKiloJoules * 1800.0 + HydrogenKiloJoules * 200.0 + 2.0 * MethaneKiloJoules * 900.0 + 2.0 * HydrogenKiloJoules * 100.0);
            Near(energy, stated, 1e-6 * stated, "the heat of 1,000 mol of oxygen and 1,000 of nitrous oxide burnt on Vulcan");
        }

        /// <summary>5. Methane on Europa's oxygen: the fuel is held back, the oxygen shared; lit at 580 K, not at 560 K.</summary>
        private static void FuelScarce(FireRule rule)
        {
            Burning burning = Burns(Step(rule, Plus(Europa(), (Chemistry.GasType.Methane, 300000.0)), 580.0, sparked: false), "Europa with 1.2 mol of methane a cell at 580 K");
            Need(burning.Cause == FireCause.Heat, "Europa with methane at 580 K was not lit by heat");
            HeldGases held = rule.HeldBack(burning);
            Need(held.Contains(Chemistry.GasType.Methane) && !held.Contains(Chemistry.GasType.Oxygen), "with the fuel scarce, methane is not held back, or oxygen is");
            Need(Step(rule, Plus(Europa(), (Chemistry.GasType.Methane, 300000.0)), 560.0, sparked: false) is Unlit, "Europa with methane lit at 560 K");
        }

        /// <summary>6. While burning, every copy the game makes of the planet's air carries no held gas and the tank loses exactly the copy; while not, every copy carries it in proportion.</summary>
        private static void HoldBackConserves(FireRule rule)
        {
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0));
            HeldGases held = rule.HeldBack(Burns(Step(rule, tank, 1000.0, sparked: false), "Vulcan with oxygen"));
            double share = 8000.0 / Litres;
            foreach (string way in new[] { "the exchange take", "a new outdoor cell" })
            {
                GasMixture copy = Withheld(tank, held, share);
                double before = tank.TotalQuantity().ToDouble();
                tank.Remove(copy);
                Need(copy.Oxygen.Quantity.ToDouble() == 0.0, way + " carried oxygen while the planet burns");
                Near(before - tank.TotalQuantity().ToDouble(), copy.GetTotalMolesGassesAndLiquids.ToDouble(), 1e-6, way + ": the planet lost other than what was taken");
            }
            GasMixture mixing = Withheld(tank, held, 1.0).Remove(new MoleQuantity(5.0), AtmosphereHelper.MatterState.Gas);
            double held0 = tank.TotalQuantity().ToDouble();
            tank.Remove(mixing);
            Need(mixing.Oxygen.Quantity.ToDouble() == 0.0, "the mixing take carried oxygen while the planet burns");
            Near(held0 - tank.TotalQuantity().ToDouble(), 5.0, 1e-6, "the mixing take: the planet lost other than what was taken");
            Need(Withheld(tank, held, share).Oxygen.Quantity.ToDouble() == 0.0, "the read-only copy shows oxygen while the planet burns");
            GasMixture reservoir = Withheld(tank, held, share, Vulcan());
            Need(reservoir.Oxygen.Quantity.ToDouble() > 0.0 || tank.Get(Chemistry.GasType.Oxygen).ToDouble() == 0.0,
                "a copy of a mix that is not the planet's own tank was held back");

            GlobalGasMix unlit = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0));
            FireTick tick = Step(rule, unlit, 500.0, sparked: false);
            Need(tick is Unlit, "Vulcan with oxygen at a peak of 500 K is lit");
            HeldGases none = rule.HeldBack(tick);
            double expected = unlit.Get(Chemistry.GasType.Oxygen).ToDouble() * share;
            Near(Withheld(unlit, none, share).Oxygen.Quantity.ToDouble(), expected, 1e-12, "a copy of an unlit planet does not carry its share of oxygen");
            Need(none.IsEmpty, "something is held back while the planet is not burning");
        }

        /// <summary>7. Lit by heat at 573.2 K, still lit at 572.0 and 569.0 K within the 5 K margin, out at 567.0 K.</summary>
        private static void Flapping(FireRule rule)
        {
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 1.0e6));
            FireTick previous = NothingToBurn.Instance;
            foreach ((double peak, bool lit) in new[] { (573.2, true), (572.0, true), (569.0, true), (567.0, false) })
            {
                previous = Step(rule, tank, peak, sparked: false, previous: previous);
                Need(IsBurning(previous, FireCause.Heat) == lit, string.Format(CultureInfo.InvariantCulture,
                    "with the peak stepped down to {0} K the planet was {1}", peak, lit ? "out" : "still lit"));
            }
        }

        /// <summary>8. A cold planet holding methane is not lit by heat, burns at the game's share while sparked, stops when the spark does, and fizzles.</summary>
        private static void Spark(FireRule rule)
        {
            const double peak = 291.0;
            GlobalGasMix tank = Plus(Mars(), (Chemistry.GasType.Methane, 1000.0));
            double capacity = tank.GetHeatCapacity().ToDouble();
            Need(Step(rule, tank, peak, sparked: false) is Unlit, "Mars with 1,000 mol of methane was lit at 291 K without a spark");
            double added = 0.0;
            for (int i = 0; i < 10; i++)
            {
                double methane = tank.Get(Chemistry.GasType.Methane).ToDouble();
                Burning burning = Burns(Step(rule, tank, peak, sparked: true), "Mars with methane, sparked");
                Need(burning.Cause == FireCause.Spark, "a sparked cold planet burnt by heat");
                Near(burning.Rate, CurvedShare(peak, nitrous: false), 1e-12, "the sparked share a tick");
                Near(tank.Get(Chemistry.GasType.Methane).ToDouble(), methane * (1.0 - burning.Rate), 1e-9 * methane, "a sparked tick burnt other than the game's share of the methane");
                added += burning.Added;
            }
            Need(Step(rule, tank, peak, sparked: false) is Unlit, "a cold planet kept burning once the spark stopped");
            Need(added / capacity < 10.0, string.Format(CultureInfo.InvariantCulture, "ten sparked ticks of 1,000 mol of methane added {0:0.###} K to Mars", added / capacity));
        }

        /// <summary>9. A cold planet whose own burn lifts its peak past ignition keeps burning once the spark ends.</summary>
        private static void SelfSustain(FireRule rule)
        {
            GlobalGasMix tank = new GlobalGasMix(new VolumeLitres(8.0e6));
            Set(tank, 1000.0, (Chemistry.GasType.Methane, 10.0), (Chemistry.GasType.Oxygen, 10.0), (Chemistry.GasType.Nitrogen, 10.0));
            const double basePeak = 540.0;
            StoredHeats heats = new StoredHeats(0.0, 0.0, 0.0);
            FireTick previous = NothingToBurn.Instance;
            for (int i = 0; i < 6; i++)
            {
                double capacity = tank.GetHeatCapacity().ToDouble();
                double peak = basePeak + heats.External / capacity;
                previous = rule.Step(tank, new FireConditions(peak, peak, capacity, i < 3), previous, heats);
                Burning burning = Burns(previous, "a cold planet sparked into a self-sustaining fire, tick " + (i + 1));
                heats = burning.Heats;
                Need(i < 3 || burning.Cause == FireCause.Heat, "a fire whose heat lifted the peak past ignition went out with its spark");
            }
        }

        /// <summary>10. L = L0 C1/C0; X = X0 C1/C0 + E - (C1 - C0) T; H = H0 C1/C0 + E - (C1 - C0) T, with E from the enthalpies.</summary>
        private static void HeatBooking(FireRule rule)
        {
            const double kelvin = 900.0;
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0));
            Dictionary<Chemistry.GasType, double> start = Totals(tank);
            double c0 = tank.GetHeatCapacity().ToDouble();
            StoredHeats before = new StoredHeats(1.0e9, 3.0e9, 2.0e9);
            Burning burning = Burns(rule.Step(tank, new FireConditions(1000.0, kelvin, c0, false), NothingToBurn.Instance, before), "Vulcan with oxygen");
            double c1 = tank.GetHeatCapacity().ToDouble();
            Dictionary<Chemistry.GasType, double> end = Totals(tank);
            double energy = 1e3 * (MethaneKiloJoules * (start[Chemistry.GasType.Methane] - end[Chemistry.GasType.Methane])
                + HydrogenKiloJoules * (start[Chemistry.GasType.Hydrogen] - end[Chemistry.GasType.Hydrogen]));
            double net = energy - (c1 - c0) * kelvin;
            double tolerance = 1e-6 * Math.Abs(net) + 1.0;
            Near(burning.Heats.Latent, before.Latent * c1 / c0, tolerance, "latent heat after a burn");
            Near(burning.Heats.External, before.External * c1 / c0 + net, tolerance, "external heat after a burn");
            Near(burning.Heats.Combustion, before.Combustion * c1 / c0 + net, tolerance, "combustion heat after a burn");
        }

        /// <summary>11. Combustion heat worth 300 K survives settling apart from its fade; other added heat is still held to the 50 K limit.</summary>
        private static void NoCap(FireRule rule)
        {
            const double capacity = 1.0e8;
            const double tick = 0.5;
            double fade = Math.Pow(0.5, tick / (60.0 * 60.0));
            StoredHeats settled = rule.Settle(new StoredHeats(100.0 * capacity, 400.0 * capacity, 300.0 * capacity), capacity, 60.0, 50.0, tick);
            Near(settled.Combustion, 300.0 * capacity * fade, 1e-6, "combustion heat after settling");
            Near(settled.External, 300.0 * capacity * fade + 50.0 * capacity, 1e-6, "external heat after settling, combustion heat plus the 50 K limit");
            Near(settled.Latent, 50.0 * capacity, 1e-6, "latent heat after settling, at the 50 K limit");
            StoredHeats cold = rule.Settle(new StoredHeats(0.0, 300.0 * capacity - 80.0 * capacity, 300.0 * capacity), capacity, 60.0, 50.0, tick);
            Near(cold.External, 300.0 * capacity * fade - 50.0 * capacity, 1e-6, "external heat below combustion heat by more than the limit");
        }

        /// <summary>12. A trace of hydrazine is never lit by heat but burns when sparked; 1.2 mol a cell lights just above the game's threshold and not 5 K below it.</summary>
        private static void Hydrazine(FireRule rule)
        {
            foreach (double peak in new[] { 300.0, 600.0, 1000.0, 3000.0 })
            {
                Need(Step(rule, Plus(Mars(), (Chemistry.GasType.Hydrazine, 100.0)), peak, sparked: false) is Unlit,
                    "100 mol of hydrazine on Mars was lit by heat at " + peak + " K");
            }
            Burning sparked = Burns(Step(rule, Plus(Mars(), (Chemistry.GasType.Hydrazine, 100.0)), 300.0, sparked: true), "100 mol of hydrazine on Mars, sparked");
            Need(rule.HeldBack(sparked).Contains(Chemistry.GasType.Hydrazine), "burning hydrazine on its own is not held back");
            double threshold = HydrazineIgnitionKelvin;
            Need(threshold > 0.0 && !double.IsInfinity(threshold), "the game's hydrazine threshold is not a temperature");
            Need(IsBurning(Step(rule, Plus(Mars(), (Chemistry.GasType.Hydrazine, 300000.0)), threshold + 0.1, sparked: false), FireCause.Heat),
                "1.2 mol of hydrazine a cell was not lit just above the game's threshold");
            Need(Step(rule, Plus(Mars(), (Chemistry.GasType.Hydrazine, 300000.0)), threshold - 5.0, sparked: false) is Unlit,
                "1.2 mol of hydrazine a cell was lit 5 K below the game's threshold");
        }

        /// <summary>13. 26 mol of nitrous oxide on Vulcan leaves the threshold at 573.15 K; 1.2 mol a cell lowers it to 323.15 K.</summary>
        private static void NitrousOxideOffset(FireRule rule)
        {
            foreach ((double moles, double threshold) in new[] { (26.0, 573.15), (300000.0, 323.15) })
            {
                Need(IsBurning(Step(rule, Plus(Vulcan(), (Chemistry.GasType.NitrousOxide, moles)), threshold + 0.05, sparked: false), FireCause.Heat),
                    "Vulcan with " + moles + " mol of nitrous oxide was not lit just above " + threshold + " K");
                Need(Step(rule, Plus(Vulcan(), (Chemistry.GasType.NitrousOxide, moles)), threshold - 0.05, sparked: false) is Unlit,
                    "Vulcan with " + moles + " mol of nitrous oxide was lit just below " + threshold + " K");
            }
        }

        /// <summary>14. Liquid oxygen and liquid methane in the tank are never burnt, written or held back.</summary>
        private static void LiquidsUntouched(FireRule rule)
        {
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0), (Chemistry.GasType.LiquidOxygen, 5000.0), (Chemistry.GasType.LiquidMethane, 1000.0));
            HeldGases held = HeldGases.None;
            RunToTheEnd(rule, tank, 1000.0, (before, burning) => held = held.With(rule.HeldBack(burning)));
            Need(tank.Get(Chemistry.GasType.LiquidOxygen).ToDouble() == 5000.0 && tank.Get(Chemistry.GasType.LiquidMethane).ToDouble() == 1000.0,
                "the planet's fire burnt or wrote a liquid");
            Need(!held.Contains(Chemistry.GasType.LiquidOxygen) && !held.Contains(Chemistry.GasType.LiquidMethane), "the planet's fire held back a liquid");
        }

        /// <summary>15. With the mod's own copies unheld, a take and give with the planet burning return the tank to its exact total.</summary>
        private static void SelfTestUnheld(FireRule rule)
        {
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0));
            HeldGases held = rule.HeldBack(Burns(Step(rule, tank, 1000.0, sparked: false), "Vulcan with oxygen"));
            double before = tank.TotalQuantity().ToDouble();
            GasMixture taken;
            using (PlanetCombustion.Unheld.Begin())
            {
                taken = Withheld(tank, held, 8000.0 / Litres);
            }
            Need(taken.Oxygen.Quantity.ToDouble() > 0.0, "the mod's own take was held back");
            tank.Remove(taken);
            tank.Add(taken);
            Need(tank.TotalQuantity().ToDouble() == before, "a take and give of the planet's whole air did not return it to its total");
            Need(Withheld(tank, held, 8000.0 / Litres).Oxygen.Quantity.ToDouble() == 0.0, "the hold-back stayed off after the mod's own take");
        }

        /// <summary>16. A gas the fire holds back, or the trace hold keeps, gets no gathering budget; an unrelated trace does.</summary>
        private static void Gathering(FireRule rule, TraceHoldRule traces)
        {
            bool enabled = Effective.TraceGasGatheringEnabled;
            double factor = Effective.TraceGasGathering;
            double line = Effective.TraceGasLine;
            try
            {
                Effective.TraceGasGatheringEnabled = true;
                Effective.TraceGasGathering = 200.0;
                Effective.TraceGasLine = 1e-3;
                GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 25.0), (Chemistry.GasType.Helium, 25.0));
                HeldGases held = rule.HeldBack(Burns(Step(rule, Plus(Vulcan(), (Chemistry.GasType.Oxygen, 25.0)), 1000.0, sparked: false), "Vulcan with a trace of oxygen"));
                TraceGases.Refresh(tank, held);
                Need(TraceGases.BudgetFor(Chemistry.GasType.Oxygen) == 0.0, "a gas the planet's fire holds back was given a gathering budget");
                Need(TraceGases.BudgetFor(Chemistry.GasType.Helium) > 0.0, "an unrelated trace gas was given no gathering budget");
                // 10 mol of helium on 250,000 cells is 4e-5 mol a cell, 8e-6 at an exchange rate of 0.2: kept.
                GlobalGasMix thinTank = Plus(Vulcan(), (Chemistry.GasType.Helium, 10.0), (Chemistry.GasType.NitrousOxide, 25.0));
                HeldGases thin = traces.Thin(thinTank, 0.2);
                Need(thin.Contains(Chemistry.GasType.Helium), "4e-5 mol of helium a cell at an exchange rate of 0.2 is not kept by the trace hold");
                TraceGases.Refresh(thinTank, thin);
                Need(TraceGases.BudgetFor(Chemistry.GasType.Helium) == 0.0, "a gas the trace hold keeps was given a gathering budget");
                Need(TraceGases.BudgetFor(Chemistry.GasType.NitrousOxide) > 0.0, "a trace the trace hold does not keep was given no gathering budget");
            }
            finally
            {
                Effective.TraceGasGatheringEnabled = enabled;
                Effective.TraceGasGathering = factor;
                Effective.TraceGasLine = line;
                TraceGases.Refresh(null, HeldGases.None);
            }
        }

        /// <summary>
        /// 17. The armed hold-back: on Vulcan, past its ignition point, every oxidiser is held with none
        /// in the air yet and no fuel is; 5 K short of ignition it is armed, 6 K short it is not, and at
        /// 500 K it is not. A fuel scarce beside oxygen (Europa) holds the fuel. Oxygen that reaches the
        /// planet after the hold was decided, the tick a leak drains, reaches no copy, and the planet
        /// loses exactly the copy. A cold planet lit by a spark is not armed.
        /// </summary>
        private static void ArmedHoldBack(FireRule rule)
        {
            HeldGases armed = rule.Armed(Vulcan(), 975.0);
            Need(Holds(armed, Chemistry.GasType.Oxygen, Chemistry.GasType.NitrousOxide, Chemistry.GasType.Ozone),
                "on Vulcan past ignition with no oxidiser yet, the armed hold-back does not hold every oxidiser");
            Need(!armed.Contains(Chemistry.GasType.Methane) && !armed.Contains(Chemistry.GasType.Hydrogen), "on Vulcan the armed hold-back holds a fuel");
            Need(rule.Armed(Vulcan(), 500.0).IsEmpty, "on Vulcan at a peak of 500 K the hold-back is armed");
            double ignition = Chemistry.AutoIgnitionMethane.ToDouble();
            Need(!rule.Armed(Vulcan(), ignition - 4.9).IsEmpty, "4.9 K short of ignition the hold-back is not armed, so the 5 K margin is missing");
            Need(rule.Armed(Vulcan(), ignition - 5.1).IsEmpty, "5.1 K short of ignition the hold-back is armed");
            HeldGases europa = rule.Armed(Plus(Europa(), (Chemistry.GasType.Methane, 300000.0)), 580.0);
            Need(europa.Contains(Chemistry.GasType.Methane) && !europa.Contains(Chemistry.GasType.Oxygen), "with the fuel scarce, the armed hold-back does not hold methane, or holds oxygen");

            GlobalGasMix tank = Vulcan();
            FireTick tick = Step(rule, tank, 975.0, sparked: false);
            Need(tick is NothingToBurn, "Vulcan with no oxidiser had something to burn");
            PlanetHold hold = PlanetHold.Of(tick, rule.HeldBack(tick), rule.Armed(tank, 975.0), true, HeldGases.None, false);
            Need(hold.Fire is ArmedHold, "the armed hold-back on Vulcan is not reported as armed");
            tank = Plus(tank, (Chemistry.GasType.Oxygen, 1000.0));
            GasMixture copy = Withheld(tank, hold.All, 8000.0 / Litres);
            double before = tank.TotalQuantity().ToDouble();
            tank.Remove(copy);
            Need(copy.Oxygen.Quantity.ToDouble() == 0.0, "oxygen that reached an armed planet was handed to a cell the same tick");
            Near(before - tank.TotalQuantity().ToDouble(), copy.GetTotalMolesGassesAndLiquids.ToDouble(), 1e-6, "the armed hold-back: the planet lost other than what was taken");
            Near(tank.Get(Chemistry.GasType.Oxygen).ToDouble(), 1000.0, 1e-9, "the armed hold-back: the planet's oxygen");

            Need(rule.Armed(Plus(Mars(), (Chemistry.GasType.Methane, 1000.0)), 291.0).IsEmpty, "a cold planet is armed");
        }

        /// <summary>
        /// 18. Both settings decide the hold. Armed hold-back off: the scarce side is held only while
        /// burning, as before. On, with the air past ignition: held burning or not. On, with the gate
        /// closed and a spark burning: held while burning. Trace hold off: nothing is kept as a trace.
        /// </summary>
        private static void HoldSettings(FireRule rule)
        {
            HeldGases oxidisers = FireGases.OxidiserSide;
            HeldGases helium = HeldGases.Of(new[] { Chemistry.GasType.Helium });
            Unlit unlit = new Unlit(oxidisers);
            Burning burning = Burns(Step(rule, Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0)), 975.0, sparked: false), "Vulcan with oxygen");
            Need(PlanetHold.Of(NothingToBurn.Instance, HeldGases.None, oxidisers, false, HeldGases.None, false).IsEmpty,
                "with the armed hold-back off, something is held while nothing burns");
            Need(PlanetHold.Of(unlit, rule.HeldBack(unlit), oxidisers, false, HeldGases.None, false).IsEmpty,
                "with the armed hold-back off, something is held while the planet is unlit");
            Need(PlanetHold.Of(burning, rule.HeldBack(burning), oxidisers, false, HeldGases.None, false).Fire is BurningHold,
                "with the armed hold-back off, a burning planet does not hold back while burning");
            PlanetHold armed = PlanetHold.Of(NothingToBurn.Instance, HeldGases.None, oxidisers, true, HeldGases.None, false);
            Need(armed.Fire is ArmedHold && armed.All.Contains(Chemistry.GasType.Oxygen), "with the armed hold-back on and the gate open, oxygen is not held while nothing burns");
            Burning spark = Burns(Step(rule, Plus(Mars(), (Chemistry.GasType.Methane, 1000.0)), 291.0, sparked: true), "Mars with methane, sparked");
            PlanetHold sparked = PlanetHold.Of(spark, rule.HeldBack(spark), HeldGases.None, true, HeldGases.None, false);
            Need(sparked.Fire is BurningHold && sparked.All.Contains(Chemistry.GasType.Methane), "a fire a spark lit, with the gate closed, does not hold back while burning");
            Need(PlanetHold.Of(NothingToBurn.Instance, HeldGases.None, HeldGases.None, true, helium, false).IsEmpty, "with the trace hold off, a trace is kept");
            PlanetHold traced = PlanetHold.Of(NothingToBurn.Instance, HeldGases.None, HeldGases.None, false, helium, true);
            Need(traced.All.Contains(Chemistry.GasType.Helium) && traced.Reasons(Chemistry.GasType.Helium) == "trace hold", "with the trace hold on, a thin gas is not kept as a trace");
            PlanetHold both = PlanetHold.Of(NothingToBurn.Instance, HeldGases.None, oxidisers, true, HeldGases.Of(new[] { Chemistry.GasType.Oxygen }), true);
            Need(both.Reasons(Chemistry.GasType.Oxygen) == "armed hold-back and trace hold", "a gas held for both reasons does not name both");
        }

        /// <summary>
        /// 19. The trace hold is the game's own test on a cell that starts the tick without the gas:
        /// the cell's worth times the exchange rate, under the least a cell keeps (1e-5 mol). 4e-5 a cell
        /// at 0.2 is kept, 6e-5 is not, 4e-5 at 1.0 is not, none is not. A kept gas reaches no copy and
        /// stays in the planet whole, and the planet loses exactly the copy.
        /// </summary>
        private static void TraceHold(TraceHoldRule traces)
        {
            Need(traces.Deleted(4e-5, 0.2), "4e-5 mol a cell at an exchange rate of 0.2 is not deleted by the game's test");
            Need(!traces.Deleted(6e-5, 0.2), "6e-5 mol a cell at an exchange rate of 0.2 is deleted by the game's test");
            Need(!traces.Deleted(4e-5, 1.0), "4e-5 mol a cell at an exchange rate of 1 is deleted by the game's test");
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Helium, 10.0), (Chemistry.GasType.Ozone, 15.0));
            HeldGases thin = traces.Thin(tank, 0.2);
            Need(thin.Contains(Chemistry.GasType.Helium), "10 mol of helium on 250,000 cells is not kept");
            Need(!thin.Contains(Chemistry.GasType.Ozone), "15 mol of ozone on 250,000 cells, 1.2e-5 a cell at 0.2, is kept");
            Need(!thin.Contains(Chemistry.GasType.Methane) && !thin.Contains(Chemistry.GasType.Oxygen), "a gas the planet holds plenty of, or none of, is kept as a trace");
            GasMixture copy = Withheld(tank, thin, 8000.0 / Litres);
            double before = tank.TotalQuantity().ToDouble();
            tank.Remove(copy);
            Need(copy.GetMoleValue(Chemistry.GasType.Helium).Quantity.ToDouble() == 0.0, "a copy of the planet's air carried a gas the trace hold keeps");
            Near(tank.Get(Chemistry.GasType.Helium).ToDouble(), 10.0, 1e-12, "the helium the trace hold keeps");
            Near(before - tank.TotalQuantity().ToDouble(), copy.GetTotalMolesGassesAndLiquids.ToDouble(), 1e-6, "the trace hold: the planet lost other than what was taken");
        }

        /// <summary>
        /// 20. The self-test's verdict counts what a take actually handed over, so a take made while the
        /// planet holds gases back (both holds on, and not the mod's own unheld take) still balances; the
        /// same take judged against the planet's whole share does not.
        /// </summary>
        private static void SelfTestCountsWhatWasHanded(FireRule rule, TraceHoldRule traces)
        {
            GlobalGasMix tank = Plus(Vulcan(), (Chemistry.GasType.Oxygen, 5000.0), (Chemistry.GasType.Helium, 10.0));
            FireTick tick = Step(rule, tank, 975.0, sparked: false);
            PlanetHold hold = PlanetHold.Of(tick, rule.HeldBack(tick), rule.Armed(tank, 975.0), true, traces.Thin(tank, 0.2), true);
            Need(hold.All.Contains(Chemistry.GasType.Oxygen) && hold.All.Contains(Chemistry.GasType.Helium), "the self-test case holds nothing back");
            double share = 8000.0 / Litres;
            double whole = OneCell(tank, 300.0).GetTotalMolesGassesAndLiquids.ToDouble();
            double before = tank.TotalQuantity().ToDouble();
            GasMixture taken = Withheld(tank, hold.All, share);
            tank.Remove(taken);
            double between = tank.TotalQuantity().ToDouble();
            double moved = taken.GetTotalMolesGassesAndLiquids.ToDouble();
            tank.Add(taken);
            double after = tank.TotalQuantity().ToDouble();
            string problem = SelfTest.RoundTripProblem(before, between, moved, after);
            Need(problem == null, "a take and give with gases held back failed the self-test's verdict: " + problem);
            Need(SelfTest.RoundTripProblem(before, between, whole, after) != null, "the self-test's verdict cannot tell a take's own moles from the planet's whole share");
        }

        // ---- worlds -------------------------------------------------------------------------------

        /// <summary>VulcanV2's air (VulcanV2.xml): per cell carbon dioxide 12, methane 27, hydrogen 3, pollutant 15.</summary>
        private static GlobalGasMix Vulcan() => World((Chemistry.GasType.CarbonDioxide, 12.0), (Chemistry.GasType.Methane, 27.0),
            (Chemistry.GasType.Hydrogen, 3.0), (Chemistry.GasType.Pollutant, 15.0));

        /// <summary>Mars2's air: per cell carbon dioxide 8.66, nitrogen 0.27, oxygen 0.131, pollutant 0.058.</summary>
        private static GlobalGasMix Mars() => World((Chemistry.GasType.CarbonDioxide, 8.66), (Chemistry.GasType.Nitrogen, 0.27),
            (Chemistry.GasType.Oxygen, 0.131), (Chemistry.GasType.Pollutant, 0.058));

        /// <summary>Europa3's air: oxygen 340 per cell.</summary>
        private static GlobalGasMix Europa() => World((Chemistry.GasType.Oxygen, 340.0));

        private static GlobalGasMix World(params (Chemistry.GasType Type, double PerCell)[] air)
        {
            GlobalGasMix tank = new GlobalGasMix(new VolumeLitres(Litres));
            Set(tank, Cells, air);
            return tank;
        }

        private static void Set(GlobalGasMix tank, double cells, params (Chemistry.GasType Type, double PerCell)[] air)
        {
            foreach ((Chemistry.GasType type, double perCell) in air)
            {
                tank.Set(new MoleQuantity(perCell * cells), type);
            }
        }

        private static GlobalGasMix Plus(GlobalGasMix tank, params (Chemistry.GasType Type, double Moles)[] added)
        {
            foreach ((Chemistry.GasType type, double moles) in added)
            {
                tank.Set(new MoleQuantity(tank.Get(type).ToDouble() + moles), type);
            }
            return tank;
        }

        // ---- helpers ------------------------------------------------------------------------------

        private static FireTick Step(FireRule rule, GlobalGasMix tank, double peak, bool sparked, FireTick previous = null)
        {
            return rule.Step(tank, new FireConditions(peak, peak, tank.GetHeatCapacity().ToDouble(), sparked),
                previous ?? NothingToBurn.Instance, new StoredHeats(0.0, 0.0, 0.0));
        }

        /// <summary>Burns at a fixed peak until there is nothing left to burn, calling <paramref name="each"/> with the totals before every tick.</summary>
        private static void RunToTheEnd(FireRule rule, GlobalGasMix tank, double peak, Action<Dictionary<Chemistry.GasType, double>, Burning> each)
        {
            FireTick previous = NothingToBurn.Instance;
            for (int i = 0; i < 100000; i++)
            {
                Dictionary<Chemistry.GasType, double> before = Totals(tank);
                previous = Step(rule, tank, peak, sparked: false, previous: previous);
                if (!(previous is Burning burning))
                {
                    Need(previous is NothingToBurn, "a fire at " + peak + " K went out with something left to burn");
                    return;
                }
                each(before, burning);
            }
            throw new CheckFailed("a fire at " + peak + " K never went out");
        }

        /// <summary>One cell's worth of the tank at <paramref name="kelvin"/>, built here rather than by the rule: each gas over the cell's share of the volume.</summary>
        private static GasMixture OneCell(GlobalGasMix tank, double kelvin)
        {
            GasMixture cell = GasMixtureHelper.Create();
            double share = 8000.0 / tank.Volume.ToDouble();
            foreach (Chemistry.GasType type in FireGases.Gases)
            {
                MoleQuantity quantity = new MoleQuantity(tank.Get(type).ToDouble() * share);
                cell.Add(new Mole(type, quantity, IdealGas.Energy(new TemperatureKelvin(kelvin), Mole.SpecificHeat(type), quantity)));
            }
            return cell;
        }

        /// <summary>A copy of the tank's gas times <paramref name="share"/>, as the game copies the planet's air for a cell, through the hold-back.</summary>
        private static GasMixture Withheld(GlobalGasMix tank, HeldGases held, double share, GlobalGasMix source = null)
        {
            GasMixture copy = FireRule.Copy(tank, share, 300.0);
            PlanetCombustion.Withhold(tank, held, source ?? tank, ref copy);
            return copy;
        }

        /// <summary>Atmosphere.GetCombustionMultiplierCurved written out, as docs/PLANET-COMBUSTION.md states it.</summary>
        private static double CurvedShare(double kelvin, bool nitrous)
        {
            double share = nitrous
                ? 0.05 + 1.0 / Math.Pow(0.0025 * (kelvin + 273.15), 1.01)
                : 0.05 + 1.0 / Math.Pow(0.002 * (kelvin + 273.15), 1.6);
            return Math.Max(0.0, Math.Min(1.0, share)) / 5.0;
        }

        private static Dictionary<Chemistry.GasType, double> Totals(GlobalGasMix tank)
        {
            Dictionary<Chemistry.GasType, double> totals = new Dictionary<Chemistry.GasType, double>();
            foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
            {
                if (type != Chemistry.GasType.Undefined && Mole.MatterState(type) != AtmosphereHelper.MatterState.None)
                {
                    totals[type] = tank.Get(type).ToDouble();
                }
            }
            return totals;
        }

        /// <summary>Every gas moved by exactly the listed amounts, to 1e-6 of each, and every other gas not at all.</summary>
        private static void Changed(Dictionary<Chemistry.GasType, double> before, Dictionary<Chemistry.GasType, double> after, string what,
            params (Chemistry.GasType Type, double By)[] expected)
        {
            Dictionary<Chemistry.GasType, double> by = new Dictionary<Chemistry.GasType, double>();
            foreach ((Chemistry.GasType type, double change) in expected)
            {
                by[type] = change;
            }
            foreach (KeyValuePair<Chemistry.GasType, double> gas in before)
            {
                double change = after[gas.Key] - gas.Value;
                double want = by.TryGetValue(gas.Key, out double listed) ? listed : 0.0;
                Near(change, want, 1e-6 * Math.Abs(want) + 1e-6, what + ": " + gas.Key);
            }
        }

        private static bool IsBurning(FireTick tick, FireCause cause) => tick is Burning burning && burning.Cause == cause;

        private static Burning Burns(FireTick tick, string what)
        {
            return tick as Burning ?? throw new CheckFailed(what + " did not burn (" + tick.GetType().Name + ")");
        }

        private static bool Holds(HeldGases held, params Chemistry.GasType[] gases)
        {
            foreach (Chemistry.GasType gas in gases)
            {
                if (!held.Contains(gas))
                {
                    return false;
                }
            }
            return true;
        }

        private static void Near(double actual, double expected, double tolerance, string what)
        {
            if (!(Math.Abs(actual - expected) <= tolerance))
            {
                throw new CheckFailed(string.Format(CultureInfo.InvariantCulture, "{0} is {1:R}, expected {2:R}", what, actual, expected));
            }
        }

        private static void Need(bool condition, string what)
        {
            if (!condition)
            {
                throw new CheckFailed(what);
            }
        }
    }
}
