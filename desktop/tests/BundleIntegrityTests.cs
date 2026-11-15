using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static class BundleIntegrityTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static string Fixture(string root, string name, bool extracted, params string[] omit)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        if (extracted) File.WriteAllText(Path.Combine(folder, "System.Private.CoreLib.dll"), "x");
        foreach (string assembly in BundleIntegrity.HandoffAssemblies)
            if (!omit.Contains(assembly + ".dll")) File.WriteAllText(Path.Combine(folder, assembly + ".dll"), "x");
        return folder;
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "remsup-bundle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Complete cache: nothing to repair, the real assemblies load.
            int repairs = 0;
            string complete = Fixture(root, "complete", true);
            Check(BundleIntegrity.Verify(complete, "app.exe", null, (_, _) => { repairs++; return true; }) == null && repairs == 0, "a complete cache was reported or repaired");

            // Pipes/Overlapped deleted by temp cleanup: one repair start of the same EXE restores them.
            string damaged = Fixture(root, "damaged", true, "System.IO.Pipes.dll", "System.Threading.Overlapped.dll");
            Check(BundleIntegrity.MissingFiles(damaged, BundleIntegrity.HandoffAssemblies).SequenceEqual(new[] { "System.IO.Pipes.dll", "System.Threading.Overlapped.dll" }), "missing files were not listed");
            string repairedExe = null;
            string healed = BundleIntegrity.Verify(damaged, "installed\\Remsup.exe", null, (exe, timeout) =>
            {
                repairs++; repairedExe = exe;
                Check(timeout > TimeSpan.Zero, "repair needs a bounded wait");
                foreach (string name in new[] { "System.IO.Pipes.dll", "System.Threading.Overlapped.dll" }) File.WriteAllText(Path.Combine(damaged, name), "x");
                return true;
            });
            Check(healed == null && repairs == 1 && repairedExe == "installed\\Remsup.exe", "a damaged cache was not repaired through the same EXE");

            // Repair cannot restore the file: refuse with the file name and the restart instruction.
            string broken = Fixture(root, "broken", true, "System.IO.Pipes.dll");
            string refused = BundleIntegrity.Verify(broken, "app.exe", null, (_, _) => false);
            Check(refused != null && refused.Contains("System.IO.Pipes.dll") && refused.Contains("Restart Remsup and retry"), "unrepairable cache was not refused clearly: " + refused);
            Check(refused == BundleIntegrity.Incomplete("System.IO.Pipes.dll"), "refusal text must come from one place");

            // Not a self-extracted layout (framework-dependent run, tests): no file check, no repair, real assemblies load.
            repairs = 0;
            string plain = Fixture(root, "plain", false, "System.IO.Pipes.dll");
            Check(BundleIntegrity.Verify(plain, "app.exe", null, (_, _) => { repairs++; return true; }) == null && repairs == 0, "a non-extracted layout must not be repaired");
            Check(!BundleIntegrity.IsExtractedBundle(plain) && BundleIntegrity.IsExtractedBundle(complete), "extracted layout detection is wrong");

            // A name that cannot be loaded is reported as its file; the real handoff list loads in this process.
            Check(BundleIntegrity.FirstUnloadable(new[] { "System.Linq", "Remsup.Missing.Assembly" }) == "Remsup.Missing.Assembly.dll", "unloadable assembly was not reported");
            Check(BundleIntegrity.FirstUnloadable(BundleIntegrity.HandoffAssemblies) == null && BundleIntegrity.WarmUp() == null, "handoff assemblies do not load");
            Check(!BundleIntegrity.Repair(Path.Combine(root, "missing.exe"), TimeSpan.FromSeconds(1)), "repair of a missing EXE must fail quietly");
            Console.WriteLine("Bundle cache verification, repair and refusal PASS");
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
