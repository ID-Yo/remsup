using System;
internal sealed class DeviceEndpoint {
 internal readonly string Host;internal readonly int Port;internal readonly string Pin;internal readonly bool IsLan;
 internal DeviceEndpoint(string host,int port,string pin=null,bool isLan=false){Host=host;Port=port;Pin=pin??"";IsLan=isLan;}
 internal string Display{get{return Host.IndexOf(':')>=0?"["+Host+"]:"+Port:Host+":"+Port;}}
 internal static DeviceEndpoint Resolve(DeviceRecord device,Func<RelayConfiguration> global){
  if(device.Transport==DeviceTransport.Relay&&device.UseDefaultRelay){var configured=global();if(configured==null)throw new InvalidOperationException(RelayConfiguration.Help);var relay=configured.Validated();return new DeviceEndpoint(relay.Host,relay.Port,relay.Pin,false);}
  if(device.Port<1||device.Port>65535)throw new ArgumentException("Endpoint port must be 1-65535.");
  string host=RelayConfiguration.ValidateHost(device.Host);bool lan=device.Transport==DeviceTransport.Lan;
  return new DeviceEndpoint(host,device.Port,RelayTls.PinFor(host,device.Port,device.Pin,lan),lan);
 }
}
