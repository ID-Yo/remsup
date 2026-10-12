using System;
using System.Collections.Generic;
using System.IO;

internal sealed class RelayConfiguration {
 internal readonly string Host;
 internal readonly int Port;
 internal readonly string Pin;
 internal static readonly string DefaultHost=RelayTls.DefaultHost;
 internal const int DefaultPort=443;
 internal RelayConfiguration(string host,int port,string pin=null){Host=host;Port=port;Pin=pin??"";}
 internal const string Help="Configure a relay server, TLS port and public-key pin in Settings > Network. Device ID is not a server address.";
 internal static RelayConfiguration Parse(IEnumerable<string> lines){
  string host=DefaultHost;int port=DefaultPort;string pin="";
  foreach(var line in lines){int split=line.IndexOf('=');if(split<0)continue;string key=line.Substring(0,split).Trim();
   if(key.Equals("RelayHost",StringComparison.OrdinalIgnoreCase)){var value=line.Substring(split+1).Trim();host=value.Length==0?DefaultHost:value;}
   if(key.Equals("RelayPort",StringComparison.OrdinalIgnoreCase)){var value=line.Substring(split+1).Trim();int parsed;port=value.Length==0?DefaultPort:int.TryParse(value,out parsed)?parsed:0;}
   if(key.Equals("RelayPin",StringComparison.OrdinalIgnoreCase))pin=line.Substring(split+1).Trim();
  }
  if(string.Equals(host,DefaultHost,StringComparison.OrdinalIgnoreCase)&&port==8443&&string.IsNullOrWhiteSpace(pin)){port=DefaultPort;pin=RelayTls.DefaultPin;}
  if(string.Equals(host,DefaultHost,StringComparison.OrdinalIgnoreCase)&&port==DefaultPort&&string.IsNullOrWhiteSpace(pin))pin=RelayTls.DefaultPin;
  return new RelayConfiguration(host,port,pin);
 }
 internal static RelayConfiguration Load(){
  var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemsupPortable","settings.txt");
  try{return File.Exists(path)?Parse(File.ReadLines(path)):Parse(new string[0]);}
  catch(IOException){throw new InvalidOperationException("Relay settings could not be read. "+Help);}
  catch(UnauthorizedAccessException){throw new InvalidOperationException("Relay settings cannot be accessed. "+Help);}
 }
 internal static string ValidateHost(string value){
  string host=(value??"").Trim();
  if(host.Length>1&&host[0]=='['&&host[host.Length-1]==']')host=host.Substring(1,host.Length-2);
  if(Uri.CheckHostName(host)==UriHostNameType.Unknown)throw new ArgumentException("Enter a hostname or IP address, without a URL, port or Device ID.");
  return host.TrimEnd('.').ToLowerInvariant();
 }
 internal RelayConfiguration Validated(){
  try{if(Port<1||Port>65535)throw new ArgumentException();string host=ValidateHost(Host);int port=Port;string configuredPin=Pin;if(string.Equals(host,DefaultHost,StringComparison.OrdinalIgnoreCase)&&port==8443&&string.IsNullOrWhiteSpace(configuredPin)){port=DefaultPort;configuredPin=RelayTls.DefaultPin;}string pin=RelayTls.PinFor(host,port,configuredPin,false);return new RelayConfiguration(host,port,pin);}
  catch(ArgumentException){throw new InvalidOperationException("Relay is not configured correctly. "+Help);}
 }
 internal string Display {get{return Host.IndexOf(':')>=0?"["+Host+"]:"+Port:Host+":"+Port;}}
}
