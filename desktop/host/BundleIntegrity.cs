using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

// A self-contained single-file EXE extracts its managed and native dependencies to a cache (normally %TEMP%\.net\<exe>\<id>).
// Temp cleanup (Storage Sense, cleaners) can delete every cache file that the running process has not loaded yet, and the first
// lazy load afterwards fails with a FileNotFoundException that has an empty message (#137). Windows locks loaded images, so loading
// the update-handoff dependencies at start protects them. Verify() repairs or reports a damaged cache before an update changes anything.
internal static class BundleIntegrity
{
    internal const string VerifyArgument = "--verify-bundle";

    // Assemblies first used on the update path (pipe handoff, candidate launch, readiness wait, rollback, state files, checksums).
    // UpdateProcessTests fails when a real handoff still loads an assembly that is not listed here or already loaded at start.
    internal static readonly string[] HandoffAssemblies =
    {
        "System.IO.Pipes", "System.Threading.Overlapped", "System.Security.AccessControl", "System.Security.Principal.Windows", "System.Security.Claims",
        "System.Diagnostics.Process", "System.ComponentModel.Primitives", "System.Collections.Concurrent", "System.Linq",
        "System.Text.Json", "System.Text.RegularExpressions", "System.Diagnostics.Tracing", "System.Diagnostics.DiagnosticSource",
        "System.Net.Primitives", "System.Net.Security", "System.Net.Http", "System.Security.Cryptography",
        "System.Threading.ThreadPool", "System.Text.Encodings.Web", "System.Numerics.Vectors", "System.Reflection.Emit.Lightweight",
        "System.Reflection.Primitives", "System.IO.Pipelines"
    };

    static readonly TimeSpan RepairTimeout = TimeSpan.FromSeconds(30);

    // A self-extracted cache keeps System.Private.CoreLib.dll next to the app; a framework-dependent run (tests) does not.
    internal static bool IsExtractedBundle(string baseDirectory) =>
        !string.IsNullOrEmpty(baseDirectory) && File.Exists(Path.Combine(baseDirectory, "System.Private.CoreLib.dll"));

    internal static List<string> MissingFiles(string baseDirectory, IEnumerable<string> names) =>
        names.Select(name => name + ".dll").Where(file => !File.Exists(Path.Combine(baseDirectory, file))).ToList();

    // Loads every listed assembly and returns the first file name that cannot be loaded, or null.
    internal static string FirstUnloadable(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            try { Assembly.Load(new AssemblyName(name)); }
            catch (Exception failure) when (failure is FileNotFoundException || failure is FileLoadException || failure is BadImageFormatException)
            {
                return name + ".dll";
            }
        }
        return null;
    }

    // Start-up warm-up. A damaged cache is reported by Verify() when an update is attempted, so this never throws.
    internal static string WarmUp()
    {
        try { return FirstUnloadable(HandoffAssemblies); }
        catch { return null; }
    }

    // Starting the same EXE again makes the .NET host re-extract missing files into the same cache before any managed code runs.
    internal static bool Repair(string executablePath, TimeSpan timeout)
    {
        try
        {
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath)) return false;
            var start = new ProcessStartInfo(executablePath, VerifyArgument) { UseShellExecute = false, CreateNoWindow = true };
            using Process process = Process.Start(start);
            if (process == null) return false;
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    internal static string Incomplete(string file) =>
        "Remsup's temporary runtime files are incomplete (missing " + file + "). Restart Remsup and retry the update.";

    // Returns null when the update handoff can load everything it needs, otherwise a user-facing reason.
    // Optional parameters let tests substitute the cache folder, the EXE and the repair step.
    internal static string Verify(string baseDirectory = null, string executablePath = null, IEnumerable<string> names = null, Func<string, TimeSpan, bool> repair = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        names ??= HandoffAssemblies;
        repair ??= Repair;
        string[] list = names.ToArray();
        if (IsExtractedBundle(baseDirectory))
        {
            List<string> missing = MissingFiles(baseDirectory, list);
            if (missing.Count > 0)
            {
                repair(executablePath ?? Environment.ProcessPath, RepairTimeout);
                missing = MissingFiles(baseDirectory, list);
                if (missing.Count > 0) return Incomplete(missing[0]);
            }
        }
        string unloadable = FirstUnloadable(list);
        return unloadable == null ? null : Incomplete(unloadable);
    }
}
