using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

// #137 end-to-end fixture. Published like the product (self-contained, compressed single file, IncludeAllContentForSelfExtract) and run
// from scripts/test-bundle-selfheal.ps1. It stands in for the application: it first loads what the running application has already
// loaded by the time an update starts, then deletes every extraction-cache file that is not locked (what a Windows temp cleanup did on a field computer),
// then runs the real UpdateManager/HostUpdateHandoff against a candidate bundle.
//   --mode legacy  v1.55 behaviour: no warm-up, no bundle check
//   --mode warm    start-up warm-up, then the normal check
//   --mode heal    no warm-up, normal check: the same EXE is started with --verify-bundle to restore the files
//   --mode refuse  no warm-up, repair unavailable: the update must be refused before anything changes
internal static class BundleSelfHealTests
{
    static string Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    sealed class ImmediateHandoff : IUpdateHandoff
    {
        public Task<bool> TrySuspendIfIdleAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> StartCandidateAndWaitForReadyAsync(string executablePath, string readyEventName, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> ResumeOldRegistrationsAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CompleteOldProcessAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // The running application has used these before an update starts; Pipes and Overlapped are first used by the handoff itself.
    static async Task Prime(string root)
    {
        using (var self = System.Diagnostics.Process.GetCurrentProcess()) { _ = self.Id; }
        _ = System.Text.Json.JsonSerializer.Serialize(new[] { "prime" });
        _ = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        _ = new System.Collections.Concurrent.ConcurrentQueue<int>();
        string dir = Path.Combine(root, "prime"), exe = Path.Combine(dir, "Remsup.exe"), package = Path.Combine(dir, "package.exe");
        Directory.CreateDirectory(dir);
        File.WriteAllText(exe, "old"); File.WriteAllText(package, "new");
        using var manager = new UpdateManager(new HttpClient(), "v1.52", root) { BundleCheck = () => null };
        var result = await manager.InstallAsync(new VerifiedUpdate(package, "v1.53", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant()), exe, new ImmediateHandoff());
        if (result.Status != UpdateInstallStatus.Installed) throw new InvalidOperationException("priming failed: " + result.Status + " " + result.Error);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == BundleIntegrity.VerifyArgument) return 0;
        if (Array.IndexOf(args, "--update-pipe") >= 0) return Candidate(args);

        string root = Arg(args, "--root"), package = Arg(args, "--package"), mode = Arg(args, "--mode");
        var report = new StringBuilder();
        void Log(string text) => report.AppendLine(text);
        Log("mode=" + mode + " process=" + Environment.ProcessPath);
        if (mode == "warm") Log("warmUpMissing=" + (BundleIntegrity.WarmUp() ?? "none"));

        string executable = Environment.ProcessPath;
        byte[] oldBytes = File.ReadAllBytes(executable);
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        int suspends = 0;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Prime(root);
                var handoff = new HostUpdateHandoff(() => { suspends++; return true; }, () => Task.FromResult(true), () => File.WriteAllText(Path.Combine(root, "candidate-complete"), "x"), () => "fixture-ready", client: false);
                byte[] bytes = File.ReadAllBytes(package);
                string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                using var manager = new UpdateManager(new HttpClient(), "v1.52", root);
                if (mode == "legacy") manager.BundleCheck = () => null;
                if (mode == "refuse") manager.BundleCheck = () => BundleIntegrity.Verify(repair: (_, _) => false);

                int before = Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories).Length;
                foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
                    try { File.Delete(file); } catch { }
                int after = Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories).Length;
                Log("cacheFiles before=" + before + " afterExternalCleanup=" + after + " pipesPresent=" + File.Exists(Path.Combine(AppContext.BaseDirectory, "System.IO.Pipes.dll")));

                UpdateInstallResult result = await manager.InstallAsync(new VerifiedUpdate(package, "v1.99", hash), executable, handoff, CancellationToken.None);
                Log("RESULT status=" + result.Status + " code=" + result.ErrorCode + " error=" + result.Error);
                Log("suspends=" + suspends + " executableUnchanged=" + File.ReadAllBytes(executable).AsSpan().SequenceEqual(oldBytes) + " pipesPresentAfter=" + File.Exists(Path.Combine(AppContext.BaseDirectory, "System.IO.Pipes.dll")));
            }
            catch (Exception failure) { Log("HARNESS EXCEPTION " + failure); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
        }));
        Dispatcher.Run();
        File.WriteAllText(Path.Combine(root, "result-" + mode + ".txt"), report.ToString());
        return 0;
    }

    static int Candidate(string[] args)
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath);
        try
        {
            using var timeout = new CancellationTokenSource(10000);
            string marker = UpdateBootstrap.ReceiveAsync(Arg(args, "--update-pipe"), timeout.Token).GetAwaiter().GetResult();
            UpdateBootstrap.Ready(Arg(args, "--update-ready"));
            File.WriteAllText(Path.Combine(dir, "candidate-ran.txt"), "marker=" + marker);
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(Path.Combine(dir, "..", "candidate-complete")) && DateTime.UtcNow < deadline) Thread.Sleep(50);
            return 0;
        }
        catch (Exception failure) { File.WriteAllText(Path.Combine(dir, "candidate-error.txt"), failure.ToString()); return 24; }
    }
}
