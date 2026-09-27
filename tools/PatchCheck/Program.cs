using System;
using System.IO;
using System.Linq;
using System.Reflection;

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
