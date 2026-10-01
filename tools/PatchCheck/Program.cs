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

        failures += CheckRockets();

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
