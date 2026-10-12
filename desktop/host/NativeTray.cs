using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
// Shell owns the notification area; no second UI framework is needed for its three actions.
internal sealed class NativeTray:IDisposable {
 const int Callback=0x8001;readonly Window owner;readonly Action show,settings,exit;readonly HwndSource source;readonly int taskbarCreated;NotifyData data;bool visible,disposed,menuOpen;readonly Func<uint,bool> notifyOverride;readonly DispatcherTimer health=new DispatcherTimer();
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct NotifyData {
  internal uint size;internal IntPtr window;internal uint id,flags,message;internal IntPtr icon;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string tip;internal uint state,stateMask;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]internal string info;internal uint version;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]internal string title;internal uint infoFlags;internal Guid guid;internal IntPtr balloon;
 }
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern bool Shell_NotifyIconW(uint message,ref NotifyData data);
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern uint ExtractIconExW(string file,int index,out IntPtr large,out IntPtr small,uint count);
 [DllImport("user32.dll")]static extern bool DestroyIcon(IntPtr icon);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessageW(string message);
 [DllImport("user32.dll")]static extern IntPtr CreatePopupMenu();
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool AppendMenuW(IntPtr menu,uint flags,UIntPtr id,string text);
 [DllImport("user32.dll")]static extern bool DestroyMenu(IntPtr menu);
 [DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr window);
 [DllImport("user32.dll")]static extern bool PostMessageW(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll")]static extern int TrackPopupMenuEx(IntPtr menu,uint flags,int x,int y,IntPtr window,IntPtr parameters);
 [DllImport("user32.dll")]static extern bool GetCursorPos(out Point position);
 [StructLayout(LayoutKind.Sequential)]struct Point {internal int x,y;}
 internal NativeTray(Window window,Action show,Action settings,Action exit,Func<uint,bool> notify=null){
  notifyOverride=notify;owner=window;this.show=show;this.settings=settings;this.exit=exit;IntPtr handle=new WindowInteropHelper(window).Handle;source=HwndSource.FromHwnd(handle);
  ExtractIconExW(Environment.ProcessPath,0,out var large,out var small,1);if(large!=IntPtr.Zero)DestroyIcon(large);
  data=new NotifyData{size=(uint)Marshal.SizeOf<NotifyData>(),window=handle,id=1,flags=1|2|4|128,message=Callback,icon=small,tip="Remsup visible support",info="",title="",version=4};
  taskbarCreated=(int)RegisterWindowMessageW("TaskbarCreated");source?.AddHook(Messages);health.Interval=TimeSpan.FromSeconds(2);health.Tick+=(_,__)=>CheckRegistration();
 }
 bool Notify(uint operation)=>notifyOverride!=null?notifyOverride(operation):Shell_NotifyIconW(operation,ref data);
 internal void CheckRegistration(){if(disposed||!visible)return;if(!Notify(1)){visible=false;health.Stop();Notify(2);show();}}
 internal bool Show(){
  if(disposed||source==null||data.icon==IntPtr.Zero||taskbarCreated==0)return false;if(visible){CheckRegistration();return visible;}
  if(!Notify(0))return false;
  if(!Notify(4)){Notify(2);return false;}visible=true;health.Start();return true;
 }
 internal void Hide(){health.Stop();if(!visible)return;Notify(2);visible=false;}
 IntPtr Messages(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled){
  if(disposed)return IntPtr.Zero;
  // TrackPopupMenu posts WM_COMMAND before returning. Retire its gate after those queued messages.
  if(message==Callback+1){menuOpen=false;handled=true;return IntPtr.Zero;}
  if(message==0x111&&visible&&menuOpen&&lParam==IntPtr.Zero&&(wParam.ToInt64()>>16)==0){int selected=(int)wParam.ToInt64();if(selected>=1&&selected<=3){handled=true;owner.Dispatcher.BeginInvoke(selected==1?show:selected==2?settings:exit);}return IntPtr.Zero;}
  if(taskbarCreated!=0&&message==taskbarCreated&&visible){visible=false;if(!Show())show();return IntPtr.Zero;}
  if(message!=Callback||!visible)return IntPtr.Zero;int notification=(int)(lParam.ToInt64()&0xffff);handled=true;
  if(notification==0x400||notification==0x401||notification==0x203)owner.Dispatcher.BeginInvoke(show);
  else if(notification==0x7b){int x=(short)(wParam.ToInt64()&0xffff),y=(short)((wParam.ToInt64()>>16)&0xffff);if(x==-1&&y==-1&&GetCursorPos(out var point)){x=point.x;y=point.y;}Menu(x,y);}
  return IntPtr.Zero;
 }
 void Menu(int x,int y){
  IntPtr menu=CreatePopupMenu();if(menu==IntPtr.Zero){show();return;}
  try{if(!AppendMenuW(menu,0,(UIntPtr)1,"Show")||!AppendMenuW(menu,0,(UIntPtr)2,"Settings")||!AppendMenuW(menu,0,(UIntPtr)3,"Exit")){show();return;}
   SetForegroundWindow(data.window);menuOpen=true;TrackPopupMenuEx(menu,0x2,x,y,data.window,IntPtr.Zero);PostMessageW(data.window,0,IntPtr.Zero,IntPtr.Zero);Notify(3);
  }finally{DestroyMenu(menu);if(!PostMessageW(data.window,Callback+1,IntPtr.Zero,IntPtr.Zero))menuOpen=false;}
 }
 public void Dispose(){if(disposed)return;Hide();disposed=true;source?.RemoveHook(Messages);if(data.icon!=IntPtr.Zero){DestroyIcon(data.icon);data.icon=IntPtr.Zero;}}
}
