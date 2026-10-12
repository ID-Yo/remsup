using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Opt-in native input only to this test's child window; no desktop capture.
internal static class NativeMouseTests {
 [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;}
 [DllImport("user32.dll")]static extern bool GetCursorPos(out Point point);
 [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point point);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll")]static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int w,int h,uint flags);
 [DllImport("user32.dll")]static extern bool SetCursorPos(int x,int y);
 [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
 static uint Owner(IntPtr window){GetWindowThreadProcessId(window,out uint pid);return pid;}
 static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
 [STAThread]static int Main(string[] args){
  if(args.Length>0&&args[0]=="--child")return Child(args);
  try{WindowsScreenSurface.CheckMouseOutcome(3,3,false);throw new Exception("held pointer reported success");}catch(ScreenInputFailure ex){Check(ex.Message=="input-not-applied"&&ex.Dispatched==true,"fully submitted pointer held elsewhere is dispatched");}
  try{WindowsScreenSurface.CheckMouseOutcome(1,3,true);throw new Exception("partial submission reported success");}catch(ScreenInputFailure ex){Check(ex.Message=="outcome-unknown"&&ex.Dispatched==null,"partial input remains unknown even at target");}
  WindowsScreenSurface.CheckMouseOutcome(3,3,true);
  var cloakWindow=new Window{Title="Remsup owned cloak guard fixture",Width=100,Height=100,ShowActivated=false,ShowInTaskbar=false};
  try{cloakWindow.Show();IntPtr handle=new WindowInteropHelper(cloakWindow).Handle;Check(!WindowsScreenSurface.IsCloaked(handle),"own ordinary window is available");int cloak=1;Check(DwmSetWindowAttribute(handle,13,ref cloak,4)==0,"own window cloak fixture applied");Check(WindowsScreenSurface.IsCloaked(handle),"cloaked window rejected before dispatch");}
  finally{cloakWindow.Close();}
  if(Environment.GetEnvironmentVariable("REMSUP_TEST_OWN_NATIVE_INPUT")!="1"){Console.WriteLine("SKIP native input: explicit own-fixture opt-in absent");return 0;}
  IntPtr dpi=SetThreadDpiAwarenessContext(new IntPtr(-4));GetCursorPos(out Point original);Point final=original;Process child=null;
  try{
   Check(!WindowsScreenSurface.CursorAtTarget(0,0,new ScreenPoint(500,300)),"unchanged off-target cursor cannot be reported as success");
   string root=args[0];Directory.CreateDirectory(root);var surface=new WindowsScreenSurface();var monitors=surface.Monitors();
   foreach(var geometry in monitors){
    string record=Path.Combine(root,"events-"+Guid.NewGuid().ToString("N")+".txt");
    var start=new ProcessStartInfo(Environment.ProcessPath){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(string a in new[]{"--child",record,(geometry.X+Math.Min(100,geometry.Width/8)).ToString(),(geometry.Y+Math.Min(100,geometry.Height/8)).ToString()})start.ArgumentList.Add(a);
    child=Process.Start(start);var ready=child.StandardOutput.ReadLineAsync();if(!ready.Wait(8000))throw new Exception("fixture startup timeout");var target=JsonDocument.Parse(ready.Result).RootElement;
    var session=new ScreenSession();Guid lease=session.Request("own-child",geometry.MonitorId,true);session.GrantLocal(lease,true);var frames=new ScreenFrames(session);double scale=Math.Min(1,1920.0/Math.Max(geometry.Width,geometry.Height));int width=(int)Math.Round(geometry.Width*scale),height=(int)Math.Round(geometry.Height*scale);var frame=frames.Record("own-child",lease,geometry,width,height);
    foreach(string name in new[]{"button","menu"}){
     int physicalX=target.GetProperty(name+"X").GetInt32(),physicalY=target.GetProperty(name+"Y").GetInt32();
     int imageX=(int)Math.Round((physicalX-geometry.X)*(width-1.0)/(geometry.Width-1)),imageY=(int)Math.Round((physicalY-geometry.Y)*(height-1.0)/(geometry.Height-1));
     var point=frames.ValidateMouse("own-child",lease,frame.FrameId,geometry,"click",imageX,imageY,0,geometry.Version);
     Check(Math.Abs(point.X-physicalX)<=2&&Math.Abs(point.Y-physicalY)<=2,"frame maps to own fixture physical point");
     var p=new Point{X=point.X,Y=point.Y};Check(Owner(WindowFromPoint(p))==(uint)child.Id,"input target belongs to test-owned child only");
     GetCursorPos(out Point before);Exception failure=null;
     var worker=new Thread(()=>{try{surface.Mouse("click",point,0,()=>{if(Owner(WindowFromPoint(p))!=(uint)child.Id)throw new InvalidOperationException("test target changed");});}catch(Exception e){failure=e;}});worker.Start();worker.Join(5000);if(failure!=null)throw failure;
     bool received=SpinWait.SpinUntil(()=>File.Exists(record)&&File.ReadAllText(record).Contains(name),2000);GetCursorPos(out final);
     string evidence=JsonSerializer.Serialize(new{monitor=geometry.MonitorId,geometry.X,geometry.Y,geometry.Width,geometry.Height,imageX,imageY,targetX=point.X,targetY=point.Y,beforeX=before.X,beforeY=before.Y,afterX=final.X,afterY=final.Y,cursorNearTarget=Math.Abs(final.X-point.X)<=2&&Math.Abs(final.Y-point.Y)<=2,foregroundOwn=Owner(GetForegroundWindow())==(uint)child.Id,received});
     File.AppendAllText(Path.Combine(root,"result.jsonl"),evidence+"\n");Console.WriteLine(evidence);
     Check(received,"OS "+(name=="menu"?"menu opened":"button Click received"));Check(Math.Abs(final.X-point.X)<=2&&Math.Abs(final.Y-point.Y)<=2,"actual cursor reached target");Check(Owner(GetForegroundWindow())==(uint)child.Id,"actual foreground is own child");Thread.Sleep(250);
    }
    child.CloseMainWindow();if(!child.WaitForExit(3000)){child.Kill();child.WaitForExit();}child.Dispose();child=null;
   }
   return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally{if(child!=null){if(!child.HasExited){child.Kill();child.WaitForExit();}child.Dispose();}GetCursorPos(out Point current);if(current.X==final.X&&current.Y==final.Y)SetCursorPos(original.X,original.Y);if(dpi!=IntPtr.Zero)SetThreadDpiAwarenessContext(dpi);}
 }
 static int Child(string[] args){
  var app=new Application();string record=args[1];var window=new Window{Title="Remsup test-owned native mouse fixture",Width=600,Height=340,Topmost=true,WindowStartupLocation=WindowStartupLocation.Manual};
  var panel=new DockPanel();var menu=new Menu();var file=new MenuItem{Header="File"};file.Items.Add(new MenuItem{Header="Synthetic item"});file.SubmenuOpened+=(_,__)=>File.AppendAllText(record,"menu\n");menu.Items.Add(file);DockPanel.SetDock(menu,Dock.Top);panel.Children.Add(menu);var button=new Button{Content="Test-owned click target",Margin=new Thickness(30),Height=90,VerticalAlignment=VerticalAlignment.Top};button.Click+=(_,__)=>File.AppendAllText(record,"button\n");panel.Children.Add(button);window.Content=panel;
  window.ContentRendered+=(_,__)=>{IntPtr dpi=SetThreadDpiAwarenessContext(new IntPtr(-4));try{SetWindowPos(new WindowInteropHelper(window).Handle,IntPtr.Zero,int.Parse(args[2]),int.Parse(args[3]),600,340,0x4);window.UpdateLayout();var b=button.PointToScreen(new System.Windows.Point(button.ActualWidth/2,button.ActualHeight/2));var m=file.PointToScreen(new System.Windows.Point(file.ActualWidth/2,file.ActualHeight/2));Console.WriteLine(JsonSerializer.Serialize(new{buttonX=(int)b.X,buttonY=(int)b.Y,menuX=(int)m.X,menuY=(int)m.Y}));Console.Out.Flush();}finally{if(dpi!=IntPtr.Zero)SetThreadDpiAwarenessContext(dpi);}};
  app.Run(window);return 0;
 }
}
