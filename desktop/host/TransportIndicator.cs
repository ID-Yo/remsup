internal sealed class TransportIndicator {
 public string color {get;private set;} public string label {get;private set;}
 TransportIndicator(string color,string label){this.color=color;this.label=label;}
 internal static TransportIndicator From(SupportSnapshot live,bool stopped=false){
  if(stopped||live.State==SupportState.Stopped)return new TransportIndicator("red","No operational transport; support is stopped.");
  if(live.ColorName=="Green")return new TransportIndicator("green",(live.RelaySession?"Encrypted connection; active support session.":"Encrypted connection; ready for requests."));
  if(live.ColorName=="Yellow")return new TransportIndicator("yellow","Pinned TLS LAN ready; relay is unavailable or not connected.");
  return new TransportIndicator("red","No operational transport. "+live.Reason);
 }
}
