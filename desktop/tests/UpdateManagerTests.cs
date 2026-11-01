using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class UpdateManagerTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static HttpResponseMessage Response(string text) => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    static HttpResponseMessage Response(byte[] bytes, string contentType = "application/octet-stream") => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } }
    };

    sealed class UpdateHandler : HttpMessageHandler
    {
        internal readonly List<string> Paths = new List<string>();
        internal readonly byte[] Exe;
        internal string Version = "v1.26";
        internal string Filename = "RemsupDesktop_v1.26.exe";
        internal string ManifestHash;
        internal string SumsHash;

        internal UpdateHandler(byte[] exe)
        {
            Exe = exe;
            ManifestHash = Hash(exe);
            SumsHash = ManifestHash;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri.AbsolutePath;
            Paths.Add(request.RequestUri.AbsoluteUri);
            if (path == "/latest.json")
                return Task.FromResult(Response($"{{\"version\":\"{Version}\",\"filename\":\"{Filename}\",\"sha256\":\"{ManifestHash}\"}}"));
            if (path == "/SHA256SUMS.txt")
                return Task.FromResult(Response(Encoding.UTF8.GetBytes($"{SumsHash} *{Filename}\n"), "text/plain"));
            if (path == "/" + Filename)
                return Task.FromResult(Response(Exe));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    sealed class TestHandoff : IUpdateHandoff
    {
        internal bool SuspendResult = true;
        internal bool CandidateReady = true;
        internal bool ResumeResult = true;
        internal int Suspends;
        internal int Starts;
        internal int Resumes;
        internal int Completes;
        internal string ReadyEvent;
        internal TimeSpan ReadyTimeout;

        public Task<bool> TrySuspendIfIdleAsync(CancellationToken cancellationToken)
        {
            Suspends++;
            return Task.FromResult(SuspendResult);
        }

        public Task<bool> StartCandidateAndWaitForReadyAsync(string executablePath, string readyEventName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Starts++;
            ReadyEvent = readyEventName;
            ReadyTimeout = timeout;
            return Task.FromResult(CandidateReady);
        }

        public Task<bool> ResumeOldRegistrationsAsync(CancellationToken cancellationToken)
        {
            Resumes++;
            return Task.FromResult(ResumeResult);
        }

        public Task CompleteOldProcessAsync(CancellationToken cancellationToken)
        {
            Completes++;
            return Task.CompletedTask;
        }
    }

    static async Task Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "remsup-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckMetadataAndDownload(root);
            await RejectUnsafeOrMismatchedMetadata(root);
            await VerifySuccessfulInstall(root);
            await VerifyStableInstallAndRotation(root);
            await VerifyFailedCandidateRollsBack(root);
            await VerifyBusyInstallPreservesVerifiedPackage(root);
            await VerifyRuntimePackageStagingAndRollback(root);
            VerifyLockedCleanupAndStartup(root);
            Console.WriteLine("Updater manifest, TLS resource allowlist, SHA256 verification, install handoff and rollback PASS");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static void VerifyLockedCleanupAndStartup(string root)
    {
        string folder = Path.Combine(root, "locked-cleanup");
        Directory.CreateDirectory(folder);
        string old = Path.Combine(folder, "RemsupDesktop_v1.25.exe"), current = Path.Combine(folder, "RemsupDesktop_v1.26.exe");
        File.WriteAllText(old, "old"); File.WriteAllText(current, "new");
        string marker = current + ".cleanup.json";
        File.WriteAllText(marker, System.Text.Json.JsonSerializer.Serialize(new[] { old }));
        string backup = old + ".old-" + Guid.NewGuid().ToString("N");
        string temp = old + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
        string unrelated = old + ".update-user.tmp";
        File.WriteAllText(backup, "backup"); File.WriteAllText(temp, "temp"); File.WriteAllText(unrelated, "preserve");
        using (var locked = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            UpdateManager.CleanupCommittedUpdate(current, false);
            Check(File.Exists(old) && File.Exists(marker), "locked predecessor lost retry marker");
        }
        UpdateManager.CleanupCommittedUpdate(current, false);
        Check(!File.Exists(old) && !File.Exists(marker) && !File.Exists(backup) && !File.Exists(temp) && File.Exists(current) && File.Exists(unrelated), "cleanup retry failed or touched unrelated files");
        Check(UpdateManager.UpdatedStartupValue("\"" + old + "\" --client", old, current) == "\"" + current + "\" --client", "startup update lost client argument");
        Check(UpdateManager.UpdatedStartupValue(null, old, current) == null, "startup enabled itself");
        Check(UpdateManager.UpdatedStartupValue("other.exe", old, current) == "other.exe", "unrelated startup path changed");
    }

    static async Task CheckMetadataAndDownload(string root)
    {
        byte[] exe = Encoding.UTF8.GetBytes("synthetic-v1.26-exe");
        var handler = new UpdateHandler(exe);
        using var client = new HttpClient(handler);
        using var manager = new UpdateManager(client, "v1.25", root);
        UpdateCheckResult check = await manager.CheckAsync();
        Check(check.Status == UpdateCheckStatus.Available && check.Release != null && check.Release.Version == "v1.26", "new version was not offered");
        VerifiedUpdate downloaded = await manager.DownloadAsync(check.Release, CancellationToken.None);
        Check(File.ReadAllBytes(downloaded.Path).AsSpan().SequenceEqual(exe), "verified package bytes differ");
        Check(downloaded.Sha256 == Hash(exe), "verified package hash differs");
        Check(handler.Paths.Count == 3 && handler.Paths[0] == "https://91.227.77.177:8444/latest.json" &&
              handler.Paths[1] == "https://91.227.77.177:8444/SHA256SUMS.txt" &&
              handler.Paths[2] == "https://91.227.77.177:8444/RemsupDesktop_v1.26.exe", "updater requested a non-fixed resource");

        var currentHandler = new UpdateHandler(exe) { Version = "v1.25", Filename = "RemsupDesktop_v1.25.exe" };
        using var currentClient = new HttpClient(currentHandler);
        using var currentManager = new UpdateManager(currentClient, "v1.25", root);
        Check((await currentManager.CheckAsync()).Status == UpdateCheckStatus.Current, "current version reported an update");
    }

    static async Task RejectUnsafeOrMismatchedMetadata(string root)
    {
        byte[] exe = Encoding.UTF8.GetBytes("synthetic-exe");
        var unsafeHandler = new UpdateHandler(exe) { Filename = "..%2f..%2foutside.exe" };
        using var unsafeClient = new HttpClient(unsafeHandler);
        using var unsafeManager = new UpdateManager(unsafeClient, "v1.25", root);
        Check((await unsafeManager.CheckAsync()).Status == UpdateCheckStatus.Error, "unsafe manifest path was accepted");

        var mismatchHandler = new UpdateHandler(exe) { SumsHash = new string('0', 64) };
        using var mismatchClient = new HttpClient(mismatchHandler);
        using var mismatchManager = new UpdateManager(mismatchClient, "v1.25", root);
        UpdateCheckResult check = await mismatchManager.CheckAsync();
        Check(check.Status == UpdateCheckStatus.Available, "valid manifest was rejected before download");
        try { await mismatchManager.DownloadAsync(check.Release, CancellationToken.None); throw new Exception("checksum mismatch should fail"); }
        catch (InvalidDataException) { }

        var mismatchManifest = new UpdateHandler(exe) { ManifestHash = new string('0', 63) };
        using var hashClient = new HttpClient(mismatchManifest);
        using var hashManager = new UpdateManager(hashClient, "v1.25", root);
        check = await hashManager.CheckAsync();
        Check(check.Status == UpdateCheckStatus.Error, "invalid manifest checksum was accepted");

        var redirectHandler = new RedirectHandler();
        using var redirectClient = new HttpClient(redirectHandler);
        using var redirectManager = new UpdateManager(redirectClient, "v1.25", root);
        Check((await redirectManager.CheckAsync()).Status == UpdateCheckStatus.Error, "redirected manifest was accepted");
    }

    sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.invalid/latest.json") } });
        }
    }

    static async Task VerifySuccessfulInstall(string root)
    {
        byte[] update = Encoding.UTF8.GetBytes("new client image");
        string package = Path.Combine(root, "success-stage.exe");
        string exe = Path.Combine(root, "success", "RemsupDesktop.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe));
        File.WriteAllBytes(package, update);
        File.WriteAllBytes(exe, Encoding.UTF8.GetBytes("old client image"));
        using var manager = new UpdateManager(new HttpClient(new UpdateHandler(update)), "v1.25", root);
        var handoff = new TestHandoff();bool beforeExit=false;manager.InstallOutcome=r=>beforeExit=r.Status==UpdateInstallStatus.Installed&&handoff.Completes==0;
        UpdateInstallResult result = await manager.InstallAsync(new VerifiedUpdate(package, "v1.26", Hash(update)), exe, handoff, CancellationToken.None);
        Check(result.Status == UpdateInstallStatus.Installed && File.ReadAllBytes(exe).AsSpan().SequenceEqual(update), "ready candidate was not committed");
        Check(beforeExit,"committed diagnostic outcome must precede predecessor exit");
        Check(handoff.Suspends == 1 && handoff.Starts == 1 && handoff.Resumes == 0 && handoff.Completes == 1, "successful handoff ordering is wrong");
        Check(handoff.ReadyTimeout == TimeSpan.FromSeconds(60) && handoff.ReadyEvent.StartsWith("Local\\Remsup.Update.Ready.", StringComparison.Ordinal), "readiness signal contract is wrong");
        string[] backups = Directory.GetFiles(Path.GetDirectoryName(exe), Path.GetFileName(exe) + ".old-*", SearchOption.TopDirectoryOnly);
        Check(backups.Length == 1 && File.Exists(exe), "successful install did not preserve old image before cleanup");
        string unrelated = exe + ".old-user-file";
        File.WriteAllText(unrelated, "preserve");
        UpdateManager.CleanupCommittedUpdate(exe,false);
        Check(File.Exists(exe) && !File.Exists(backups[0]) && File.Exists(unrelated), "startup cleanup did not stay limited to updater GUID backups");
    }

    static async Task VerifyStableInstallAndRotation(string root) {
        string folder=Path.Combine(root,"installed"),exe=Path.Combine(folder,"Remsup.exe"),package=Path.Combine(root,"stable-package.exe");Directory.CreateDirectory(folder);File.WriteAllText(exe,"old");File.WriteAllText(package,"new");File.WriteAllText(Path.Combine(folder,"Remsup.previous.exe"),"older");
        using var manager=new UpdateManager(new HttpClient(),"v1.25",root);var handoff=new TestHandoff();
        var result=await manager.InstallAsync(new VerifiedUpdate(package,"v1.26",Hash(File.ReadAllBytes(package))),exe,handoff);
        Check(result.Status==UpdateInstallStatus.Installed&&File.ReadAllText(exe)=="new"&&File.ReadAllText(Path.Combine(folder,"Remsup.previous.exe"))=="old","stable path or backup rotation failed");
        UpdateManager.CleanupCommittedUpdate(exe,false);Check(Directory.GetFiles(folder,"*.old-*").Length==0,"rotated backup not cleaned");
        string backup=Path.Combine(folder,"Remsup.previous.exe");File.SetLastWriteTimeUtc(backup,DateTime.UtcNow.AddDays(-8));using(var locked=new FileStream(backup,FileMode.Open,FileAccess.Read,FileShare.Read)){UpdateManager.CleanupAgedBackup(exe);Check(File.Exists(backup),"locked backup lost");}UpdateManager.CleanupAgedBackup(exe);Check(!File.Exists(backup),"aged backup retry failed");
        File.WriteAllText(backup,"retained");File.WriteAllText(package,"broken");handoff=new TestHandoff{CandidateReady=false};result=await manager.InstallAsync(new VerifiedUpdate(package,"v1.27",Hash(File.ReadAllBytes(package))),exe,handoff);Check(result.Status==UpdateInstallStatus.RolledBack&&File.ReadAllText(exe)=="new"&&File.ReadAllText(backup)=="retained","stable rollback lost predecessor or retained backup");
        string portable=Path.Combine(root,"portable.exe"),target=Path.Combine(root,"install-new","Remsup.exe");File.WriteAllText(portable,"copy");bool committed=false;result=await manager.InstallAsync(new VerifiedUpdate(portable,"v1.25",Hash(File.ReadAllBytes(portable))),portable,new TestHandoff(),default,target,true,()=>committed=true);Check(result.Status==UpdateInstallStatus.Installed&&File.ReadAllText(portable)=="copy"&&File.ReadAllText(target)=="copy"&&committed,"same-version installation lost portable source or commit");
    }

    static async Task VerifyFailedCandidateRollsBack(string root)
    {
        byte[] update = Encoding.UTF8.GetBytes("broken client image");
        byte[] old = Encoding.UTF8.GetBytes("known good client image");
        string package = Path.Combine(root, "rollback-stage.exe");
        string exe = Path.Combine(root, "rollback", "RemsupDesktop.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe));
        File.WriteAllBytes(package, update);
        File.WriteAllBytes(exe, old);
        using var manager = new UpdateManager(new HttpClient(new UpdateHandler(update)), "v1.25", root);
        var handoff = new TestHandoff { CandidateReady = false };
        UpdateInstallResult result = await manager.InstallAsync(new VerifiedUpdate(package, "v1.26", Hash(update)), exe, handoff, CancellationToken.None);
        Check(result.Status == UpdateInstallStatus.RolledBack && File.ReadAllBytes(exe).AsSpan().SequenceEqual(old), "failed candidate did not restore exact prior EXE");
        Check(handoff.Suspends == 1 && handoff.Starts == 1 && handoff.Resumes == 1 && handoff.Completes == 0, "failed handoff did not resume old registrations");

        File.WriteAllBytes(package, update);
        File.WriteAllBytes(exe, old);
        handoff = new TestHandoff { CandidateReady = false, ResumeResult = false };
        result = await manager.InstallAsync(new VerifiedUpdate(package, "v1.26", Hash(update)), exe, handoff, CancellationToken.None);
        Check(result.Status == UpdateInstallStatus.RecoveryFailed && !result.OldRegistrationsRestored, "failed old registration was reported as recovered");
    }

    static async Task VerifyBusyInstallPreservesVerifiedPackage(string root)
    {
        byte[] update = Encoding.UTF8.GetBytes("ready update");
        string package = Path.Combine(root, "busy-stage.exe");
        string exe = Path.Combine(root, "busy", "RemsupDesktop.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe));
        File.WriteAllBytes(package, update);
        byte[] old = Encoding.UTF8.GetBytes("still running old client");
        File.WriteAllBytes(exe, old);
        using var manager = new UpdateManager(new HttpClient(new UpdateHandler(update)), "v1.25", root);
        var handoff = new TestHandoff { SuspendResult = false };
        UpdateInstallResult result = await manager.InstallAsync(new VerifiedUpdate(package, "v1.26", Hash(update)), exe, handoff, CancellationToken.None);
        Check(result.Status == UpdateInstallStatus.Deferred && File.Exists(package), "busy device discarded its downloaded update");
        Check(File.ReadAllBytes(exe).AsSpan().SequenceEqual(old) && handoff.Starts == 0 && handoff.Resumes == 0 && handoff.Completes == 0, "busy device changed its active executable or registrations");
    }

    static async Task VerifyRuntimePackageStagingAndRollback(string root)
    {
        string packageDir = Path.Combine(root, "runtime-pkg");
        Directory.CreateDirectory(packageDir);
        string packageExe = Path.Combine(packageDir, "RemsupDesktop.exe");
        byte[] pkgBytes = Encoding.UTF8.GetBytes("new client package");
        File.WriteAllBytes(packageExe, pkgBytes);

        string pkgRuntimes = Path.Combine(packageDir, "runtimes", "webview2");
        Directory.CreateDirectory(pkgRuntimes);
        string pkgMain = Path.Combine(pkgRuntimes, "msedgewebview2.exe");
        File.WriteAllText(pkgMain, "new webview2 runtime");
        string pkgManifest = System.Text.Json.JsonSerializer.Serialize(new RuntimeManifest {
            schema = "remsup-runtime-v1",
            version = "155.0.4283.45",
            files = new List<RuntimeFileEntry> {
                new RuntimeFileEntry { path = "msedgewebview2.exe", sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("new webview2 runtime"))).ToLowerInvariant() }
            }
        });
        File.WriteAllText(Path.Combine(pkgRuntimes, UserInstallation.RuntimeManifestName), pkgManifest);

        string installDir = Path.Combine(root, "app-installed");
        Directory.CreateDirectory(installDir);
        string installExe = Path.Combine(installDir, "Remsup.exe");
        byte[] oldBytes = Encoding.UTF8.GetBytes("old client executable");
        File.WriteAllBytes(installExe, oldBytes);

        string oldRuntimes = Path.Combine(installDir, "runtimes", "webview2");
        Directory.CreateDirectory(oldRuntimes);
        string oldMain = Path.Combine(oldRuntimes, "msedgewebview2.exe");
        File.WriteAllText(oldMain, "old webview2 runtime");
        string oldUserDoc = Path.Combine(oldRuntimes, "preserved-user-note.txt");
        File.WriteAllText(oldUserDoc, "user data to preserve");
        string oldManifest = System.Text.Json.JsonSerializer.Serialize(new RuntimeManifest {
            schema = "remsup-runtime-v1",
            version = "155.0.4283.45",
            files = new List<RuntimeFileEntry> {
                new RuntimeFileEntry { path = "msedgewebview2.exe", sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("old webview2 runtime"))).ToLowerInvariant() }
            }
        });
        File.WriteAllText(Path.Combine(oldRuntimes, UserInstallation.RuntimeManifestName), oldManifest);

        string pkgManifestSha = Hash(File.ReadAllBytes(Path.Combine(pkgRuntimes, UserInstallation.RuntimeManifestName)));

        // Case 1: Candidate fails readiness -> rolls back both EXE and runtime!
        using var manager = new UpdateManager(new HttpClient(new UpdateHandler(pkgBytes)), "v1.25", root);
        var handoffFailed = new TestHandoff { CandidateReady = false };
        UpdateInstallResult resultFail = await manager.InstallAsync(
            new VerifiedUpdate(packageExe, "v1.26", Hash(pkgBytes), runtimeVersion: "155.0.4283.45", runtimeSha256: pkgManifestSha),
            installExe,
            handoffFailed,
            CancellationToken.None);

        Check(resultFail.Status == UpdateInstallStatus.RolledBack, "failed candidate did not roll back");
        Check(File.ReadAllBytes(installExe).AsSpan().SequenceEqual(oldBytes), "failed candidate did not restore old EXE");
        Check(File.ReadAllText(oldMain) == "old webview2 runtime", "failed candidate did not restore old runtime");
        Check(File.Exists(oldUserDoc), "failed candidate rollback lost unlisted user note");

        // Case 2: Candidate succeeds readiness -> commits new runtime and preserves unlisted file!
        var handoffSuccess = new TestHandoff { CandidateReady = true };
        UpdateInstallResult resultSuccess = await manager.InstallAsync(
            new VerifiedUpdate(packageExe, "v1.26", Hash(pkgBytes), runtimeVersion: "155.0.4283.45", runtimeSha256: pkgManifestSha),
            installExe,
            handoffSuccess,
            CancellationToken.None);

        Check(resultSuccess.Status == UpdateInstallStatus.Installed, "ready candidate failed install");
        Check(File.ReadAllBytes(installExe).AsSpan().SequenceEqual(pkgBytes), "ready candidate did not update EXE");
        Check(File.ReadAllText(oldMain) == "new webview2 runtime", "ready candidate did not commit new runtime");
        Check(File.Exists(oldUserDoc), "successful runtime commit lost unlisted user note");
        Check(resultSuccess.Error != null && resultSuccess.Error.Contains("preserved in runtime backup"), "commit error did not report preserved unlisted note");

        // Case 3: VerifiedUpdate with tampered runtime hash fails closed before candidate launch or suspension!
        var handoffTampered = new TestHandoff { CandidateReady = true };
        UpdateInstallResult resultTampered = await manager.InstallAsync(
            new VerifiedUpdate(packageExe, "v1.26", Hash(pkgBytes), runtimeVersion: "155.0.4283.45", runtimeSha256: "0000000000000000000000000000000000000000000000000000000000000000"),
            installExe,
            handoffTampered,
            CancellationToken.None);
        Check(resultTampered.Status == UpdateInstallStatus.Failed && resultTampered.Error.Contains("runtime manifest hash does not match"), "tampered runtime hash binding did not fail closed");
        Check(handoffTampered.Starts == 0 && handoffTampered.Completes == 0, "tampered runtime launched candidate");
        Check(File.ReadAllBytes(installExe).AsSpan().SequenceEqual(pkgBytes), "tampered runtime update modified executable");

        // Case 4: VerifiedUpdate with missing runtime directory when runtimeVersion is declared fails closed!
        var handoffMissing = new TestHandoff { CandidateReady = true };
        string missingPackageExe = Path.Combine(root, "missing-runtime-pkg", "RemsupDesktop.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(missingPackageExe));
        File.WriteAllBytes(missingPackageExe, pkgBytes);
        UpdateInstallResult resultMissing = await manager.InstallAsync(
            new VerifiedUpdate(missingPackageExe, "v1.26", Hash(pkgBytes), runtimeVersion: "155.0.4283.45", runtimeSha256: "0000000000000000000000000000000000000000000000000000000000000000"),
            installExe,
            handoffMissing,
            CancellationToken.None);
        Check(resultMissing.Status == UpdateInstallStatus.Failed && resultMissing.Error.Contains("requires an app-local runtime package"), "missing runtime package did not fail closed");
        Check(handoffMissing.Starts == 0 && handoffMissing.Completes == 0, "missing runtime package launched candidate");

        // Case 5: VerifiedUpdate with valid paired runtime metadata succeeds!
        var handoffPaired = new TestHandoff { CandidateReady = true };
        UpdateInstallResult resultPaired = await manager.InstallAsync(
            new VerifiedUpdate(packageExe, "v1.26", Hash(pkgBytes), runtimeVersion: "155.0.4283.45", runtimeSha256: pkgManifestSha),
            installExe,
            handoffPaired,
            CancellationToken.None);
        Check(resultPaired.Status == UpdateInstallStatus.Installed, "valid paired runtime update failed");
        Check(handoffPaired.Starts == 1 && handoffPaired.Completes == 1, "valid paired runtime update did not complete handoff");
    }
}
