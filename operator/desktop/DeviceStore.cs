using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

internal sealed class DeviceStore
{
    private const int CurrentVersion = 2;
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private readonly string _directory;
    private readonly string _mutexName;
    internal string StorePath { get { return _path; } }

    public DeviceStore()
        : this(ResolveStorePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remsup", "Operator")))
    {
    }

    internal static string ResolveStorePath(string directory)
    {
        var current=Path.Combine(directory,"devices-v2.json");
        var earlier=Path.Combine(directory,"devices.json");
        if(File.Exists(current)||!File.Exists(earlier))return current;
        // Inspect only the format marker, never load/decrypt/migrate a v1 vault.
        try {
            if(new FileInfo(earlier).Length>MaximumFileBytes)return earlier;
            using(var stream=File.OpenRead(earlier)) {
                var marker=(DeviceStoreVersion)new DataContractJsonSerializer(typeof(DeviceStoreVersion)).ReadObject(stream);
                if(marker!=null&&marker.Version==1)return current;
            }
        } catch(IOException) { } catch(UnauthorizedAccessException) { } catch(SerializationException) { }
        // Existing v2 candidate profiles retain their path. Ambiguous/damaged files
        // stay fail-safe on Load rather than disappearing behind a new empty store.
        return earlier;
    }

    public DeviceStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A device store path is required.", "path");
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(_directory)) throw new ArgumentException("The device store path must include a directory.", "path");
        _mutexName = CreateMutexName(_path);
    }

    public DeviceStoreSnapshot Load()
    {
        return WithMutex(delegate
        {
            EnsurePrivateDirectory();
            return ReadCurrent();
        });
    }

    public void Save(DeviceStoreSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException("snapshot");
        if (!string.Equals(snapshot.StorePath, _path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The snapshot belongs to a different device store.", "snapshot");

        WithMutex(delegate
        {
            EnsurePrivateDirectory();
            var current = ReadCurrent();
            if (snapshot.Revision != current.Revision || !string.Equals(snapshot.Fingerprint, current.Fingerprint, StringComparison.Ordinal))
                throw new DeviceStoreConflictException("The device list changed after it was loaded. Reload it before saving.");

            var nextRevision = checked(current.Revision + 1);
            var document = CreateDocument(snapshot.GetRecordsForSave(), nextRevision);
            var bytes = Serialize(document);
            try
            {
                var fingerprint = Fingerprint(bytes);
                WriteAtomically(bytes);
                snapshot.SetSavedState(nextRevision, fingerprint);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
            return 0;
        });
    }

    private DeviceStoreSnapshot ReadCurrent()
    {
        if (!File.Exists(_path)) return new DeviceStoreSnapshot(_path, 0, null, new List<DeviceRecord>());

        var bytes = File.ReadAllBytes(_path);
        if (bytes.Length == 0 || bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("The device vault is empty or exceeds its size limit; no data was changed.");

        try
        {
            var document = Deserialize(bytes);
            var snapshot = CreateSnapshot(document, Fingerprint(bytes), _path);
            EnsurePrivateFile();
            return snapshot;
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static DeviceStoreDocument CreateDocument(IList<DeviceRecord> records, long revision)
    {
        var document = new DeviceStoreDocument
        {
            Version = CurrentVersion,
            Revision = revision,
            Records = new List<StoredDeviceRecord>()
        };
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in records)
        {
            var record = source.Clone();
            record.ValidateAndNormalize();
            if (!ids.Add(record.RecordId)) throw new ArgumentException("Device record IDs must be unique.", "records");
            document.Records.Add(new StoredDeviceRecord
            {
                RecordId = record.RecordId,
                DisplayName = record.DisplayName,
                DeviceId = record.DeviceId,
                Transport = record.Transport == DeviceTransport.Relay ? "Relay" : "Lan",
                Host = record.Host,
                Pin = string.IsNullOrWhiteSpace(record.Pin)?null:record.Pin,
                Port = record.Port,
                RelayMode = record.Transport==DeviceTransport.Relay?(record.UseDefaultRelay?"Default":"Custom"):"Lan",
                Note = record.Note,
                IsFavorite = record.IsFavorite,
                LastSuccessfulConnectionUtc = record.LastSuccessfulConnectionUtc,
                ProtectedPassword = record.SavePermanentPassword ? CredentialProtector.Protect(record.PermanentPassword) : null
            });
        }
        return document;
    }

    private static DeviceStoreSnapshot CreateSnapshot(DeviceStoreDocument document, string fingerprint, string storePath)
    {
        if (document == null) throw new InvalidDataException("The device vault is corrupt; no data was changed.");
        if (document.Version != CurrentVersion) throw new InvalidDataException("Profile store format is unsupported by this v2 application. The file was preserved; use the matching application or contact support. No data was changed.");
        if (document.Revision < 0 || document.Records == null)
            throw new InvalidDataException("The device vault is corrupt; no data was changed.");

        var records = new List<DeviceRecord>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var stored in document.Records)
            {
                if (stored == null) throw new InvalidDataException("The device vault contains an empty record.");
                var record = new DeviceRecord
                {
                    RecordId = stored.RecordId,
                    DisplayName = stored.DisplayName,
                    DeviceId = stored.DeviceId,
                    Transport = ParseTransport(stored.Transport),
                    Host = stored.Host,
                    Pin = stored.Pin,
                    Port = stored.Port,
                    UseDefaultRelay = stored.RelayMode=="Default",
                    Note = stored.Note,
                    IsFavorite = stored.IsFavorite,
                    LastSuccessfulConnectionUtc = stored.LastSuccessfulConnectionUtc
                };
                if((record.Transport==DeviceTransport.Relay&&stored.RelayMode!="Default"&&stored.RelayMode!="Custom")||(record.Transport==DeviceTransport.Lan&&stored.RelayMode!="Lan"))throw new InvalidDataException("Unsupported device connection mode; no data was changed.");
                if(record.UseDefaultRelay&&(!string.IsNullOrEmpty(stored.Host)||stored.Port!=0))throw new InvalidDataException("Default relay record contains an override; no data was changed.");
                record.ValidateAndNormalize();
                if (!ids.Add(record.RecordId)) throw new InvalidDataException("The device vault contains duplicate record IDs.");
                if (stored.ProtectedPassword != null)
                {
                    record.PermanentPassword = CredentialProtector.Unprotect(stored.ProtectedPassword);
                    record.SavePermanentPassword = true;
                }
                records.Add(record);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("A device record or saved credential could not be read; no data was changed.", ex);
        }
        return new DeviceStoreSnapshot(storePath, document.Revision, fingerprint, records);
    }

    private static DeviceTransport ParseTransport(string value)
    {
        if (string.Equals(value, "Relay", StringComparison.Ordinal)) return DeviceTransport.Relay;
        if (string.Equals(value, "Lan", StringComparison.Ordinal)) return DeviceTransport.Lan;
        throw new InvalidDataException("The device vault contains an unsupported transport.");
    }

    private static byte[] Serialize(DeviceStoreDocument document)
    {
        try
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(DeviceStoreDocument)).WriteObject(stream, document);
                return stream.ToArray();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("The device vault could not be serialized.", ex);
        }
    }

    private static DeviceStoreDocument Deserialize(byte[] bytes)
    {
        try
        {
            using (var stream = new MemoryStream(bytes, false))
                return (DeviceStoreDocument)new DataContractJsonSerializer(typeof(DeviceStoreDocument)).ReadObject(stream);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("The device vault is corrupt; no data was changed.", ex);
        }
    }

    private void WriteAtomically(byte[] bytes)
    {
        var temporaryPath = Path.Combine(_directory, ".devices-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            new FileInfo(temporaryPath).SetAccessControl(CreatePrivateFileSecurity());
            using (var stream = new FileStream(temporaryPath, FileMode.Truncate, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(_path)) File.Replace(temporaryPath, _path, null);
            else File.Move(temporaryPath, _path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    private void EnsurePrivateDirectory()
    {
        Directory.CreateDirectory(_directory);
        var sid = WindowsIdentity.GetCurrent().User;
        if (sid == null) throw new UnauthorizedAccessException("The current Windows user could not be identified.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(security);
    }

    private void EnsurePrivateFile()
    {
        new FileInfo(_path).SetAccessControl(CreatePrivateFileSecurity());
    }

    private static FileSecurity CreatePrivateFileSecurity()
    {
        var sid = WindowsIdentity.GetCurrent().User;
        if (sid == null) throw new UnauthorizedAccessException("The current Windows user could not be identified.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static string CreateMutexName(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User;
        var identity = sid == null ? "unknown" : sid.Value;
        var bytes = Encoding.UTF8.GetBytes(path.ToUpperInvariant() + "|" + identity);
        try
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                try { return "Local\\Remsup.DeviceStore." + BitConverter.ToString(hash).Replace("-", ""); }
                finally { Array.Clear(hash, 0, hash.Length); }
            }
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static string Fingerprint(byte[] bytes)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(bytes);
            try { return Convert.ToBase64String(hash); }
            finally { Array.Clear(hash, 0, hash.Length); }
        }
    }

    private T WithMutex<T>(Func<T> action)
    {
        using (var mutex = new Mutex(false, _mutexName))
        {
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(); }
                catch (AbandonedMutexException) { acquired = true; }
                return action();
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
    }
}

internal sealed class DeviceStoreSnapshot
{
    private readonly string _storePath;
    private readonly List<DeviceRecord> _records;
    private string _fingerprint;

    internal DeviceStoreSnapshot(string storePath, long revision, string fingerprint, IList<DeviceRecord> records)
    {
        _storePath = storePath;
        Revision = revision;
        _fingerprint = fingerprint;
        _records = new List<DeviceRecord>();
        foreach (var record in records) _records.Add(record.Clone());
    }

    public long Revision { get; private set; }

    public IList<DeviceRecord> Records
    {
        get
        {
            var copy = new List<DeviceRecord>();
            foreach (var record in _records) copy.Add(record.Clone());
            return copy;
        }
    }

    internal string StorePath { get { return _storePath; } }
    internal string Fingerprint { get { return _fingerprint; } }

    public DeviceRecord Find(string recordId)
    {
        Guid parsed;
        if (!Guid.TryParse(recordId, out parsed)) return null;
        var id = parsed.ToString("D");
        var record = _records.Find(delegate(DeviceRecord item) { return string.Equals(item.RecordId, id, StringComparison.OrdinalIgnoreCase); });
        return record == null ? null : record.Clone();
    }

    public IList<DeviceRecord> FindDuplicates(DeviceRecord candidate)
    {
        if (candidate == null) throw new ArgumentNullException("candidate");
        var normalizedId = DeviceRecord.NormalizeDeviceId(candidate.DeviceId);
        var normalizedHost = candidate.UseDefaultRelay?"":DeviceRecord.NormalizeHost(candidate.Host);
        var matches = new List<DeviceRecord>();
        foreach (var record in _records)
        {
            if (string.Equals(record.RecordId, candidate.RecordId, StringComparison.OrdinalIgnoreCase)) continue;
            var sameDevice = string.Equals(record.DeviceId, normalizedId, StringComparison.Ordinal);
            var sameEndpoint = record.Transport == candidate.Transport && record.UseDefaultRelay==candidate.UseDefaultRelay && record.Port == candidate.Port &&
                string.Equals(record.UseDefaultRelay?"":DeviceRecord.NormalizeHost(record.Host), normalizedHost, StringComparison.Ordinal);
            if (sameDevice || sameEndpoint) matches.Add(record.Clone());
        }
        return matches;
    }

    public void Upsert(DeviceRecord record)
    {
        if (record == null) throw new ArgumentNullException("record");
        var copy = record.Clone();
        copy.ValidateAndNormalize();
        var index = _records.FindIndex(delegate(DeviceRecord item) { return string.Equals(item.RecordId, copy.RecordId, StringComparison.OrdinalIgnoreCase); });
        if (index < 0) _records.Add(copy);
        else _records[index] = copy;
    }

    public bool Remove(string recordId)
    {
        Guid parsed;
        if (!Guid.TryParse(recordId, out parsed)) return false;
        var id = parsed.ToString("D");
        var index = _records.FindIndex(delegate(DeviceRecord item) { return string.Equals(item.RecordId, id, StringComparison.OrdinalIgnoreCase); });
        if (index < 0) return false;
        _records.RemoveAt(index);
        return true;
    }

    internal IList<DeviceRecord> GetRecordsForSave()
    {
        return Records;
    }

    internal void SetSavedState(long revision, string fingerprint)
    {
        Revision = revision;
        _fingerprint = fingerprint;
    }
}

internal sealed class DeviceStoreConflictException : IOException
{
    public DeviceStoreConflictException(string message) : base(message) { }
}

[DataContract]
internal sealed class DeviceStoreVersion
{
    [DataMember(Name = "version", IsRequired = true)] public int Version { get; set; }
}

[DataContract]
internal sealed class DeviceStoreDocument
{
    [DataMember(Name = "version", IsRequired = true, Order = 1)] public int Version;
    [DataMember(Name = "revision", IsRequired = true, Order = 2)] public long Revision;
    [DataMember(Name = "records", IsRequired = true, Order = 3)] public List<StoredDeviceRecord> Records;
}

[DataContract]
internal sealed class StoredDeviceRecord
{
    [DataMember(Name = "recordId", IsRequired = true, Order = 1)] public string RecordId;
    [DataMember(Name = "displayName", IsRequired = true, Order = 2)] public string DisplayName;
    [DataMember(Name = "deviceId", IsRequired = true, Order = 3)] public string DeviceId;
    [DataMember(Name = "transport", IsRequired = true, Order = 4)] public string Transport;
    [DataMember(Name = "host", IsRequired = true, Order = 5)] public string Host;
    [DataMember(Name = "port", IsRequired = true, Order = 6)] public int Port;
    [DataMember(Name = "note", IsRequired = true, Order = 7)] public string Note;
    [DataMember(Name = "isFavorite", IsRequired = true, Order = 8)] public bool IsFavorite;
    [DataMember(Name = "lastSuccessfulConnectionUtc", IsRequired = true, Order = 9)] public DateTime? LastSuccessfulConnectionUtc;
    [DataMember(Name = "protectedPassword", EmitDefaultValue = false, Order = 10)] public string ProtectedPassword;
    // Read the version before rejecting old documents; v2 still requires a valid mode below.
    [DataMember(Name = "relayMode", Order = 11)] public string RelayMode;
    [DataMember(Name = "pin", EmitDefaultValue = false, Order = 12)] public string Pin;
}
