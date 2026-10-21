using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Assets.Scripts.Atmospherics;

/// <summary>
/// Applies the mod's real patch set to the installed game's real Assembly-CSharp, outside the game.
/// Proves that every patch target still exists with the expected shape and that the rewritten IL
/// compiles. It cannot prove behaviour: nothing here runs a simulation tick.
///
/// Run after a game update, before launching the game:  tools\PatchCheck\run.ps1
/// </summary>
internal static class Program
{
    private static string[] _probe;

    private static int Main(string[] args)
    {
        string game = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("STATIONEERS_DIR")
            ?? @"C:\Program Files (x86)\Steam\steamapps\common\Stationeers";
        string mod = args.Length > 1 ? args[1]
            : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\src\bin\Release"));

        _probe = new[]
        {
            mod,
            Path.Combine(game, @"rocketstation_Data\Managed"),
            Path.Combine(game, @"BepInEx\core"),
            Path.Combine(game, @"BepInEx\plugins\StationeersLaunchPad"),
        };
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        return Run();
    }

    // Separate so the game and mod types resolve after the resolver is installed.
    private static int Run()
    {
        var report = TerraformingReloaded.Patching.Patcher.Apply(new HarmonyLib.Harmony("patchcheck"));

        Console.WriteLine();
        Console.WriteLine("armed:   " + report.Armed);
        foreach (string line in report.Applied) Console.WriteLine("applied: " + line);
        // Outside Unity the CLR refuses to compile any method that reaches a Unity native call
        // ("ECall methods must be packaged into a system module"). Harmony only gets that far once
        // the target was found and the patch body bound to it, so for those parts that is all this
        // tool can vouch for; the rest needs the game.
        // Armed needs the save guard, which is one of the parts that cannot compile here, so judge
        // the conversion of the tank methods instead.
        Console.WriteLine("tank:    " + (report.TankConverted ? "all 7 methods converted" : "NOT converted"));
        int failures = report.TankConverted ? 0 : 1;
        foreach (string line in report.Failed)
        {
            string name = line.Split(':')[0];
            bool needsUnity = report.Errors.TryGetValue(name, out Exception error)
                && error.ToString().Contains("ECall methods must be packaged");
            if (needsUnity)
            {
                Console.WriteLine("bound:   " + name + " (target found, patch body fits; compiling it needs Unity)");
            }
            else
            {
                Console.WriteLine("FAILED:  " + line);
                failures++;
            }
        }

        // Force each patched method through the JIT so invalid IL surfaces here, not in the game.
        var harmonyPatched = HarmonyLib.Harmony.GetAllPatchedMethods().ToList();
        Console.WriteLine("patched methods: " + harmonyPatched.Count);
        foreach (MethodBase method in harmonyPatched)
        {
            Console.WriteLine("  " + method.DeclaringType.Name + "." + method.Name);
        }

        // The world is never marked allowed here, so Enabled() is false and the rewritten methods
        // take their vanilla path. Calling one proves the rewrite produced runnable code.
        try
        {
            Assets.Scripts.PlanetaryAtmosphereSimulation.AddEnergy(new Assets.Scripts.Atmospherics.MoleEnergy(1.0));
            Assets.Scripts.PlanetaryAtmosphereSimulation.RemoveEnergy(new Assets.Scripts.Atmospherics.MoleEnergy(1.0));
            Console.WriteLine("rewritten AddEnergy/RemoveEnergy ran");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  calling a rewritten method: " + e);
            failures++;
        }

        double moon = TerraformingReloaded.Patching.Climate.EquilibriumKelvin(1367.0, 0.3);
        double mimas = TerraformingReloaded.Patching.Climate.EquilibriumKelvin(15.0, 0.3);
        Console.WriteLine($"airless settle temperature: Moon-like {moon:0.0} K, Saturn-like {mimas:0.0} K");
        if (Math.Abs(moon - 254.8) > 1.0)
        {
            Console.WriteLine("FAILED:  equilibrium temperature is off");
            failures++;
        }

        // Patcher applies the extras only once the save guard is on, which cannot compile here, so
        // the trace gas transpiler is applied on its own to prove its target and its one rewrite.
        try
        {
            TerraformingReloaded.Patching.TraceGases.Apply(new HarmonyLib.Harmony("patchcheck.tracegases"));
            Console.WriteLine("applied: trace gases gather (the outdoor lerp's one take rewritten)");
        }
        catch (Exception e) when (e.ToString().Contains("ECall methods must be packaged"))
        {
            Console.WriteLine("bound:   trace gases gather (target found, one take rewritten; compiling it needs Unity)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  trace gases gather: " + e.Message);
            failures++;
        }

        string gathering = TerraformingReloaded.Patching.TraceGases.CheckArithmetic();
        Console.WriteLine("trace gas gathering arithmetic: " + (gathering ?? "conserves and holds its bounds"));
        if (gathering != null)
        {
            Console.WriteLine("FAILED:  trace gas gathering arithmetic");
            failures++;
        }

        // Storm bounds: the rule by hand and across a grid, then wrong rules it must catch.
        string bounds = TerraformingReloaded.Patching.StormBounds.Check();
        failures += Judge(bounds == null, "storm bounds: " + (bounds ?? "a storm stops at the floor and ceiling, never moves a planet already past them, and leaves everything else alone"));
        var wrongBounds = new (string What, Func<TerraformingReloaded.Patching.StormBounds, double, double, double> Rule)[]
        {
            ("clamping the whole temperature, which raises a planet already below the floor",
                (b, t, e) => Math.Min(Math.Max(t, b.FloorKelvin ?? double.MinValue), b.CeilingKelvin ?? double.MaxValue)),
            ("ignoring the bounds", (b, t, e) => t),
            ("taking the storm out entirely while a bound is set", (b, t, e) => b.IsOff ? t : t - e),
            ("swapping floor and ceiling", (b, t, e) => new TerraformingReloaded.Patching.StormBounds(b.CeilingKelvin, b.FloorKelvin).Limit(t, e)),
        };
        foreach (var wrong in wrongBounds)
        {
            string caught = TerraformingReloaded.Patching.StormBounds.Check(wrong.Rule);
            failures += Judge(caught != null, "storm bounds check fails " + wrong.What + (caught != null ? ": " + caught : ""));
        }

        // Gas released in space. The shape check proves the places space gas reaches the planet are
        // still exactly where the rule expects. This runtime's mscorlib has no System.Span, so it
        // cannot open the body of the mixing method at all; the check is handed a counter that reads
        // the same assembly file with Mono.Cecil instead. Patching the method needs Unity's runtime,
        // so that part is only bound here.
        try
        {
            TerraformingReloaded.Patching.Space.CheckShape(CecilCalls);
            Console.WriteLine("checked: gas lost in space (lerp, mixing share and removal each give once; Atmosphere gives twice)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  gas lost in space, shape: " + e.Message);
            failures++;
        }
        try
        {
            TerraformingReloaded.Patching.Space.Apply(new HarmonyLib.Harmony("patchcheck.space"));
            Console.WriteLine("applied: gas lost in space");
        }
        catch (Exception e) when (e.ToString().Contains("ECall methods must be packaged") || e.ToString().Contains("System.Span"))
        {
            Console.WriteLine("bound:   gas lost in space (patching the mixing needs Unity's runtime)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  gas lost in space: " + e.Message);
            failures++;
        }

        // The counter the game uses opens method bodies through the runtime, which works here only
        // for methods with no Span in them. For those it must agree with Mono.Cecil exactly, and the
        // shape check must refuse a game that gives to the planet one more time than expected.
        try
        {
            Type atmosphere = typeof(Assets.Scripts.Atmospherics.Atmosphere);
            Type simulation = typeof(Assets.Scripts.PlanetaryAtmosphereSimulation);
            MethodInfo give = HarmonyLib.AccessTools.DeclaredMethod(simulation, "GiveToGlobal", new[] { typeof(Assets.Scripts.Atmospherics.GasMixture) });
            MethodInfo inSpace = HarmonyLib.AccessTools.DeclaredMethod(simulation, "IsInSpaceAtmosphere", new[] { typeof(Assets.Scripts.GridSystem.WorldGrid) });
            MethodInfo take = HarmonyLib.AccessTools.DeclaredMethod(simulation, "TakeGlobalGasMix", new[] { typeof(Assets.Scripts.Atmospherics.VolumeLitres) });
            MethodInfo lerp = HarmonyLib.AccessTools.DeclaredMethod(atmosphere, "LerpToGlobalAtmosphere", Type.EmptyTypes);
            MethodInfo share = HarmonyLib.AccessTools.DeclaredMethod(atmosphere, "GiveAtmospheresMixInWorld", Type.EmptyTypes);
            MethodInfo deregister = HarmonyLib.AccessTools.DeclaredMethod(typeof(Assets.Scripts.Atmospherics.AtmosphericsManager), "Deregister", new[] { atmosphere });
            var pairs = new (MethodInfo Method, MethodInfo Target)[] { (lerp, give), (lerp, inSpace), (lerp, take), (share, give), (deregister, give), (deregister, inSpace) };
            string disagree = null;
            foreach (var pair in pairs)
            {
                int runtime = TerraformingReloaded.Patching.Space.CountCalls(pair.Method, pair.Target);
                int cecil = CecilCalls(pair.Method, pair.Target);
                if (runtime != cecil)
                {
                    disagree = $"{pair.Method.Name} calls {pair.Target.Name}: runtime counter {runtime}, Mono.Cecil {cecil}";
                }
            }
            bool refused = false;
            try
            {
                TerraformingReloaded.Patching.Space.CheckShape((m, t) => CecilCalls(m, t) + (m.Name == "GiveAtmospheresMixInWorld" && t.Name == "GiveToGlobal" ? 1 : 0));
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            if (disagree != null || !refused)
            {
                Console.WriteLine("FAILED:  gas lost in space, call counter: " + (disagree ?? "a game that gives to the planet once more was not refused"));
                failures++;
            }
            else
            {
                Console.WriteLine("checked: gas lost in space counter agrees with Mono.Cecil on " + pairs.Length + " pairs, and an extra give is refused");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  gas lost in space, call counter: " + e.Message);
            failures++;
        }

        string ledger = TerraformingReloaded.Patching.Space.CheckLedger();
        Console.WriteLine("gas lost in space running total: " + (ledger ?? "counts only while marked, per thread, keeps every add, and a planet reset zeroes it"));
        if (ledger != null)
        {
            Console.WriteLine("FAILED:  gas lost in space running total");
            failures++;
        }

        // Rain to snow and back. The switch the tick asks for is held and made on the main thread;
        // the holding is checked here, the patches only bound, since the update they hook needs Unity.
        try
        {
            TerraformingReloaded.Patching.WeatherSwitch.Apply(new HarmonyLib.Harmony("patchcheck.weatherswitch"));
            Console.WriteLine("applied: weather switch on the main thread");
        }
        catch (Exception e) when (e.ToString().Contains("ECall methods must be packaged"))
        {
            Console.WriteLine("bound:   weather switch on the main thread (target found, patch body fits; compiling it needs Unity)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  weather switch on the main thread: " + e.Message);
            failures++;
        }
        string weather = TerraformingReloaded.Patching.WeatherSwitch.Check();
        Console.WriteLine("weather switch: " + (weather ?? "held as one switch, made once, dropped when the weather it stops has ended, a later one replaces an earlier"));
        if (weather != null)
        {
            Console.WriteLine("FAILED:  weather switch");
            failures++;
        }

        failures += CheckRockets();
        failures += CheckPlanetFire();
        failures += CheckComets();
        failures += CheckCelestial();

        Console.WriteLine(failures == 0 ? "OK" : failures + " problem(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The game's own call count, read from the assembly file rather than through the runtime. The
    /// method is found by its metadata token, which is the same number in both views of one file;
    /// a call matches when its declaring type, name and parameter types are the target's.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, object> Modules = new System.Collections.Generic.Dictionary<string, object>();

    private static int CecilCalls(MethodBase method, MethodInfo target)
    {
        // Read once per file. Held in an object so that Program's own static initialiser, which
        // runs before the assembly resolver is installed, never has to load Mono.Cecil.
        string path = method.Module.FullyQualifiedName;
        if (!Modules.TryGetValue(path, out object read))
        {
            read = Mono.Cecil.ModuleDefinition.ReadModule(path);
            Modules[path] = read;
        }
        Mono.Cecil.ModuleDefinition module = (Mono.Cecil.ModuleDefinition)read;
        Mono.Cecil.MethodDefinition definition = module.LookupToken(method.MetadataToken) as Mono.Cecil.MethodDefinition;
        if (definition == null)
        {
            throw new InvalidOperationException("Mono.Cecil did not find " + method.DeclaringType + "." + method.Name + " by its token");
        }
        if (!definition.HasBody)
        {
            return 0;
        }
        string declaring = target.DeclaringType.FullName.Replace('+', '/');
        string[] parameters = target.GetParameters().Select(p => p.ParameterType.FullName.Replace('+', '/')).ToArray();
        return definition.Body.Instructions.Count(i =>
            (i.OpCode == Mono.Cecil.Cil.OpCodes.Call || i.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt)
            && i.Operand is Mono.Cecil.MethodReference called
            && called.Name == target.Name
            && called.DeclaringType.FullName == declaring
            && called.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters));
    }

    /// <summary>
    /// Rocket engines burn completely. The shape check reads the engine's burn with Mono.Cecil and
    /// must find the shipped rate; it must refuse an engine that burns twice or works its rate out;
    /// the patch must go in (or bind, where compiling needs Unity); and with no world running the
    /// rate an engine is handed must be the shipped one. Then the game's own combustion is run on a
    /// free chamber at both rates, which is the outcome the setting exists for: what is left over
    /// in the exhaust.
    /// </summary>
    private static int CheckRockets()
    {
        int failures = 0;
        double shipped = double.NaN;
        try
        {
            shipped = TerraformingReloaded.Patching.Rockets.CheckShape(CecilBody);
            Console.WriteLine("checked: rocket combustion (one constant rate, " + shipped.ToString("R", CultureInfo.InvariantCulture) + ", into the engine's one TryCombust)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  rocket combustion, shape: " + e.Message);
            failures++;
        }

        // Two game builds the rule must refuse: an engine that burns twice, and one that works its rate out.
        var mutations = new (string What, Func<IList<KeyValuePair<OpCode, object>>, IList<KeyValuePair<OpCode, object>>> Change)[]
        {
            ("burns twice", body =>
            {
                int at = TerraformingReloaded.Patching.Rockets.RateSite(body, TryCombustMethod());
                List<KeyValuePair<OpCode, object>> twice = body.ToList();
                twice.InsertRange(at + 3, body.Skip(at).Take(3));
                return twice;
            }),
            ("works its rate out", body =>
            {
                int at = TerraformingReloaded.Patching.Rockets.RateSite(body, TryCombustMethod());
                List<KeyValuePair<OpCode, object>> worked = body.ToList();
                worked[at] = new KeyValuePair<OpCode, object>(OpCodes.Ldloc_0, null);
                return worked;
            }),
        };
        foreach (var mutation in mutations)
        {
            bool refused = false;
            try
            {
                TerraformingReloaded.Patching.Rockets.CheckShape(m => mutation.Change(CecilBody(m)));
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            Console.WriteLine((refused ? "checked: rocket combustion refuses an engine that " : "FAILED:  rocket combustion accepted an engine that ") + mutation.What);
            if (!refused)
            {
                failures++;
            }
        }

        try
        {
            TerraformingReloaded.Patching.Rockets.Apply(new HarmonyLib.Harmony("patchcheck.rockets"));
            Console.WriteLine("applied: rocket combustion (the engine's one rate rewritten)");
        }
        catch (Exception e) when (e.ToString().Contains("ECall methods must be packaged"))
        {
            Console.WriteLine("bound:   rocket combustion (target found, one rate rewritten; compiling it needs Unity)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  rocket combustion: " + e.Message);
            failures++;
        }

        double offWorld = TerraformingReloaded.Patching.Rockets.CombustionRate();
        bool vanilla = offWorld.Equals(shipped) && offWorld.Equals(TerraformingReloaded.Patching.Rockets.ShippedRate);
        Console.WriteLine((vanilla ? "checked: " : "FAILED:  ") + "with no world running an engine burns at " + offWorld.ToString("R", CultureInfo.InvariantCulture)
            + ", the shipped rate, so the rated thrust worked out at load is the game's own");
        if (!vanilla)
        {
            failures++;
        }

        try
        {
            failures += RocketBurns(shipped);
        }
        catch (Exception e) when (e.ToString().Contains("ECall methods must be packaged"))
        {
            Console.WriteLine("skipped: rocket burns (the game's combustion needs Unity here; LiveCheck -Rockets runs it)");
        }
        return failures;
    }

    /// <summary>
    /// The game's own combustion on a free chamber of propellant, at the shipped rate and
    /// at a complete burn: a 2:1 methane and oxygen mix, the 68 % methane mix the docs recommend, an
    /// oxygen-rich mix, and hydrazine on its own.
    /// </summary>
    private static int RocketBurns(double shipped)
    {
        int failures = 0;
        double least = Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble();
        double complete = TerraformingReloaded.Patching.Rockets.CompleteBurn;
        CultureInfo c = CultureInfo.InvariantCulture;
        const Chemistry.GasType Ch4 = Chemistry.GasType.Methane;
        const Chemistry.GasType O2 = Chemistry.GasType.Oxygen;
        const Chemistry.GasType N2h4 = Chemistry.GasType.Hydrazine;

        foreach (double methane in new[] { 2.0 / 3.0, 0.68 })
        {
            double oxygen = 10.0 * (1.0 - methane);
            Dictionary<Chemistry.GasType, double> on = Burn(complete, (Ch4, 10.0 * methane), (O2, oxygen));
            Dictionary<Chemistry.GasType, double> off = Burn(shipped, (Ch4, 10.0 * methane), (O2, oxygen));
            double expected = oxygen * (1.0 - shipped);
            failures += Judge(on[O2] < least && Math.Abs(off[O2] - expected) < 1e-9, string.Format(c,
                "{0:0.###} methane premix, 10 mol: oxygen left {1:G4} mol burning completely, {2:G4} mol as shipped (expected {3:G4})",
                methane, on[O2], off[O2], expected));
        }

        Dictionary<Chemistry.GasType, double> rich = Burn(complete, (Ch4, 5.0), (O2, 5.0));
        failures += Judge(rich[Ch4] < least && Math.Abs(rich[O2] - 2.5) < 1e-9, string.Format(c,
            "half methane, half oxygen, 10 mol, burning completely: methane left {0:G4} mol, oxygen left {1:G6} mol (the 2.5 mol excess)", rich[Ch4], rich[O2]));

        Dictionary<Chemistry.GasType, double> hydrazineOn = Burn(complete, (N2h4, 10.0));
        Dictionary<Chemistry.GasType, double> hydrazineOff = Burn(shipped, (N2h4, 10.0));
        failures += Judge(hydrazineOn[N2h4] < least && Math.Abs(hydrazineOff[N2h4] - 10.0 * (1.0 - shipped)) < 1e-9, string.Format(c,
            "hydrazine, 10 mol: left {0:G4} mol burning completely, {1:G4} mol as shipped", hydrazineOn[N2h4], hydrazineOff[N2h4]));
        return failures;
    }

    /// <summary>
    /// The planet's air burns. The shape check must find every copy of the planet's air going
    /// through the one method the hold-back watches, and refuse a game with one more; the patch must
    /// go in (or bind, where patching the mixing needs Unity); the rule must pass its own check on the
    /// game's types (Appendix H, cases 1 to 20); and each of twelve broken rules must fail it.
    /// </summary>
    private static int CheckPlanetFire()
    {
        int failures = 0;
        try
        {
            TerraformingReloaded.Patching.PlanetCombustion.CheckShape(CecilCalls);
            Console.WriteLine("checked: planet fire (the exchange take, a new cell, the mixing take and the read-only copy each copy the planet's air once, through GasMixtureHelper.Create)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  planet fire, shape: " + e.Message);
            failures++;
        }
        bool refused = false;
        try
        {
            TerraformingReloaded.Patching.PlanetCombustion.CheckShape((m, t) => CecilCalls(m, t) + (m.Name == "TakeGlobalGasMix" ? 1 : 0));
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        failures += Judge(refused, "planet fire refuses a game whose exchange take copies the planet's air twice");

        // The install runs the shipped rule's own check first, and the shipped rule calls the game's
        // rate, which needs Unity's mscorlib; patching the mixing for the spark needs Unity's runtime
        // too. So the install is expected to stop on one of those here, and the hold-back postfix is
        // bound to its target on its own to prove it fits.
        try
        {
            TerraformingReloaded.Patching.PlanetCombustion.Apply(new HarmonyLib.Harmony("patchcheck.planetfire"), CecilCalls);
            Console.WriteLine("applied: planet fire");
        }
        catch (Exception e) when (e.ToString().Contains("ECall methods must be packaged") || e.ToString().Contains("System.Span") || e.ToString().Contains("System.Math.Clamp"))
        {
            Console.WriteLine("bound:   planet fire install (stops on what needs Unity's runtime: " + (e.ToString().Contains("Math.Clamp") ? "the game's rate, Math.Clamp" : "the mixing, a Span") + ")");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  planet fire: " + e.Message);
            failures++;
        }
        try
        {
            MethodInfo create = TerraformingReloaded.Patching.PlanetCombustion.CheckShape(CecilCalls);
            new HarmonyLib.Harmony("patchcheck.planetfire.holdback").Patch(create,
                postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.DeclaredMethod(typeof(TerraformingReloaded.Patching.PlanetCombustion), "HoldBackPostfix")));
            Console.WriteLine("applied: planet fire hold-back (postfix on GasMixtureHelper.Create(GlobalGasMix, MatterState))");
        }
        catch (Exception e) when (e.ToString().Contains("ECall methods must be packaged"))
        {
            Console.WriteLine("bound:   planet fire hold-back (target found, postfix fits; compiling it needs Unity)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  planet fire hold-back: " + e.Message);
            failures++;
        }

        // Atmosphere.GetCombustionMultiplierCurved calls Math.Clamp, which this runtime's mscorlib
        // lacks, so the game's own rate cannot run here. Its constants are read off its IL instead,
        // and the check runs with the formula written out in its place; in the game the load-time
        // check runs the shipped rule, which calls the game's method, against the same formula.
        failures += CheckRateConstants();
        string shipped = TerraformingReloaded.Patching.PlanetCombustionCheck.CheckArithmetic(new OfflineRule());
        failures += Judge(shipped == null, "planet fire, Appendix H cases 1 to 20: " + (shipped ?? "gate, rate, products, heat, hold-back, margin, spark, self-sustain, limit, hydrazine, nitrous oxide, liquids, self-test, gathering, armed hold-back, both hold settings, trace hold and the self-test's count all hold"));
        failures += Judge(!TerraformingReloaded.Settings.PlanetAirBurns && !TerraformingReloaded.Settings.PlanetHoldsBackWhileIgnitable && !TerraformingReloaded.Settings.PlanetKeepsTraceGas,
            "the planet's fire, the armed hold-back and the trace hold all ship off");
        Console.WriteLine("info:    the game's hydrazine self-ignition point is "
            + TerraformingReloaded.Patching.PlanetCombustionCheck.HydrazineIgnitionKelvin.ToString("0.###", CultureInfo.InvariantCulture) + " K");

        PrintFireFigures();

        var mutants = new (string What, TerraformingReloaded.Patching.FireRule Rule)[]
        {
            ("a gate on the planet's totals", new GateOnTotals()),
            ("a fixed 573 K ignition point", new FixedIgnition()),
            ("a hold-back of one gas instead of a side", new OneGasHeld()),
            ("a hold-back while not burning", new HeldWhileUnlit()),
            ("fire heat held to the 50 K limit", new FireHeatLimited()),
            ("heat without the heat-capacity term", new NoCapacityTerm()),
            ("liquids written back", new LiquidsBurnt()),
            ("an armed hold-back that ignores the ignition gate", new ArmedWithGateClosed()),
            ("an armed hold-back without the 5 K margin", new ArmedWithoutMargin()),
            ("an armed hold-back that waits for an oxidiser to arrive", new ArmedWaitsForOxidiser()),
        };
        foreach (var mutant in mutants)
        {
            string caught = TerraformingReloaded.Patching.PlanetCombustionCheck.CheckArithmetic(mutant.Rule);
            failures += Judge(caught != null, "planet fire check fails " + mutant.What + (caught != null ? ": " + caught : ""));
        }
        var traceMutants = new (string What, TerraformingReloaded.Patching.TraceHoldRule Rule)[]
        {
            ("a trace test without the exchange rate", new TraceWithoutRate()),
            ("a trace hold that keeps everything", new TraceKeepsAll()),
        };
        foreach (var mutant in traceMutants)
        {
            string caught = TerraformingReloaded.Patching.PlanetCombustionCheck.CheckArithmetic(new OfflineRule(), mutant.Rule);
            failures += Judge(caught != null, "planet fire check fails " + mutant.What + (caught != null ? ": " + caught : ""));
        }
        return failures;
    }

    /// <summary>
    /// The figures docs/PLANET-COMBUSTION.md quotes, worked out by the rule on the game's types and
    /// printed so the document can be checked against them: the rise from oxygen burnt on a Vulcan
    /// of 250,000 cells starting at 1,000 K and warming with its own heat, booked as the game books a burnt cell, and the heat ten sparked
    /// ticks add to Mars. The heat capacity is the tank's alone, as on a world with empty clouds and caps.
    /// </summary>
    private static void PrintFireFigures()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        foreach (double oxygen in new[] { 5000.0, 3.0e6 })
        {
            Assets.Scripts.GlobalGasMix tank = FireWorld(2.0e9, (Chemistry.GasType.CarbonDioxide, 12.0), (Chemistry.GasType.Methane, 27.0),
                (Chemistry.GasType.Hydrogen, 3.0), (Chemistry.GasType.Pollutant, 15.0));
            tank.Set(new MoleQuantity(oxygen), Chemistry.GasType.Oxygen);
            double c0 = tank.GetHeatCapacity().ToDouble();
            double energy = 0.0, added = 0.0;
            int ticks = 0;
            TerraformingReloaded.Patching.FireTick previous = TerraformingReloaded.Patching.NothingToBurn.Instance;
            var heats = new TerraformingReloaded.Patching.StoredHeats(0.0, 0.0, 0.0);
            while (true)
            {
                double capacity = tank.GetHeatCapacity().ToDouble();
                double kelvin = 1000.0 + heats.External / capacity;
                previous = new OfflineRule().Step(tank, new TerraformingReloaded.Patching.FireConditions(kelvin, kelvin, capacity, false), previous, heats);
                if (!(previous is TerraformingReloaded.Patching.Burning burning))
                {
                    break;
                }
                heats = burning.Heats;
                energy += burning.Burnt.Energy;
                added += burning.Added;
                ticks++;
            }
            double c1 = tank.GetHeatCapacity().ToDouble();
            Console.WriteLine(string.Format(c, "info:    {0:N0} mol of oxygen on a Vulcan of 250,000 cells from 1,000 K: {1} ticks ({2:0.#} s), heat of combustion {3:0.###} GJ, heat capacity {4:0.0} to {5:0.0} MJ/K, stored heat now {6:0.#} K booked as the game books a cell ({7:0.#} K unnetted over the heat capacity after)",
                oxygen, ticks, ticks * 0.5, energy / 1e9, c0 / 1e6, c1 / 1e6, heats.External / c1, energy / c1));
        }
        {
            Assets.Scripts.GlobalGasMix mars = FireWorld(2.0e9, (Chemistry.GasType.CarbonDioxide, 8.66), (Chemistry.GasType.Nitrogen, 0.27),
                (Chemistry.GasType.Oxygen, 0.131), (Chemistry.GasType.Pollutant, 0.058));
            mars.Set(new MoleQuantity(1000.0), Chemistry.GasType.Methane);
            double added = 0.0;
            double capacity = mars.GetHeatCapacity().ToDouble();
            for (int i = 0; i < 10; i++)
            {
                var tick = (TerraformingReloaded.Patching.Burning)new OfflineRule().Step(mars, new TerraformingReloaded.Patching.FireConditions(291.0, 291.0, capacity, true),
                    TerraformingReloaded.Patching.NothingToBurn.Instance, new TerraformingReloaded.Patching.StoredHeats(0.0, 0.0, 0.0));
                added += tick.Added;
            }
            Console.WriteLine(string.Format(c, "info:    Mars with 1,000 mol of methane, sparked for 10 ticks at 291 K: {0:0.#} mol of methane left, {1:0.###} K added ({2:0.0} MJ/K)",
                mars.Get(Chemistry.GasType.Methane).ToDouble(), added / capacity, capacity / 1e6));
        }
    }

    private static Assets.Scripts.GlobalGasMix FireWorld(double litres, params (Chemistry.GasType Type, double PerCell)[] air)
    {
        var tank = new Assets.Scripts.GlobalGasMix(new VolumeLitres(litres));
        foreach (var gas in air)
        {
            tank.Set(new MoleQuantity(gas.PerCell * litres / 8000.0), gas.Type);
        }
        return tank;
    }

    /// <summary>
    /// The game's combustion rate constants, read off Atmosphere.GetCombustionMultiplierCurved's IL:
    /// the formula <see cref="OfflineRule"/> writes out must be the one the game ships.
    /// </summary>
    private static int CheckRateConstants()
    {
        MethodInfo curved = HarmonyLib.AccessTools.DeclaredMethod(typeof(Atmosphere), "GetCombustionMultiplierCurved", Type.EmptyTypes);
        HashSet<double> constants = new HashSet<double>(CecilBody(curved).Where(i => i.Value is double).Select(i => (double)i.Value));
        double[] expected = { 0.1, 0.05, 1.0, 0.002, 1.6, 0.0025, 1.01, 5.0, 0.0 };
        double[] missing = expected.Where(e => !constants.Contains(e)).ToArray();
        return Judge(missing.Length == 0, "the game's combustion rate still uses the constants the planet fire's offline check writes out"
            + (missing.Length > 0 ? "; missing " + string.Join(", ", missing.Select(m => m.ToString("R", CultureInfo.InvariantCulture))) : ""));
    }

    /// <summary>
    /// The shipped rule with the game's rate written out (docs/PLANET-COMBUSTION.md, Decisions): the
    /// curve on the temperature plus 273.15, its nitrous oxide or ozone branch above a tenth of the
    /// oxidiser, and all of it when either side is under the game's 0.0003 mol.
    /// </summary>
    private class OfflineRule : TerraformingReloaded.Patching.FireRule
    {
        public override double Rate(GasMixture sample)
        {
            double least = AtmosphereHelper.MinimumMolesForProcessing.ToDouble();
            double fuel = sample.TotalFuel.ToDouble();
            double oxidiser = sample.TotalOxidiser.ToDouble();
            if ((fuel < least || oxidiser < least) && sample.TotalHypergolics.ToDouble() < least)
            {
                return 1.0;
            }
            double nitrous = (sample.NitrousOxide.Quantity + sample.LiquidNitrousOxide.Quantity).ToDouble() / oxidiser;
            double ozone = (sample.Ozone.Quantity + sample.LiquidOzone.Quantity).ToDouble() / oxidiser;
            double kelvin = sample.Temperature.ToDouble() + 273.15;
            double share = nitrous > 0.1 || ozone > 0.1
                ? 0.05 + 1.0 / Math.Pow(0.0025 * kelvin, 1.01)
                : 0.05 + 1.0 / Math.Pow(0.002 * kelvin, 1.6);
            return Math.Max(0.0, Math.Min(1.0, share)) / 5.0;
        }
    }

    private sealed class GateOnTotals : OfflineRule
    {
        public override GasMixture Sample(Assets.Scripts.GlobalGasMix tank, double kelvin) => Copy(tank, 1.0, kelvin);
    }

    private sealed class FixedIgnition : OfflineRule
    {
        public override bool LitByHeat(GasMixture sample) => sample.Temperature.ToDouble() > 573.15;
    }

    private sealed class OneGasHeld : OfflineRule
    {
        public override TerraformingReloaded.Patching.HeldGases ScarceSide(GasMixture sample)
        {
            TerraformingReloaded.Patching.HeldGases side = base.ScarceSide(sample);
            foreach (Chemistry.GasType type in TerraformingReloaded.Patching.FireGases.Burnable)
            {
                if (side.Contains(type) && sample.GetMoleValue(type).Quantity.ToDouble() > 0.0)
                {
                    return TerraformingReloaded.Patching.HeldGases.Of(new[] { type });
                }
            }
            return side;
        }
    }

    private sealed class HeldWhileUnlit : OfflineRule
    {
        public override TerraformingReloaded.Patching.HeldGases HeldBack(TerraformingReloaded.Patching.FireTick tick)
            => tick is TerraformingReloaded.Patching.Unlit unlit ? unlit.Scarce : base.HeldBack(tick);
    }

    private sealed class FireHeatLimited : OfflineRule
    {
        public override TerraformingReloaded.Patching.StoredHeats Settle(TerraformingReloaded.Patching.StoredHeats heats, double capacity, double? halfLifeMinutes, double limitKelvin, double tickSeconds)
        {
            TerraformingReloaded.Patching.StoredHeats settled = base.Settle(new TerraformingReloaded.Patching.StoredHeats(heats.Latent, heats.External, 0.0), capacity, halfLifeMinutes, limitKelvin, tickSeconds);
            return new TerraformingReloaded.Patching.StoredHeats(settled.Latent, settled.External, Math.Min(heats.Combustion, settled.External));
        }
    }

    private sealed class NoCapacityTerm : OfflineRule
    {
        public override double NetHeat(TerraformingReloaded.Patching.Burnt burnt, double kelvin) => burnt.Energy;
    }

    /// <summary>The whole tank, liquids too, through the game's burn and written back.</summary>
    private sealed class LiquidsBurnt : OfflineRule
    {
        public override TerraformingReloaded.Patching.Burnt Burn(Assets.Scripts.GlobalGasMix tank, double rate, double kelvin)
        {
            var types = ((Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
                .Where(t => t != Chemistry.GasType.Undefined && Mole.MatterState(t) != AtmosphereHelper.MatterState.None).ToArray();
            GasMixture whole = GasMixtureHelper.Create();
            foreach (Chemistry.GasType type in types)
            {
                whole.SetMoleValue(type, tank.Get(type), MoleEnergy.Zero);
            }
            whole.TotalEnergy = new MoleEnergy(whole.HeatCapacity, new TemperatureKelvin(kelvin));
            double capacity = tank.GetHeatCapacity().ToDouble();
            double energy = whole.Combust(rate, out _, out _).ToDouble();
            foreach (Chemistry.GasType type in types)
            {
                tank.Set(whole.GetMoleValue(type).Quantity, type);
            }
            return new TerraformingReloaded.Patching.Burnt(energy, tank.GetHeatCapacity().ToDouble() - capacity, 0.0, 0.0, 0.0);
        }
    }

    private sealed class ArmedWithGateClosed : OfflineRule
    {
        public override TerraformingReloaded.Patching.HeldGases Armed(Assets.Scripts.GlobalGasMix tank, double peakKelvin) => SideToHold(Sample(tank, peakKelvin));
    }

    private sealed class ArmedWithoutMargin : OfflineRule
    {
        public override TerraformingReloaded.Patching.HeldGases Armed(Assets.Scripts.GlobalGasMix tank, double peakKelvin)
        {
            GasMixture gate = Sample(tank, peakKelvin);
            return LitByHeat(gate) ? SideToHold(gate) : TerraformingReloaded.Patching.HeldGases.None;
        }
    }

    private sealed class ArmedWaitsForOxidiser : OfflineRule
    {
        public override TerraformingReloaded.Patching.HeldGases SideToHold(GasMixture sample) => ScarceSide(sample);
    }

    private sealed class TraceWithoutRate : TerraformingReloaded.Patching.TraceHoldRule
    {
        public override bool Deleted(double share, double exchangeRate) => share < Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble();
    }

    private sealed class TraceKeepsAll : TerraformingReloaded.Patching.TraceHoldRule
    {
        public override bool Deleted(double share, double exchangeRate) => true;
    }

    /// <summary>
    /// A comet's rules (docs/COMET.md sections 2, 3, 5 and 8) by their own check, then wrong rules the
    /// check must catch; its reactions by worked cases; and the darker sky's two postfixes bound to
    /// their targets, which need Unity to compile.
    /// </summary>
    private static int CheckComets()
    {
        int failures = 0;
        string rules = TerraformingReloaded.Comets.CometRules.Check(TerraformingReloaded.Comets.CometRules.Shipped);
        failures += Judge(rules == null, "comet rules: " + (rules ?? "the safe distance, the wave, the push bands and the darker sky hold docs/COMET.md's tables"));
        var wrongRules = new (string What, TerraformingReloaded.Comets.CometRules Rules)[]
        {
            ("a wave that falls with the distance rather than its square", new LinearWave()),
            ("a wave that goes on growing past a billion moles", new UncappedWave()),
            ("a safe distance 500 m short", new ShortSafeDistance()),
            ("a hard push past the game's 20 m/s", new OverspeedPush()),
            ("suit damage that can knock a player out", new KnockoutPush()),
            ("a gentle push with no upward part", new FlatPush()),
            ("a sky that never clears", new StuckSky()),
        };
        foreach (var wrong in wrongRules)
        {
            string caught = TerraformingReloaded.Comets.CometRules.Check(wrong.Rules);
            failures += Judge(caught != null, "comet rules check fails " + wrong.What + (caught != null ? ": " + caught : ""));
        }
        string ground = TerraformingReloaded.Comets.CometGround.Check(TerraformingReloaded.Comets.CometGround.Shipped);
        failures += Judge(ground == null, "comet ground: " + (ground ?? "the impact's height from the voxels, the world's edge outside them, and the curved ground's drop as measured on Mars"));
        var wrongGround = new (string What, TerraformingReloaded.Comets.CometGround Ground)[]
        {
            ("a column read only every 8 m", new StridedGround()),
            ("the ground at the bottom of its top voxel", new GroundOneLow()),
            ("the lowest ground in a column rather than the highest", new LowestGround()),
            ("a point outside the voxel world read where it is", new UnclampedGround()),
            ("a drop that starts at the viewer", new DropFromViewer()),
            ("a drop that grows with the distance rather than its square", new LinearDrop()),
            ("a drop that leaves out the orbital view's lift", new DropWithoutLift()),
            ("a drop taken from the point's place before recentring", new DropNotRecentred()),
        };
        foreach (var wrong in wrongGround)
        {
            string caught = TerraformingReloaded.Comets.CometGround.Check(wrong.Ground);
            failures += Judge(caught != null, "comet ground check fails " + wrong.What + (caught != null ? ": " + caught : ""));
        }
        string reactions = TerraformingReloaded.Comets.CometReactions.Check();
        failures += Judge(reactions == null, "comet reactions: " + (reactions ?? "steam, burning, dry ice, toxic and ionised each as strong as their share, fuel burning only with an oxidiser"));
        try
        {
            TerraformingReloaded.Patching.CometSky.Apply(new HarmonyLib.Harmony("patchcheck.cometsky"));
            Console.WriteLine("applied: comet sky");
        }
        catch (Exception e) when (e.ToString().Contains("ECall methods must be packaged"))
        {
            Console.WriteLine("bound:   comet sky (both targets found, every value it scales is there; compiling it needs Unity)");
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED:  comet sky: " + e.Message);
            failures++;
        }
        return failures;
    }

    private sealed class StridedGround : TerraformingReloaded.Comets.CometGround
    {
        public override double? Surface(Func<int, double> density, int top)
        {
            for (int y = top; y >= 0; y -= Stride)
            {
                if (density(y) >= SolidDensity)
                {
                    return y + 1;
                }
            }
            return null;
        }
    }

    private sealed class GroundOneLow : TerraformingReloaded.Comets.CometGround
    {
        public override double? Surface(Func<int, double> density, int top) => base.Surface(density, top) - 1.0;
    }

    private sealed class LowestGround : TerraformingReloaded.Comets.CometGround
    {
        public override double? Surface(Func<int, double> density, int top)
        {
            for (int y = 0; y <= top; y++)
            {
                if (density(y) < SolidDensity)
                {
                    return y == 0 ? (double?)null : y;
                }
            }
            return top + 1;
        }
    }

    private sealed class UnclampedGround : TerraformingReloaded.Comets.CometGround
    {
        public override Column ColumnFor(double x, double z, int halfWidth) => new Column(x, z, base.ColumnFor(x, z, halfWidth).Inside);
    }

    private sealed class DropFromViewer : TerraformingReloaded.Comets.CometGround
    {
        public override double Drop(double metres, Curve curve) =>
            base.Drop(metres + curve.Start, curve);
    }

    private sealed class LinearDrop : TerraformingReloaded.Comets.CometGround
    {
        public override double Drop(double metres, Curve curve) =>
            Math.Max(0.0, metres - curve.Start) * curve.Amount * 0.6 - curve.Lift;
    }

    private sealed class DropWithoutLift : TerraformingReloaded.Comets.CometGround
    {
        public override double Drop(double metres, Curve curve) => base.Drop(metres, curve) + curve.Lift;
    }

    private sealed class DropNotRecentred : TerraformingReloaded.Comets.CometGround
    {
        public override double DropAt(double x, double z, double viewerX, double viewerZ, Curve curve) =>
            Drop(Math.Sqrt((x - viewerX) * (x - viewerX) + (z - viewerZ) * (z - viewerZ)), curve);
    }

    private sealed class LinearWave : TerraformingReloaded.Comets.CometRules
    {
        public override double PeakKPa(double moles, double metres) => base.PeakKPa(moles, metres) * Math.Max(100.0, metres) / ReferenceMetres;
    }

    private sealed class UncappedWave : TerraformingReloaded.Comets.CometRules
    {
        public override double PeakKPa(double moles, double metres)
        {
            double ratio = ReferenceMetres / Math.Max(NearestMetres, metres);
            return ReferencePeakKPa * Math.Pow(moles / ReferenceMoles, 2.0 / 3.0) * ratio * ratio;
        }
    }

    private sealed class ShortSafeDistance : TerraformingReloaded.Comets.CometRules
    {
        public override double SafeDistance(double scale) => base.SafeDistance(scale) - 500.0;
    }

    private sealed class OverspeedPush : TerraformingReloaded.Comets.CometRules
    {
        public override Push PushFor(double peakKPa)
        {
            Push push = base.PushFor(peakKPa);
            return push.Band == 3 ? new Push(20.0, push.Up, push.SuitImpulse, 3) : push;
        }
    }

    private sealed class KnockoutPush : TerraformingReloaded.Comets.CometRules
    {
        public override Push PushFor(double peakKPa)
        {
            Push push = base.PushFor(peakKPa);
            return push.Band == 3 ? new Push(push.Away, push.Up, push.SuitImpulse * 2.0, 3) : push;
        }
    }

    private sealed class FlatPush : TerraformingReloaded.Comets.CometRules
    {
        public override Push PushFor(double peakKPa)
        {
            Push push = base.PushFor(peakKPa);
            return push.Band == 1 ? new Push(push.Away, 0.0, 0.0, 1) : push;
        }
    }

    private sealed class StuckSky : TerraformingReloaded.Comets.CometRules
    {
        public override double SkyDim(double scale, double secondsSinceImpact) => base.SkyDim(scale, Math.Min(secondsSinceImpact, SkyFallSeconds));
    }

    /// <summary>
    /// The Celestial terraforming rules (docs/COMET-ROCKETS.md) by their own check, then wrong rules the
    /// check must catch, then the game's own figures the doc's worked examples rest on, read from the
    /// installed game: the molar masses of the engines' premix and of water, and the methane to oxygen
    /// ratio the game burns.
    /// </summary>
    private static int CheckCelestial()
    {
        int failures = 0;
        string table = TerraformingReloaded.Comets.CelestialSettings.CheckTable();
        failures += Judge(table == null, "celestial settings: " + (table ?? "each at its place, every key once, every default in its own range"));
        string rules = TerraformingReloaded.Comets.CelestialRules.Check(TerraformingReloaded.Comets.CelestialRules.Shipped);
        failures += Judge(rules == null, "celestial rules: " + (rules ?? "flight against the game's ticks, a comet's place along a route over time and where it runs dry, the size multiplier against the planet's size, Cut, the sky rule, the reserve, the orbit, the zones, the losses, and every default's derivation"));
        var wrongRules = new (string What, TerraformingReloaded.Comets.CelestialRules Rules)[]
        {
            ("a flight that keeps the mass it started with", new ConstantMassFlight()),
            ("a flight that leaves the engines' mass out", new MasslessEngines()),
            ("a hop whose delta-v is its distance, not twice it", new UndoubledHop()),
            ("a difficulty that multiplies orbit connections too", new DifficultyEverywhere()),
            ("a comet that arrives however little fuel it has", new NeverStranded()),
            ("a Cut that grows past the size multiplier times the site", new UncappedCut()),
            ("a sky rule by the sine of the height, not the angle", new SineSky()),
            ("a sky rule with east and north swapped", new MirroredSky()),
            ("a disc the same on every curvature", new FlatDisc()),
            ("a reserve that steers by all its fuel, however short the fall", new UncappedReach()),
            ("a landing that overshoots the target by the rest of the reach", new OvershootLanding()),
            ("a landing allowed out of the disc", new UnclampedLanding()),
            ("an orbit period upside down", new InvertedOrbit()),
            ("passes judged by the zenith's height alone", new UntiltedPasses()),
            ("ground zero growing with the moles, not their cube root", new LinearGroundZero()),
            ("ground zero growing past where the wave stops", new UncappedGroundZero()),
            ("zones judged outermost first", new OutermostZone()),
            ("own fuel burnt one to one", new OneToOneFuel()),
            ("a mineral comet that takes more than the planet has", new UnclampedMineral()),
            ("a flight along a route whose mass does not fall as it burns", new ConstantMassProgress()),
            ("a comet that goes on gaining speed once its fuel is gone", new BurnsPastItsFuel()),
            ("progress through a hop counted over the whole route", new WholeRouteProgress()),
            ("a comet that runs dry and is still put at the end of its route", new StrandedAtTheEnd()),
            ("a size multiplier that ignores the planet's size", new FixedSizeMultiplier()),
            ("a size multiplier that falls under 1 on a small planet", new UnflooredSizeMultiplier()),
        };
        foreach (var wrong in wrongRules)
        {
            string caught = TerraformingReloaded.Comets.CelestialRules.Check(wrong.Rules);
            failures += Judge(caught != null, "celestial rules check fails " + wrong.What + (caught != null ? ": " + caught : ""));
        }

        // The game's figures under the doc's examples, from the installed game.
        double methane = Mole.MolarMass(Chemistry.GasType.Methane);
        double oxygen = Mole.MolarMass(Chemistry.GasType.Oxygen);
        double premix = TerraformingReloaded.Comets.CelestialRules.PremixKgPerMole(methane, oxygen);
        failures += Judge(Math.Abs(premix - 0.016) < 1e-12,
            string.Format(CultureInfo.InvariantCulture, "the engines' 2:1 premix weighs {0:0.######} kg/mol in the game (methane {1}, oxygen {2} g/mol), as the worked example takes", premix, methane, oxygen));
        double ratio = Combustion.ResultMethaneOxygen.FuelRatio.ToDouble();
        failures += Judge(ratio == TerraformingReloaded.Comets.CelestialRules.MethanePerOxygen,
            "the game burns " + ratio.ToString(CultureInfo.InvariantCulture) + " methane to one oxygen, as a comet feeding its own engines does");
        TerraformingReloaded.Comets.Flight example = TerraformingReloaded.Comets.CelestialRules.Shipped.Fly(18000.0, 2, 16092.0, 383.0, premix);
        failures += Judge(example.Arrives && Math.Abs(example.Seconds - 223.5) < 0.05 && Math.Abs(example.FuelMoles - 16091.7) < 1.0,
            string.Format(CultureInfo.InvariantCulture, "an 18 t comet with two engines flies Mars Orbit to Frozen Vapors (383 delta-v) in {0:0.0} s on {1:N0} mol of the game's premix", example.Seconds, example.FuelMoles));
        double water = Mole.MolarMass(Chemistry.GasType.Water);
        double tonnes = TerraformingReloaded.Comets.CelestialRules.Shipped.MassKg(new[] { 2.3e6 }, new[] { water }) / 1000.0;
        failures += Judge(Math.Abs(tonnes - 248.4) < 0.05,
            string.Format(CultureInfo.InvariantCulture, "a comet of 2.3 million moles of water weighs {0:0.#} t at the game's {1} g/mol", tonnes, water));
        return failures;
    }

    private sealed class ConstantMassFlight : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.Flight Fly(double cometKg, int engines, double fuelMoles, double routeDeltaV, double fuelKgPerMole)
        {
            TerraformingReloaded.Comets.Flight real = base.Fly(cometKg, engines, fuelMoles, routeDeltaV, fuelKgPerMole);
            if (engines <= 0 || !(fuelMoles > 0.0))
            {
                return real;
            }
            double mass = cometKg + EngineMassKg * engines + fuelMoles * fuelKgPerMole;
            double accel = engines * EngineThrustNewtons / mass;
            double flow = engines * EngineMolesPerSecond;
            double seconds = routeDeltaV / accel;
            if (seconds * flow <= fuelMoles)
            {
                return new TerraformingReloaded.Comets.Flight(seconds, seconds * flow, routeDeltaV, 1.0, true);
            }
            double reached = accel * fuelMoles / flow;
            return new TerraformingReloaded.Comets.Flight(fuelMoles / flow, fuelMoles, reached, reached / routeDeltaV, false);
        }
    }

    private sealed class MasslessEngines : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.Flight Fly(double cometKg, int engines, double fuelMoles, double routeDeltaV, double fuelKgPerMole) =>
            base.Fly(cometKg - EngineMassKg * engines, engines, fuelMoles, routeDeltaV, fuelKgPerMole);
    }

    private sealed class UndoubledHop : TerraformingReloaded.Comets.CelestialRules
    {
        public override double HopDeltaV(double mapDistance, bool standardConnection, double difficultyMultiplier) =>
            base.HopDeltaV(mapDistance, standardConnection, difficultyMultiplier) / 2.0;
    }

    private sealed class DifficultyEverywhere : TerraformingReloaded.Comets.CelestialRules
    {
        public override double HopDeltaV(double mapDistance, bool standardConnection, double difficultyMultiplier) =>
            base.HopDeltaV(mapDistance, true, difficultyMultiplier);
    }

    private sealed class NeverStranded : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.Flight Fly(double cometKg, int engines, double fuelMoles, double routeDeltaV, double fuelKgPerMole)
        {
            TerraformingReloaded.Comets.Flight real = base.Fly(cometKg, engines, fuelMoles, routeDeltaV, fuelKgPerMole);
            return real.Arrives || engines <= 0 || !(fuelMoles > 0.0) ? real : new TerraformingReloaded.Comets.Flight(real.Seconds, real.FuelMoles, routeDeltaV, 1.0, true);
        }
    }

    private sealed class UncappedCut : TerraformingReloaded.Comets.CelestialRules
    {
        public override double CometMoles(double siteMoles, double cycles, double sharePerCycle, double sizeMultiplier) =>
            sizeMultiplier * siteMoles * cycles * sharePerCycle;
    }

    private sealed class SineSky : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.GroundPoint SkyPoint(double padX, double padZ, double dirX, double dirY, double dirZ, double radius)
        {
            double length = Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
            double flat = Math.Sqrt(dirX * dirX + dirZ * dirZ);
            double distance = radius * (1.0 - dirY / length) / 2.0;
            return flat > 1e-12 * length
                ? new TerraformingReloaded.Comets.GroundPoint(padX + dirX / flat * distance, padZ + dirZ / flat * distance)
                : new TerraformingReloaded.Comets.GroundPoint(padX, padZ + distance);
        }
    }

    private sealed class MirroredSky : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.GroundPoint SkyPoint(double padX, double padZ, double dirX, double dirY, double dirZ, double radius) =>
            base.SkyPoint(padX, padZ, dirZ, dirY, dirX, radius);
    }

    private sealed class FlatDisc : TerraformingReloaded.Comets.CelestialRules
    {
        public override double DiscRadius(double settingMetres, double curvature) => settingMetres;
    }

    private sealed class UncappedReach : TerraformingReloaded.Comets.CelestialRules
    {
        public override double ReachMetres(double cometKg, int engines, double fuelMoles, double metresPerDeltaV, double windowSeconds, double fuelKgPerMole) =>
            base.ReachMetres(cometKg, engines, fuelMoles, metresPerDeltaV, 1e9, fuelKgPerMole);
    }

    private sealed class OvershootLanding : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.GroundPoint Landing(TerraformingReloaded.Comets.GroundPoint sky, TerraformingReloaded.Comets.GroundPoint target, double reachMetres, double padX, double padZ, double radius)
        {
            double dx = target.X - sky.X;
            double dz = target.Z - sky.Z;
            double gap = Math.Sqrt(dx * dx + dz * dz);
            TerraformingReloaded.Comets.GroundPoint far = gap > 0.0
                ? new TerraformingReloaded.Comets.GroundPoint(sky.X + dx / gap * reachMetres, sky.Z + dz / gap * reachMetres)
                : sky;
            return base.Landing(far, far, 0.0, padX, padZ, radius);
        }
    }

    private sealed class UnclampedLanding : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.GroundPoint Landing(TerraformingReloaded.Comets.GroundPoint sky, TerraformingReloaded.Comets.GroundPoint target, double reachMetres, double padX, double padZ, double radius) =>
            base.Landing(sky, target, reachMetres, padX, padZ, 1e12);
    }

    private sealed class InvertedOrbit : TerraformingReloaded.Comets.CelestialRules
    {
        public override double OrbitPeriodDays(double orbitSeconds, double playerPeriodDays, double timeScale) =>
            360.0 / (orbitSeconds * playerPeriodDays * timeScale);
    }

    private sealed class UntiltedPasses : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.Pass ClosestPass(double latitudeDegrees, double inclinationDegrees) =>
            new TerraformingReloaded.Comets.Pass(Math.Abs(latitudeDegrees) / 180.0, Math.Abs(latitudeDegrees) / 180.0);
    }

    private sealed class LinearGroundZero : TerraformingReloaded.Comets.CelestialRules
    {
        public override double GroundZeroMetres(double moles, double metresAt1e6) =>
            metresAt1e6 * Math.Min(Math.Max(0.0, moles), TerraformingReloaded.Comets.CometRules.WaveMolesCap) / ZoneReferenceMoles;
    }

    private sealed class UncappedGroundZero : TerraformingReloaded.Comets.CelestialRules
    {
        public override double GroundZeroMetres(double moles, double metresAt1e6) =>
            metresAt1e6 * Math.Pow(Math.Max(0.0, moles) / ZoneReferenceMoles, 1.0 / 3.0);
    }

    private sealed class OutermostZone : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.CometZone ZoneAt(double metres, double moles, TerraformingReloaded.Comets.CelestialTuning tuning)
        {
            double groundZero = GroundZeroMetres(moles, tuning.GroundZeroMetresAt1e6);
            if (metres <= groundZero * tuning.DebrisRadiusFactor)
            {
                return TerraformingReloaded.Comets.CometZone.Debris;
            }
            return TerraformingReloaded.Comets.CometZone.Wave;
        }
    }

    private sealed class OneToOneFuel : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.OwnFuel BurnOwnFuel(double methane, double oxygen, double fuelWanted)
        {
            double burnt = Math.Min(fuelWanted, 2.0 * Math.Min(methane, oxygen));
            return new TerraformingReloaded.Comets.OwnFuel(burnt / 2.0, burnt / 2.0);
        }
    }

    private sealed class UnclampedMineral : TerraformingReloaded.Comets.CelestialRules
    {
        public override double MineralRemovedMoles(double cometKg, double airKgPerKg, double planetMoles, double planetGramsPerMole) =>
            base.MineralRemovedMoles(cometKg, airKgPerKg, double.MaxValue, planetGramsPerMole);
    }

    private sealed class ConstantMassProgress : TerraformingReloaded.Comets.CelestialRules
    {
        public override double DeltaVAfter(double cometKg, int engines, double fuelMoles, double seconds, double fuelKgPerMole)
        {
            if (engines <= 0 || !(fuelMoles > 0.0) || !(seconds > 0.0))
            {
                return 0.0;
            }
            double mass = cometKg + EngineMassKg * engines + fuelMoles * fuelKgPerMole;
            return engines * EngineThrustNewtons / mass * Math.Min(seconds, fuelMoles / (engines * EngineMolesPerSecond));
        }
    }

    private sealed class BurnsPastItsFuel : TerraformingReloaded.Comets.CelestialRules
    {
        public override double DeltaVAfter(double cometKg, int engines, double fuelMoles, double seconds, double fuelKgPerMole)
        {
            double burnout = engines > 0 ? fuelMoles / (engines * EngineMolesPerSecond) : 0.0;
            double gained = base.DeltaVAfter(cometKg, engines, fuelMoles, seconds, fuelKgPerMole);
            return seconds > burnout && engines > 0 ? gained + engines * EngineThrustNewtons / (cometKg + EngineMassKg * engines) * (seconds - burnout) : gained;
        }

        public override TerraformingReloaded.Comets.RoutePosition PositionAt(double[] hopDeltaVs, double cometKg, int engines, double fuelMoles, double seconds, double fuelKgPerMole)
        {
            TerraformingReloaded.Comets.RoutePosition real = base.PositionAt(hopDeltaVs, cometKg, engines, fuelMoles, seconds, fuelKgPerMole);
            if (real.Arrived || !real.Settled)
            {
                return real;
            }
            double route = hopDeltaVs.Sum();
            double gained = Math.Min(route, DeltaVAfter(cometKg, engines, fuelMoles, seconds, fuelKgPerMole));
            return new TerraformingReloaded.Comets.RoutePosition(StopAt(hopDeltaVs, gained), gained, gained >= route, gained >= route, seconds, 0.0);
        }
    }

    private sealed class WholeRouteProgress : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.RoutePosition PositionAt(double[] hopDeltaVs, double cometKg, int engines, double fuelMoles, double seconds, double fuelKgPerMole)
        {
            TerraformingReloaded.Comets.RoutePosition real = base.PositionAt(hopDeltaVs, cometKg, engines, fuelMoles, seconds, fuelKgPerMole);
            TerraformingReloaded.Comets.Stop stop = new TerraformingReloaded.Comets.Stop(real.Stop.Hop, Math.Min(1.0, real.DeltaV / hopDeltaVs.Sum()));
            return new TerraformingReloaded.Comets.RoutePosition(stop, real.DeltaV, real.Arrived, real.Settled, real.BurnSeconds, real.FuelLeft);
        }
    }

    private sealed class StrandedAtTheEnd : TerraformingReloaded.Comets.CelestialRules
    {
        public override TerraformingReloaded.Comets.RoutePosition PositionAt(double[] hopDeltaVs, double cometKg, int engines, double fuelMoles, double seconds, double fuelKgPerMole)
        {
            TerraformingReloaded.Comets.RoutePosition real = base.PositionAt(hopDeltaVs, cometKg, engines, fuelMoles, seconds, fuelKgPerMole);
            return real.Settled && !real.Arrived
                ? new TerraformingReloaded.Comets.RoutePosition(new TerraformingReloaded.Comets.Stop(hopDeltaVs.Length - 1, 1.0), hopDeltaVs.Sum(), true, true, real.BurnSeconds, real.FuelLeft)
                : real;
        }
    }

    private sealed class FixedSizeMultiplier : TerraformingReloaded.Comets.CelestialRules
    {
        public override double SizeMultiplier(double standardMultiplier, double planetSize) => standardMultiplier;
    }

    private sealed class UnflooredSizeMultiplier : TerraformingReloaded.Comets.CelestialRules
    {
        public override double SizeMultiplier(double standardMultiplier, double planetSize) => standardMultiplier * planetSize / StandardPlanetSize;
    }

    private static int Judge(bool passed, string what)
    {
        Console.WriteLine((passed ? "checked: " : "FAILED:  ") + what);
        return passed ? 0 : 1;
    }

    /// <summary>
    /// A forced burn of a chamber holding <paramref name="propellant"/>. GasMixture.Combust is what
    /// Atmosphere.TryCombust runs once forced; TryCombust itself also records flame figures through
    /// the game's network manager, which cannot start outside Unity, so the mixture is burnt directly.
    /// </summary>
    private static Dictionary<Chemistry.GasType, double> Burn(double rate, params (Chemistry.GasType Type, double Moles)[] propellant)
    {
        GasMixture chamber = GasMixtureHelper.Create();
        TemperatureKelvin kelvin = new TemperatureKelvin(293.15);
        foreach ((Chemistry.GasType type, double moles) in propellant)
        {
            MoleQuantity quantity = new MoleQuantity(moles);
            chamber.Add(new Mole(type, quantity, IdealGas.Energy(kelvin, Mole.SpecificHeat(type), quantity)));
        }
        chamber.Combust(rate, out _, out _);
        Dictionary<Chemistry.GasType, double> left = new Dictionary<Chemistry.GasType, double>();
        foreach ((Chemistry.GasType type, double _) in propellant)
        {
            left[type] = chamber.GetMoleValue(type).Quantity.ToDouble();
        }
        return left;
    }

    private static MethodInfo TryCombustMethod()
    {
        return HarmonyLib.AccessTools.DeclaredMethod(typeof(Atmosphere), "TryCombust", new[] { typeof(double), typeof(bool) });
    }

    /// <summary>
    /// A method's instructions as the rule reads them, from the assembly file through Mono.Cecil:
    /// each opcode, a double constant as itself, and a call as the method it calls, resolved by its
    /// token in the same module one at a time. Anything else carries no operand.
    /// </summary>
    private static IList<KeyValuePair<OpCode, object>> CecilBody(MethodBase method)
    {
        string path = method.Module.FullyQualifiedName;
        if (!Modules.TryGetValue(path, out object read))
        {
            read = Mono.Cecil.ModuleDefinition.ReadModule(path);
            Modules[path] = read;
        }
        Mono.Cecil.MethodDefinition definition = ((Mono.Cecil.ModuleDefinition)read).LookupToken(method.MetadataToken) as Mono.Cecil.MethodDefinition;
        if (definition == null || !definition.HasBody)
        {
            throw new InvalidOperationException("Mono.Cecil did not find the body of " + method.DeclaringType + "." + method.Name);
        }
        Dictionary<short, OpCode> codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null))
            .GroupBy(code => code.Value)
            .ToDictionary(g => g.Key, g => g.First());
        List<KeyValuePair<OpCode, object>> body = new List<KeyValuePair<OpCode, object>>();
        foreach (Mono.Cecil.Cil.Instruction instruction in definition.Body.Instructions)
        {
            object operand = null;
            if (instruction.Operand is double constant)
            {
                operand = constant;
            }
            else if (instruction.Operand is Mono.Cecil.MethodReference called)
            {
                try
                {
                    operand = method.Module.ResolveMethod(called.MetadataToken.ToInt32());
                }
                catch (Exception)
                {
                    // Not resolvable on this runtime, so not the call the rule looks for.
                }
            }
            body.Add(new KeyValuePair<OpCode, object>(codes[instruction.OpCode.Value], operand));
        }
        return body;
    }

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string file = new AssemblyName(e.Name).Name + ".dll";
        foreach (string dir in _probe)
        {
            string path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                return Assembly.LoadFrom(path);
            }
        }
        return null;
    }
}
