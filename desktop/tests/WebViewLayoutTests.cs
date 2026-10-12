using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;
internal static class WebViewLayoutTests {
 [DllImport("user32.dll")]static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
 [DllImport("user32.dll")]static extern bool AreDpiAwarenessContextsEqual(IntPtr a,IntPtr b);
 [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
 [StructLayout(LayoutKind.Sequential)]struct Rect {public int Left,Top,Right,Bottom;}
 delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,IntPtr rect,IntPtr data);
 [StructLayout(LayoutKind.Sequential)]struct MonitorInfo {public int Size;public Rect Monitor,Work;public uint Flags;}
 [DllImport("user32.dll")]static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
 [DllImport("user32.dll")]static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
 [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
 static async Task Layout(ModernWindow window,WebView2 view,string step){
  await Task.Delay(350);window.UpdateLayout();var hwnd=new WindowInteropHelper(window).Handle;GetClientRect(hwnd,out var client);
  using var metrics=JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("(()=>{const f=document.querySelector('.footer');const r=f?.getBoundingClientRect();return {width:innerWidth,height:innerHeight,dpr:devicePixelRatio,footer:!!r&&r.width>0&&r.height>0&&r.top>=0&&r.bottom<=innerHeight+1};})()"));
  var m=metrics.RootElement;double scale=m.GetProperty("dpr").GetDouble();Console.WriteLine(step+" native="+client.Right+"x"+client.Bottom+" view="+view.ActualWidth+"x"+view.ActualHeight+" DOM="+m);
  Check(Math.Abs(m.GetProperty("width").GetDouble()*scale-client.Right)<=2&&Math.Abs(m.GetProperty("height").GetDouble()*scale-client.Bottom)<=2,step+" real DOM viewport fills native client; DPI="+GetDpiForWindow(hwnd)+" dpr="+scale);
  Check(m.GetProperty("footer").GetBoolean(),step+" status footer fully inside viewport");
 }
 [STAThread]static int Main(string[] args){RuntimeSetup.Initialize();Directory.CreateDirectory(args[0]);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=async(_,__)=>{
  ModernWindow window=null;Process legacy=null;
  try{
   string fixture=Path.Combine(args[0],"fixture");
   if(args.Length>1){string evidence=Path.Combine(args[0],"legacy");var start=new ProcessStartInfo(args[1]){UseShellExecute=false};start.ArgumentList.Add("--fixture-root");start.ArgumentList.Add(fixture);start.ArgumentList.Add("--artifacts");start.ArgumentList.Add(evidence);legacy=Process.Start(start);var limit=DateTime.UtcNow.AddSeconds(30);while(!File.Exists(Path.Combine(evidence,"ready.json"))&&DateTime.UtcNow<limit&&!legacy.HasExited)await Task.Delay(100);Check(File.Exists(Path.Combine(evidence,"ready.json")),"legacy SystemAware renderer running during candidate startup");}
   window=new ModernWindow(new HostOptions(new[]{"--fixture-root",fixture}));app.MainWindow=window;window.Show();
   Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(new WindowInteropHelper(window).Handle),new IntPtr(-4)),"modern HWND uses PerMonitorV2 manifest");
   var view=(WebView2)typeof(ModernWindow).GetField("view",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
   var until=DateTime.UtcNow.AddSeconds(30);bool loaded=false;while(DateTime.UtcNow<until){if(view.CoreWebView2!=null&&await view.CoreWebView2.ExecuteScriptAsync("!!document.querySelector('.footer')")=="true"){loaded=true;break;}await Task.Delay(100);}Check(loaded,"real embedded product UI ready");
   await Layout(window,view,"initial");window.Width=780;window.Height=510;await Layout(window,view,"resize");window.WindowState=WindowState.Maximized;await Layout(window,view,"maximize");window.WindowState=WindowState.Normal;await Layout(window,view,"restore");
   window.WindowState=WindowState.Minimized;await Task.Delay(150);SendMessageW(new WindowInteropHelper(window).Handle,0x8001,IntPtr.Zero,new IntPtr(0x400));await Layout(window,view,"tray Show");Check(window.IsVisible,"tray Show restored test window");
   var monitors=new List<MonitorInfo>();EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(monitor,dc,r,data)=>{var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};if(GetMonitorInfo(monitor,ref info))monitors.Add(info);return true;},IntPtr.Zero);
   foreach(var monitor in monitors){Check(SetWindowPos(new WindowInteropHelper(window).Handle,IntPtr.Zero,monitor.Work.Left+30,monitor.Work.Top+30,960,650,0x14),"move own test window to enumerated monitor");await Layout(window,view,"monitor "+monitor.Work.Left+","+monitor.Work.Top);}
   Console.WriteLine("Active local monitors tested="+monitors.Count+"; mixed-DPI transitions require multiple active monitors. Virtual-desktop switching is not simulated.");
   File.WriteAllText(Path.Combine(args[0],"result.txt"),"PASS real WebView viewport/footer after resize, maximize/restore and tray Show. Actual local DPI reported above. No remote window manipulation or global display changes.");
  }catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}finally{window?.RequestExit();if(legacy!=null){if(!legacy.HasExited)legacy.Kill(true);legacy.Dispose();}app.Shutdown();}
 };app.Run();return Environment.ExitCode;}
 [DllImport("user32.dll")]static extern IntPtr SendMessageW(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
}
