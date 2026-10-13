using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
#if NET6_0_OR_GREATER
using System.Net.Http;
#endif

internal static class RelayTls
{
    internal static readonly string DefaultPin = Environment.GetEnvironmentVariable("REMSUP_RELAY_PIN") ?? "b826918726ce38a6825ad8ea9df38180dad007a9d724673d6df6a5d3efe8d0f4";
    internal static readonly string DefaultHost = Environment.GetEnvironmentVariable("REMSUP_RELAY_HOST") ?? "91.227.77.177";
    internal const int DefaultPort = 443;
    const SslProtocols Tls13 = (SslProtocols)12288;
    const SslProtocols AllowedProtocols = SslProtocols.Tls12 | Tls13;
    const int HandshakeTimeoutMs = 10000;

    internal static string NormalizePin(string pin)
    {
        string value = (pin ?? "").Trim();
        if (value.StartsWith("sha256/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var digest = Convert.FromBase64String(value.Substring(7));
                if (digest.Length != 32) throw new FormatException();
                value = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }
            catch (FormatException) { throw new ArgumentException("Enter the SPKI SHA-256 pin as 64 hexadecimal characters."); }
        }
        value = value.Replace(":", "").Replace(" ", "").ToLowerInvariant();
        if (value.Length != 64) throw new ArgumentException("Enter the SPKI SHA-256 pin as 64 hexadecimal characters.");
        for (int i = 0; i < value.Length; i++)
            if (!((value[i] >= '0' && value[i] <= '9') || (value[i] >= 'a' && value[i] <= 'f')))
                throw new ArgumentException("Enter the SPKI SHA-256 pin as 64 hexadecimal characters.");
        return value;
    }

    internal static string PinFor(string host, int port, string configuredPin, bool isLan)
    {
        if (isLan && string.IsNullOrWhiteSpace(configuredPin))
            throw new InvalidOperationException("A verified receiver SPKI SHA-256 pin is required for LAN. Copy it from the receiving computer's screen.");
        if (!string.IsNullOrWhiteSpace(configuredPin)) return NormalizePin(configuredPin);
        if (string.Equals((host ?? "").TrimEnd('.'), DefaultHost, StringComparison.OrdinalIgnoreCase) && port == DefaultPort)
            return DefaultPin;
        throw new InvalidOperationException("A relay SPKI SHA-256 pin is required for this endpoint.");
    }

    internal static void ValidateLoopbackFixture(string host)
    {
        System.Net.IPAddress address;
        if(!System.Net.IPAddress.TryParse(host,out address)||!System.Net.IPAddress.IsLoopback(address))
            throw new ArgumentException("Plaintext fixtures require an explicit loopback address.");
    }

    internal static SslStream OpenStream(TcpClient client, string expectedHost, string pin, CancellationToken token)
    {
        if (client == null) throw new ArgumentNullException("client");
        string expectedPin = NormalizePin(pin);
        bool certificateRejected = false;
        var stream = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) =>
        {
            bool matches = IsPinned(certificate, expectedPin);
            if (certificate != null && !matches) certificateRejected = true;
            return matches;
        });
        using (token.Register(() => { try { client.Close(); } catch { } }))
        {
            try
            {
                var authentication = System.Threading.Tasks.Task.Run(() => stream.AuthenticateAsClient(expectedHost, null, AllowedProtocols, false));
                if (!authentication.Wait(HandshakeTimeoutMs))
                    throw new TimeoutException();
                authentication.GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                return stream;
            }
            catch (Exception ex)
            {
                try { client.Close(); } catch { }
                try { stream.Dispose(); } catch { }
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                var aggregate = ex as AggregateException;
                if (aggregate != null) ex = aggregate.GetBaseException();
                if (ex is TimeoutException)
                    throw new IOException("TLS handshake failed: timed out.");
                if (ex is AuthenticationException)
                    throw new IOException(certificateRejected ? "Relay certificate does not match the configured pin." : "TLS handshake failed.");
                if (ex is IOException && ex.Message.StartsWith("TLS handshake failed", StringComparison.Ordinal)) throw;
                throw new IOException("TLS handshake failed.");
            }
        }
    }

    internal static string ComputePin(X509Certificate2 certificate)
    {
        if (certificate == null) throw new ArgumentNullException("certificate");
        var key = certificate.PublicKey;
        byte[] algorithm = DerSequence(DerOid(key.Oid.Value), key.EncodedParameters.RawData ?? new byte[0]);
        byte[] bitString = new byte[checked(key.EncodedKeyValue.RawData.Length + 1)];
        Buffer.BlockCopy(key.EncodedKeyValue.RawData, 0, bitString, 1, bitString.Length - 1);
        byte[] spki = DerSequence(algorithm, DerValue(0x03, bitString));
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(spki);
            try { return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); }
            finally { Array.Clear(hash, 0, hash.Length); Array.Clear(spki, 0, spki.Length); Array.Clear(bitString, 0, bitString.Length); }
        }
    }

    static bool IsPinned(X509Certificate certificate, string expectedPin)
    {
        if (certificate == null) return false;
        using (var parsed = new X509Certificate2(certificate))
            return string.Equals(ComputePin(parsed), expectedPin, StringComparison.Ordinal);
    }

#if NET6_0_OR_GREATER
    internal static HttpClient CreatePinnedHttpClient(string host, string pin)
    {
        string expectedPin = NormalizePin(PinFor(host, 443, pin, false));
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            SslProtocols = AllowedProtocols,
            ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                certificate != null && string.Equals(ComputePin(new X509Certificate2(certificate)), expectedPin, StringComparison.Ordinal)
        };
        return new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }
#endif

    static byte[] DerSequence(params byte[][] values)
    {
        int length = 0;
        foreach (var value in values) length = checked(length + value.Length);
        byte[] content = new byte[length]; int offset = 0;
        foreach (var value in values) { Buffer.BlockCopy(value, 0, content, offset, value.Length); offset += value.Length; }
        return DerValue(0x30, content);
    }

    static byte[] DerOid(string oid)
    {
        string[] parts = oid.Split('.');
        var content = new System.Collections.Generic.List<byte>();
        ulong first = ulong.Parse(parts[0]), second = ulong.Parse(parts[1]);
        WriteBase128(content, checked(first * 40 + second));
        for (int i = 2; i < parts.Length; i++) WriteBase128(content, ulong.Parse(parts[i]));
        return DerValue(0x06, content.ToArray());
    }

    static void WriteBase128(System.Collections.Generic.List<byte> output, ulong value)
    {
        byte[] temp = new byte[10]; int count = 0;
        do { temp[count++] = (byte)(value & 0x7f); value >>= 7; } while (value != 0);
        while (count > 0) { byte item = temp[--count]; if (count > 0) item |= 0x80; output.Add(item); }
    }

    static byte[] DerValue(byte tag, byte[] content)
    {
        int length = content.Length;
        byte[] lengthBytes;
        if (length < 128) lengthBytes = new[] { (byte)length };
        else
        {
            int count = 0, value = length; while (value > 0) { count++; value >>= 8; }
            lengthBytes = new byte[count + 1]; lengthBytes[0] = (byte)(0x80 | count);
            for (int i = count; i > 0; i--) { lengthBytes[i] = (byte)(length & 0xff); length >>= 8; }
        }
        var result = new byte[checked(1 + lengthBytes.Length + content.Length)]; result[0] = tag;
        Buffer.BlockCopy(lengthBytes, 0, result, 1, lengthBytes.Length); Buffer.BlockCopy(content, 0, result, 1 + lengthBytes.Length, content.Length);
        return result;
    }
}
