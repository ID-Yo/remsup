using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal enum UpdateCheckStatus
{
    Current,
    Available,
    Error
}

internal sealed class UpdateRelease
{
    internal string Version { get; }
    internal string Filename { get; }
    internal string Sha256 { get; }

    internal UpdateRelease(string version, string filename, string sha256)
    {
        Version = version;
        Filename = filename;
        Sha256 = sha256;
    }
}

internal sealed class UpdateCheckResult
{
    internal UpdateCheckStatus Status { get; }
    internal string Version { get; }
    internal string Error { get; }
    internal UpdateRelease Release { get; }

    internal UpdateCheckResult(UpdateCheckStatus status, string version = "", string error = "", UpdateRelease release = null)
    {
        Status = status;
        Version = version;
        Error = error;
        Release = release;
    }
}

internal sealed class VerifiedUpdate
{
    internal string Path { get; }
    internal string Version { get; }
    internal string Sha256 { get; }

    internal VerifiedUpdate(string path, string version, string sha256)
    {
        Path = path;
        Version = version;
        Sha256 = sha256;
    }
}

internal enum UpdateInstallStatus
{
    Installed,
    Deferred,
    RolledBack,
    RecoveryFailed,
    Failed
}

internal sealed class UpdateInstallResult
{
    internal UpdateInstallStatus Status { get; }
    internal bool OldRegistrationsRestored { get; }
    internal string Error { get; }

    internal UpdateInstallResult(UpdateInstallStatus status, bool oldRegistrationsRestored = false, string error = "")
    {
        Status = status;
        OldRegistrationsRestored = oldRegistrationsRestored;
        Error = error;
    }
}

internal interface IUpdateHandoff
{
    // Must test both channel gates and suspend only after confirming that neither channel is active.
    Task<bool> TrySuspendIfIdleAsync(CancellationToken cancellationToken);

    // Launches the candidate and returns true only after COMMAND and SCREEN both reach TLS relay WAIT.
    // On false or exception, the candidate process tree must already be stopped.
    Task<bool> StartCandidateAndWaitForReadyAsync(string executablePath, string readyEventName, TimeSpan timeout, CancellationToken cancellationToken);

    // Re-registers both channels of the still-running old process and returns true only after both reach WAIT.
    Task<bool> ResumeOldRegistrationsAsync(CancellationToken cancellationToken);

    // Called only after the candidate readiness event was observed; gracefully closes this old process.
    Task CompleteOldProcessAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Downloads only the configured Remsup release resources over the caller-supplied pinned TLS client,
/// verifies the published SHA256SUMS entry and manifest hash, then performs a rollback-capable EXE handoff.
/// </summary>
internal sealed class UpdateManager : IDisposable
{
    internal static readonly string UpdateHost = Environment.GetEnvironmentVariable("REMSUP_UPDATE_HOST") ?? "updates.example.invalid";
    internal static readonly int UpdatePort = int.Parse(Environment.GetEnvironmentVariable("REMSUP_UPDATE_PORT") ?? "8444");
    internal static readonly string UpdatePin = Environment.GetEnvironmentVariable("REMSUP_UPDATE_PIN") ?? "0000000000000000000000000000000000000000000000000000000000000000";
    internal static string BaseUrl => new UriBuilder("https", UpdateHost, UpdatePort).Uri.AbsoluteUri;
    internal static string ManifestUrl => BaseUrl + "latest.json";
    internal static string ChecksumsUrl => BaseUrl + "SHA256SUMS.txt";
    static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(180);
    internal static readonly TimeSpan CandidateReadyTimeout = TimeSpan.FromSeconds(60);

    const long MaxManifestBytes = 16 * 1024;
    const long MaxChecksumsBytes = 128 * 1024;
    const long MaxExecutableBytes = 512L * 1024 * 1024;
    static readonly Regex VersionPattern = new Regex(@"^v1\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?$", RegexOptions.CultureInvariant);
    static readonly Regex HashPattern = new Regex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
    static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    readonly HttpClient client;
    readonly string currentVersion;
    readonly string dataDirectory;
    bool disposed;

    internal UpdateManager(HttpClient pinnedClient, string currentVersion, string dataDirectory)
    {
        client = pinnedClient ?? throw new ArgumentNullException(nameof(pinnedClient));
        if (!TryParseVersion(currentVersion, out _)) throw new ArgumentException("Current version must use the v1.N or v1.N.P format.", nameof(currentVersion));
        this.currentVersion = NormalizeVersion(currentVersion);
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("Update data directory is required.", nameof(dataDirectory));
        this.dataDirectory = Path.GetFullPath(dataDirectory);
    }

    internal async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            byte[] bytes = await GetBytesAsync(new Uri(ManifestUrl), MaxManifestBytes, MetadataTimeout, cancellationToken).ConfigureAwait(false);
            UpdateRelease release = ParseManifest(bytes);
            if (CompareVersions(release.Version, currentVersion) <= 0)
                return new UpdateCheckResult(UpdateCheckStatus.Current, currentVersion);
            return new UpdateCheckResult(UpdateCheckStatus.Available, release.Version, release: release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (IsExpectedUpdateFailure(failure))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, error: failure.Message);
        }
    }

    internal async Task<VerifiedUpdate> DownloadAsync(UpdateRelease release, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRelease(release);
        if (CompareVersions(release.Version, currentVersion) <= 0) throw new InvalidDataException("The release is not newer than the running version.");

        byte[] sumBytes = await GetBytesAsync(new Uri(ChecksumsUrl), MaxChecksumsBytes, MetadataTimeout, cancellationToken).ConfigureAwait(false);
        string sumHash = ParseChecksums(sumBytes, release.Filename);
        if (!HashEquals(sumHash, release.Sha256)) throw new InvalidDataException("The manifest hash does not match SHA256SUMS.txt.");

        string folder = Path.Combine(dataDirectory, "updates", release.Version);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, release.Filename);
        string partial = path + ".part";
        try
        {
            await DownloadFileAsync(new Uri(BaseUrl + Uri.EscapeDataString(release.Filename)), partial, cancellationToken).ConfigureAwait(false);
            string actualHash = await HashFileAsync(partial, cancellationToken).ConfigureAwait(false);
            if (!HashEquals(actualHash, release.Sha256)) throw new InvalidDataException("The downloaded executable does not match its published SHA256.");
            File.Move(partial, path, true);
            return new VerifiedUpdate(path, release.Version, actualHash);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    internal async Task<UpdateInstallResult> InstallAsync(VerifiedUpdate update, string executablePath, IUpdateHandoff handoff, CancellationToken cancellationToken = default, string destinationPath = null, bool installCopy = false, Action committed = null)
    {
        ThrowIfDisposed();
        if (update == null) throw new ArgumentNullException(nameof(update));
        if (handoff == null) throw new ArgumentNullException(nameof(handoff));
        string fullExe = Path.GetFullPath(executablePath ?? throw new ArgumentNullException(nameof(executablePath)));
        string fullPackage = Path.GetFullPath(update.Path ?? throw new ArgumentException("Verified update path is missing.", nameof(update)));
        string candidate = Path.GetFullPath(destinationPath ?? fullExe);
        bool inPlace = string.Equals(candidate, fullExe, StringComparison.OrdinalIgnoreCase);
        bool installed = string.Equals(Path.GetFileName(candidate), "Remsup.exe", StringComparison.OrdinalIgnoreCase);
        string backup = installed ? Path.Combine(Path.GetDirectoryName(candidate), "Remsup.previous.exe") : candidate + ".old-" + Guid.NewGuid().ToString("N");
        string adjacent = candidate + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
        string directory = Path.GetDirectoryName(candidate);
        bool predecessorMoved = false;
        string rotatedBackup = null;
        bool suspended = false;
        bool candidateMoved = false;
        bool ready = false;
        string phase = "prepare and suspend update";

        try
        {
            ValidateVersionAndHash(update.Version, update.Sha256);
            if (!inPlace && File.Exists(candidate)) return new UpdateInstallResult(UpdateInstallStatus.Failed, error: "The installation target already exists; it was not overwritten.");
            if (!File.Exists(fullExe)) return new UpdateInstallResult(UpdateInstallStatus.Failed, error: "The current executable was not found.");
            if (!File.Exists(fullPackage)) return new UpdateInstallResult(UpdateInstallStatus.Failed, error: "The verified update file was not found.");
            if (!installCopy && string.Equals(fullExe, fullPackage, StringComparison.OrdinalIgnoreCase)) return new UpdateInstallResult(UpdateInstallStatus.Failed, error: "The update package cannot replace itself in place.");
            if (!installCopy && CompareVersions(update.Version, currentVersion) <= 0) return new UpdateInstallResult(UpdateInstallStatus.Failed, error: "The update version is not newer than the running version.");
            if (!HashEquals(await HashFileAsync(fullPackage, cancellationToken).ConfigureAwait(false), update.Sha256))
                return new UpdateInstallResult(UpdateInstallStatus.Failed, error: "The downloaded executable changed after verification.");

            Directory.CreateDirectory(directory);
            await CopyFileAsync(fullPackage, adjacent, cancellationToken).ConfigureAwait(false);
            if (!HashEquals(await HashFileAsync(adjacent, cancellationToken).ConfigureAwait(false), update.Sha256))
                throw new InvalidDataException("The adjacent update copy failed SHA256 verification.");

            if (!await handoff.TrySuspendIfIdleAsync(cancellationToken).ConfigureAwait(false))
            {
                TryDelete(adjacent);
                return new UpdateInstallResult(UpdateInstallStatus.Deferred, error: "A COMMAND or SCREEN session is active; the verified update is kept for later.");
            }
            suspended = true;

            phase = "replace executable in place";
            if (inPlace) {
                if (File.Exists(backup)) {
                    rotatedBackup = candidate + ".old-" + Guid.NewGuid().ToString("N");
                    File.Move(backup, rotatedBackup);
                }
                File.Move(fullExe, backup);
                predecessorMoved = true;
            }
            File.Move(adjacent, candidate);
            candidateMoved = true;

            string eventName = "Local\\Remsup.Update.Ready." + Guid.NewGuid().ToString("N");
            phase = "launch candidate";
            ready = await handoff.StartCandidateAndWaitForReadyAsync(candidate, eventName, CandidateReadyTimeout, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                bool fileRestored = RestorePredecessor(candidate, backup, candidateMoved, predecessorMoved, rotatedBackup);
                bool registrationsRestored = await TryResumeAsync(handoff, CancellationToken.None).ConfigureAwait(false);
                bool restored = fileRestored && registrationsRestored;
                return new UpdateInstallResult(restored ? UpdateInstallStatus.RolledBack : UpdateInstallStatus.RecoveryFailed, registrationsRestored,
                    restored ? "The candidate did not register both TLS relay channels; the previous executable and relay registrations were restored." :
                    fileRestored ? "The previous executable was restored, but its relay registrations did not recover." : "The candidate failed and the previous executable could not be restored.");
            }

            string commitError = "";
            try {
                // The portable source of an installation is intentionally retained.
                string cleanup = inPlace ? (installed ? rotatedBackup : backup) : null;
                if (cleanup != null) File.WriteAllText(candidate + ".cleanup.json", JsonSerializer.Serialize(new[] { cleanup }));
                if (installed && predecessorMoved) File.SetLastWriteTimeUtc(backup, DateTime.UtcNow);
                committed?.Invoke();
            } catch (Exception error) { commitError = "The app started, but installation registration/cleanup needs repair: " + error.Message; }
            // Once readiness is accepted, always close the predecessor, even if registration fails.
            try { await handoff.CompleteOldProcessAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { commitError += " The old process did not close cleanly: " + error.Message; }
            return new UpdateInstallResult(UpdateInstallStatus.Installed, error: commitError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool fileRestored = RestorePredecessor(candidate, backup, candidateMoved, predecessorMoved, rotatedBackup);
            bool registrationsRestored = !suspended || await TryResumeAsync(handoff, CancellationToken.None).ConfigureAwait(false);
            bool restored = fileRestored && registrationsRestored;
            return new UpdateInstallResult(restored ? (suspended ? UpdateInstallStatus.RolledBack : UpdateInstallStatus.Failed) : UpdateInstallStatus.RecoveryFailed,
                registrationsRestored, "The update handoff was cancelled.");
        }
        catch (Exception failure)
        {
            bool fileRestored = RestorePredecessor(candidate, backup, candidateMoved, predecessorMoved, rotatedBackup);
            bool registrationsRestored = !suspended || await TryResumeAsync(handoff, CancellationToken.None).ConfigureAwait(false);
            bool restored = fileRestored && registrationsRestored;
            return new UpdateInstallResult(!restored ? UpdateInstallStatus.RecoveryFailed : suspended ? UpdateInstallStatus.RolledBack : UpdateInstallStatus.Failed,
                registrationsRestored, "Update handoff failed during "+phase+" ("+failure.GetType().Name+"; 0x"+failure.HResult.ToString("X8")+"): "+failure.Message);
        }
        finally
        {
            TryDelete(adjacent);
        }
    }

    // Call on a later ordinary startup, after the previous process has had time to exit.
    // The handoff-starting process must keep the just-renamed image until its old process closes.
    internal static void CleanupPreviousImages(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return;
        try
        {
            string fullExe = Path.GetFullPath(executablePath);
            string directory = Path.GetDirectoryName(fullExe);
            string prefix = Path.GetFileName(fullExe) + ".old-";
            if (!Directory.Exists(directory)) return;
            foreach (string path in Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                string suffix = Path.GetFileName(path).Substring(prefix.Length);
                if (Guid.TryParseExact(suffix, "N", out _)) TryDelete(path);
            }
        }
        catch { }
    }

    internal static async Task RetryCommittedCleanupAsync(string executablePath, CancellationToken token)
    {
        bool first = true;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(first || File.Exists(executablePath + ".cleanup.json") ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(10), token).ConfigureAwait(false);
            first = false;
            CleanupCommittedUpdate(executablePath);
            CleanupAgedBackup(executablePath);
        }
    }

    internal static string UpdatedStartupValue(string value, string previous, string current)
    {
        if (string.IsNullOrEmpty(value)) return value;
        string quoted = "\"" + previous + "\"";
        if (value.Equals(previous, StringComparison.OrdinalIgnoreCase)) return "\"" + current + "\"";
        if (value.StartsWith(quoted, StringComparison.OrdinalIgnoreCase) &&
            (value.Length == quoted.Length || char.IsWhiteSpace(value[quoted.Length])))
            return "\"" + current + "\"" + value.Substring(quoted.Length);
        return value;
    }

    internal static void CleanupCommittedUpdate(string executablePath, bool updateStartup = true)
    {
        try
        {
            string current = Path.GetFullPath(executablePath);
            string marker = current + ".cleanup.json";
            if (!File.Exists(marker)) return;
            string[] previousPaths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(marker));
            if (previousPaths == null || previousPaths.Length != 1) return;
            string previous = Path.GetFullPath(previousPaths[0]);
            string directory = Path.GetDirectoryName(current);
            if (!string.Equals(Path.GetDirectoryName(previous), directory, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(previous, current, StringComparison.OrdinalIgnoreCase) ||
                !(Path.GetFileName(previous).StartsWith("RemsupDesktop", StringComparison.Ordinal) && previous.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) &&
                !(previous.StartsWith(current + ".old-", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(previous.Substring(current.Length + 5), "N", out _))) return;
            if (updateStartup)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                string value = key?.GetValue("Remsup Support") as string;
                string replacement = UpdatedStartupValue(value, previous, current);
                if (replacement != value) key.SetValue("Remsup Support", replacement);
            }
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(previous)))
            {
                using (process)
                    if (string.Equals(process.MainModule.FileName, previous, StringComparison.OrdinalIgnoreCase)) return;
            }
            // Only the predecessor named by the committed handoff and updater GUID files qualify.
            TryDelete(previous);
            CleanupPreviousImages(previous);
            string prefix = Path.GetFileName(previous) + ".update-";
            foreach (string path in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
            {
                string suffix = Path.GetFileName(path).Substring(prefix.Length);
                if (Guid.TryParseExact(suffix.Substring(0, suffix.Length - 4), "N", out _)) TryDelete(path);
            }
            if (!File.Exists(previous) && !HasUpdaterFiles(previous)) TryDelete(marker);
        }
        catch { /* Synchronization locks and registry failures are retried without blocking support. */ }
    }

    static bool HasUpdaterFiles(string previous)
    {
        string directory = Path.GetDirectoryName(previous), name = Path.GetFileName(previous);
        foreach (string path in Directory.EnumerateFiles(directory, name + ".old-*"))
            if (Guid.TryParseExact(Path.GetFileName(path).Substring(name.Length + 5), "N", out _)) return true;
        foreach (string path in Directory.EnumerateFiles(directory, name + ".update-*.tmp"))
        {
            string suffix = Path.GetFileName(path).Substring(name.Length + 8);
            if (Guid.TryParseExact(suffix.Substring(0, suffix.Length - 4), "N", out _)) return true;
        }
        return false;
    }

    static UpdateRelease ParseManifest(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("latest.json must be a JSON object.");
        bool versionSeen = false, filenameSeen = false, hashSeen = false;
        string version = null, filename = null, hash = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "version":
                    if (versionSeen || property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("latest.json has an invalid version field.");
                    versionSeen = true;
                    version = property.Value.GetString();
                    break;
                case "filename":
                    if (filenameSeen || property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("latest.json has an invalid filename field.");
                    filenameSeen = true;
                    filename = property.Value.GetString();
                    break;
                case "sha256":
                    if (hashSeen || property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("latest.json has an invalid sha256 field.");
                    hashSeen = true;
                    hash = property.Value.GetString();
                    break;
                default:
                    throw new InvalidDataException("latest.json contains an unsupported field.");
            }
        }
        if (!versionSeen || !filenameSeen || !hashSeen) throw new InvalidDataException("latest.json is missing a required field.");
        var release = new UpdateRelease(version, filename, hash);
        ValidateRelease(release);
        return release;
    }

    static void ValidateRelease(UpdateRelease release)
    {
        if (release == null) throw new InvalidDataException("No update release is available.");
        ValidateVersionAndHash(release.Version, release.Sha256);
        string expectedFilename = "RemsupDesktop_" + release.Version + ".exe";
        if (!string.Equals(release.Filename, expectedFilename, StringComparison.Ordinal))
            throw new InvalidDataException("The release filename does not match its version.");
    }

    static void ValidateVersionAndHash(string version, string hash)
    {
        if (!TryParseVersion(version, out _)) throw new InvalidDataException("The release version is invalid.");
        if (string.IsNullOrEmpty(hash) || !HashPattern.IsMatch(hash)) throw new InvalidDataException("The release SHA256 is invalid.");
    }

    static string ParseChecksums(byte[] bytes, string filename)
    {
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException failure) { throw new InvalidDataException("SHA256SUMS.txt is not valid UTF-8.", failure); }
        string found = null;
        foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || !HashPattern.IsMatch(fields[0])) throw new InvalidDataException("SHA256SUMS.txt has an invalid line.");
            string listedName = fields[1].StartsWith("*", StringComparison.Ordinal) ? fields[1].Substring(1) : fields[1];
            if (!string.Equals(listedName, filename, StringComparison.Ordinal)) continue;
            if (found != null) throw new InvalidDataException("SHA256SUMS.txt lists the executable more than once.");
            found = fields[0];
        }
        return found ?? throw new InvalidDataException("SHA256SUMS.txt has no entry for the release executable.");
    }

    async Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        EnsureFixedResource(uri);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
        EnsureNoRedirect(response, uri);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("The update server did not return HTTP 200.");
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("The update response exceeds its size limit.");
        using Stream input = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeoutSource.Token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidDataException("The update response exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    async Task DownloadFileAsync(Uri uri, string partialPath, CancellationToken cancellationToken)
    {
        EnsureFixedResource(uri);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(DownloadTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
        EnsureNoRedirect(response, uri);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("The update server did not return HTTP 200.");
        if (response.Content.Headers.ContentLength > MaxExecutableBytes) throw new InvalidDataException("The executable exceeds the update size limit.");
        using Stream input = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
        await using var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeoutSource.Token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > MaxExecutableBytes) throw new InvalidDataException("The executable exceeds the update size limit.");
            await output.WriteAsync(buffer, 0, read, timeoutSource.Token).ConfigureAwait(false);
        }
        await output.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
    }

    static void EnsureFixedResource(Uri uri)
    {
        if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, UpdateHost, StringComparison.Ordinal) || uri.Port != UpdatePort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("The update resource is outside the fixed TLS endpoint.");
        if (uri.AbsolutePath != "/latest.json" && uri.AbsolutePath != "/SHA256SUMS.txt" && !Regex.IsMatch(uri.AbsolutePath, @"\A/RemsupDesktop_v1\.[0-9]+(?:\.[0-9]+)?\.exe\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The update resource path is not allowed.");
    }

    static void EnsureNoRedirect(HttpResponseMessage response, Uri requested)
    {
        Uri final = response.RequestMessage?.RequestUri;
        if (final != null && !Uri.Equals(final, requested)) throw new InvalidDataException("The update server redirected the request.");
    }

    static bool TryParseVersion(string text, out Version version)
    {
        version = null;
        if (string.IsNullOrEmpty(text) || !VersionPattern.IsMatch(text)) return false;
        string[] parts = text.Substring(1).Split('.');
        if (!int.TryParse(parts[1], out int minor)) return false;
        int patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], out patch)) return false;
        version = new Version(1, minor, patch);
        return true;
    }

    static string NormalizeVersion(string value) => value.StartsWith("v", StringComparison.Ordinal) ? value : "v" + value;

    static int CompareVersions(string left, string right)
    {
        if (!TryParseVersion(left, out Version leftVersion) || !TryParseVersion(right, out Version rightVersion)) throw new InvalidDataException("Cannot compare invalid Remsup versions.");
        return leftVersion.CompareTo(rightVersion);
    }

    static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    static bool HashEquals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static void CleanupAgedBackup(string executablePath) {
        if (!string.Equals(Path.GetFileName(executablePath), "Remsup.exe", StringComparison.OrdinalIgnoreCase)) return;
        string backup = Path.Combine(Path.GetDirectoryName(executablePath), "Remsup.previous.exe");
        if (File.Exists(backup) && DateTime.UtcNow - File.GetLastWriteTimeUtc(backup) >= TimeSpan.FromDays(7)) TryDelete(backup);
    }

    static bool RestorePredecessor(string executable, string backup, bool candidateMoved, bool predecessorMoved, string rotated) {
        try {
            if (!RemoveFailedCandidate(executable, candidateMoved)) return false;
            if (predecessorMoved) File.Move(backup, executable);
            if (rotated != null && File.Exists(rotated)) File.Move(rotated, backup);
            return true;
        } catch { return false; }
    }

    static bool RemoveFailedCandidate(string executable, bool candidateMoved)
    {
        try
        {
            if (candidateMoved && File.Exists(executable)) File.Delete(executable);
            return !candidateMoved || !File.Exists(executable);
        }
        catch
        {
            return false;
        }
    }

    static async Task<bool> TryResumeAsync(IUpdateHandoff handoff, CancellationToken cancellationToken)
    {
        try { return await handoff.ResumeOldRegistrationsAsync(cancellationToken).ConfigureAwait(false); }
        catch { return false; }
    }

    static bool IsExpectedUpdateFailure(Exception failure) => failure is HttpRequestException || failure is IOException || failure is JsonException || failure is InvalidDataException || failure is FormatException || failure is UnauthorizedAccessException || failure is DecoderFallbackException || failure is ArgumentException || failure is OperationCanceledException;

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(UpdateManager));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Dispose();
    }
}
