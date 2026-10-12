using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
// Isolated SystemAware browser matching the pre-PMv2 profile during updater overlap.
internal static class LegacyRendererFixture {
 [STAThread]static void Main(string[] args){
  string root=args[Array.IndexOf(args,"--fixture-root")+1],evidence=args[Array.IndexOf(args,"--artifacts")+1];
  var app=new Application();var view=new WebView2();var window=new Window{Title="Remsup legacy renderer fixture",Width=400,Height=260,Content=view};
  window.Loaded+=async(_,__)=>{try{var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"webview"));await view.EnsureCoreWebView2Async(environment);view.CoreWebView2.NavigationCompleted+=(_,e)=>{if(e.IsSuccess){Directory.CreateDirectory(evidence);File.WriteAllText(Path.Combine(evidence,"ready.json"),"{}");}};view.NavigateToString("<html><body>Owned legacy renderer fixture</body></html>");}catch{Environment.ExitCode=1;window.Close();}};
  window.Closed+=(_,__)=>view.Dispose();app.Run(window);
 }
}
