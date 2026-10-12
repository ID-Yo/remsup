using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
internal static class TrayLifecycleTests {
 [DllImport("user32.dll")]static extern IntPtr SendMessageW(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessageW(string name);
 static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
 static NativeTray Tray(ModernWindow window)=>(NativeTray)typeof(ModernWindow).GetField("tray",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
 static void ShellDelete(NativeTray tray)=>typeof(NativeTray).GetMethod("Notify",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(tray,new object[]{2u});
 [STAThread]static int Main(string[] args){RuntimeSetup.Initialize();var input=typeof(WindowsScreenSurface).GetNestedType("Input",BindingFlags.NonPublic);Check(Marshal.SizeOf(input)==40&&Marshal.OffsetOf(input,"Data").ToInt32()==8,"win64 SendInput ABI 40 bytes union offset8");var keyEvent=typeof(WindowsScreenSurface).GetMethod("KeyEvent",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{(ushort)0,(ushort)'Ж',4u});var data=input.GetField("Data",BindingFlags.Public|BindingFlags.Instance).GetValue(keyEvent);var keyboard=data.GetType().GetField("Keyboard").GetValue(data);Check((uint)input.GetField("Type").GetValue(keyEvent)==1&&(ushort)keyboard.GetType().GetField("VirtualKey").GetValue(keyboard)==0&&(ushort)keyboard.GetType().GetField("Scan").GetValue(keyboard)=='Ж'&&(uint)keyboard.GetType().GetField("Flags").GetValue(keyboard)==4,"Unicode native packet fields without dispatch");Check(WindowsScreenSurface.AbsoluteCoordinate(-1920,-1920,3840)==0&&WindowsScreenSurface.AbsoluteCoordinate(1919,-1920,3840)==65535,"virtual desktop negative origin endpoints");Directory.CreateDirectory(args[0]);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Startup+=async(_,__)=>{
  ModernWindow window=null;Window faultWindow=null;
  try{
   window=new ModernWindow(new HostOptions(new[]{"--fixture-root",Path.Combine(args[0],"fixture")}));app.MainWindow=window;window.Show();await Task.Delay(2500);
   using(var activation=new ApplicationInstance("Local\\Remsup.OwnWindowActivation."+Guid.NewGuid(),true)){
    activation.OnActivate(()=>app.Dispatcher.BeginInvoke(new Action(window.Restore)));window.Hide();
    await Task.Run(()=>{using var duplicate=new ApplicationInstance(activation.Name,true);Check(!duplicate.IsOwner,"duplicate activation client owns no mutex/window");});
    await Task.Delay(300);Check(window.IsVisible&&window.WindowState==WindowState.Normal,"duplicate startup restores existing own window");
   }
IntPtr hwnd=new WindowInteropHelper(window).Handle;bool shell=(bool)typeof(NativeTray).GetField("visible",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Tray(window));Check(!shell||window.IsVisible,"tray already registered while main window visible");if(shell){
   var host=(HostController)typeof(ModernWindow).GetField("host",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);var screens=(ScreenWorkspace)typeof(HostController).GetField("screens",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(host);var access=(ScreenAccess)typeof(ScreenWorkspace).GetField("receiver",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(screens);var lease=access.Request("own-window",access.Monitors()[0].MonitorId,true);access.GrantLocal(lease,true);
   SendMessageW(hwnd,0x10,IntPtr.Zero,IntPtr.Zero);await Task.Delay(200);Check(!window.IsVisible,"actual own WM_CLOSE hides after shell registration");Check(access.Snapshot.CanView,"main minimize retains receiving screen grant");
   SendMessageW(hwnd,0x8001,IntPtr.Zero,new IntPtr(0x400));await Task.Delay(200);Check(window.IsVisible&&window.WindowState==WindowState.Normal,"actual tray Show callback restores own main window");Check((bool)typeof(NativeTray).GetField("visible",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Tray(window)),"tray remains registered after Restore");
   window.WindowState=WindowState.Minimized;await Task.Delay(100);Check(!window.IsVisible,"actual minimize hides");
   ShellDelete(Tray(window));SendMessageW(hwnd,RegisterWindowMessageW("TaskbarCreated"),IntPtr.Zero,IntPtr.Zero);await Task.Delay(100);Check(!window.IsVisible,"taskbar recreation re-registers own tray");
   ShellDelete(Tray(window));await Task.Delay(2600);Check(window.IsVisible&&window.WindowState==WindowState.Normal,"lost real shell registration restores visible main window within timer bound");
   }else Console.WriteLine("SKIP real Shell lifecycle: notification area unavailable on this runner");
   // Shell failure injection is restricted to this explicit test window.
   faultWindow=new Window{Title="Remsup tray failure synthetic test",Width=320,Height=160};faultWindow.Show();bool allow=true;int restored=0;using(var faultTray=new NativeTray(faultWindow,()=>{restored++;faultWindow.Show();faultWindow.WindowState=WindowState.Normal;},()=>{},()=>{},op=>allow)){
    Check(faultTray.Show(),"injected registration succeeds");faultWindow.Hide();allow=false;faultTray.CheckRegistration();Check(faultWindow.IsVisible&&restored==1,"failed MODIFY recovers visible window");Check(!faultTray.Show(),"failed ADD refuses hidden state");allow=true;Check(faultTray.Show(),"registration can recover");faultWindow.Hide();allow=false;SendMessageW(new WindowInteropHelper(faultWindow).Handle,RegisterWindowMessageW("TaskbarCreated"),IntPtr.Zero,IntPtr.Zero);Check(faultWindow.IsVisible,"taskbar re-add failure restores visible window");}
   faultWindow.Close();faultWindow=null;
   if(shell){window.WindowState=WindowState.Minimized;await Task.Delay(100);var tray=Tray(window);
   // Exercise the actual registered native WM_COMMAND exit routing on our HWND.
   typeof(NativeTray).GetField("menuOpen",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(tray,true);
   SendMessageW(hwnd,0x111,new IntPtr(3),IntPtr.Zero);await Task.Delay(300);Check(!window.IsVisible&&PresentationSource.FromVisual(window)==null,"native tray Exit callback disposes and closes own app");window=null;}else{window.RequestExit();window=null;}
   File.WriteAllText(Path.Combine(args[0],"result.txt"),"PASS deterministic shell failure fallback. Real Shell lifecycle "+(shell?"PASS":"SKIP")+". Native Exit routing uses the own menu gate and WM_COMMAND, not a physical menu click. No Explorer restart, external device or personal desktop input.");
  }catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}finally{faultWindow?.Close();window?.RequestExit();app.Shutdown();}
 };app.Run();return Environment.ExitCode;}
}
