using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

// Controlled-test provider: requires visible local consent. Native tests target
// only their separate test-owned child window; never capture a personal desktop.
internal sealed class WindowsScreenSurface:IScreenSurface
{
    [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct MonitorInfo {public int Size;public Rect Monitor,Work;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;}
    delegate bool MonitorProc(IntPtr monitor,IntPtr dc,ref Rect bounds,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr rect,MonitorProc callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,int length,out int needed);
    [DllImport("wtsapi32.dll")] static extern bool WTSQuerySessionInformation(IntPtr server,int session,int kind,out IntPtr buffer,out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int width,int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest,int dx,int dy,int dw,int dh,IntPtr source,int sx,int sy,int sw,int sh,uint operation);
    [StructLayout(LayoutKind.Sequential)] struct NativePoint {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo {public int Size;public uint Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public Rect CaretRect;}
    [StructLayout(LayoutKind.Sequential)] struct MouseInput {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Explicit)] struct InputUnion {[FieldOffset(0)] public MouseInput Mouse;[FieldOffset(0)] public KeyboardInput Keyboard;}
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput {public ushort VirtualKey,Scan;public uint Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)] struct Input {public uint Type;public InputUnion Data;}
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window,uint flags);
    internal static bool IsCloaked(IntPtr window){IntPtr root=GetAncestor(window,2);return root==IntPtr.Zero||DwmGetWindowAttribute(root,14,out int value,sizeof(int))!=0||value!=0;}

    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    readonly Dictionary<string,ScreenGeometry> monitors=new Dictionary<string,ScreenGeometry>();int version=1;
    public bool IsInteractive
    {
        get
        {
            IntPtr state;int length;
            if(!WTSQuerySessionInformation(IntPtr.Zero,Process.GetCurrentProcess().SessionId,8,out state,out length))return false;
            try{if(length<4||Marshal.ReadInt32(state)!=0)return false;}finally{WTSFreeMemory(state);}
            IntPtr desktop=OpenInputDesktop(0,false,1);if(desktop==IntPtr.Zero)return false;
            try{var name=new StringBuilder(128);int needed;return GetUserObjectInformation(desktop,2,name,name.Capacity*2,out needed)&&name.ToString()=="Default";}
            finally{CloseDesktop(desktop);}
        }
    }
    void RequireDesktop(){if(!IsInteractive||DesktopName(GetThreadDesktop(GetCurrentThreadId()))!="Default")throw new InvalidOperationException("desktop-unavailable");}
    public ScreenGeometry[] Monitors()
    {
        RequireDesktop();IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));if(previous==IntPtr.Zero)throw new InvalidOperationException("dpi-unavailable");
        try
        {
            var current=new Dictionary<string,ScreenGeometry>();bool failed=false;
            bool success=EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,delegate(IntPtr handle,IntPtr dc,ref Rect bounds,IntPtr data)
            {
                var info=new MonitorInfo{Size=Marshal.SizeOf(typeof(MonitorInfo))};if(!GetMonitorInfo(handle,ref info)){failed=true;return false;}
                Rect r=info.Monitor;current[info.Device]=new ScreenGeometry(info.Device,version,r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top);return true;
            },IntPtr.Zero);
            if(!success||failed||current.Count==0)throw new InvalidOperationException("monitor-unavailable");
            bool changed=current.Count!=monitors.Count;
            foreach(var pair in current){ScreenGeometry prior;if(!monitors.TryGetValue(pair.Key,out prior)||!pair.Value.Matches(prior))changed=true;}
            if(changed)version++;
            monitors.Clear();foreach(var pair in current){var g=pair.Value;monitors[pair.Key]=new ScreenGeometry(pair.Key,version,g.X,g.Y,g.Width,g.Height);}
            var result=new ScreenGeometry[monitors.Count];monitors.Values.CopyTo(result,0);return result;
        }
        finally{SetThreadDpiAwarenessContext(previous);}
    }
    public ScreenGeometry Geometry(string monitorId)
    {
        foreach(var geometry in Monitors())if(geometry.MonitorId==monitorId)return geometry;
        throw new InvalidOperationException("monitor-unavailable");
    }
    public ScreenImage Capture(ScreenGeometry geometry)
    {
        RequireDesktop();if(!geometry.Matches(Geometry(geometry.MonitorId)))throw new InvalidOperationException("geometry-changed");
        double scale=Math.Min(1,1920.0/Math.Max(geometry.Width,geometry.Height));int width=Math.Max(2,(int)Math.Round(geometry.Width*scale)),height=Math.Max(2,(int)Math.Round(geometry.Height*scale));
        IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));if(previous==IntPtr.Zero)throw new InvalidOperationException("dpi-unavailable");
        IntPtr source=IntPtr.Zero,dest=IntPtr.Zero,bitmap=IntPtr.Zero,original=IntPtr.Zero;
        try
        {
            RequireDesktop();source=GetDC(IntPtr.Zero);if(source==IntPtr.Zero)throw new InvalidOperationException("capture-failed");
            dest=CreateCompatibleDC(source);bitmap=CreateCompatibleBitmap(source,width,height);
            if(dest==IntPtr.Zero||bitmap==IntPtr.Zero)throw new InvalidOperationException("capture-failed");
            original=SelectObject(dest,bitmap);if(original==IntPtr.Zero||original==new IntPtr(-1))throw new InvalidOperationException("capture-failed");
            RequireDesktop();if(!StretchBlt(dest,0,0,width,height,source,geometry.X,geometry.Y,geometry.Width,geometry.Height,0x40CC0020))throw new InvalidOperationException("capture-failed");
            RequireDesktop();var image=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());image.Freeze();
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using(var bytes=new MemoryStream()){encoder.Save(bytes);if(bytes.Length>4194304)throw new InvalidOperationException("image-too-large");return new ScreenImage(bytes.ToArray(),width,height);}
        }
        finally
        {
            if(original!=IntPtr.Zero&&original!=new IntPtr(-1)&&dest!=IntPtr.Zero)SelectObject(dest,original);
            if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);if(dest!=IntPtr.Zero)DeleteDC(dest);if(source!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,source);SetThreadDpiAwarenessContext(previous);
        }
    }
    public bool IsProtectedPoint(ScreenPoint point)
    {
        IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));if(previous==IntPtr.Zero)return true;
        try
        {
            uint pid;IntPtr window=WindowFromPoint(new NativePoint{X=point.X,Y=point.Y});
            if(window==IntPtr.Zero||GetWindowThreadProcessId(window,out pid)==0)return true;
            return pid==(uint)Environment.ProcessId||IsCloaked(window);
        }
        finally{SetThreadDpiAwarenessContext(previous);}
    }
    static uint Submit(Input[] events){int error;return Submit(events,out error);}
    static uint Submit(Input[] events,out int error){uint sent=SendInput((uint)events.Length,events,Marshal.SizeOf(typeof(Input)));error=Marshal.GetLastWin32Error();if(sent!=(uint)events.Length){try{File.WriteAllText(Path.Combine(Path.GetTempPath(),"RemsupScreenInput.log"),DateTime.UtcNow.ToString("o")+" submitted="+sent+" requested="+events.Length+" win32="+error+" outcome=unknown\n");}catch(IOException){}catch(UnauthorizedAccessException){}}return sent;}
    static Input KeyEvent(ushort key,ushort scan,uint flags){return new Input{Type=1,Data=new InputUnion{Keyboard=new KeyboardInput{VirtualKey=key,Scan=scan,Flags=flags}}};}
    struct KeyboardTarget {public IntPtr Foreground,Focus;}
    KeyboardTarget ReadKeyboardTarget(ScreenGeometry geometry) {
        RequireDesktop();if(!geometry.Matches(Geometry(geometry.MonitorId)))throw new InvalidOperationException("geometry-changed");
        IntPtr foreground=GetForegroundWindow();uint pid=0;uint thread=foreground==IntPtr.Zero?0:GetWindowThreadProcessId(foreground,out pid);
        if(foreground==IntPtr.Zero||!IsWindow(foreground)||!IsWindowVisible(foreground)||!IsWindowEnabled(foreground)||thread==0||pid==0)throw new InvalidOperationException("foreground-required");
        if(pid==(uint)Environment.ProcessId||IsCloaked(foreground))throw new InvalidOperationException("protected-window");
        var monitor=new MonitorInfo{Size=Marshal.SizeOf(typeof(MonitorInfo))};IntPtr monitorHandle=MonitorFromWindow(foreground,0);
        if(monitorHandle==IntPtr.Zero||!GetMonitorInfo(monitorHandle,ref monitor)||monitor.Device!=geometry.MonitorId)throw new InvalidOperationException("foreground-monitor-mismatch");
        var gui=new GuiThreadInfo{Size=Marshal.SizeOf(typeof(GuiThreadInfo))};
        if(!GetGUIThreadInfo(thread,ref gui)||gui.Focus==IntPtr.Zero||!IsWindow(gui.Focus)||!IsWindowVisible(gui.Focus)||!IsWindowEnabled(gui.Focus))throw new InvalidOperationException("foreground-required");
        uint focusPid; if(GetWindowThreadProcessId(gui.Focus,out focusPid)==0||focusPid==0)throw new InvalidOperationException("foreground-required");
        if(focusPid==(uint)Environment.ProcessId||IsCloaked(gui.Focus))throw new InvalidOperationException("protected-window");
        return new KeyboardTarget{Foreground=foreground,Focus=gui.Focus};
    }
    void RequireKeyboardTarget(ScreenGeometry geometry,KeyboardTarget expected) {
        KeyboardTarget current=ReadKeyboardTarget(geometry);
        if(current.Foreground!=expected.Foreground||current.Focus!=expected.Focus)throw new InvalidOperationException("keyboard-context-changed");
    }
    static void RequireNoLocalModifiers() {
        foreach(int key in new[]{16,17,18,91,92,160,161,162,163,164,165})if((GetAsyncKeyState(key)&0x8000)!=0)throw new InvalidOperationException("local-input-active");
    }
    // Keep each Unicode scalar's down/up events together, but let the receiver
    // consume them before the next scalar. A partial string is never replayed.
    internal static void SubmitText(string text,Action guard) {
        ScreenKeyboard.Validate(text,null);
        bool attempted=false;
        try {
            for(int i=0;i<text.Length;i++) {
                guard();var scalar=new List<Input>{KeyEvent(0,text[i],4),KeyEvent(0,text[i],6)};
                if(char.IsHighSurrogate(text[i])){i++;scalar.Add(KeyEvent(0,text[i],4));scalar.Add(KeyEvent(0,text[i],6));}
                attempted=true;if(Submit(scalar.ToArray())!=(uint)scalar.Count)throw new InvalidOperationException("input-outcome-unknown");
                Thread.Sleep(25);
            }
        }catch(Exception ex){if(attempted)throw new InvalidOperationException("input-outcome-unknown",ex);throw;}
    }
    static ushort KeyCode(string key) {
        switch(key){case "ctrl+l":return 0x4c;case "ctrl+a":return 0x41;case "ctrl+s":case "ctrl+shift+s":return 0x53;case "enter":return 0x0d;case "tab":case "shift+tab":return 0x09;case "escape":return 0x1b;case "backspace":return 0x08;case "delete":return 0x2e;case "up":return 0x26;case "down":return 0x28;case "left":return 0x25;case "right":return 0x27;case "home":return 0x24;case "end":return 0x23;default:throw new InvalidOperationException("key-not-allowed");}
    }
    static void SubmitKey(string key) {
        ScreenKeyboard.Validate(null,key);ushort code=KeyCode(key);bool ctrl=key.StartsWith("ctrl+",StringComparison.Ordinal),shift=key=="shift+tab"||key=="ctrl+shift+s";
        var modifiers=new List<ushort>();if(ctrl)modifiers.Add(17);if(shift)modifiers.Add(16);
        var events=new List<Input>();foreach(ushort modifier in modifiers)events.Add(KeyEvent(modifier,0,0));
        uint extended=(code==0x2e||code==0x26||code==0x28||code==0x25||code==0x27||code==0x24||code==0x23)?1u:0u;
        events.Add(KeyEvent(code,0,extended));events.Add(KeyEvent(code,0,extended|2u));for(int i=modifiers.Count-1;i>=0;i--)events.Add(KeyEvent(modifiers[i],0,2));
        int error;uint sent=Submit(events.ToArray(),out error);if(sent==(uint)events.Count)return;
        // SendInput inserts an ordered prefix. Release only keys still down from
        // that prefix, including modifiers this call actually pressed.
        var held=new List<ushort>();for(int i=0;i<sent;i++){var keyEvent=events[i].Data.Keyboard;if((keyEvent.Flags&2)!=0)held.Remove(keyEvent.VirtualKey);else if(!held.Contains(keyEvent.VirtualKey))held.Add(keyEvent.VirtualKey);}
        for(int i=held.Count-1;i>=0;i--)SendInput(1,new[]{KeyEvent(held[i],0,2u|((held[i]==code)?extended:0u))},Marshal.SizeOf(typeof(Input)));
        throw new InvalidOperationException("input-outcome-unknown");
    }
    public void Keyboard(string text,string key,ScreenGeometry geometry,Action authorize) {
        ScreenKeyboard.Validate(text,key);KeyboardTarget target=ReadKeyboardTarget(geometry);RequireNoLocalModifiers();
        Action guard=()=>{RequireKeyboardTarget(geometry,target);RequireNoLocalModifiers();authorize();RequireKeyboardTarget(geometry,target);RequireNoLocalModifiers();};
        if(text!=null){SubmitText(text,guard);return;}
        guard();SubmitKey(key);
    }
    static string DesktopName(IntPtr desktop){if(desktop==IntPtr.Zero)return "unavailable";var name=new StringBuilder(128);int needed;if(!GetUserObjectInformation(desktop,2,name,name.Capacity*2,out needed))return "unavailable";return name.ToString()=="Default"?"Default":name.ToString()=="Winlogon"?"Winlogon":"other";}
    static string InputDesktopName(){IntPtr desktop=OpenInputDesktop(0,false,1);try{return DesktopName(desktop);}finally{if(desktop!=IntPtr.Zero)CloseDesktop(desktop);}}
    static string ForegroundProcess(){uint pid;if(GetWindowThreadProcessId(GetForegroundWindow(),out pid)==0)return "unavailable";try{using(var process=Process.GetProcessById((int)pid)){string name=process.ProcessName;return name.Length<=80?name:"other";}}catch(ArgumentException){return "unavailable";}catch(System.ComponentModel.Win32Exception){return "unavailable";}}
    internal static bool CursorAtTarget(int x,int y,ScreenPoint point){return Math.Abs((long)x-point.X)<=2&&Math.Abs((long)y-point.Y)<=2;}
    static void WriteMouseDiagnostic(object value){try{File.WriteAllText(Path.Combine(Path.GetTempPath(),"RemsupScreenMouse.log"),JsonSerializer.Serialize(value));}catch(IOException){}catch(UnauthorizedAccessException){}}
    internal static int AbsoluteCoordinate(int point,int origin,int size){if(size<2||point<origin||(long)point>=origin+(long)size)throw new InvalidOperationException("coordinates-out-of-range");return (int)Math.Round((point-(double)origin)*65535/(size-1));}
    static Input Event(int x,int y,uint flags,uint data=0){return new Input{Type=0,Data=new InputUnion{Mouse=new MouseInput{X=x,Y=y,Flags=flags,Data=data}}};}
    internal static void CheckMouseOutcome(uint sent,uint requested,bool atTarget){
        if(sent!=requested)throw new ScreenInputFailure("outcome-unknown",null);
        if(!atTarget)throw new ScreenInputFailure("input-not-applied",true);
    }
    public void Mouse(string action,ScreenPoint point,int scrollSteps,Action authorize)
    {
        RequireDesktop();if(IsProtectedPoint(point))throw new InvalidOperationException("protected-window");
        // Never combine remote actions with locally held modifiers or buttons.
        foreach(int key in new[]{1,2,4,16,17,18,91,92})if((GetAsyncKeyState(key)&0x8000)!=0)throw new InvalidOperationException("local-input-active");
        if(action!="click"&&action!="double-click"&&action!="right-click"&&action!="scroll")throw new InvalidOperationException("invalid-action");
        if(action=="scroll"?(scrollSteps==0||scrollSteps<-10||scrollSteps>10):scrollSteps!=0)throw new InvalidOperationException("invalid-scroll");
        IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));if(previous==IntPtr.Zero)throw new InvalidOperationException("dpi-unavailable");
        try
        {
            int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
            if(width<2||height<2||point.X<left||point.Y<top||(long)point.X>=left+(long)width||(long)point.Y>=top+(long)height)throw new InvalidOperationException("coordinates-out-of-range");
            int x=AbsoluteCoordinate(point.X,left,width),y=AbsoluteCoordinate(point.Y,top,height);
            var monitor=new MonitorInfo{Size=Marshal.SizeOf(typeof(MonitorInfo))};bool monitorKnown=GetMonitorInfo(MonitorFromPoint(new NativePoint{X=point.X,Y=point.Y},0),ref monitor);
            string threadDesktop=DesktopName(GetThreadDesktop(GetCurrentThreadId())),inputDesktop=InputDesktopName();
            if(threadDesktop!="Default"||inputDesktop!="Default")throw new InvalidOperationException("desktop-unavailable");
            bool beforeKnown=GetCursorPos(out NativePoint before);string foregroundBefore=ForegroundProcess();uint foregroundDpiBefore=GetDpiForWindow(GetForegroundWindow());
            var events=new List<Input>{Event(x,y,0xC001)};
            if(action=="scroll")events.Add(Event(0,0,0x0800,unchecked((uint)(scrollSteps*120))));
            else
            {
                uint down=action=="right-click"?8u:2u,up=action=="right-click"?16u:4u;
                events.Add(Event(0,0,down));events.Add(Event(0,0,up));
                if(action=="double-click"){events.Add(Event(0,0,down));events.Add(Event(0,0,up));}
            }
            RequireDesktop();if(IsProtectedPoint(point))throw new InvalidOperationException("protected-window");
            // Any partial/failed submission has unknown outcome. Never replay it.
            authorize();int error;uint sent=Submit(events.ToArray(),out error);
            NativePoint after=default;bool afterKnown=false,atTarget=false;var watch=Stopwatch.StartNew();
            // Observe bounded cursor motion only; never resend or force foreground.
            do{afterKnown=GetCursorPos(out after);atTarget=afterKnown&&CursorAtTarget(after.X,after.Y,point);if(atTarget||sent!=(uint)events.Count)break;Thread.Sleep(5);}while(watch.ElapsedMilliseconds<100);
            WriteMouseDiagnostic(new{utc=DateTime.UtcNow.ToString("o"),session=Process.GetCurrentProcess().SessionId,threadDesktop,inputDesktop,virtualX=left,virtualY=top,virtualWidth=width,virtualHeight=height,monitorKnown,monitor=monitorKnown?monitor.Device:"unavailable",monitorX=monitor.Monitor.Left,monitorY=monitor.Monitor.Top,monitorWidth=monitor.Monitor.Right-monitor.Monitor.Left,monitorHeight=monitor.Monitor.Bottom-monitor.Monitor.Top,targetX=point.X,targetY=point.Y,absoluteX=x,absoluteY=y,beforeKnown,beforeX=before.X,beforeY=before.Y,afterKnown,afterX=after.X,afterY=after.Y,foregroundBefore,foregroundAfter=ForegroundProcess(),foregroundDpiBefore,foregroundDpiAfter=GetDpiForWindow(GetForegroundWindow()),submitted=sent,requested=events.Count,win32=error,cursorAtTarget=atTarget});
            CheckMouseOutcome(sent,(uint)events.Count,atTarget);
        }
        finally{SetThreadDpiAwarenessContext(previous);}
    }
}
