using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class FriendlyUiTests
{
    public static void Run() { WorkspaceUiTests.Run(); }
    internal static void Capture(Form form, string path)
    {
        WorkspaceUi.FitWindow(form);Cursor.Position=form.PointToScreen(new Point(5,5));
        uint process;uint foregroundThread=GetWindowThreadProcessId(GetForegroundWindow(),out process);uint currentThread=GetCurrentThreadId();
        bool attached=foregroundThread!=0&&foregroundThread!=currentThread&&AttachThreadInput(currentThread,foregroundThread,true);
        try{form.BringToFront();form.Activate();SetForegroundWindow(form.Handle);form.Refresh();Application.DoEvents();}
        finally{if(attached)AttachThreadInput(currentThread,foregroundThread,false);}
        // Activation can briefly return no foreground window during a native
        // transition. Retry only this owned window, then reject an invalid shot.
        for(int attempt=0;attempt<3&&GetForegroundWindow()!=form.Handle;attempt++){Thread.Sleep(100);form.Activate();SetForegroundWindow(form.Handle);Application.DoEvents();}
        IntPtr context = SetThreadDpiAwarenessContext(new IntPtr(-2));
        try
        {
            NativeRect rect; Check(GetWindowRect(form.Handle, out rect), "physical window bounds available");
            NativeRect frame;
            if (DwmGetWindowAttribute(form.Handle, 9, out frame, Marshal.SizeOf(typeof(NativeRect))) == 0) rect = frame;
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

            Check(new Rectangle(0,0,GetSystemMetrics(0),GetSystemMetrics(1)).Contains(bounds), "physical capture window fits display: "+bounds+" screen="+GetSystemMetrics(0)+"x"+GetSystemMetrics(1));
            Check(GetForegroundWindow() == form.Handle, "test window is foreground for screen capture (expected "+form.Handle+", actual "+GetForegroundWindow()+")");
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                if(form is DesktopForm && !SystemInformation.HighContrast)
                {
                    var selector=Field<RadioButton>(form,Field<RadioButton>(form,"homeNav").Checked?"homeNav":"settingsNav");
                    NativeRect buttonRect; Check(GetWindowRect(selector.Handle,out buttonRect),"physical selector bounds available");
                    var fill=bitmap.GetPixel(buttonRect.Left-bounds.Left+8,buttonRect.Top-bounds.Top+8);
                    Check(Contrast(selector.ForeColor,fill)>=4.5,"selected view has readable actual painted contrast");
                }
                bitmap.Save(path);
            }
        }
        finally { SetThreadDpiAwarenessContext(context); }
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread,uint attach,bool join);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect rect, int size);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    static Button FindButton(Control root, string caption)
    {
        var button = root as Button; if (button != null && button.Text == caption) return button;
        foreach (Control child in root.Controls) { var found = FindButton(child, caption); if (found != null) return found; }
        return null;
    }
    static void CheckFooter(RemsupSupportPanel panel)
    {
        foreach(var caption in new[]{"Settings","Close"})
        {
            var button=FindButton(panel,caption);
            var screen=button.RectangleToScreen(button.ClientRectangle);
            for(Control ancestor=button.Parent;ancestor!=null;ancestor=ancestor.Parent)
                Check(ancestor.ClientRectangle.Contains(ancestor.RectangleToClient(screen)), "unclipped footer action at minimum size: "+caption);
            Check(button.Visible && button.Enabled && button.TabStop, "available keyboard footer action: "+caption);
        }
    }
    static T Find<T>(Control root) where T : Control
    {
        if (root is T) return (T)root;
        foreach (Control child in root.Controls) { var found = Find<T>(child); if (found != null) return found; }
        return null;
    }
    static T Field<T>(object owner, string name) where T : class { return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner); }
    static double Contrast(Color a, Color b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05); }
    static double Luminance(Color c) { return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B); }
    static double Linear(int channel) { double c = channel / 255.0; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
    static void Check(bool ok, string message) { if (!ok) throw new Exception("FriendlyUiTests: " + message); }
}
