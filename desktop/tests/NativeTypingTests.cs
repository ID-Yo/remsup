using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

internal static class NativeTypingTests
{
    const string ExactText = "RemSup remote screen test. Тест на писане от Codex. 😀";

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public NativeRect CaretRect; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
        Transcript.Add("PASS " + message);
    }

    static readonly List<string> Transcript = new List<string>();

    static void Reject(string expected, Action action)
    {
        try { action(); }
        catch (InvalidOperationException error)
        {
            if (error.Message != expected) throw new Exception("Expected " + expected + ", received " + error.Message);
            return;
        }
        throw new Exception("Expected refusal " + expected);
    }

    static int Validate()
    {
        var foreground=new IntPtr(10);
        Check(WindowsScreenSurface.ConsoleContext(true,"ConsoleWindowClass",true,true,true,true,foreground,foreground,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0),"eligible classic console has explicit target kind with zero child focus");
        foreach(int failure in Enumerable.Range(0,9))Check(!WindowsScreenSurface.ConsoleContext(failure!=0,failure==1?"ordinary-window":"ConsoleWindowClass",failure!=2,failure!=3,failure!=4,failure!=5,foreground,failure==6?new IntPtr(11):foreground,failure==7?new IntPtr(1):IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,failure==8?1u:0u),"console rejects failed query/unknown class/host/foreign user/elevation/session/conflicting context "+failure);
        Check(!WindowsScreenSurface.ConsoleContext(true,"ConsoleWindowClass",true,true,true,true,foreground,foreground,new IntPtr(1),IntPtr.Zero,IntPtr.Zero,0),"console rejects active pointer capture on console");
        Check(!WindowsScreenSurface.ConsoleContext(true,"ConsoleWindowClass",true,true,true,true,foreground,foreground,IntPtr.Zero,new IntPtr(1),IntPtr.Zero,0),"console rejects active menu owner on console");
        Check(!WindowsScreenSurface.ConsoleContext(true,"ConsoleWindowClass",true,true,true,true,foreground,foreground,IntPtr.Zero,IntPtr.Zero,new IntPtr(1),0),"console rejects active window move/resize on console");
        Check(WindowsScreenSurface.ConsoleFallbackAllowed(true,"ConsoleWindowClass",foreground,foreground),"foreground-thread fallback accepts a classic console that is the active window");
        Check(!WindowsScreenSurface.ConsoleFallbackAllowed(false,"ConsoleWindowClass",foreground,foreground),"foreground-thread fallback rejects a failed foreground-thread query");
        Check(!WindowsScreenSurface.ConsoleFallbackAllowed(true,"Chrome_WidgetWin_1",foreground,foreground),"foreground-thread fallback rejects a window that is not a classic console");
        Check(!WindowsScreenSurface.ConsoleFallbackAllowed(true,"",foreground,foreground),"foreground-thread fallback rejects an unreadable window class");
        Check(!WindowsScreenSurface.ConsoleFallbackAllowed(true,"ConsoleWindowClass",foreground,new IntPtr(11)),"foreground-thread fallback rejects a different active window");
        Check(!WindowsScreenSurface.ConsoleFallbackAllowed(true,"ConsoleWindowClass",foreground,IntPtr.Zero),"foreground-thread fallback rejects a missing active window");
        Check(!WindowsScreenSurface.ConsoleFallbackAllowed(true,"ConsoleWindowClass",IntPtr.Zero,IntPtr.Zero),"foreground-thread fallback rejects a missing foreground window");
        string[] expected = {
            "ctrl+l", "ctrl+a", "ctrl+c", "ctrl+v", "ctrl+x", "ctrl+z", "ctrl+y", "ctrl+w", "ctrl+f", "ctrl+s", "ctrl+shift+s",
            "enter", "tab", "shift+tab", "escape", "backspace", "delete",
            "up", "down", "left", "right", "home", "end"
        };
        Check(ScreenKeyboard.Keys.SequenceEqual(expected, StringComparer.Ordinal), "shared key allowlist exactly matches the v1.22 contract");
        foreach (string key in expected) ScreenKeyboard.Validate(null, key);
        foreach (string key in new[] { "alt+f4", "shift+delete", "CTRL+L", "unknown" })
            Reject("key-not-allowed", () => ScreenKeyboard.Validate(null, key));
        Reject("invalid-keyboard-operation", () => ScreenKeyboard.Validate(null, null));
        Reject("invalid-keyboard-operation", () => ScreenKeyboard.Validate("text", "enter"));
        Reject("text-size-limit", () => ScreenKeyboard.Validate("", null));
        ScreenKeyboard.Validate(new string('x', 128), null);
        Reject("text-size-limit", () => ScreenKeyboard.Validate(new string('x', 129), null));
        ScreenKeyboard.Validate("A\r\n\t😀", null);
        ScreenKeyboard.Validate("Тест на писане на кирилица от оператор", null);
        Reject("invalid-text", () => ScreenKeyboard.Validate("A\u0001", null));
        Reject("invalid-text", () => ScreenKeyboard.Validate("\uD800", null));
        Reject("invalid-text", () => ScreenKeyboard.Validate("\uDC00", null));
        Console.WriteLine("PASS bounded text, control-character, and Unicode-scalar validation");
        CheckConsoleIdentity();
        CheckInjectedKeyOwnership();
        return 0;
    }

    static void CheckConsoleIdentity()
    {
        const string windows = @"C:\Windows";
        foreach (string trusted in new[] {
            @"C:\Windows\System32\conhost.exe", @"C:\Windows\System32\cmd.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            @"C:\Windows\SysWOW64\cmd.exe", @"c:\windows\system32\CMD.EXE" })
            Check(WindowsScreenSurface.TrustedConsoleHost(trusted, windows), "console host identity accepts " + trusted);
        foreach (string rejected in new string[] {
            null, "", "cmd.exe", @"C:\ProgramData\cmd.exe", @"C:\Windows\Temp\cmd.exe", @"C:\Windows\System32\..\Temp\cmd.exe",
            @"C:\Windows\System32\cmd.exe.exe", @"C:\Windows\System32\pwsh.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\cmd.exe",
            @"\\server\share\Windows\System32\cmd.exe", @"\\?\C:\Windows\System32\cmd.exe", @"D:\Windows\System32\cmd.exe" })
            Check(!WindowsScreenSurface.TrustedConsoleHost(rejected, windows), "console host identity rejects " + (rejected ?? "<null>"));
        Check(!WindowsScreenSurface.TrustedConsoleHost(@"C:\Windows\System32\cmd.exe", null), "console host identity fails closed without a Windows directory");
    }

    static string Describe(WindowsScreenSurface.Input[] events)
    {
        return string.Join(",", events.Select(e => e.Data.Keyboard.VirtualKey + "/" + e.Data.Keyboard.Scan + "/" + e.Data.Keyboard.Flags));
    }

    // Recording sender: results are scripted per call; unscripted calls accept everything.
    static WindowsScreenSurface Scripted(List<string> calls, params uint[] results)
    {
        var script = new Queue<uint>(results);
        var surface = new WindowsScreenSurface();
        surface.Sender = events => { calls.Add(Describe(events)); return script.Count > 0 ? script.Dequeue() : (uint)events.Length; };
        return surface;
    }

    static void CheckInjectedKeyOwnership()
    {
        var calls = new List<string>();
        var idle = Scripted(calls);
        Check(idle.ReleaseHeldInputs() && !idle.HasPendingRelease && calls.Count == 0, "release with nothing injected sends no key events, so a physically held local modifier is untouched");

        calls.Clear();
        var surface = Scripted(calls, 2, 2);
        Reject("input-outcome-unknown", () => surface.SubmitKey("ctrl+shift+s"));
        Check(calls.Count == 2 && calls[0] == "17/0/0,16/0/0,83/0/0,83/0/2,16/0/2,17/0/2" && calls[1] == "16/0/2,17/0/2", "partial key injection releases only the two modifiers it pressed (" + string.Join(" | ", calls) + ")");
        Check(!surface.HasPendingRelease && surface.ReleaseHeldInputs() && calls.Count == 2, "fully released partial injection leaves nothing owed");

        calls.Clear();
        surface = Scripted(calls, 2, 1);
        Reject("input-outcome-unknown", () => surface.SubmitKey("ctrl+shift+s"));
        Check(surface.HasPendingRelease && calls[1] == "16/0/2,17/0/2", "refused partial release keeps the unreleased key owed");
        Check(surface.ReleaseHeldInputs() && calls[2] == "17/0/2" && !surface.HasPendingRelease && calls.Count == 3, "later release sends only the owed Ctrl key-up");

        calls.Clear();
        var failing = new WindowsScreenSurface();
        bool fail = false; failing.Sender = events => { calls.Add(Describe(events)); if (fail) throw new InvalidOperationException("sendinput-failed"); fail = true; return 3; };
        Reject("input-outcome-unknown", () => failing.SubmitKey("ctrl+shift+s"));
        Check(failing.HasPendingRelease && calls[1] == "83/0/2,16/0/2,17/0/2", "release failure keeps every pressed key owed, key before modifiers");
        fail = false; failing.Sender = events => { calls.Add(Describe(events)); return (uint)events.Length; };
        Check(failing.ReleaseHeldInputs() && !failing.HasPendingRelease && calls[2] == "83/0/2,16/0/2,17/0/2", "owed keys are released once the sender recovers");

        calls.Clear();
        surface = Scripted(calls, 1);
        Reject("input-outcome-unknown", () => surface.SubmitKey("right"));
        Check(calls[1] == "39/0/3" && !surface.HasPendingRelease, "partial extended key keeps the extended flag on its release");

        calls.Clear();
        surface = Scripted(calls, 2, 1);
        Reject("input-outcome-unknown", () => surface.SubmitText("AB", () => { }));
        Check(calls.Count == 3 && calls[0] == "0/65/4,0/65/6" && calls[1] == "0/66/4,0/66/6" && calls[2] == "0/66/6" && !surface.HasPendingRelease, "partial text scalar releases only its own unmatched down event, without replay");

        calls.Clear();
        surface = Scripted(calls, 3);
        Reject("input-outcome-unknown", () => surface.SubmitText("\uD83D\uDE00", () => { }));
        Check(calls.Count == 2 && calls[1] == "0/56832/6", "partial surrogate pair releases the unmatched low-surrogate down event");

        calls.Clear();
        surface = Scripted(calls);
        Reject("keyboard-context-changed", () => surface.SubmitText("AB", () => throw new InvalidOperationException("keyboard-context-changed")));
        Check(calls.Count == 0 && !surface.HasPendingRelease, "pre-dispatch refusal injects nothing and owes nothing");
    }

    static ProcessStartInfo SelfStart(params string[] arguments)
    {
        string executable = Environment.ProcessPath;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly().Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    static ScreenGeometry FirstMonitor(WindowsScreenSurface surface)
    {
        ScreenGeometry[] monitors = surface.Monitors();
        if (monitors.Length == 0) throw new Exception("No interactive monitor is available");
        return monitors.FirstOrDefault(monitor => monitor.MonitorId.EndsWith("DISPLAY2", StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(monitor => monitor.X >= 0)
            ?? monitors[0];
    }

    static void Place(IntPtr window, ScreenGeometry geometry)
    {
        if (window == IntPtr.Zero) throw new Exception("Test-owned window did not appear");
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new Exception("Could not set test placement DPI context");
        try { SetWindowPos(window, new IntPtr(-1), geometry.X + 40, geometry.Y + 40, 720, 440, 0x0010); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    static void ClickOwnedTarget(WindowsScreenSurface surface, IntPtr window, uint pid, int x, int y)
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new Exception("Could not set owned-click DPI context");
        try
        {
            var point = new ScreenPoint(x, y);
            var native = new NativePoint { X = x, Y = y };
            uint targetPid = 0;
            IntPtr initialTarget = IntPtr.Zero;
            if (!SpinWait.SpinUntil(() =>
            {
                initialTarget = WindowFromPoint(native);
                return GetWindowThreadProcessId(initialTarget, out targetPid) != 0 && targetPid == pid;
            }, 3000))
                throw new Exception("Owned focus target did not settle at ready coordinate; hwnd=" + initialTarget.ToInt64() + " pid=" + targetPid + " expected=" + pid + " point=" + x + "," + y);
            Console.WriteLine("Owned click target hwnd=" + initialTarget.ToInt64() + " pid=" + targetPid + " point=" + x + "," + y);
            GetCursorPos(out NativePoint original);
            surface.Mouse("click", point, 0, () =>
            {
                uint owner;
                IntPtr currentTarget = WindowFromPoint(native);
                uint thread = GetWindowThreadProcessId(currentTarget, out owner);
                if (thread == 0 || owner != pid)
                    throw new InvalidOperationException("keyboard-context-changed hwnd=" + currentTarget.ToInt64() + " pid=" + owner + " expected=" + pid + " thread=" + thread);
            });
            if (!SpinWait.SpinUntil(() =>
            {
                uint foregroundPid;
                return GetForegroundWindow() == window && GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid) != 0 && foregroundPid == pid;
            }, 3000)) throw new Exception("Owned click did not focus the test-owned window");
            GetCursorPos(out NativePoint current);
            if (Math.Abs((long)x - current.X) <= 2 && Math.Abs((long)y - current.Y) <= 2)
                SetCursorPos(original.X, original.Y);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    static void WaitForeground(IntPtr window, uint pid)
    {
        if (!SpinWait.SpinUntil(() =>
        {
            uint foregroundPid;
            return GetForegroundWindow() == window && GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid) != 0 && foregroundPid == pid;
        }, 3000)) throw new Exception("Test-owned window did not become foreground");
    }

    static IntPtr FocusedChild(IntPtr window)
    {
        uint pid;
        uint thread = GetWindowThreadProcessId(window, out pid);
        if (thread == 0 || pid == 0) return IntPtr.Zero;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    static string ReadText(string path)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
        }
        catch (IOException) { return null; }
    }

    static void ClearOwnedText(WindowsScreenSurface surface, ScreenGeometry geometry, IntPtr hwnd, uint pid, Action authorize, string record)
    {
        surface.Keyboard(null, "ctrl+a", geometry, authorize);
        surface.Keyboard(null, "backspace", geometry, authorize);
        Check(SpinWait.SpinUntil(() => ReadText(record) == "", 2500), "test-owned edit cleared through its native keyboard path");
    }

    static void CheckHeldModifierRefusal(WindowsScreenSurface surface, ScreenGeometry geometry, Action authorize)
    {
        var down = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0x11 } } };
        var up = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0x11, Flags = 2 } } };
        if (SendInput(1, new[] { down }, Marshal.SizeOf(typeof(Input))) != 1)
            throw new Exception("Could not inject the test-owned Ctrl-down event");
        try
        {
            if ((GetAsyncKeyState(0x11) & 0x8000) == 0) throw new Exception("Injected Ctrl state was not observable");
            Reject("local-input-active", () => surface.Keyboard(null, "ctrl+a", geometry, authorize));
        }
        finally
        {
            if (SendInput(1, new[] { up }, Marshal.SizeOf(typeof(Input))) != 1)
                throw new Exception("Could not release the Ctrl modifier injected by this test");
        }
        Check((GetAsyncKeyState(0x11) & 0x8000) == 0, "held local Ctrl is refused and the test releases only its own modifier");
        // Owner-held modifier survives the provider's release path.
        if (SendInput(1, new[] { down }, Marshal.SizeOf(typeof(Input))) != 1)
            throw new Exception("Could not inject the second test-owned Ctrl-down event");
        try
        {
            Check(surface.ReleaseHeldInputs() && (GetAsyncKeyState(0x11) & 0x8000) != 0, "ReleaseHeldInputs leaves a physically held local Ctrl down");
        }
        finally
        {
            if (SendInput(1, new[] { up }, Marshal.SizeOf(typeof(Input))) != 1)
                throw new Exception("Could not release the second test-owned Ctrl modifier");
        }
    }

    static void RunEditFixture(WindowsScreenSurface surface, ScreenGeometry geometry)
    {
        string record = Path.Combine(Path.GetTempPath(), "RemsupTypingEdit-" + Guid.NewGuid().ToString("N") + ".txt");
        Process child = null;
        try
        {
            child = Process.Start(SelfStart("--fixture-child", record, (geometry.X + 40).ToString(), (geometry.Y + 40).ToString()));
            if (child == null) throw new Exception("Could not start owned edit fixture");
            Task<string> readyLine = child.StandardOutput.ReadLineAsync();
            if (!readyLine.Wait(40000) || !readyLine.Result.StartsWith("READY ", StringComparison.Ordinal))
                throw new Exception("Owned edit fixture did not become ready");
            string[] ready = readyLine.Result.Split(' ');
            IntPtr hwnd = new IntPtr(long.Parse(ready[1]));
            Place(hwnd, geometry);
            ClickOwnedTarget(surface, hwnd, (uint)child.Id, int.Parse(ready[2]), int.Parse(ready[3]));
            WaitForeground(hwnd, (uint)child.Id);
            Action authorize = () => RequireOwnForeground(hwnd, (uint)child.Id);

            CheckHeldModifierRefusal(surface, geometry, authorize);

            surface.Keyboard(ExactText, null, geometry, authorize);
            bool exactTextReceived = SpinWait.SpinUntil(() => ReadText(record) == ExactText, 5000);
            Check(exactTextReceived, "test-owned WPF edit received exact Unicode text (actual=" + ReadText(record) + ", key=" + ReadText(record + ".key") + ")");

            surface.Keyboard(null, "ctrl+a", geometry, authorize);
            surface.Keyboard("abc", null, geometry, authorize);
            Check(SpinWait.SpinUntil(() => ReadText(record) == "abc", 2000), "Ctrl+A selected existing text before replacement");
            surface.Keyboard(null, "home", geometry, authorize);
            Check(SpinWait.SpinUntil(() => ReadText(record + ".caret") == "0", 1000), "Home moved the caret to the start (caret=" + ReadText(record + ".caret") + ")");
            surface.Keyboard(null, "right", geometry, authorize);
            Check(SpinWait.SpinUntil(() => ReadText(record + ".caret") == "1", 1000), "Right moved the caret by one character");
            surface.Keyboard(null, "delete", geometry, authorize);
            Check(SpinWait.SpinUntil(() => ReadText(record) == "ac", 3000), "Home, Right and Delete affected only the owned edit");
            surface.Keyboard(null, "end", geometry, authorize);
            surface.Keyboard(null, "backspace", geometry, authorize);
            Check(SpinWait.SpinUntil(() => ReadText(record) == "a", 3000), "End and Backspace affected only the owned edit");

            ClearOwnedText(surface, geometry, hwnd, (uint)child.Id, authorize, record);
            int authorizations = 0;
            Reject("input-outcome-unknown", () => surface.Keyboard("AB", null, geometry, () =>
            {
                RequireOwnForeground(hwnd, (uint)child.Id);
                if (++authorizations == 2) throw new InvalidOperationException("keyboard-context-changed");
            }));
            Check(authorizations == 2 && SpinWait.SpinUntil(() => ReadText(record) == "A", 1500), "context loss after one scalar stops text without replay");

            ClearOwnedText(surface, geometry, hwnd, (uint)child.Id, authorize, record);
            Reject("keyboard-context-changed", () => surface.Keyboard("must not type", null, geometry, () => throw new InvalidOperationException("keyboard-context-changed")));
            Check(ReadText(record) == "", "pre-dispatch refusal sends no text");
        }
        finally
        {
            if (child != null)
            {
                try { child.CloseMainWindow(); if (!child.WaitForExit(2000)) child.Kill(true); }
                catch (InvalidOperationException) { }
                child.Dispose();
            }
            try { if (File.Exists(record)) File.Delete(record); } catch (IOException) { }
            try { if (File.Exists(record + ".caret")) File.Delete(record + ".caret"); } catch (IOException) { }
        }
    }

    static void RequireOwnForeground(IntPtr window, uint pid)
    {
        uint current;
        if (GetForegroundWindow() != window || GetWindowThreadProcessId(window, out current) == 0 || current != pid)
            throw new InvalidOperationException("keyboard-context-changed");
    }

    static string BrowserExecutable()
    {
        string[] candidates = {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new Exception("A local Chrome or Edge browser is required for the owned browser fixture");
    }

    static string WindowTitle(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    static void RunBrowserFixture(WindowsScreenSurface surface, ScreenGeometry geometry)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rt" + Guid.NewGuid().ToString("N").Substring(0, 10));
        Directory.CreateDirectory(directory);
        string page = Path.Combine(directory, "keyboard-page.html");
        string profile = Path.Combine(directory, "profile");
        string initialTitle = "Remsup blank browser keyboard test page";
        File.WriteAllText(page,
            "<!doctype html><meta charset=\"utf-8\"><title>" + initialTitle + "</title>" +
            "<textarea id=\"editor\" autofocus></textarea><script>const e=document.getElementById('editor');" +
            "window.addEventListener('load',()=>e.focus());e.addEventListener('input',()=>document.title=e.value);</script>",
            Encoding.UTF8);
        string url = new Uri(page).AbsoluteUri;
        if (url.Length > 128) throw new Exception("Browser fixture URL exceeds the keyboard text bound");
        Process browser = null;
        try
        {
            var start = new ProcessStartInfo(BrowserExecutable()) { UseShellExecute = false };
            foreach (string argument in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--new-window", "about:blank" })
                start.ArgumentList.Add(argument);
            browser = Process.Start(start);
            if (browser == null || !SpinWait.SpinUntil(() => { browser.Refresh(); return browser.MainWindowHandle != IntPtr.Zero; }, 12000))
                throw new Exception("Owned browser window did not appear");
            IntPtr hwnd = browser.MainWindowHandle;
            Place(hwnd, geometry);
            int clientX = geometry.X + 40 + 240, clientY = geometry.Y + 40 + 260;
            ClickOwnedTarget(surface, hwnd, (uint)browser.Id, clientX, clientY);
            WaitForeground(hwnd, (uint)browser.Id);
            Action authorize = () => RequireOwnForeground(hwnd, (uint)browser.Id);

            surface.Keyboard(null, "ctrl+l", geometry, authorize);
            surface.Keyboard(url, null, geometry, authorize);
            surface.Keyboard(null, "enter", geometry, authorize);
            Check(SpinWait.SpinUntil(() => WindowTitle(hwnd).Contains(initialTitle, StringComparison.Ordinal), 12000), "Ctrl+L and Enter opened the owned blank browser test page from the exact local URL");

            surface.Keyboard(ExactText, null, geometry, authorize);
            Check(SpinWait.SpinUntil(() => WindowTitle(hwnd).Contains(ExactText, StringComparison.Ordinal), 5000), "owned browser edit received the exact Unicode text");
        }
        finally
        {
            if (browser != null)
            {
                try { browser.CloseMainWindow(); if (!browser.WaitForExit(3000)) browser.Kill(true); }
                catch (InvalidOperationException) { }
                browser.Dispose();
            }
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void RunFocusedChildChangeFixture(WindowsScreenSurface surface, ScreenGeometry geometry)
    {
        string record = Path.Combine(Path.GetTempPath(), "RemsupTypingFocus-" + Guid.NewGuid().ToString("N") + ".txt");
        Process child = null;
        try
        {
            child = Process.Start(SelfStart("--focus-fixture", record, (geometry.X + 40).ToString(), (geometry.Y + 40).ToString()));
            if (child == null) throw new Exception("Could not start owned focus-change fixture");
            Task<string> readyLine = child.StandardOutput.ReadLineAsync();
            if (!readyLine.Wait(40000) || !readyLine.Result.StartsWith("READY ", StringComparison.Ordinal))
                throw new Exception("Owned focus-change fixture did not become ready");
            string[] ready = readyLine.Result.Split(' ');
            IntPtr hwnd = new IntPtr(long.Parse(ready[1]));
            Place(hwnd, geometry);
            int firstX = int.Parse(ready[2]), firstY = int.Parse(ready[3]);
            IntPtr firstChild = new IntPtr(long.Parse(ready[4]));
            IntPtr secondChild = new IntPtr(long.Parse(ready[5]));
            ClickOwnedTarget(surface, hwnd, (uint)child.Id, firstX, firstY);
            WaitForeground(hwnd, (uint)child.Id);
            int authorizations = 0;
            Action authorize = () =>
            {
                RequireOwnForeground(hwnd, (uint)child.Id);
                if (++authorizations == 2)
                    SpinWait.SpinUntil(() => FocusedChild(hwnd) == secondChild, 2500);
            };

            try { surface.Keyboard("AB", null, geometry, authorize); throw new Exception("Focused-child change was not refused; first=" + ReadText(record) + " second=" + ReadText(record + ".second") + " focus=" + FocusedChild(hwnd).ToInt64()); }
            catch (InvalidOperationException error) { if (error.Message != "input-outcome-unknown") throw; }
            Check(SpinWait.SpinUntil(() => FocusedChild(hwnd) == secondChild, 1500), "first scalar moved focus to the second native child HWND");
            Check(SpinWait.SpinUntil(() => ReadText(record) == "A", 2000), "focused native child edit changed after the first Unicode scalar");
            Check(string.IsNullOrEmpty(ReadText(record + ".second")), "focused child HWND change blocks the next scalar without replay");
        }
        finally
        {
            if (child != null)
            {
                try { child.CloseMainWindow(); if (!child.WaitForExit(2000)) child.Kill(true); }
                catch (InvalidOperationException) { }
                child.Dispose();
            }
            try { if (File.Exists(record)) File.Delete(record); } catch (IOException) { }
            try { if (File.Exists(record + ".second")) File.Delete(record + ".second"); } catch (IOException) { }
        }
    }

    static List<KeyValuePair<IntPtr, uint>> VisibleConsoleWindows()
    {
        var found = new List<KeyValuePair<IntPtr, uint>>();
        EnumWindows((window, _) =>
        {
            var name = new StringBuilder(64);
            if (GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "ConsoleWindowClass" && IsWindowVisible(window))
            {
                uint pid;
                if (GetWindowThreadProcessId(window, out pid) != 0) found.Add(new KeyValuePair<IntPtr, uint>(window, pid));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // #128: a classic console window (conhost.exe + cmd.exe) has no focused child HWND, and its window is owned by
    // the cmd.exe client process. Typing must work there only after the full executable identity passes.
    // Returns true when the positive typing path ran and passed; false when only the elevated refusal could be checked.
    static bool RunConsoleFixture(WindowsScreenSurface surface, ScreenGeometry geometry)
    {
        string record = Path.Combine(Path.GetTempPath(), "rsconsole-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".txt");
        Process host = null, client = null;
        try
        {
            var before = new HashSet<IntPtr>(VisibleConsoleWindows().Select(w => w.Key));
            // The product refuses elevated console targets, so an elevated test runner must start the console de-elevated.
            // ShellExecute (or runas) gives the console host its own new window instead of the runner's redirected console.
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string console = "\"" + Path.Combine(system, "conhost.exe") + "\" \"" + Path.Combine(system, "cmd.exe") + "\" /q /k";
            bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            var start = elevated
                ? new ProcessStartInfo(Path.Combine(system, "runas.exe")) { UseShellExecute = false, CreateNoWindow = true, Arguments = "/trustlevel:0x20000 \"" + console.Replace("\"", "") + "\"" }
                : new ProcessStartInfo(Path.Combine(system, "conhost.exe")) { UseShellExecute = true, Arguments = "\"" + Path.Combine(system, "cmd.exe") + "\" /q /k" };
            host = Process.Start(start);
            IntPtr hwnd = IntPtr.Zero; uint pid = 0;
            if (host == null || !SpinWait.SpinUntil(() =>
            {
                foreach (var window in VisibleConsoleWindows())
                {
                    if (before.Contains(window.Key)) continue;
                    try { client = Process.GetProcessById((int)window.Value); } catch (ArgumentException) { continue; }
                    if (!client.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase)) { client.Dispose(); client = null; continue; }
                    hwnd = window.Key; pid = window.Value; return true;
                }
                return false;
            }, 40000)) throw new Exception("Owned classic console window did not appear");
            Place(hwnd, geometry);
            ClickOwnedTarget(surface, hwnd, pid, geometry.X + 40 + 240, geometry.Y + 40 + 200);
            WaitForeground(hwnd, pid);
            Check(FocusedChild(hwnd) == IntPtr.Zero, "fixture reaches the classic-console path (no focused child HWND is reported for the console window)");
            Action authorize = () => RequireOwnForeground(hwnd, pid);
            object lua = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", null);
            if (lua is int enabled && enabled == 0)
            {
                // With UAC off every process of an administrator is elevated, and the product refuses elevated consoles.
                Reject("foreground-required", () => surface.Keyboard("echo REMSUP_CONSOLE_MUST_NOT_RUN>" + record, null, geometry, authorize));
                Check(!File.Exists(record) && !surface.HasPendingRelease, "elevated classic console is refused before dispatch (UAC is disabled on this machine)");
                string skip = "SKIP positive classic-console typing: UAC is disabled here, so no non-elevated console can be created; run this fixture on a UAC-enabled machine";
                Console.WriteLine(skip); Transcript.Add(skip);
                return false;
            }
            surface.Keyboard("echo REMSUP_CONSOLE_OK>" + record, null, geometry, authorize);
            surface.Keyboard(null, "enter", geometry, authorize);
            Check(SpinWait.SpinUntil(() => (ReadText(record) ?? "").Contains("REMSUP_CONSOLE_OK", StringComparison.Ordinal), 8000), "classic console received typed text and Enter after the full-path identity check");
            Check(!surface.HasPendingRelease, "console typing leaves no owed key release");
            return true;
        }
        finally
        {
            foreach (Process process in new[] { client, host })
            {
                if (process == null) continue;
                try { process.Kill(true); process.WaitForExit(3000); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            try { if (File.Exists(record)) File.Delete(record); } catch (IOException) { }
        }
    }

    static int Native()
    {
        if (Environment.GetEnvironmentVariable("REMSUP_TEST_OWN_NATIVE_INPUT") != "1")
        {
            Console.WriteLine("SKIP native desktop input: set REMSUP_TEST_OWN_NATIVE_INPUT=1 to run test-owned fixtures");
            return 0;
        }
        var surface = new WindowsScreenSurface();
        ScreenGeometry geometry = FirstMonitor(surface);
        int failures = 0;
        RunNativeFixture("owned WPF edit", () => RunEditFixture(surface, geometry), ref failures);
        RunNativeFixture("owned WinForms focused-child change", () => RunFocusedChildChangeFixture(surface, geometry), ref failures);
        bool positiveConsole = false; int beforeConsole = failures;
        RunNativeFixtureResult("classic console (#128)", () => { positiveConsole = RunConsoleFixture(surface, geometry); return positiveConsole ? "PASS" : "SKIP"; }, ref failures);
        if (!positiveConsole && failures == beforeConsole && RequirePositiveConsole())
        {
            failures++; Console.Error.WriteLine("FAIL fixture classic console (#128): REMSUP_REQUIRE_POSITIVE_CONSOLE=1 but the positive path did not run");
        }
        RunNativeFixture("isolated browser", () => RunBrowserFixture(surface, geometry), ref failures);
        return failures == 0 ? 0 : 1;
    }

    static bool RequirePositiveConsole() { return Environment.GetEnvironmentVariable("REMSUP_REQUIRE_POSITIVE_CONSOLE") == "1"; }

    // Runs only the classic-console fixture (#128) and records the outcome; intended for a UAC-enabled, non-elevated
    // tester session. Exit codes: 0 PASS (or SKIP when a positive result is not required), 1 FAIL, 2 bad arguments,
    // 3 SKIP while REMSUP_REQUIRE_POSITIVE_CONSOLE=1.
    static int ConsoleFixtureMode(string[] args)
    {
        string resultPath = args.Length > 1 ? args[1] : null;
        if (resultPath != null && !Path.IsPathFullyQualified(resultPath)) { Console.Error.WriteLine("The result file path must be absolute."); return 2; }
        object lua = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", null);
        bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        bool required = RequirePositiveConsole();
        string environment = "machine=" + Environment.MachineName + " user=" + System.Security.Principal.WindowsIdentity.GetCurrent().Name + " os=" + Environment.OSVersion.VersionString.Replace(' ', '_') + " enableLUA=" + (lua ?? "unset") + " elevated=" + elevated + " utc=" + DateTime.UtcNow.ToString("o");
        Console.WriteLine("CONSOLE-FIXTURE " + environment); Transcript.Add("CONSOLE-FIXTURE " + environment);
        string result, detail = "";
        if (Environment.GetEnvironmentVariable("REMSUP_TEST_OWN_NATIVE_INPUT") != "1") { result = "SKIP"; detail = " reason=REMSUP_TEST_OWN_NATIVE_INPUT-is-not-1"; }
        else
        {
            try
            {
                var surface = new WindowsScreenSurface();
                result = RunConsoleFixture(surface, FirstMonitor(surface)) ? "PASS" : "SKIP";
                if (result == "SKIP") detail = " reason=elevated-refusal-checked-only";
            }
            catch (Exception error) { result = "FAIL"; detail = " reason=" + error.Message.Replace('\r', ' ').Replace('\n', ' '); }
        }
        string summary = "CONSOLE-FIXTURE-RESULT: " + result + " positive=" + (result == "PASS" ? "true" : "false") + " required=" + required + detail;
        Console.WriteLine(summary); Transcript.Add(summary);
        if (resultPath != null) File.WriteAllLines(resultPath, Transcript, new UTF8Encoding(false));
        return result == "PASS" ? 0 : result == "FAIL" ? 1 : required ? 3 : 0;
    }

    static void RunNativeFixtureResult(string name, Func<string> run, ref int failures)
    {
        try { Console.WriteLine(run() + " fixture " + name); }
        catch (Exception error) { failures++; Console.Error.WriteLine("FAIL fixture " + name + Environment.NewLine + error); }
    }

    static void RunNativeFixture(string name, Action run, ref int failures)
    {
        try { run(); Console.WriteLine("PASS fixture " + name); }
        catch (Exception error) { failures++; Console.Error.WriteLine("FAIL fixture " + name + Environment.NewLine + error); }
    }

    static int FocusFixture(string[] args)
    {
        string firstRecord = args[1], secondRecord = firstRecord + ".second";
        IntPtr previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previousDpi == IntPtr.Zero) throw new Exception("Could not set focused-child fixture DPI context");
        var form = new System.Windows.Forms.Form { Text = "Remsup owned focus-child fixture", StartPosition = System.Windows.Forms.FormStartPosition.Manual, Size = new System.Drawing.Size(720, 440), TopMost = true };
        var first = new System.Windows.Forms.TextBox { Multiline = true, Location = new System.Drawing.Point(16, 24), Size = new System.Drawing.Size(650, 150) };
        var second = new System.Windows.Forms.TextBox { Multiline = true, Location = new System.Drawing.Point(16, 220), Size = new System.Drawing.Size(650, 150) };
        form.Controls.Add(first); form.Controls.Add(second);
        first.TextChanged += (_, __) =>
        {
            File.WriteAllText(firstRecord, first.Text, Encoding.UTF8);
            if (first.Text == "A") second.Focus();
        };
        second.TextChanged += (_, __) => File.WriteAllText(secondRecord, second.Text, Encoding.UTF8);
        form.Shown += (_, __) =>
        {
            SetWindowPos(form.Handle, new IntPtr(-1), int.Parse(args[2]), int.Parse(args[3]), 720, 440, 0x0010);
            first.Focus();
            System.Drawing.Point target = first.PointToScreen(new System.Drawing.Point(first.Width / 2, first.Height / 2));
            Console.WriteLine("READY " + form.Handle.ToInt64() + " " + target.X + " " + target.Y + " " + first.Handle.ToInt64() + " " + second.Handle.ToInt64());
            Console.Out.Flush();
        };
        try { System.Windows.Forms.Application.Run(form); }
        finally { SetThreadDpiAwarenessContext(previousDpi); }
        return 0;
    }

    static int Fixture(string[] args)
    {
        string record = args[1];
        var app = new Application();
        var first = new TextBox { Name = "FirstInput", AcceptsReturn = true, FontSize = 24, Margin = new Thickness(16), Height = 150 };
        var second = new TextBox { Name = "SecondInput", AcceptsReturn = true, FontSize = 24, Margin = new Thickness(16) };
        var panel = new StackPanel();
        panel.Children.Add(first); panel.Children.Add(second);
        var window = new Window { Title = "Remsup test-owned edit fixture", Width = 720, Height = 440, Topmost = true, WindowStartupLocation = WindowStartupLocation.Manual, Content = panel };
        first.TextChanged += (_, __) => File.WriteAllText(record, first.Text, Encoding.UTF8);
        first.SelectionChanged += (_, __) => File.WriteAllText(record + ".caret", first.SelectionStart.ToString());
        second.TextChanged += (_, __) => File.WriteAllText(record + ".second", second.Text, Encoding.UTF8);
        window.ContentRendered += (_, __) =>
        {
            IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), int.Parse(args[2]), int.Parse(args[3]), 720, 440, 0x0010);
                first.Focus();
                Thread.Sleep(100);
                System.Windows.Point target = first.PointToScreen(new System.Windows.Point(first.ActualWidth / 2, first.ActualHeight / 2));
                System.Windows.Point next = second.PointToScreen(new System.Windows.Point(second.ActualWidth / 2, second.ActualHeight / 2));
                Console.WriteLine("READY " + new WindowInteropHelper(window).Handle.ToInt64() + " " + ((int)target.X) + " " + ((int)target.Y) + " " + ((int)next.X) + " " + ((int)next.Y));
                Console.Out.Flush();
            }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        };
        app.Run(window);
        return 0;
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--fixture-child") return Fixture(args);
            if (args.Length > 0 && args[0] == "--focus-fixture") return FocusFixture(args);
            if (args.Length > 0 && args[0] == "--native") return Native();
            if (args.Length > 0 && args[0] == "--console-fixture") return ConsoleFixtureMode(args);
            return Validate();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
