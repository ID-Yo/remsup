using System;
using System.Reflection;
using System.Runtime.InteropServices;
internal static class HelpContent {
 internal const string Releases="https://github.com/ID-Yo/remsup/releases";
 internal const string McpConfig="[mcp_servers.remsup]\nurl = \"http://127.0.0.1:18765/mcp\"\nbearer_token_env_var = \"REMSUP_MCP_TOKEN\"";
 internal const string Tunnel="ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:18765:127.0.0.1:8765 APPROVED_VPS_ALIAS";
 internal const string Probe="{\n  \"device_id\": \"<authorized Device ID>\",\n  \"otp\": \"<protected session input>\",\n  \"command\": \"hostname & whoami\"\n}";
 internal const string Scope="Use Remsup only for <authorized task and limits>. Expected Device ID: <ID>; hostname: <name>; Windows user: <user>. Discover remsup_health and remsup_exec_command on <approved connector>. Credentials come from the approved protected session mechanism; never echo OTP/password or MCP bearer token. First run only hostname & whoami. Stop on mismatch, missing secure credential path, unavailable transport or uncertain result. Ask before actions outside scope. Report evidence without secrets; finish with device-owner tray Exit and any approved token revocation.";
 internal static string Version=>typeof(HelpContent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??typeof(HelpContent).Assembly.GetName().Version.ToString();
 internal static string Report(bool client,SupportSnapshot live)=>"Remsup problem report\nVersion: "+Version+"\nMode: "+(client?"Client":"Desktop")+"\nPlatform: Windows "+Environment.OSVersion.Version+" / "+RuntimeInformation.ProcessArchitecture+"\nReceiving state: "+live.State+"\nTransport: "+live.ColorName+"\n\nSteps to reproduce: <describe without private data>\nExpected: <expected behavior>\nObserved: <redacted behavior>\n\nNo device ID, hostname, username, IP/endpoint, password, OTP, bearer token, file path, command output or raw logs are included. Review your additions before sharing.";
 internal static string Template(string name,bool client,SupportSnapshot live){switch(name){case "mcp-config":return McpConfig;case "mcp-tunnel":return Tunnel;case "identity-probe":return Probe;case "scope":return Scope;case "problem-report":return Report(client,live);default:throw new ArgumentException("Unknown help template.");}}
}
