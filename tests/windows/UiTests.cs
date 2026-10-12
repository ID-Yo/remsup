using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class UiTests
{
    public static void Run()
    {
        var evidencePath = Environment.GetEnvironmentVariable("REMSUP_UI_EVIDENCE");
        if (string.IsNullOrEmpty(evidencePath)) { Console.WriteLine("SKIP UI evidence: REMSUP_UI_EVIDENCE not set"); return; }
        Directory.CreateDirectory(evidencePath);

        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RemsupUi-" + Guid.NewGuid().ToString("N")));
        var store = new DeviceStore(Path.Combine(tempRoot, "devices.json"));
        try
        {
            Application.EnableVisualStyles();
            using (var emptyForm = new DesktopForm(new DeviceStore(Path.Combine(tempRoot, "empty", "devices.json")), SyntheticPanel(),()=>new RelayConfiguration("relay.example.test",8443,RelayTls.DefaultPin),false))
            {
                emptyForm.TopLevel = false; emptyForm.Show(); Application.DoEvents();
                var empty = Field<Label>(emptyForm, "emptyState");
                Check(empty.Visible && empty.Text.Contains("No saved devices"), "empty vault shows a useful empty state");
            }

            var data = store.Load();
            data.Upsert(new DeviceRecord { DisplayName = "Lab workstation", DeviceId = "123456789", Host = "relay.example.test", IsFavorite = true });
            data.Upsert(new DeviceRecord { DisplayName = "Test laptop", DeviceId = "987654321", Host = "192.168.1.25", Transport = DeviceTransport.Lan, Port = 8444 });
            store.Save(data);

            CheckReconfigureCloseRace();
            CheckShellMatrix(tempRoot);
            CheckIndependentDesktopSession(tempRoot);
        }
        finally
        {
            var tempBase = Path.GetFullPath(Path.GetTempPath());
            if (tempRoot.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase) && Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    static RemsupSupportPanel SyntheticPanel(RemsupSettings settings = null) {
        return new RemsupSupportPanel(() => settings ?? new RemsupSettings { RelayHost = "" },
            () => new SupportStatus { DeviceId = "111222333" });
    }

    static void PumpUntil(Func<bool> done, int milliseconds, string message) {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(), message);
    }

    static void CheckReconfigureCloseRace() {
        using(var panel = SyntheticPanel()) {
            panel.CreateControl();
            var first = panel.StartSupportAsync(); var pending=panel.StartSupportAsync(); panel.RequestClose();
            PumpUntil(() => pending.IsCompleted, 5000, "pending Start completes after Close");pending.GetAwaiter().GetResult();
            PumpUntil(() => first.IsCompleted,5000,"earlier reconfigure completes after Close");first.GetAwaiter().GetResult();
            Check(panel.Snapshot.State == SupportState.Stopped && panel.CurrentPassword == "", "Close fences queued Start");
        }
        Console.WriteLine("PASS UI queued launch/reconfigure/Close fencing");
    }

    static void CheckShellMatrix(string tempRoot) {
        string relay = Environment.GetEnvironmentVariable("REMSUP_TEST_RELAY");string relayPin=Environment.GetEnvironmentVariable("REMSUP_TEST_RELAY_PIN");
        IPAddress lan = null;int lanPort = 0;
        if(Environment.GetEnvironmentVariable("REMSUP_TEST_LAN_FIREWALL") == "1") {
            lan = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(SupportHost.PrivateAddress).FirstOrDefault(LanAccessPolicy.IsPrivateProfile);
            Check(lan != null, "shell matrix Private LAN");
            var probe = new TcpListener(lan, 0);probe.Start();lanPort = ((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
            LanAccessPolicy.AddRule(lan, lanPort);
        }
        try {
            foreach(bool desktop in new[]{false,true}) foreach(bool permanent in new[]{false,true}) foreach(bool local in new[]{false,true}) {
                if(local ? lan == null : string.IsNullOrEmpty(relay)||string.IsNullOrWhiteSpace(relayPin)) { Console.WriteLine("SKIP shell transport matrix: " + (local ? "LAN" : "pinned relay") + " unavailable");continue; }
                string host;int port;
                if(local) { host=lan.ToString();port=lanPort; } else RemsupProtocol.ParseEndpoint(relay,out host,out port);
                var settings = new RemsupSettings { RelayHost = local ? "" : host, RelayPort = port, RelayPin=local?"":relayPin,
                    LanEnabled=local,LanAddress=local?host:"",LanPort=local?port:8444,PermanentPassword=permanent?"synthetic-permanent":"" };
                var panel = SyntheticPanel(settings);
                using(Form form = desktop ? (Form)new DesktopForm(new DeviceStore(Path.Combine(tempRoot,"matrix","devices.json")),panel) : new RemsupClientForm(true,panel)) {
                    form.Show();Application.DoEvents();
                    PumpUntil(()=>panel.Snapshot.ColorName == (local?"Yellow":"Green") && !string.IsNullOrEmpty(panel.CurrentPassword),15000,"shell transport and UI ready");
                    string secret=panel.CurrentPassword;Check(!string.IsNullOrEmpty(secret),"shell password displayed");
                    var command=Task.Run(()=>OperatorConnection.Execute(host,port,"111222333",secret,"echo shell-matrix",3,1024,CancellationToken.None,null,local?null:relayPin,local));
                    PumpUntil(()=>command.IsCompleted,10000,"shell command returns");
                    var result=command.GetAwaiter().GetResult();Check(result.ExitCode==0&&result.Stdout.Contains("shell-matrix"),"shell executes with configured credential");
                    if(desktop) Field<NotifyIcon>(form,"tray").ContextMenu.MenuItems[2].PerformClick();else ((RemsupClientForm)form).RequestClose();
                    Check(panel.Snapshot.State==SupportState.Stopped,"shell Close stops receiving");
                }
                Console.WriteLine("PASS " + (desktop?"universal":"remote-only") + " / " + (local?"LAN":"relay") + " / " + (permanent?"permanent":"OTP") + " automatic launch/command/Close");
            }
        } finally { if(lan != null) LanAccessPolicy.RemoveRule(lan,lanPort); }
    }

    static void CheckIndependentDesktopSession(string tempRoot) {
        string relay = Environment.GetEnvironmentVariable("REMSUP_TEST_RELAY");string relayPin=Environment.GetEnvironmentVariable("REMSUP_TEST_RELAY_PIN");
        if(string.IsNullOrEmpty(relay)||string.IsNullOrWhiteSpace(relayPin)) { Console.WriteLine("SKIP independent desktop session: pinned relay unavailable");return; }
        string host;int port;RemsupProtocol.ParseEndpoint(relay,out host,out port);
        var store = new DeviceStore(Path.Combine(tempRoot,"session","devices.json"));var data=store.Load();
        data.Upsert(new DeviceRecord { DisplayName="Authorized synthetic target",DeviceId="444555666",Host=host,Port=port,Pin=relayPin,SavePermanentPassword=true,PermanentPassword="session-test-password" });store.Save(data);
        using(var remote = new SupportHost()) {
            remote.Start(relay,"444555666","session-test-password",null,0,relayPin);
            PumpUntil(()=>remote.Snapshot.ColorName=="Green",10000,"separate remote registered");
            var panel=SyntheticPanel(new RemsupSettings { RelayHost=host,RelayPort=port,RelayPin=relayPin });
            using(var form=new DesktopForm(store,panel,()=>new RelayConfiguration(host,port,relayPin),false,null,_=>true)) {
                form.Show();Application.DoEvents();var start=panel.StartSupportAsync();PumpUntil(()=>start.IsCompleted,10000,"operator receiving Start");start.GetAwaiter().GetResult();
                PumpUntil(()=>panel.Snapshot.ColorName=="Green",10000,"operator receiving relay ready");
                var target=store.Load().Records[0];
                var connected=(Task)typeof(DesktopForm).GetMethod("Connect",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{target}); PumpUntil(()=>connected.IsCompleted,10000,"checked separate target opens tab");connected.GetAwaiter().GetResult();

                var outgoing=(Task)typeof(DesktopForm).GetMethod("Execute",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{"ping -n 3 127.0.0.1 > nul & echo outgoing-ok"});
                PumpUntil(()=>remote.Snapshot.State==SupportState.SessionActive,10000,"outgoing command owns separate target");
                string inboundSecret=panel.CurrentPassword;
                var incoming=Task.Run(()=>OperatorConnection.Execute(host,port,"111222333",inboundSecret,"echo incoming-ok",3,1024,CancellationToken.None,null,relayPin));
                PumpUntil(()=>incoming.IsCompleted&&outgoing.IsCompleted,15000,"simultaneous incoming and outgoing complete");outgoing.GetAwaiter().GetResult();
                Check(incoming.GetAwaiter().GetResult().Stdout.Contains("incoming-ok"),"incoming target identity kept separate");
                Check(form.Workspace.Sessions[0].Result.Stdout.Contains("outgoing-ok")&&form.Workspace.Sessions[0].Result.ExitCode==0,"desktop shows actual outgoing result");
                Check(store.Load().Records[0].LastSuccessfulConnectionUtc.HasValue,"desktop persists success timestamp after valid result");
                Field<NotifyIcon>(form,"tray").ContextMenu.MenuItems[2].PerformClick();
            }
        }
        Console.WriteLine("PASS desktop encrypted credential/result/timestamp and independent inbound/outbound lifecycle");
    }

    private static T Field<T>(object owner, string name) where T : class
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new Exception("UI test field missing: " + name);
        return field.GetValue(owner) as T;
    }

    private static bool ContainsText(Control control, string value)
    {
        if (control.Text == value) return true;
        foreach (Control child in control.Controls) if (ContainsText(child, value)) return true;
        return false;
    }

    private static void ScaleFonts(Control root, float scale)
    {
        var controls = new List<Control>();
        CaptureControls(root, controls);
        var fonts = new Dictionary<Control, Font>();
        foreach (var control in controls) fonts.Add(control, control.Font);
        foreach (var pair in fonts)
        {
            var font = pair.Value;
            pair.Key.Font = new Font(font.FontFamily, Math.Max(8, font.Size * scale), font.Style, font.Unit);
        }
    }

    private static void CaptureControls(Control control, IList<Control> values)
    {
        values.Add(control);
        foreach (Control child in control.Controls) CaptureControls(child, values);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("UiTests: " + message);
    }
}
