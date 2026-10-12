using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
internal sealed class EmbeddedAsset {
 internal readonly byte[] Bytes;internal readonly string Mime;
 internal EmbeddedAsset(byte[] bytes,string mime){Bytes=bytes;Mime=mime;}
}
internal sealed class EmbeddedUi {
 readonly Dictionary<string,EmbeddedAsset> assets=new Dictionary<string,EmbeddedAsset>(StringComparer.Ordinal);
 internal EmbeddedUi():this(name=>typeof(EmbeddedUi).Assembly.GetManifestResourceStream(name)){}
 internal EmbeddedUi(Func<string,Stream> resource){
  using var manifest=resource("Remsup.Ui.integrity.json")??throw new InvalidOperationException("Embedded UI integrity manifest missing.");
  var entries=JsonSerializer.Deserialize<Dictionary<string,string>>(manifest)??throw new InvalidOperationException("Invalid embedded UI manifest.");
  var names=new HashSet<string>(StringComparer.Ordinal);
  foreach(var item in entries){
   string path=item.Key;if(path.Length==0||path.StartsWith("/")||path.Contains("\\")||path.Contains("..")||path.Contains("%")||path.Contains(":"))throw new InvalidOperationException("Invalid embedded UI path.");
   string name=Path.GetFileName(path);if(!names.Add(name))throw new InvalidOperationException("Duplicate embedded resource name.");
   using var stream=resource("Remsup.Ui."+name)??throw new InvalidOperationException("Embedded UI asset missing.");using var data=new MemoryStream();stream.CopyTo(data);byte[] bytes=data.ToArray();if(Convert.ToHexString(SHA256.HashData(bytes))!=item.Value)throw new InvalidOperationException("Embedded UI integrity check failed.");
   string mime=Path.GetExtension(name) switch{".html"=>"text/html; charset=utf-8",".js"=>"text/javascript; charset=utf-8",".css"=>"text/css; charset=utf-8",".png"=>"image/png",_=>throw new InvalidOperationException("Unsupported embedded UI content type.")};assets.Add("/"+path,new EmbeddedAsset(bytes,mime));
  }
  if(!assets.ContainsKey("/index.html"))throw new InvalidOperationException("Embedded UI entry missing.");
 }
 internal bool TryGet(string address,out EmbeddedAsset asset){
  asset=null;if(!Uri.TryCreate(address,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="remsup.local"||!uri.IsDefaultPort||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.AbsolutePath.Contains('%'))return false;
  // Uri canonicalizes dot segments: reject their raw spelling as well as non-allowlisted paths.
  if(address.Contains('%')||address.Contains("/../")||address.Contains("/./")||address.Contains("\\"))return false;
  return assets.TryGetValue(uri.AbsolutePath,out asset);
 }
}
