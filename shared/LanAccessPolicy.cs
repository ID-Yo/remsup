using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
internal static class LanAccessPolicy
{
    public static bool IsPrivateProfile(IPAddress address)
    {
        return NetworkCategory(address)=="Private";
    }
    public static string NetworkCategory(IPAddress address)
    {
        int index = -1;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            if (nic.OperationalStatus == OperationalStatus.Up)
                foreach (var item in nic.GetIPProperties().UnicastAddresses)
                    if (item.Address.Equals(address)) index = nic.GetIPProperties().GetIPv4Properties().Index;
        if (index < 0) return "";
        var script = "Get-NetConnectionProfile -InterfaceIndex " + index + " -ErrorAction SilentlyContinue | Select-Object -ExpandProperty NetworkCategory";
        using (var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            if (!process.WaitForExit(10000)) { process.Kill(); return ""; }
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : "";
        }
    }
    public static string UnavailableReason(IPAddress address,int port)
    {
        return UnavailableReason(address,port,SupportHost.LocalPrivateAddress,NetworkCategory,HasRule);
    }
    internal static string UnavailableReason(IPAddress address,int port,Func<IPAddress,bool> active,Func<IPAddress,string> category,Func<IPAddress,int,bool> rule)
    {
        if(!active(address))return "LAN IP is inactive. Refresh addresses and choose this computer's active private IPv4.";
        if(port<1||port>65535)return "LAN port must be 1-65535.";
        string profile=category(address);
        if(profile=="Public")return "The network is Public — change it to Private in Windows Settings before using LAN.";
        if(profile!="Private")return "LAN requires a Windows Private network. Check the network profile in Windows Settings.";
        if(!rule(address,port))return "The scoped LAN firewall rule is missing. Select Prepare LAN firewall for this address and app.";
        return "";
    }
    public static bool SameSubnet(IPAddress local, IPAddress remote)
    {
        if (local.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || local.AddressFamily != remote.AddressFamily) return false;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var item in nic.GetIPProperties().UnicastAddresses)
                if (item.Address.Equals(local) && item.IPv4Mask != null)
                {
                    var a = local.GetAddressBytes(); var b = remote.GetAddressBytes(); var mask = item.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) if ((a[i] & mask[i]) != (b[i] & mask[i])) return false;
                    return true;
                }
        return false;
    }
    internal static string ExecutablePath
    {
        get
        {
#if NET10_0_OR_GREATER
            return Environment.ProcessPath;
#else
            return Assembly.GetEntryAssembly().Location;
#endif
        }
    }
    public static string RuleName(IPAddress address, int port)
    {
        using (var hash = SHA256.Create())
        {
            var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(ExecutablePath.ToLowerInvariant()));
            return "Remsup LAN " + BitConverter.ToString(bytes, 0, 6).Replace("-", "") + " " + address + ":" + port;
        }
    }
    public static bool HasRule(IPAddress address, int port)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
        try
        {
            foreach (dynamic rule in policy.Rules)
                try
                {
                    if (rule.Name == RuleName(address, port))
                        return rule.Enabled && rule.Direction == 1 && rule.Action == 1 && rule.Profiles == 2
                            && rule.Protocol == 6 && rule.LocalPorts == port.ToString()
                            && string.Equals((string)rule.ApplicationName, ExecutablePath, StringComparison.OrdinalIgnoreCase)
                            && ((string)rule.RemoteAddresses).Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase)
                            && (((string)rule.LocalAddresses).Equals(address.ToString(), StringComparison.Ordinal)
                                || ((string)rule.LocalAddresses).Equals(address.ToString()+"/255.255.255.255", StringComparison.Ordinal));
                }
                finally { Marshal.ReleaseComObject(rule); }
            return false;
        }
        finally { Marshal.ReleaseComObject(policy); }
    }
    internal static bool ShouldRemoveRule(string name,string application,string current,bool all)
    {
        return name!=null&&System.Text.RegularExpressions.Regex.IsMatch(name,@"^Remsup LAN [0-9A-F]{12} [0-9]{1,3}(?:\.[0-9]{1,3}){3}:[0-9]{1,5}$")
            && (all||!string.Equals(application,current,StringComparison.OrdinalIgnoreCase));
    }
    public static bool HasObsoleteRules()
    {
        dynamic policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
        try {foreach(dynamic rule in policy.Rules)try {if(ShouldRemoveRule((string)rule.Name,(string)rule.ApplicationName,ExecutablePath,false))return true;}finally{Marshal.ReleaseComObject(rule);}return false;}
        finally{Marshal.ReleaseComObject(policy);}
    }
    static void RemoveOwnedRules(dynamic policy,bool all)
    {
        var names=new System.Collections.Generic.HashSet<string>();
        foreach(dynamic rule in policy.Rules)try {if(ShouldRemoveRule((string)rule.Name,(string)rule.ApplicationName,ExecutablePath,all))names.Add((string)rule.Name);}finally{Marshal.ReleaseComObject(rule);}
        foreach(string name in names)policy.Rules.Remove(name);
    }
    public static void RemoveObsoleteRules()
    {
        dynamic policy=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
        try{RemoveOwnedRules(policy,false);}finally{Marshal.ReleaseComObject(policy);}
    }
    public static void AddRule(IPAddress address, int port)
    {
        if (!SupportHost.LocalPrivateAddress(address) || !IsPrivateProfile(address) || port < 1 || port > 65535)
            throw new ArgumentException("Choose an active IPv4 address on a Windows Private network.");
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
        try
        {
            policy.Rules.Remove(RuleName(address, port));
            rule.Name = RuleName(address, port); rule.Description = "User-enabled Remsup LAN support. Private profile and local subnet only.";
            rule.ApplicationName = ExecutablePath; rule.Protocol = 6; rule.LocalPorts = port.ToString();
            rule.LocalAddresses = address.ToString(); rule.RemoteAddresses = "LocalSubnet"; rule.Direction = 1;
            rule.Profiles = 2; rule.Action = 1; rule.Enabled = true; policy.Rules.Add(rule);
            RemoveOwnedRules(policy,false);
        }
        finally { Marshal.ReleaseComObject(rule); Marshal.ReleaseComObject(policy); }
    }
    public static void RemoveRule(IPAddress address, int port)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
        try { RemoveOwnedRules(policy,true); }
        finally { Marshal.ReleaseComObject(policy); }
    }
}
