using System;
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
    }

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
        string[] expected = {
            "ctrl+l", "ctrl+a", "ctrl+s", "ctrl+shift+s", "enter", "tab", "shift+tab",
            "escape", "backspace", "delete", "up", "down", "left", "right", "home", "end"
        };
        Check(ScreenKeyboard.Keys.SequenceEqual(expected, StringComparer.Ordinal), "shared key allowlist exactly matches the v1.22 contract");
        foreach (string key in expected) ScreenKeyboard.Validate(null, key);
        foreach (string key in new[] { "ctrl+c", "ctrl+v", "alt+f4", "shift+delete", "CTRL+L", "unknown" })
            Reject("key-not-allowed", () => ScreenKeyboard.Validate(null, key));
        Reject("invalid-keyboard-operation", () => ScreenKeyboard.Validate(null, null));
        Reject("invalid-keyboard-operation", () => ScreenKeyboard.Validate("text", "enter"));
        Reject("text-size-limit", () => ScreenKeyboard.Validate("", null));
        ScreenKeyboard.Validate(new string('x', 128), null);
        Reject("text-size-limit", () => ScreenKeyboard.Validate(new string('x', 129), null));
        ScreenKeyboard.Validate("A\r\n\t😀", null);
        Reject("invalid-text", () => ScreenKeyboard.Validate("A\u0001", null));
        Reject("invalid-text", () => ScreenKeyboard.Validate("\uD800", null));
        Reject("invalid-text", () => ScreenKeyboard.Validate("\uDC00", null));
        Console.WriteLine("PASS bounded text, control-character, and Unicode-scalar validation");
        return 0;
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
            if (!readyLine.Wait(8000) || !readyLine.Result.StartsWith("READY ", StringComparison.Ordinal))
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
            if (!readyLine.Wait(8000) || !readyLine.Result.StartsWith("READY ", StringComparison.Ordinal))
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
        RunNativeFixture("isolated browser", () => RunBrowserFixture(surface, geometry), ref failures);
        return failures == 0 ? 0 : 1;
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
            return Validate();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
