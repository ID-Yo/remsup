using System;
using System.Security.Cryptography;
using System.Text;

internal static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Remsup.Operator.DeviceStore.v1");
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("A non-empty credential is required.", "value");
        var clearBytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var encryptedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
            try { return Convert.ToBase64String(encryptedBytes); }
            finally { Array.Clear(encryptedBytes, 0, encryptedBytes.Length); }
        }
        finally
        {
            Array.Clear(clearBytes, 0, clearBytes.Length);
        }
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) throw new ArgumentException("Protected credential is required.", "protectedValue");
        var protectedBytes = Convert.FromBase64String(protectedValue);
        try
        {
            var clearBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try { return StrictUtf8.GetString(clearBytes); }
            finally { Array.Clear(clearBytes, 0, clearBytes.Length); }
        }
        finally
        {
            Array.Clear(protectedBytes, 0, protectedBytes.Length);
        }
    }
}
