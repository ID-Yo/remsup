using System;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class RemsupSupportPanel : UserControl
{
    readonly SupportHost supportHost = new SupportHost();
    readonly ToolTip readinessTip = new ToolTip();
    readonly Button lamp = new Button();
    readonly Button themeButton = new Button();
    readonly Label status = new Label();
    readonly TextBox deviceId = new TextBox();
    readonly TextBox password = new TextBox();
    readonly TextBox addresses = new TextBox();
    readonly TextBox log = new TextBox();
    readonly Panel details = new Panel();
    internal event Action CloseRequested;
    internal event Action SettingsApplied;
    readonly Func<RemsupSettings> loadSettings;
    readonly Func<SupportStatus> getStatus;
    readonly object startSync = new object();
    readonly CancellationTokenSource copyCancellation = new CancellationTokenSource();
    int requestGeneration;
    bool closed;
    string startError;

    internal TextBox DeviceId { get { return deviceId; } }
    internal TextBox Addresses { get { return addresses; } }
    internal SupportSnapshot Snapshot { get { return supportHost.Snapshot; } }
    internal string CurrentPassword { get { return password.Text; } }

    private async Task CopyTextAsync(TextBox value, Button copy)
    {
        if (closed || string.IsNullOrEmpty(value.Text)) return;
        copy.Enabled = false;
        copy.AccessibleDescription = "Copying to clipboard...";
        readinessTip.SetToolTip(copy, copy.AccessibleDescription);
        try
        {
            bool copied = await ClipboardCopy.TrySetTextAsync(value.Text, copyCancellation.Token);
            if (closed || IsDisposed) return;
            string message = copied ? "Copied to clipboard." : "Clipboard is busy. Please try copying again.";
            copy.AccessibleDescription = message;
            readinessTip.SetToolTip(copy, message);
            readinessTip.Show(message, copy, 0, copy.Height, 3000);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !IsDisposed) MessageBox.Show(this, ex.Message, "Remsup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (!closed && !IsDisposed) copy.Enabled = true; }
    }

    private Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(68, 32), Padding = new Padding(4, 2, 4, 2) };
        button.Click += (s, e) => action();
        return button;
    }

    private Action Safe(Action action)
    {
        return () => { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Remsup", MessageBoxButtons.OK, MessageBoxIcon.Error); } };
    }

    private SupportStartRequest GetStartRequest()
    {
        var settings = loadSettings();
        var secret = string.IsNullOrEmpty(settings.PermanentPassword) ? SupportActions.GeneratePassword() : settings.PermanentPassword;
        IPAddress address = null;
        if(!string.IsNullOrWhiteSpace(settings.RelayHost)){
            settings.RelayHost=RelayConfiguration.ValidateHost(settings.RelayHost);
            if(settings.RelayPort<1||settings.RelayPort>65535)throw new ArgumentException("Relay port must be 1-65535. Open Settings > Network.");
        }
        if (settings.LanEnabled && !IPAddress.TryParse(settings.LanAddress, out address))
            throw new ArgumentException("Choose a private LAN address in Settings.");
        if(settings.LanEnabled&&(!SupportHost.LocalPrivateAddress(address)||!LanAccessPolicy.IsPrivateProfile(address)||!LanAccessPolicy.HasRule(address,settings.LanPort)))throw new ArgumentException("LAN requires this computer's active Private IPv4 and prepared firewall rule. Open Settings > Network.");
        return new SupportStartRequest {
            Relay = string.IsNullOrWhiteSpace(settings.RelayHost) ? "" : settings.RelayHost + ":" + settings.RelayPort,
            DeviceId = getStatus().DeviceId,
            Password = secret,
            RelayPin = settings.RelayPin,
            IsLanFixture = isLanFixture,
            LanAddress = settings.LanEnabled ? address : null,
            LanPort = settings.LanPort
        };
    }

    public async Task StartSupportAsync()
    {
        if(closed||IsDisposed)return;
        var request = GetStartRequest();
        int version = System.Threading.Interlocked.Increment(ref requestGeneration);
        try
        {
            await Task.Run(() => {
                lock (startSync) {
                    if (closed || version != requestGeneration) return;
                    if(request.IsLanFixture)supportHost.StartFixture(request.Relay,request.DeviceId,request.Password);else supportHost.Start(request.Relay, request.DeviceId, request.Password, request.LanAddress, request.LanPort, request.RelayPin);
                }
            });
            FinishStart(request, version);
        }
        catch(Exception ex){if(!closed&&!IsDisposed&&version==requestGeneration){startError="Support configuration failed: "+ex.Message;RenderReadiness();}throw;}
    }

    private void FinishStart(SupportStartRequest request, int version)
    {
        if (closed || IsDisposed || version != requestGeneration) return;
        startError=null;
        password.Text = supportHost.Snapshot.State==SupportState.Failed&&!supportHost.Snapshot.LanReady?"":request.Password;
        RefreshIdentity();
        RenderReadiness();
        var applied=SettingsApplied;if(applied!=null)applied();
    }

    public void ShowSettings()
    {
        using (var dialog = new RemsupSettingsForm()) {dialog.SettingsSaved+=async()=>await ApplySettingsAsync();dialog.ShowDialog(this);}
        RefreshIdentity();
    }
    internal async Task ApplySettingsAsync(){var pending=StartSupportAsync();int version=requestGeneration;try{await pending;}catch(Exception ex){if(!closed&&!IsDisposed&&version==requestGeneration){startError="Settings could not be applied: "+ex.Message;RenderReadiness();}}}

    public void RequestClose() {
        if (closed) return;
        copyCancellation.Cancel();
        System.Threading.Interlocked.Increment(ref requestGeneration);
        lock (startSync) { closed = true; supportHost.Stop(); }
        if (!IsDisposed) { password.Clear(); RenderReadiness(); }
    }

    private void RefreshIdentity()
    {
        var info = getStatus();
        deviceId.Text = info.DeviceId;
        var settings = loadSettings();
        addresses.Text = "Local addresses: " + string.Join(", ", info.LanAddresses.ToArray()) + Environment.NewLine
            + (settings.LanEnabled ? "LAN configured: " + settings.LanAddress + ":" + settings.LanPort : "LAN support is off");
    }

    private void RenderReadiness()
    {
        var snapshot = supportHost.Snapshot;
        lamp.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText
            : snapshot.ColorName == "Green" ? Color.ForestGreen : snapshot.ColorName == "Yellow" ? Color.DarkGoldenrod : Color.Firebrick;
        var description=startError??snapshot.Description;
        if(description=="Relay is not configured.")description="Not configured — open Settings > Network.";
        GuiHelp.Set(lamp,description);readinessTip.SetToolTip(lamp,description);
        if (log.Text.Length>8000)log.Clear();
        if (status.Text != description) log.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + description + Environment.NewLine);
        status.Text = description;status.AccessibleDescription=description;var changed=ReadinessChanged;if(changed!=null)changed();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            copyCancellation.Cancel();
            System.Threading.Interlocked.Increment(ref requestGeneration);
            lock (startSync) { closed = true; supportHost.Dispose(); }
            readinessTip.Dispose();
            copyCancellation.Dispose();
            log.Clear();log.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class SupportStartRequest
    {
        public string Relay;
        public string DeviceId;
        public string Password;
        public string RelayPin;
        public bool IsLanFixture;
        public IPAddress LanAddress;
        public int LanPort;
    }
}

internal sealed class RemsupClientForm : Form
{
    readonly RemsupSupportPanel panel;
    readonly NotifyIcon tray;
    readonly Icon trayIcon;
    bool launched;

    public RemsupClientForm(bool autoStart = true, RemsupSupportPanel supportPanel = null)
    {
        Text = "Remsup Support";
        Size = new Size(480, 620);
        MinimumSize = new Size(480, 560);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        panel = supportPanel ?? new RemsupSupportPanel();
        panel.UsePortableRows();
        panel.CloseRequested += RequestClose;
        panel.Dock = DockStyle.Fill;
        var card = new CardPanel { Dock = DockStyle.Fill };
        var shell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        card.Controls.Add(panel); shell.Controls.Add(card); Controls.Add(shell);
        Appearance.Attach(this);

        trayIcon = AppIdentity.CreateIcon();
        tray = new NotifyIcon { Text = "Remsup Support", Icon = trayIcon, Visible = true };
        tray.ContextMenu = new ContextMenu(new[] {
            new MenuItem("Show", (s,e) => Restore()),
            new MenuItem("Settings", (s,e) => Safe(ShowSettings)),
            new MenuItem("Exit", (s,e) => RequestClose())
        });
        tray.DoubleClick += (s, e) => Restore();
        Shown += (s, e) => { if (autoStart&&!launched){launched=true;StartSupportAsyncSafe();} };
        FormClosing += (s, e) => {
            panel.RequestClose();
        };
        FormClosed += (s, e) => tray.Dispose();
    }

    private void Safe(Action action)
    {
        try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Remsup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async void StartSupportAsyncSafe()
    {
        await panel.ApplySettingsAsync();
    }

    public void ShowSettings() { panel.ShowSettings(); }
    private void Restore() { Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; Activate(); }
    public void RequestClose() { Close(); }
    protected override void Dispose(bool disposing) {
        if (disposing) { panel.RequestClose(); tray.Dispose(); trayIcon.Dispose(); }
        base.Dispose(disposing);
    }
}
