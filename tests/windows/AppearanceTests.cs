using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class AppearanceTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemsupAppearance-" + Guid.NewGuid().ToString("N"));
        string originalPath = Appearance.PreferencePath;
        bool originalDark = Appearance.Dark;
        Directory.CreateDirectory(root);
        try
        {
            Appearance.PreferencePath = Path.Combine(root, "appearance.txt"); Appearance.Dark = false;
            Check(!Appearance.Load(Appearance.PreferencePath), "first launch uses light mode");
            File.WriteAllText(Appearance.PreferencePath, "invalid");
            Check(!Appearance.Load(Appearance.PreferencePath), "invalid preference uses light mode");
            Application.EnableVisualStyles();
            using (var panel = Panel())
            using (var client = new RemsupClientForm(false, panel))
            using (var desktop = new DesktopForm(new DeviceStore(Path.Combine(root, "devices.json")), Panel(),()=>new RelayConfiguration("relay.example.test",8443,RelayTls.DefaultPin),false))
            using (var settings = new RemsupSettingsForm(new RemsupSettings()))
            using (var editor = new DeviceEditorForm(null,false,()=>new RelayConfiguration("relay.example.test",8443,RelayTls.DefaultPin)))
            using (var password = new PasswordForm())
            {
                client.TopLevel = false; client.Show(); desktop.TopLevel = false; desktop.Show(); Application.DoEvents();
                var button = (Button)typeof(RemsupSupportPanel).GetField("themeButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                Color before = panel.DeviceId.BackColor;
                button.PerformClick(); Application.DoEvents();
                Check(Appearance.Dark && Appearance.Load(Appearance.PreferencePath), "button persists dark preference");
                Check(button.Text == "Light mode" && button.TabStop && button.AccessibleName == "Switch color theme", "keyboard accessible button describes action");
                foreach (var form in new Form[] { client, desktop, settings, editor, password }) Check(form.BackColor == client.BackColor, "open forms share theme");
                Check(SystemInformation.HighContrast || panel.DeviceId.BackColor != before, "inputs change immediately");
                Check(panel.Snapshot.State == SupportState.Stopped, "theme does not start support");
                var lamp = (Button)typeof(RemsupSupportPanel).GetField("lamp", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                Check(lamp.ForeColor == (SystemInformation.HighContrast ? SystemColors.WindowText : Color.Firebrick), "readiness retains semantic color");
                using (var reopened = new PasswordForm()) Check(reopened.BackColor == client.BackColor, "new dialogs inherit dark theme");
                Capture(client, "client-dark.png"); Capture(desktop, "desktop-dark.png");
                Capture(settings, "settings-dark.png"); Capture(editor, "editor-dark.png"); Capture(password, "password-dark.png");
                var fullSize = client.Size; client.Size = client.MinimumSize; Application.DoEvents();
                Check(button.Right <= button.Parent.ClientSize.Width, "theme button fits minimum window width");
                Capture(client, "client-dark-compact.png"); client.Size = fullSize;
                button.PerformClick();
                Check(!Appearance.Dark && !Appearance.Load(Appearance.PreferencePath) && panel.DeviceId.BackColor == before, "light mode round trip persists");
                Capture(client, "client-light.png"); Capture(desktop, "desktop-light.png");
                client.RequestClose();
            }
            File.WriteAllText(Path.Combine(root, "blocked"), "file");
            Appearance.PreferencePath = Path.Combine(root, "blocked", "appearance.txt");
            try { Appearance.Toggle(); throw new Exception("Expected write failure"); }
            catch (IOException) { Check(!Appearance.Dark, "save failure preserves current theme"); }
            Console.WriteLine("PASS light/dark button, live forms/dialogs, persistence, readiness and failed save");
        }
        finally { Appearance.PreferencePath = originalPath; Appearance.Dark = originalDark; Directory.Delete(root, true); }
    }
    static RemsupSupportPanel Panel() { return new RemsupSupportPanel(() => new RemsupSettings { RelayHost = "" }, () => new SupportStatus { DeviceId = "111222333" }); }
    static void Capture(Form form, string name)
    {
        string evidence = Environment.GetEnvironmentVariable("REMSUP_UI_EVIDENCE"); if (string.IsNullOrEmpty(evidence)) return;
        Directory.CreateDirectory(evidence);
        if (!form.Visible) { form.TopLevel = false; form.Show(); Application.DoEvents(); }
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(evidence, name)); }
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception("AppearanceTests: " + message); }
}
