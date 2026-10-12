using System;
using System.Collections.Generic;
using System.Diagnostics;

using System.IO;
using System.Linq;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


internal sealed class RemsupSettings
{
    public bool AllowScreenView { get; set; }
    public bool AllowScreenControl { get; set; }
    public bool AutoAcceptScreenSessions { get; set; }
    public string PermanentPassword { get; set; }
    public string RelayHost { get; set; }
    public int RelayPort { get; set; }
    public string RelayPin { get; set; }
    public bool AutoUpdates { get; set; }
    public bool AutoStartEnabled { get; set; }
    public bool LanEnabled { get; set; }
    public string LanAddress { get; set; }
    public int LanPort { get; set; }

    public RemsupSettings()
    {
        PermanentPassword = "";
        RelayHost = "";
        RelayPin = ""; AutoUpdates = true; RelayPort = 443; LanPort = 8444; LanAddress = "";
        AutoStartEnabled = false;
        AllowScreenView = true; AllowScreenControl = true;
        AutoAcceptScreenSessions = true;
    }
}

internal sealed class SupportStatus
{
    public string ComputerName { get; set; }
    public string SshdStatus { get; set; }
    public string SshdStartupType { get; set; }
    public string PortableFirewallRule { get; set; }
    public bool SupportKeyPresent { get; set; }
    public string DeviceId { get; set; }
    public string SupportPassword { get; set; }
    public List<string> LanAddresses { get; set; }

    public SupportStatus()
    {
        ComputerName = "";
        SshdStatus = "Unknown";
        SshdStartupType = "Unknown";
        PortableFirewallRule = "Unknown";
        DeviceId = "";
        SupportPassword = "";
        LanAddresses = new List<string>();
    }
}

internal static class SupportActions
{
    private const string FirewallRuleName = "Remsup Portable SSH LocalSubnet";
    private const string AutoStartRunValueName = "Remsup Support";
    private const string SupportPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBouMBKtydf8+nwbOXVYQYTNc4QxAvQ5nGPxWYhqJlER codex-remote-laptop-temp";
    private static readonly string ClientLogPath = Path.Combine(Path.GetTempPath(), "RemsupClient.log");
    private static readonly string RuntimeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemsupPortable");
    private static readonly string RuntimeInfoPath = Path.Combine(RuntimeDir, "runtime.txt");
    private static readonly string SettingsPath = Path.Combine(RuntimeDir, "settings.txt");
    private static readonly string RequestedOtpPath = Path.Combine(Path.GetTempPath(), "RemsupRequestedOtp.txt");

    public static void WriteClientLog(string message)
    {
        try
        {
            File.AppendAllText(ClientLogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch
        {
            // Logging must never break the support client.
        }
    }

    public static string ReadPermanentPasswordSetting()
    {
        return LoadSettings().PermanentPassword;
    }

    public static RemsupSettings LoadSettings()
    {
        return LoadSettings(SettingsPath);
    }

    internal static RemsupSettings LoadSettings(string path,bool throwOnError=false)
    {
        var settings = new RemsupSettings { RelayPort = 0 };
        try
        {
            var lines=File.Exists(path)?File.ReadAllLines(path):new string[0];
            foreach (var line in lines)
            {
                var index = line.IndexOf('=');
                if (index < 0) continue;
                var key = line.Substring(0, index).Trim();
                var value = line.Substring(index + 1).Trim();
                if (key.Equals("PermanentPassword", StringComparison.OrdinalIgnoreCase)) settings.PermanentPassword = value;
                if (key == "RelayPin") settings.RelayPin = value;
                if (key == "AutoUpdates") settings.AutoUpdates = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (key == "LanEnabled") settings.LanEnabled = value == "True";
                if (key == "LanAddress") settings.LanAddress = value;
                if (key == "LanPort") { int lp; if (int.TryParse(value, out lp)) settings.LanPort = lp; }
                if (key == "AllowScreenView") settings.AllowScreenView = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (key == "AllowScreenControl") settings.AllowScreenControl = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (key == "AutoAcceptScreenSessions") settings.AutoAcceptScreenSessions = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (key.Equals("AutoStartEnabled", StringComparison.OrdinalIgnoreCase)) settings.AutoStartEnabled = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            var relay=RelayConfiguration.Parse(lines);settings.RelayHost=relay.Host;settings.RelayPort=relay.Port;settings.RelayPin=relay.Pin;
        }
        catch (Exception ex)
        {
            if(throwOnError)throw;
            WriteClientLog("Failed to read settings: " + ex.Message);
        }
        return settings;
    }

    public static void SavePermanentPasswordSetting(string password)
    {
        var settings = LoadSettings();
        settings.PermanentPassword = password;
        SaveSettings(settings);
    }

    public static void SaveSettings(RemsupSettings settings)
    {
        SaveSettings(settings,SettingsPath);
    }

    internal static void SaveSettings(RemsupSettings settings,string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[]
            {
                "PermanentPassword=" + (settings.PermanentPassword ?? "").Trim(),
                "RelayHost=" + (settings.RelayHost ?? "").Trim(),
                "RelayPort=" + settings.RelayPort, "RelayPin=" + (settings.RelayPin ?? "").Trim(), "AutoUpdates=" + settings.AutoUpdates,
                "LanEnabled=" + settings.LanEnabled, "LanAddress=" + settings.LanAddress, "LanPort=" + settings.LanPort,
                "AllowScreenView=" + settings.AllowScreenView, "AllowScreenControl=" + settings.AllowScreenControl,
                "AutoAcceptScreenSessions=" + settings.AutoAcceptScreenSessions,
                "AutoStartEnabled=" + (settings.AutoStartEnabled ? "true" : "false")
            }, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            WriteClientLog("Failed to save settings: " + ex.Message);
            throw;
        }
    }

    public static void WriteRequestedOneTimePassword(string password)
    {
        try
        {
            File.WriteAllText(RequestedOtpPath, password == null ? "" : password.Trim(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            WriteClientLog("Failed to write requested OTP: " + ex.Message);
        }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                var value = key == null ? null : key.GetValue(AutoStartRunValueName) as string;
                if (string.IsNullOrWhiteSpace(value)) return false;
                return value.IndexOf(Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch (Exception ex)
        {
            WriteClientLog("Failed to read auto-start setting: " + ex.Message);
            return false;
        }
    }

    public static void SetAutoStartEnabled(bool enabled)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (key == null) throw new InvalidOperationException("Unable to open current user Run key.");
                if (enabled)
                {
                    key.SetValue(AutoStartRunValueName, Quote(Process.GetCurrentProcess().MainModule.FileName));
                }
                else
                {
                    key.DeleteValue(AutoStartRunValueName, false);
                }
            }
        }
        catch (Exception ex)
        {
            WriteClientLog("Failed to update auto-start setting: " + ex.Message);
            throw;
        }
    }

    public static void StartSupport()
    {
        var password = ReadPermanentPasswordSetting();
        if (string.IsNullOrWhiteSpace(password))
        {
            password = ReadRequestedOneTimePassword();
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            password = GeneratePassword();
        }

        TryLegacyCleanup("firewall rule", RemoveFirewallRule);
        TryLegacyCleanup("support key", RemoveSupportKey);
        TryLegacyCleanup("support account", RemoveSupportAccount);
        WriteRuntimePassword(password);
    }

    public static void StopSupport()
    {
        TryLegacyCleanup("firewall rule", RemoveFirewallRule);
        TryLegacyCleanup("support key", RemoveSupportKey);
        TryLegacyCleanup("support account", RemoveSupportAccount);
        RemoveRuntimeFile();
    }

    private static void TryLegacyCleanup(string name, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            WriteClientLog("Legacy cleanup skipped for " + name + ": " + ex.Message);
        }
    }

    public static SupportStatus GetStatus()
    {
        var status = new SupportStatus();
        status.DeviceId = BuildDeviceId();
        status.ComputerName = Environment.MachineName;
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            if (nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    if (SupportHost.PrivateAddress(address.Address)) status.LanAddresses.Add(address.Address.ToString());
        return status;
    }
    private static SupportStatus ParseStatus(string output)
    {
        var status = new SupportStatus();
        foreach (var rawLine in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var index = rawLine.IndexOf('=');
            if (index < 0)
            {
                continue;
            }
            var key = rawLine.Substring(0, index).Trim();
            var value = rawLine.Substring(index + 1).Trim();
            switch (key)
            {
                case "ComputerName":
                    status.ComputerName = value;
                    break;
                case "SshdStatus":
                    status.SshdStatus = value;
                    break;
                case "SshdStartupType":
                    status.SshdStartupType = value;
                    break;
                case "PortableFirewallRule":
                    status.PortableFirewallRule = value;
                    break;
                case "SupportKeyPresent":
                    status.SupportKeyPresent = string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);
                    break;
                case "SupportPassword":
                    status.SupportPassword = value;
                    break;
                case "LanAddresses":
                    status.LanAddresses = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).ToList();
                    break;
            }
        }
        return status;
    }

    private static void RemoveSupportAccount()
    {
        RunPowerShell(@"
$username = 'remsup-support'
$user = Get-LocalUser -Name $username -ErrorAction SilentlyContinue
if ($user) {
  $sid = $user.Sid.Value
  Get-CimInstance Win32_UserProfile -ErrorAction SilentlyContinue | Where-Object { $_.SID -eq $sid } | Remove-CimInstance -ErrorAction SilentlyContinue
  Remove-LocalUser -Name $username
}
$legacyProfile = Join-Path $env:SystemDrive 'Users\remsup-support'
if (Test-Path $legacyProfile) {
  Remove-Item -LiteralPath $legacyProfile -Recurse -Force -ErrorAction SilentlyContinue
}
");
    }

    private static void WriteRuntimePassword(string password)
    {
        Directory.CreateDirectory(RuntimeDir);
        File.WriteAllLines(RuntimeInfoPath, new[]
        {
            "Username=" + Environment.UserName,
            "Password=" + (password ?? "")
        }, Encoding.UTF8);
    }

    private static void RemoveRuntimeFile()
    {
        try
        {
            if (File.Exists(RuntimeInfoPath))
            {
                File.Delete(RuntimeInfoPath);
            }
        }
        catch (Exception ex)
        {
            WriteClientLog("Failed to remove runtime file: " + ex.Message);
        }
    }

    private static string ReadRequestedOneTimePassword()
    {
        try
        {
            if (!File.Exists(RequestedOtpPath))
            {
                return "";
            }
            return File.ReadAllText(RequestedOtpPath, Encoding.UTF8).Trim();
        }
        catch (Exception ex)
        {
            WriteClientLog("Failed to read requested OTP: " + ex.Message);
            return "";
        }
    }

    public static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = new byte[8];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        var chars = bytes.Select(item => alphabet[item % alphabet.Length]).ToArray();
        return new string(chars);
    }

    private static string BuildDeviceId()
    {
        var seed = Environment.MachineName;
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
            {
                if (key != null)
                {
                    var machineGuid = key.GetValue("MachineGuid") as string;
                    if (!string.IsNullOrWhiteSpace(machineGuid))
                    {
                        seed = machineGuid;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteClientLog("Failed to read MachineGuid: " + ex.Message);
        }

        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
            var value = BitConverter.ToUInt32(hash, 0) % 900000000U + 100000000U;
            var digits = value.ToString("000000000");
            return digits.Substring(0, 3) + " " + digits.Substring(3, 3) + " " + digits.Substring(6, 3);
        }
    }

    private static void RemoveSupportKey()
    {
        var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "administrators_authorized_keys");
        if (!File.Exists(keyPath))
        {
            return;
        }

        var remaining = File.ReadAllLines(keyPath)
            .Where(line => line.Trim() != SupportPublicKey)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        File.WriteAllLines(keyPath, remaining);
        RunProcess("icacls.exe", Quote(keyPath) + " /inheritance:r /grant Administrators:F /grant SYSTEM:F");
    }

    private static void RemoveFirewallRule()
    {
        RunPowerShell("Get-NetFirewallRule -DisplayName 'Remsup Portable SSH LocalSubnet' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
    }

    private static void RunPowerShell(string command)
    {
        RunPowerShellWithOutput(command);
    }

    private static string RunPowerShellWithOutput(string command)
    {
        var wrapped =
            "$ProgressPreference='SilentlyContinue';" +
            "$VerbosePreference='SilentlyContinue';" +
            "$DebugPreference='SilentlyContinue';" +
            "$InformationPreference='SilentlyContinue';" +
            command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        return RunProcess("powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
    }

    private static string RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (var process = Process.Start(psi))
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                WriteClientLog(fileName + " exited with code " + process.ExitCode + ". stderr: " + error);
                var cleanError = CleanProcessError(error);
                if (string.IsNullOrWhiteSpace(cleanError))
                {
                    cleanError = "No readable error details. See " + ClientLogPath;
                }
                throw new InvalidOperationException(fileName + " exited with code " + process.ExitCode + Environment.NewLine + cleanError);
            }
            return output;
        }
    }

    private static string CleanProcessError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "";
        }

        var trimmed = error.Trim();
        if (!trimmed.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var readable = new List<string>();
        foreach (var line in trimmed.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.IndexOf("Preparing modules for first use", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }
            if (line.IndexOf(" S=\"Error\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf(" S='Error'", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                readable.Add(line.Replace("_x000D__x000A_", Environment.NewLine));
            }
        }

        return readable.Count == 0 ? "" : string.Join(Environment.NewLine, readable.ToArray());
    }

    public static bool IsAdministrator()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static void RunElevatedHelperOrAction(string helperArgument, Action action)
    {
        if (!IsAdministrator())
        {
            RunElevatedHelper(helperArgument);
            return;
        }
        action();
    }

    public static void RunElevatedHelper(string helperArgument)
    {
        var exe = Process.GetCurrentProcess().MainModule.FileName;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = helperArgument,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using (var process = Process.Start(psi))
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Elevated helper exited with code " + process.ExitCode);
            }
        }
    }

    private static string Quote(string value)
    {
        if (value == null)
        {
            return "\"\"";
        }
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
