using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

internal static class RemsupPortable
{
    private const string AppName = "Remsup Portable Support";
    private const string FirewallRuleName = "Remsup Portable SSH LocalSubnet";
    private const string SupportPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBouMBKtydf8+nwbOXVYQYTNc4QxAvQ5nGPxWYhqJlER codex-remote-laptop-temp";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var command = args.Length == 0 ? "start" : args[0].Trim().ToLowerInvariant();

        if (command == "help" || command == "--help" || command == "/?")
        {
            PrintHelp();
            return 0;
        }

        if (!IsAdministrator())
        {
            Console.WriteLine("Administrator privileges are required. Relaunching with UAC prompt...");
            return RelaunchAsAdmin(args);
        }

        try
        {
            switch (command)
            {
                case "start":
                    StartSupport();
                    return 0;
                case "status":
                    PrintStatus();
                    return 0;
                case "stop":
                case "uninstall":
                    StopSupport(removeKey: true);
                    return 0;
                default:
                    Console.Error.WriteLine("Unknown command: " + command);
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(AppName);
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  RemsupPortable.exe start   - enable LAN SSH support for this test session");
        Console.WriteLine("  RemsupPortable.exe status  - print current SSH/support status");
        Console.WriteLine("  RemsupPortable.exe stop    - remove portable rule/key and stop SSH");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  This portable build does not install a Remsup service or scheduled task.");
        Console.WriteLine("  It uses standard Windows OpenSSH and a LocalSubnet firewall rule.");
    }

    private static void StartSupport()
    {
        Console.WriteLine("Starting " + AppName + "...");
        EnsureOpenSshCapability("Server");
        EnsureOpenSshCapability("Client");
        EnsureSshdConfig();
        AddSupportKey();
        RunPowerShell("Set-Service -Name sshd -StartupType Manual; Start-Service -Name sshd");
        EnsureFirewallRule();
        PrintStatus();
        Console.WriteLine();
        Console.WriteLine("Support LAN SSH is enabled. It will not auto-start after reboot in portable mode.");
    }

    private static void StopSupport(bool removeKey)
    {
        Console.WriteLine("Stopping " + AppName + "...");
        RemoveFirewallRule();
        if (removeKey)
        {
            RemoveSupportKey();
        }
        RunPowerShell("Stop-Service -Name sshd -ErrorAction SilentlyContinue; Set-Service -Name sshd -StartupType Manual -ErrorAction SilentlyContinue");
        PrintStatus();
        Console.WriteLine();
        Console.WriteLine("Portable support access stopped.");
    }

    private static void PrintStatus()
    {
        Console.WriteLine();
        Console.WriteLine("Status:");
        RunPowerShell(@"
$svc = Get-Service sshd -ErrorAction SilentlyContinue
$rule = Get-NetFirewallRule -DisplayName 'Remsup Portable SSH LocalSubnet' -ErrorAction SilentlyContinue
$ips = Get-NetIPAddress -AddressFamily IPv4 |
  Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
  Select-Object -ExpandProperty IPAddress
$keyPath = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
$keyPresent = $false
if (Test-Path $keyPath) {
  $keyPresent = (Select-String -LiteralPath $keyPath -SimpleMatch 'codex-remote-laptop-temp' -Quiet)
}
[pscustomobject]@{
  ComputerName = $env:COMPUTERNAME
  SshdStatus = if ($svc) { $svc.Status.ToString() } else { 'Not installed' }
  SshdStartupType = if ($svc) { $svc.StartType.ToString() } else { 'Not installed' }
  PortableFirewallRule = if ($rule) { $rule.Enabled.ToString() } else { 'Missing' }
  SupportKeyPresent = $keyPresent
  LanAddresses = ($ips -join ', ')
} | Format-List
");
    }

    private static void EnsureOpenSshCapability(string capability)
    {
        Console.WriteLine("Checking OpenSSH " + capability + "...");
        var name = "OpenSSH." + capability + "~~~~0.0.1.0";
        RunPowerShell("$cap = Get-WindowsCapability -Online -Name '" + name + "'; if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name '" + name + "' | Out-Null }");
    }

    private static void EnsureSshdConfig()
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");
        var configPath = Path.Combine(sshDir, "sshd_config");
        if (!File.Exists(configPath))
        {
            return;
        }

        var content = File.ReadAllText(configPath);
        if (content.IndexOf("PubkeyAuthentication yes", StringComparison.OrdinalIgnoreCase) < 0)
        {
            File.AppendAllText(configPath, Environment.NewLine + "PubkeyAuthentication yes" + Environment.NewLine);
        }

        if (content.IndexOf("Match Group administrators", StringComparison.OrdinalIgnoreCase) < 0)
        {
            File.AppendAllText(configPath,
                Environment.NewLine +
                "Match Group administrators" + Environment.NewLine +
                "       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys" + Environment.NewLine);
        }
    }

    private static void AddSupportKey()
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");
        var keyPath = Path.Combine(sshDir, "administrators_authorized_keys");
        Directory.CreateDirectory(sshDir);

        var existing = File.Exists(keyPath) ? File.ReadAllLines(keyPath).ToList() : new System.Collections.Generic.List<string>();
        if (!existing.Any(line => line.Trim() == SupportPublicKey))
        {
            existing.Add(SupportPublicKey);
            File.WriteAllLines(keyPath, existing.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
        }

        RunProcess("icacls.exe", Quote(keyPath) + " /inheritance:r /grant Administrators:F /grant SYSTEM:F");
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

    private static void EnsureFirewallRule()
    {
        RunPowerShell(@"
$rule = Get-NetFirewallRule -DisplayName 'Remsup Portable SSH LocalSubnet' -ErrorAction SilentlyContinue
if ($rule) {
  Enable-NetFirewallRule -DisplayName 'Remsup Portable SSH LocalSubnet' | Out-Null
} else {
  New-NetFirewallRule -DisplayName 'Remsup Portable SSH LocalSubnet' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22 -RemoteAddress LocalSubnet -Profile Private,Domain | Out-Null
}
");
    }

    private static void RemoveFirewallRule()
    {
        RunPowerShell("Get-NetFirewallRule -DisplayName 'Remsup Portable SSH LocalSubnet' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
    }

    private static void RunPowerShell(string command)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        RunProcess("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded);
    }

    private static void RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using (var process = Process.Start(psi))
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (!string.IsNullOrWhiteSpace(output))
            {
                Console.Write(output);
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                Console.Error.Write(error);
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(fileName + " exited with code " + process.ExitCode);
            }
        }
    }

    private static bool IsAdministrator()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static int RelaunchAsAdmin(string[] args)
    {
        var exe = Process.GetCurrentProcess().MainModule.FileName;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(" ", args.Select(Quote))
        };
        Process.Start(psi);
        return 0;
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
