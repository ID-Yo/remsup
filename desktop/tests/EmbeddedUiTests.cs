using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Generic;
internal static class EmbeddedUiTests {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static void Main(){byte[] html=Encoding.UTF8.GetBytes("<html>fixture</html>"),script=Encoding.UTF8.GetBytes("fixture script");var entries=new Dictionary<string,string>{{"index.html",Convert.ToHexString(SHA256.HashData(html))},{"assets/main.js",Convert.ToHexString(SHA256.HashData(script))}};Stream Open(string name)=>new MemoryStream(name=="Remsup.Ui.integrity.json"?JsonSerializer.SerializeToUtf8Bytes(entries):name.EndsWith("main.js")?script:html,false);var ui=new EmbeddedUi(Open);Check(ui.TryGet(BridgePolicy.Document,out var page)&&page.Mime.StartsWith("text/html")&&page.Bytes.Length==html.Length,"valid entry");Check(ui.TryGet("https://remsup.local/assets/main.js",out var js)&&js.Mime.StartsWith("text/javascript"),"valid script MIME");foreach(string address in new[]{"http://remsup.local/index.html","https://example.com/index.html","https://remsup.local:444/index.html","https://user@remsup.local/index.html","https://remsup.local/index.html?probe=1","https://remsup.local/../index.html","https://remsup.local/assets/%2e%2e/index.html","https://remsup.local/missing.js"})Check(!ui.TryGet(address,out _),"invalid path accepted: "+address);entries["index.html"]=new string('0',64);try{new EmbeddedUi(Open);throw new Exception("Tampered resource accepted");}catch(InvalidOperationException){}Console.WriteLine("Embedded UI exact origin/path/MIME and integrity rejection PASS");}
}
