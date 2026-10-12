using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class ClipboardCopy
{
    // Called on the WinForms UI thread: await retains its STA context.
    public static async Task<bool> TrySetTextAsync(string text, CancellationToken cancellation, Action<string> write = null)
    {
        if (string.IsNullOrEmpty(text)) return false;
        write = write ?? WriteText;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { write(text); return true; }
            catch (ExternalException) { if (attempt == 19) return false; }
            await Task.Delay(100, cancellation);
        }
        return false;
    }

    static void WriteText(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        // Disable the blocking built-in retry; the bounded async loop handles contention.
        Clipboard.SetDataObject(data, true, 0, 0);
    }
}
