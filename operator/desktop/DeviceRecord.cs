using System;
using System.Collections.Generic;
using System.Linq;

internal enum DeviceTransport
{
    Relay = 0,
    Lan = 1
}

internal sealed class DeviceRecord
{
    public DeviceRecord()
    {
        RecordId = Guid.NewGuid().ToString("D");
        Transport = DeviceTransport.Relay;
        Port = 443;
        DisplayName = "";
        DeviceId = "";
        Host = "";
        Pin = "";
        Note = "";
        PermanentPassword = "";
    }

    public string RecordId { get; set; }
    public string DisplayName { get; set; }
    public string DeviceId { get; set; }
    public DeviceTransport Transport { get; set; }
    public string Host { get; set; }
    public string Pin { get; set; }
    public bool UseDefaultRelay { get; set; }
    public int Port { get; set; }
    public string Note { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime? LastSuccessfulConnectionUtc { get; set; }

    // This value exists only in memory. DeviceStore persists it only when the
    // operator explicitly enables SavePermanentPassword, and then only as DPAPI data.
    public string PermanentPassword { get; set; }
    public bool SavePermanentPassword { get; set; }

    public DeviceRecord Clone()
    {
        return new DeviceRecord
        {
            RecordId = RecordId,
            DisplayName = DisplayName,
            DeviceId = DeviceId,
            Transport = Transport,
            Host = Host,
            Pin = Pin,
            UseDefaultRelay = UseDefaultRelay,
            Port = Port,
            Note = Note,
            IsFavorite = IsFavorite,
            LastSuccessfulConnectionUtc = LastSuccessfulConnectionUtc,
            PermanentPassword = PermanentPassword,
            SavePermanentPassword = SavePermanentPassword
        };
    }

    public static string NormalizeDeviceId(string value)
    {
        if (value == null) throw new ArgumentNullException("value");
        var digits = new List<char>();
        foreach (var c in value)
        {
            if (c >= '0' && c <= '9') digits.Add(c);
            else if (!char.IsWhiteSpace(c) && c != '-' && c != '(' && c != ')' && c != '.')
                throw new ArgumentException("Device ID may contain only digits and common formatting characters.", "value");
        }
        if (digits.Count != 9) throw new ArgumentException("Device ID must contain exactly nine digits.", "value");
        return new string(digits.ToArray());
    }

    internal static string NormalizeHost(string value)
    {
        if (value == null) throw new ArgumentNullException("value");
        var host = value.Trim();
        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl))
            throw new ArgumentException("Host must be a non-empty name or address without whitespace.", "value");
        if (host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']')
            host = host.Substring(1, host.Length - 2);
        if (host.IndexOf(':') < 0) host = host.TrimEnd('.');
        if (host.Length == 0) throw new ArgumentException("Host must be a non-empty name or address.", "value");
        return host.ToLowerInvariant();
    }

    internal void ValidateAndNormalize()
    {
        Guid parsedId;
        if (string.IsNullOrWhiteSpace(RecordId) || !Guid.TryParse(RecordId, out parsedId))
            throw new ArgumentException("Record ID must be a GUID.", "RecordId");
        RecordId = parsedId.ToString("D");
        DisplayName = (DisplayName ?? "").Trim();
        if (DisplayName.Length == 0) throw new ArgumentException("Display name is required.", "DisplayName");
        DeviceId = NormalizeDeviceId(DeviceId);
        if (Transport != DeviceTransport.Relay && Transport != DeviceTransport.Lan)
            throw new ArgumentException("Transport is invalid.", "Transport");
        if(Transport==DeviceTransport.Relay&&UseDefaultRelay){Host="";Port=0;Pin="";}
        else {UseDefaultRelay=false;Host=NormalizeHost(Host);if (Port < 1 || Port > 65535) throw new ArgumentOutOfRangeException("Port");if(!string.IsNullOrWhiteSpace(Pin))Pin=RelayTls.NormalizePin(Pin);else Pin="";}
        Note = Note ?? "";
        PermanentPassword = PermanentPassword ?? "";
        if (LastSuccessfulConnectionUtc.HasValue)
        {
            if (LastSuccessfulConnectionUtc.Value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Last successful connection time must be UTC.", "LastSuccessfulConnectionUtc");
        }
        if (SavePermanentPassword && PermanentPassword.Length == 0)
            throw new ArgumentException("A permanent password is required when saving it.", "PermanentPassword");
    }
}
