using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
internal static class FixtureReview {
 internal static async Task Capture(WebView2 view,string root,string name,object state){
  if(name.Length>60||!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-zA-Z0-9_-]+$"))throw new ArgumentException("Invalid capture name.");Directory.CreateDirectory(root);using(var stream=File.Create(Path.Combine(root,name+".png")))await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);
  string metrics=await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({innerWidth,innerHeight,dpr:devicePixelRatio,theme:document.documentElement.dataset.theme,bodyScrollWidth:document.body.scrollWidth})");
  var observed=JsonSerializer.SerializeToElement(state);File.WriteAllText(Path.Combine(root,name+".json"),JsonSerializer.Serialize(new{viewport=JsonSerializer.Deserialize<string>(metrics),zoom=view.ZoomFactor,outerDip=new{Window.GetWindow(view).ActualWidth,Window.GetWindow(view).ActualHeight,Window.GetWindow(view).MinWidth,Window.GetWindow(view).MinHeight},workArea=new{SystemParameters.WorkArea.Width,SystemParameters.WorkArea.Height},runtime=view.CoreWebView2.Environment.BrowserVersionString,state,fixture=true,receivingStopped=observed.GetProperty("receivingState").GetString()=="Stopped"}));
 }
 internal static async Task Run(WebView2 view,HostOptions options,Func<object> snapshot,System.Threading.CancellationToken cancellation){
  await view.CoreWebView2.ExecuteScriptAsync("window.fixtureShots=[];window.fixtureCaptured='';window.fixtureDone=false;window.fixtureError='';");
  await view.CoreWebView2.ExecuteScriptAsync("(async()=>{try{"+File.ReadAllText(options.FixtureScript)+";window.fixtureDone=true;}catch(e){window.fixtureError=String(e);window.fixtureDone=true;}})()");
  for(int i=0;i<600;i++){await Task.Delay(100,cancellation);cancellation.ThrowIfCancellationRequested();string next=JsonSerializer.Deserialize<string>(await view.CoreWebView2.ExecuteScriptAsync("window.fixtureShots.shift()||''"));if(next.Length>0){await Capture(view,options.Artifacts,next,snapshot());await view.CoreWebView2.ExecuteScriptAsync("window.fixtureCaptured="+JsonSerializer.Serialize(next));}if(await view.CoreWebView2.ExecuteScriptAsync("window.fixtureDone")!="true")continue;string error=JsonSerializer.Deserialize<string>(await view.CoreWebView2.ExecuteScriptAsync("window.fixtureError"));if(error.Length>0)throw new InvalidOperationException(error);File.WriteAllText(Path.Combine(options.Artifacts,"review-result.json"),await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.fixtureResult||{})"));return;}throw new TimeoutException("Local fixture review script timed out.");
 }
}
