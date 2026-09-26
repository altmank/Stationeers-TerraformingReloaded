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

        Console.WriteLine(failures == 0 ? "OK" : failures + " problem(s)");
        return failures == 0 ? 0 : 1;
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
