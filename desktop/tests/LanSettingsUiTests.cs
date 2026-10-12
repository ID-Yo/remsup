using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
internal static class LanSettingsUiTests {
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
 [STAThread]static int Main(string[] args){RuntimeSetup.Initialize();Directory.CreateDirectory(args[0]);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=async(_,__)=>{
  ModernWindow window=null;
  try{
   using var relay=new SyntheticPairingRelay("fixture-password");File.WriteAllText(Path.Combine(args[0],"settings.txt"),"LanEnabled=True\nLanAddress=192.168.250.254\nLanPort=8444\n");
   window=new ModernWindow(new HostOptions(new[]{"--fixture-root",args[0],"--fixture-relay","127.0.0.1:"+relay.Port}));app.MainWindow=window;window.Show();var view=(WebView2)typeof(ModernWindow).GetField("view",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
   var until=DateTime.UtcNow.AddSeconds(30);while(DateTime.UtcNow<until){if(view.CoreWebView2!=null&&await view.CoreWebView2.ExecuteScriptAsync("document.body.innerText.includes('LAN IP is inactive')")=="true")break;await Task.Delay(100);}
   File.WriteAllText(Path.Combine(args[0],"initial-ui.json"),await view.CoreWebView2.ExecuteScriptAsync("document.body.innerText"));
   Check(await view.CoreWebView2.ExecuteScriptAsync("document.body.innerText.includes('LAN IP is inactive')")=="true","real renderer shows independent inactive-IP warning");
   await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[role=tab]')].find(e=>e.textContent==='Settings').click()");await Task.Delay(200);
   await view.CoreWebView2.ExecuteScriptAsync("(()=>{const input=[...document.querySelectorAll('label.field')].find(e=>e.textContent.includes('Relay server')).querySelector('input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'https://invalid.example');input.dispatchEvent(new Event('input',{bubbles:true}));})()");await Task.Delay(100);
   await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(e=>e.textContent==='Save changes').click()");await Task.Delay(350);
   Check(await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('.action-footer').innerText.includes('Not saved:')&&document.querySelector('.action-footer').innerText.includes('still staged')")=="true","failed Save explains retained draft beside button");
   await view.CoreWebView2.ExecuteScriptAsync("(()=>{const input=[...document.querySelectorAll('label.field')].find(e=>e.textContent.includes('Relay server')).querySelector('input');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'relay.example.invalid');input.dispatchEvent(new Event('input',{bubbles:true}));})()");await Task.Delay(100);
   await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(e=>e.textContent==='Save changes').click()");await Task.Delay(500);
   Check(await view.CoreWebView2.ExecuteScriptAsync("(()=>{const text=document.querySelector('.action-footer').innerText;return text.includes('Settings saved.')&&text.includes('LAN is unavailable:')&&!text.includes('Changes are staged');})()")=="true","successful Save distinguishes stored settings from unavailable LAN");
   Check(await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('select option')].some(e=>e.textContent.includes('(not active)'))&&document.body.innerText.includes('Refresh addresses')")=="true","inactive saved IP offers active addresses and refresh");
   Check(await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('.action-footer').getBoundingClientRect().bottom<=innerHeight")=="true","Save result/footer remains inside viewport");
   var host=(HostController)typeof(ModernWindow).GetField("host",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);await FixtureReview.Capture(view,args[0],"settings-lan",host.Snapshot());
  }catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}finally{window?.RequestExit();app.Shutdown();}
 };app.Run();return Environment.ExitCode;}
}
