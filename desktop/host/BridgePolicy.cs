using System;
internal static class BridgePolicy {
 internal const string Document="https://remsup.local/index.html";
 internal static bool Authorize(string origin,string kind,bool client,int bytes) {
  if(origin!=Document||bytes<1||bytes>65536)return false;
  switch(kind){
   case "screen.stop":return true;
   case "screen.request":case "screen.status":case "screen.capture":case "screen.text":case "screen.key":case "screen.mouse":case "screen.close":case "screen.fixture.consent":case "screen.fixture.paint":return !client;
   case "installation.uninstall":case "update.check":case "ready":case "snapshot":case "window.close":case "window.minimize":case "theme":case "identity.copy":case "identity.reveal":case "diagnostics":case "licenses":case "help.copy":case "help.releases":case "error.clear":case "settings.secret":case "settings.clear":case "settings.save":case "settings.discard":case "lan.prepare":case "lan.remove":return true;
   case "profile.save":case "profile.remove":case "profile.favorite":case "profile.secret":case "profile.clear":case "profile.discard":case "session.connect":case "session.run":case "session.cancel":case "session.close":case "session.output":case "check.cancel":return !client;
   default:return false;
  }
 }
}
