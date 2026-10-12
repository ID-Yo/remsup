using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class ClipboardTests
{
    public static void Run()
    {
        Exception failure = null;
        using (var form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized })
        {
            var panel = new RemsupSupportPanel(() => new RemsupSettings { RelayHost = "" },
                () => new SupportStatus { DeviceId = "123 456 789" }) { Dock = DockStyle.Fill };
            form.Controls.Add(panel);
            form.Shown += async (s, e) => {
                try { await CheckRetriesAndCancellation(); await RunInteractive(panel); }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw new Exception("Clipboard tests failed.", failure);
    }

    static async Task CheckRetriesAndCancellation()
    {
        int attempts = 0; int thread = Thread.CurrentThread.ManagedThreadId;
        var copy = ClipboardCopy.TrySetTextAsync("synthetic", CancellationToken.None, value => {
            if (Thread.CurrentThread.ManagedThreadId != thread || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new Exception("Clipboard retry left the STA UI context.");
            if (++attempts < 3) throw new ExternalException("Fixture is busy.");
        });
        await copy;
        if (!copy.GetAwaiter().GetResult() || attempts != 3) throw new Exception("Transient failure must recover.");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        copy = ClipboardCopy.TrySetTextAsync("synthetic", CancellationToken.None, value => { throw new ExternalException("Still busy."); });
        await copy;
        if (copy.GetAwaiter().GetResult() || watch.ElapsedMilliseconds > 4000) throw new Exception("Persistent contention must fail within a bound.");

        using (var cancel = new CancellationTokenSource())
        {
            attempts = 0;
            copy = ClipboardCopy.TrySetTextAsync("synthetic", cancel.Token, value => { attempts++; cancel.Cancel(); throw new ExternalException("Closing."); });
            bool canceled = false;
            try { await copy; } catch (OperationCanceledException) { canceled = true; }
            if (!canceled || attempts != 1) throw new Exception("Close must prevent further clipboard writes.");
        }
        copy = ClipboardCopy.TrySetTextAsync("", CancellationToken.None, value => { throw new Exception("Empty field must not write."); });
        if (copy.GetAwaiter().GetResult()) throw new Exception("Empty field must not report success.");
        copy = ClipboardCopy.TrySetTextAsync("synthetic", CancellationToken.None, value => { throw new ArgumentException("Unrelated failure."); });
        bool propagated = false;
        try { await copy; } catch (ArgumentException) { propagated = true; }
        if (!propagated) throw new Exception("Only clipboard contention may be retried.");
        Console.WriteLine("PASS clipboard STA/retry/bounded-failure/cancel/empty/error checks");
    }

    static async Task RunInteractive(RemsupSupportPanel panel)
    {
        if (Environment.GetEnvironmentVariable("REMSUP_TEST_CLIPBOARD") != "1")
        {
            Console.WriteLine("SKIP interactive clipboard: REMSUP_TEST_CLIPBOARD is not set");
            return;
        }
        await CheckLockedClipboard(panel, 1500, true);
        await CheckLockedClipboard(panel, 2700, false);
        Console.WriteLine("PASS actual Copy ID button temporary-lock recovery and persistent-busy feedback");
    }

    static async Task CheckLockedClipboard(RemsupSupportPanel panel, int holdMilliseconds, bool succeeds)
    {
        using (var opened = new ManualResetEvent(false))
        {
            Exception lockError = null;
            var holder = new Thread(() => {
                try
                {
                    if (!OpenClipboard(IntPtr.Zero)) throw new Exception("Cannot acquire test clipboard lock.");
                    opened.Set(); Thread.Sleep(holdMilliseconds);
                }
                catch (Exception ex) { lockError = ex; opened.Set(); }
                finally { CloseClipboard(); }
            }) { IsBackground = true };
            holder.Start();
            try
            {
                if (!opened.WaitOne(3000) || lockError != null) throw new Exception("Clipboard fixture failed.", lockError);
                var copy = FindCopyButton(panel);
                copy.PerformClick();
                if (copy.Enabled) throw new Exception("Copy must disable duplicate requests while pending.");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!copy.Enabled && watch.ElapsedMilliseconds < 4000) await Task.Delay(10);
                string expected = succeeds ? "Copied to clipboard." : "Clipboard is busy. Please try copying again.";
                if (!copy.Enabled || copy.AccessibleDescription != expected) throw new Exception("Copy feedback must match the clipboard result.");
                if (succeeds && Clipboard.GetText() != "123 456 789") throw new Exception("Synthetic Device ID was not copied.");
            }
            finally { if (!holder.Join(5000)) throw new Exception("Clipboard holder did not terminate."); }
        }
    }

    static Button FindCopyButton(Control owner)
    {
        foreach (Control child in owner.Controls)
        {
            var button = child as Button;
            if (button != null && button.Text == "Copy ID") return button;
            var nested = FindCopyButton(child);
            if (nested != null) return nested;
        }
        return null;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
}
