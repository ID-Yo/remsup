using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class RemsupClientProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        SupportActions.WriteClientLog("Process started");
        try
        {
            if (HandleLanHelper(args)) return;
            if (args.Length > 0 && args[0].Equals("--helper-start", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Support starts inside the visible app. Open Remsup without helper arguments.");
            }
            if (args.Length > 0 && args[0].Equals("--helper-stop", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Close the visible Remsup app to stop support.");
            }

            Application.ThreadException += (sender, eventArgs) =>
            {
                SupportActions.WriteClientLog("UI exception: " + eventArgs.Exception);
                MessageBox.Show(eventArgs.Exception.Message, "Remsup Support", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                SupportActions.WriteClientLog("Unhandled exception: " + eventArgs.ExceptionObject);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var instance = new ApplicationInstance()) Application.Run(new RemsupClientForm());
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            SupportActions.WriteClientLog("Startup exception: " + ex);
            MessageBox.Show(ex.Message, "Remsup Support", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static bool HandleLanHelper(string[] args)
    {
        if (args.Length != 1 || (args[0] != "--helper-lan-firewall" && args[0] != "--helper-lan-remove")) return false;
        var settings = SupportActions.LoadSettings();
        System.Net.IPAddress address;
        if (!System.Net.IPAddress.TryParse(settings.LanAddress, out address))
            throw new ArgumentException("Save a valid LAN address first.");
        if (args[0] == "--helper-lan-firewall") LanAccessPolicy.AddRule(address, settings.LanPort);
        else LanAccessPolicy.RemoveRule(address, settings.LanPort);
        return true;
    }
}
