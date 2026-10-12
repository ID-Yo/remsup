using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

internal static class DeviceStoreTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemsupDeviceStoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RoundTripsEncryptedRecordsAndDoesNotPersistOtp(root);
            CorruptFileCannotBeOverwritten(root);
            UnsupportedVersionCannotBeOverwritten(root);
            UnreadableCredentialCannotBeOverwritten(root);
            StaleSnapshotsCannotOverwriteNewerUpdates(root);
            EndpointDuplicatesAreNormalizedAndRecordsAreCloned(root);
            Console.WriteLine("PASS DPAPI vault roundtrip/corruption/version/cloning/stale-write checks");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void RoundTripsEncryptedRecordsAndDoesNotPersistOtp(string root)
    {
        const string secret = "vault-test-permanent-password-67291";
        var path = Path.Combine(root, "roundtrip", "devices.json");
        var store = new DeviceStore(path);
        var snapshot = store.Load();
        var record = NewRecord("345 345 345", "relay.example.test", 8443);
        record.DisplayName = "Test support device";
        record.Note = "office workstation";
        record.IsFavorite = true;
        record.LastSuccessfulConnectionUtc = new DateTime(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);
        record.PermanentPassword = secret;
        record.SavePermanentPassword = true;
        snapshot.Upsert(record);
        store.Save(snapshot);

        var json = File.ReadAllText(path);
        Check(!json.Contains(secret), "permanent password must not appear in the store file");
        Check(json.IndexOf("otp", StringComparison.OrdinalIgnoreCase) < 0, "OTP must not appear in the store file");
        Check(typeof(DeviceRecord).GetField("Otp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null, "device record must not have an OTP field");
        Check(typeof(DeviceRecord).GetProperty("Otp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null, "device record must not have an OTP property");

        var loaded = new DeviceStore(path).Load();
        Check(loaded.Revision == snapshot.Revision, "store revision must round trip");
        Check(loaded.Records.Count == 1, "one device must round trip");
        var saved = loaded.Records[0];
        Check(saved.RecordId == record.RecordId, "record identity must round trip");
        Check(saved.DeviceId == "345345345", "device id must be normalized");
        Check(saved.DisplayName == record.DisplayName && saved.Note == record.Note, "metadata must round trip");
        Check(saved.Transport == DeviceTransport.Relay && saved.Host == "relay.example.test" && saved.Port == 8443, "endpoint must round trip");
        Check(saved.IsFavorite, "favorite state must round trip");
        Check(saved.LastSuccessfulConnectionUtc == record.LastSuccessfulConnectionUtc, "UTC connection time must round trip");
        Check(saved.SavePermanentPassword && saved.PermanentPassword == secret, "explicitly saved password must decrypt for the current user");
    }

    private static void CorruptFileCannotBeOverwritten(string root)
    {
        var path = Path.Combine(root, "corrupt", "devices.json");
        var store = new DeviceStore(path);
        var snapshot = store.Load();
        snapshot.Upsert(NewRecord("123456789", "relay.example.test", 8443));
        store.Save(snapshot);
        File.WriteAllText(path, "{ broken store data");
        var corruptContents = File.ReadAllText(path);
        ExpectAnyException(delegate { new DeviceStore(path).Load(); }, "corrupt store must fail to load");
        ExpectAnyException(delegate { store.Save(snapshot); }, "corrupt store must not be overwritten by a stale snapshot");
        Check(File.ReadAllText(path) == corruptContents, "corrupt file contents must be preserved after failed save");
    }

    private static void UnsupportedVersionCannotBeOverwritten(string root)
    {
        var path = Path.Combine(root, "unsupported", "devices.json");
        var store = new DeviceStore(path);
        var snapshot = store.Load();
        snapshot.Upsert(NewRecord("123456789", "relay.example.test", 8443));
        store.Save(snapshot);
        var contents = File.ReadAllText(path);
        const string versionMarker = "\"version\":2";
        Check(contents.Contains(versionMarker), "versioned store must write its schema version");
        contents = contents.Replace(versionMarker, "\"version\":99");
        File.WriteAllText(path, contents);
        var unsupportedContents = File.ReadAllText(path);
        ExpectAnyException(delegate { new DeviceStore(path).Load(); }, "unsupported store version must fail to load");
        ExpectAnyException(delegate { store.Save(snapshot); }, "unsupported version must not be overwritten by a stale snapshot");
        Check(File.ReadAllText(path) == unsupportedContents, "unsupported-version file must be preserved after failed save");
    }

    private static void UnreadableCredentialCannotBeOverwritten(string root)
    {
        var path = Path.Combine(root, "bad-credential", "devices.json");
        var store = new DeviceStore(path);
        var snapshot = store.Load();
        var record = NewRecord("123456789", "relay.example.test", 8443);
        record.PermanentPassword = "credential-to-tamper";
        record.SavePermanentPassword = true;
        snapshot.Upsert(record);
        store.Save(snapshot);
        var contents = File.ReadAllText(path);
        var tampered = new Regex("\"protectedPassword\":\"[^\"]*\"").Replace(contents, "\"protectedPassword\":\"AQID\"", 1);
        Check(tampered != contents, "store must contain a protected credential field");
        File.WriteAllText(path, tampered);
        var unreadableContents = File.ReadAllText(path);
        ExpectAnyException(delegate { new DeviceStore(path).Load(); }, "a credential that cannot be unprotected must fail to load");
        ExpectAnyException(delegate { store.Save(snapshot); }, "unreadable credential must block writes");
        Check(File.ReadAllText(path) == unreadableContents, "unreadable-credential file must be preserved after failed save");
    }

    private static void StaleSnapshotsCannotOverwriteNewerUpdates(string root)
    {
        var path = Path.Combine(root, "concurrent", "devices.json");
        var store = new DeviceStore(path);
        var initial = store.Load();
        initial.Upsert(NewRecord("123456789", "relay.example.test", 8443));
        store.Save(initial);

        var first = store.Load();
        var stale = store.Load();
        var updated = first.Find(first.Records[0].RecordId);
        updated.Note = "first writer";
        first.Upsert(updated);
        store.Save(first);

        var staleRecord = stale.Find(stale.Records[0].RecordId);
        staleRecord.Note = "stale writer";
        stale.Upsert(staleRecord);
        Expect<DeviceStoreConflictException>(delegate { store.Save(stale); }, "stale snapshot must be rejected");
        Check(new DeviceStore(path).Load().Records[0].Note == "first writer", "stale update must not replace the newer record");
        Check(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "successful atomic updates must clean temporary files");
    }

    private static void EndpointDuplicatesAreNormalizedAndRecordsAreCloned(string root)
    {
        var path = Path.Combine(root, "duplicates", "devices.json");
        var store = new DeviceStore(path);
        var snapshot = store.Load();
        var original = NewRecord("345 345 345", "Relay.Example.Test.", 8443);
        snapshot.Upsert(original);
        original.DisplayName = "mutated outside snapshot";
        Check(snapshot.Find(original.RecordId).DisplayName == "Device", "upsert must clone input records");

        var duplicateEndpoint = NewRecord("987654321", "relay.example.test", 8443);
        snapshot.Upsert(duplicateEndpoint);
        Check(snapshot.Records.Count == 2, "different device records may intentionally share an endpoint");
        var candidate = NewRecord("555555555", "RELAY.EXAMPLE.TEST", 8443);
        var duplicates = snapshot.FindDuplicates(candidate);
        Check(duplicates.Count == 2, "endpoint duplicate search must normalize hostname case and trailing dot");
        duplicates[0].DisplayName = "mutation of returned match";
        Check(snapshot.Find(duplicates[0].RecordId).DisplayName == "Device", "duplicate results must be cloned");

        var sameId = NewRecord("345345345", "other.example.test", 9443);
        Check(snapshot.FindDuplicates(sameId).Count == 1, "duplicate search must normalize device id formatting");
        var updated = snapshot.Find(original.RecordId);
        updated.DisplayName = "updated";
        snapshot.Upsert(updated);
        snapshot.Remove(duplicateEndpoint.RecordId);
        store.Save(snapshot);
        Check(new DeviceStore(path).Load().Records.Count == 1, "remove and update must persist by record identity");
    }

    private static DeviceRecord NewRecord(string deviceId, string host, int port)
    {
        return new DeviceRecord
        {
            DisplayName = "Device",
            DeviceId = deviceId,
            Transport = DeviceTransport.Relay,
            Host = host,
            Port = port
        };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("DeviceStoreTests: " + message);
    }

    private static void Expect<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception("DeviceStoreTests: " + message);
    }

    private static void ExpectAnyException(Action action, string message)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new Exception("DeviceStoreTests: " + message);
    }
}
