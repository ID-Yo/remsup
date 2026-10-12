using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

internal static class UpdateProcessTests
{
    const string ReadyMarker = "synthetic-ready-marker";
    const string BrokenMarker = "synthetic-broken-marker";
    const string TestMutex = @"Local\Remsup.Test.UpdateProcess.1c56e33c75f94092b1f06d4e66a771aa";

    [STAThread]
    static int Main(string[] args)
    {
        if (TryRunCandidate(args, out int candidateExitCode)) return candidateExitCode;

        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        Exception failure = null;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await VerifySuccessfulChildHandoff();
                await VerifyBrokenChildRollback();
                await VerifyActiveSessionIsDeferred();
                Console.WriteLine("Real child-process update handoff, readiness, rollback, mutex transfer and active-session guard PASS");
            }
            catch (Exception ex)
            {
                failure = ex;
                Environment.ExitCode = 1;
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }));
        Dispatcher.Run();
        if (failure != null) Console.Error.WriteLine(failure);
        return Environment.ExitCode;
    }

    static bool TryRunCandidate(string[] args, out int exitCode)
    {
        exitCode = 0;
        int pipeIndex = Array.IndexOf(args, "--update-pipe");
        if (pipeIndex < 0) return false;
        int readyIndex = Array.IndexOf(args, "--update-ready");
        if (pipeIndex + 1 >= args.Length || readyIndex < 0 || readyIndex + 1 >= args.Length)
        {
            exitCode = 2;
            return true;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string marker = UpdateBootstrap.ReceiveAsync(args[pipeIndex + 1], timeout.Token).GetAwaiter().GetResult();
            // Match the production startup order: receive the protected handoff first, then acquire
            // the per-user single-instance mutex, then signal readiness only after startup succeeds.
            using var instance = new ApplicationInstance(TestMutex);
            if (marker == ReadyMarker)
            {
                UpdateBootstrap.Ready(args[readyIndex + 1]);
                string complete = Path.Combine(AppContext.BaseDirectory, "candidate-complete");
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(complete) && DateTime.UtcNow < deadline) Thread.Sleep(20);
                return true;
            }
            exitCode = 23; // Valid helper image, deliberately broken before readiness.
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Synthetic update candidate startup failed: " + ex.GetType().Name);
            exitCode = 24;
            return true;
        }
    }

    static async Task VerifySuccessfulChildHandoff()
    {
        using var scenario = new Scenario("success");
        using var instance = new ApplicationInstance(TestMutex);
        HostUpdateHandoff.Instance = instance;
        bool suspended = false, resumed = false;
        bool childHeldMutexWhenReady = false;
        int completeCalls = 0, starts = 0;
        var handoff = new HostUpdateHandoff(
            () => { Check(!scenario.Active, "suspension ran while a synthetic channel was active"); suspended = true; return true; },
            () => { resumed = true; return Task.FromResult(true); },
            () => { completeCalls++; childHeldMutexWhenReady = !CanAcquireUserMutex(); File.WriteAllText(scenario.CompleteSignal, "complete"); },
            () => ReadyMarker,
            client: false);
        var observedHandoff = new CountingHandoff(handoff, () => starts++);

        UpdateInstallResult result = await InstallScenario(scenario, observedHandoff);
        Check(result.Status == UpdateInstallStatus.Installed, "ready child was not installed: " + result.Status + " " + result.Error);
        Check(File.ReadAllBytes(scenario.Executable).AsSpan().SequenceEqual(scenario.PackageBytes), "ready child image was not committed");
        Check(suspended && !resumed && completeCalls == 1 && starts == 1, "successful process handoff order was incorrect");
        Check(childHeldMutexWhenReady, "candidate signaled ready without acquiring the single-instance mutex");
        Check(scenario.ActiveChannelInterruptions == 0, "an active synthetic channel was interrupted");
        await WaitForCandidateMutexRelease();
        HostUpdateHandoff.Instance = null;
    }

    static async Task VerifyBrokenChildRollback()
    {
        using var scenario = new Scenario("rollback");
        using var instance = new ApplicationInstance(TestMutex);
        HostUpdateHandoff.Instance = instance;
        bool suspended = false, resumed = false;
        int completeCalls = 0, starts = 0;
        var handoff = new HostUpdateHandoff(
            () => { Check(!scenario.Active, "suspension ran while a synthetic channel was active"); suspended = true; return true; },
            () => { resumed = true; Check(!CanAcquireUserMutex(), "old mutex was not reacquired before relay resume"); return Task.FromResult(true); },
            () => completeCalls++,
            () => BrokenMarker,
            client: false);

        UpdateInstallResult result = await InstallScenario(scenario, new CountingHandoff(handoff, () => starts++));
        Check(result.Status == UpdateInstallStatus.RolledBack, "broken child did not report a rollback: " + result.Status + " " + result.Error);
        Check(result.Error.Contains("exit code 23"), "candidate exit reason was lost during rollback");
        Check(File.ReadAllBytes(scenario.Executable).AsSpan().SequenceEqual(scenario.OldBytes), "rollback did not restore the byte-identical old image");
        Check(suspended && resumed && completeCalls == 0 && starts == 1, "failed process handoff order was incorrect");
        Check(!CanAcquireUserMutex(), "old process did not retain the single-instance mutex after rollback");
        Check(scenario.ActiveChannelInterruptions == 0, "an active synthetic channel was interrupted");
        HostUpdateHandoff.Instance = null;
    }

    static async Task VerifyActiveSessionIsDeferred()
    {
        using var scenario = new Scenario("busy") { Active = true };
        using var instance = new ApplicationInstance(TestMutex);
        HostUpdateHandoff.Instance = instance;
        int resumeCalls = 0, completeCalls = 0, starts = 0;
        var handoff = new HostUpdateHandoff(
            () =>
            {
                if (scenario.Active) return false;
                scenario.ActiveChannelInterruptions++;
                return true;
            },
            () => { resumeCalls++; return Task.FromResult(true); },
            () => completeCalls++,
            () => ReadyMarker,
            client: false);

        UpdateInstallResult result = await InstallScenario(scenario, new CountingHandoff(handoff, () => starts++));
        Check(result.Status == UpdateInstallStatus.Deferred, "active session did not defer update installation");
        Check(File.ReadAllBytes(scenario.Executable).AsSpan().SequenceEqual(scenario.OldBytes), "active session executable changed");
        Check(File.Exists(scenario.Package), "deferred verified update was discarded");
        Check(starts == 0 && resumeCalls == 0 && completeCalls == 0 && scenario.ActiveChannelInterruptions == 0,
            "active channel was interrupted by update handoff");
        Check(!CanAcquireUserMutex(), "deferred update released the single-instance mutex");
        HostUpdateHandoff.Instance = null;
    }

    static async Task<UpdateInstallResult> InstallScenario(Scenario scenario, IUpdateHandoff handoff)
    {
        using var manager = new UpdateManager(new HttpClient(), "v1.25", scenario.Root);
        var update = new VerifiedUpdate(scenario.Package, "v1.26", Hash(scenario.PackageBytes));
        return await manager.InstallAsync(update, scenario.Executable, handoff, CancellationToken.None);
    }

    static bool CanAcquireUserMutex()
    {
        return Task.Run(() =>
        {
            using var probe = new Mutex(false, TestMutex);
            bool acquired = false;
            try { acquired = probe.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) probe.ReleaseMutex();
            return acquired;
        }).GetAwaiter().GetResult();
    }

    static async Task WaitForCandidateMutexRelease()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (CanAcquireUserMutex()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The ready child did not finish its synthetic close after handoff success.");
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    sealed class CountingHandoff : IUpdateHandoff
    {
        readonly IUpdateHandoff inner;
        readonly Action started;
        internal CountingHandoff(IUpdateHandoff inner, Action started) { this.inner = inner; this.started = started; }
        public Task<bool> TrySuspendIfIdleAsync(CancellationToken token) => inner.TrySuspendIfIdleAsync(token);
        public Task<bool> StartCandidateAndWaitForReadyAsync(string executablePath, string readyEventName, TimeSpan timeout, CancellationToken token)
        {
            started();
            return inner.StartCandidateAndWaitForReadyAsync(executablePath, readyEventName, timeout, token);
        }
        public Task<bool> ResumeOldRegistrationsAsync(CancellationToken token) => inner.ResumeOldRegistrationsAsync(token);
        public Task CompleteOldProcessAsync(CancellationToken token) => inner.CompleteOldProcessAsync(token);
    }

    sealed class Scenario : IDisposable
    {
        internal readonly string Root;
        internal readonly string Executable;
        internal readonly string Package;
        internal readonly string CompleteSignal;
        internal readonly byte[] OldBytes = System.Text.Encoding.UTF8.GetBytes("synthetic old executable image");
        internal readonly byte[] PackageBytes;
        internal bool Active;
        internal int ActiveChannelInterruptions;

        internal Scenario(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), "remsup-update-process-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            string source = Path.Combine(AppContext.BaseDirectory, "UpdateProcessTests.exe");
            if (!File.Exists(source)) throw new FileNotFoundException("The test apphost is required for child-process handoff tests.", source);
            foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(Root, Path.GetFileName(file)), true);
            Executable = Path.Combine(Root, Path.GetFileName(source));
            CompleteSignal = Path.Combine(Root, "candidate-complete");
            File.WriteAllBytes(Executable, OldBytes);
            Package = Path.Combine(Root, "verified-candidate.exe");
            PackageBytes = File.ReadAllBytes(source);
            File.WriteAllBytes(Package, PackageBytes);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
