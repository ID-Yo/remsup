using System;
using System.Drawing;
using System.Windows.Forms;
internal sealed class DeviceEditorForm : Form {
 readonly TextBox name=new TextBox(),id=new TextBox(),host=new TextBox(),pin=new TextBox(),password=new TextBox(),note=new TextBox();
 readonly ComboBox transport=new ComboBox();readonly NumericUpDown port=new NumericUpDown();
 readonly CheckBox save=new CheckBox{AutoSize=true},custom=new CheckBox{AutoSize=true};readonly Label endpointLabel=new Label(),endpointHelp=new Label(),credentialHelp=new Label(),defaultEndpoint=new Label();
 readonly Func<RelayConfiguration> loadRelay;readonly Control hostField,portField,pinField;readonly Panel optionalFields=new Panel{Dock=DockStyle.Top,AutoSize=true};readonly CheckBox optionalToggle=new CheckBox{Text="Optional password / note",AutoSize=true,Appearance=System.Windows.Forms.Appearance.Button};
 public DeviceRecord Record {get;private set;}
 public DeviceEditorForm(DeviceRecord original,bool quick=false,Func<RelayConfiguration> relayProvider=null){
  loadRelay=relayProvider??RelayConfiguration.Load;
  Text=quick?"Quick options":original==null?"Add device":"Edit device";Width=620;Height=740;MinimumSize=new Size(500,570);StartPosition=FormStartPosition.CenterParent;AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Segoe UI",10);
  var shell=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(24)};Controls.Add(shell);
  var grid=WorkspaceUi.Stack();shell.Controls.Add(grid);
  name.Text=original==null?"":original.DisplayName;id.Text=original==null?"":original.DeviceId;host.Text=original==null?"":original.Host;pin.Text=original==null?"":original.Pin;
  transport.DropDownStyle=ComboBoxStyle.DropDownList;transport.Items.AddRange(new object[]{DeviceTransport.Relay,DeviceTransport.Lan});transport.SelectedItem=original==null?DeviceTransport.Relay:original.Transport;
  port.Minimum=1;port.Maximum=65535;port.Value=original==null||original.Port==0?443:original.Port;
  custom.Text="Custom relay";custom.Checked=original!=null&&!original.UseDefaultRelay&&original.Transport==DeviceTransport.Relay;
  password.UseSystemPasswordChar=true;password.Text=original==null?"":original.PermanentPassword;password.Enabled=!quick;
  save.Text="Save permanent password";save.Checked=!quick&&original!=null&&original.SavePermanentPassword;save.Enabled=!quick;note.Text=original==null?"":original.Note;if(quick)password.Clear();
  WorkspaceUi.Add(grid,WorkspaceUi.Text(Text,false,18));
  var identityCard=quick?WorkspaceUi.Card("Remote device",WorkspaceUi.Field("Device ID",id)):WorkspaceUi.Card("Device identity",WorkspaceUi.Field("Name",name),WorkspaceUi.Field("Device ID",id));identityCard.Padding=new Padding(16);WorkspaceUi.Add(grid,identityCard);
  endpointLabel.AutoSize=defaultEndpoint.AutoSize=endpointHelp.AutoSize=credentialHelp.AutoSize=true;endpointHelp.MaximumSize=credentialHelp.MaximumSize=new Size(490,0);endpointHelp.Tag=credentialHelp.Tag="muted";
  var endpointFields=WorkspaceUi.Stack();hostField=WorkspaceUi.Stack();WorkspaceUi.Add((TableLayoutPanel)hostField,endpointLabel);host.Dock=DockStyle.Top;WorkspaceUi.Add((TableLayoutPanel)hostField,host);portField=WorkspaceUi.Field("Port",port);pinField=WorkspaceUi.Field("SPKI SHA-256 pin (64 hex characters)",pin);WorkspaceUi.Add(endpointFields,hostField);WorkspaceUi.Add(endpointFields,portField);WorkspaceUi.Add(endpointFields,pinField);
  WorkspaceUi.Add(grid,WorkspaceUi.Card("Connection",WorkspaceUi.Field("Transport",transport),custom,defaultEndpoint,endpointFields,endpointHelp));
  credentialHelp.Text="Save only a permanent password. Current one-time passwords are never saved.";
  var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(24,8,24,16)};
  var reveal=WorkspaceUi.Button("Show",()=>{password.UseSystemPasswordChar=!password.UseSystemPasswordChar;});reveal.Enabled=!quick;reveal.Click+=(s,e)=>reveal.Text=password.UseSystemPasswordChar?"Show":"Hide";reveal.Leave+=(s,e)=>{password.UseSystemPasswordChar=true;reveal.Text="Show";};
  optionalFields.Controls.Add(WorkspaceUi.Card("Optional details",WorkspaceUi.Field("Permanent password",WorkspaceUi.Pair(password,reveal)),save,credentialHelp,WorkspaceUi.Field("Local note",note)));optionalToggle.Checked=original!=null&&(original.SavePermanentPassword||!string.IsNullOrEmpty(original.Note));optionalFields.Visible=optionalToggle.Checked;optionalToggle.CheckedChanged+=(s,e)=>optionalFields.Visible=optionalToggle.Checked;if(!quick){WorkspaceUi.Add(grid,optionalToggle);WorkspaceUi.Add(grid,optionalFields);}else WorkspaceUi.Add(grid,WorkspaceUi.Text("Current OTP/password is requested when you connect.",true));
  var ok=WorkspaceUi.Button(quick?"Use options":"Save",()=>{},true);var close=WorkspaceUi.Button("Cancel",()=>{});close.DialogResult=DialogResult.Cancel;
  buttons.Controls.Add(ok);buttons.Controls.Add(close);Controls.Add(buttons);CancelButton=close;AcceptButton=ok;
  bool portEdited=original!=null&&!original.UseDefaultRelay;port.ValueChanged+=(s,e)=>portEdited=true;
  transport.SelectedIndexChanged+=(s,e)=>{if(!portEdited){port.Value=transport.SelectedIndex==1?8444:443;portEdited=false;}UpdateEndpoint();};custom.CheckedChanged+=(s,e)=>UpdateEndpoint();
  UpdateEndpoint();
  ok.Click+=(s,e)=>{
   Control invalid=name;
   try{
    if(quick&&string.IsNullOrWhiteSpace(name.Text))name.Text="Quick connection";
    if(string.IsNullOrWhiteSpace(name.Text))throw new ArgumentException("Name is required.");invalid=id;id.Text=DeviceRecord.NormalizeDeviceId(id.Text);invalid=host;
    if(save.Checked&&password.Text.Length==0){invalid=password;throw new ArgumentException("A permanent password is required when saving it.");}
    var r=new DeviceRecord{RecordId=original==null?Guid.NewGuid().ToString():original.RecordId,DisplayName=name.Text.Trim(),DeviceId=id.Text,Transport=(DeviceTransport)transport.SelectedItem,UseDefaultRelay=transport.SelectedIndex==0&&!custom.Checked,Host=host.Text.Trim(),Pin=pin.Text.Trim(),Port=(int)port.Value,Note=note.Text,IsFavorite=original!=null&&original.IsFavorite,LastSuccessfulConnectionUtc=original==null?null:original.LastSuccessfulConnectionUtc,SavePermanentPassword=save.Checked,PermanentPassword=save.Checked?password.Text:null};
    r.ValidateAndNormalize();invalid=(r.Transport==DeviceTransport.Relay&&!r.UseDefaultRelay)?pin:host;DeviceEndpoint.Resolve(r,loadRelay);
    if(!r.UseDefaultRelay&&r.Host==r.DeviceId&&MessageBox.Show(this,"This address matches the Device ID. Put the relay server address or target LAN address here. Keep this address anyway?","Check endpoint",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes){host.Focus();return;}
    if(save.Checked){invalid=password;RemsupProtocol.ValidateCredentials(r.DeviceId,password.Text);}
    Record=r;DialogResult=DialogResult.OK;Close();
   }catch(Exception ex){endpointHelp.Text=ex.Message;endpointHelp.AccessibleDescription=ex.Message;if(invalid==password)optionalToggle.Checked=true;if(invalid.Enabled)invalid.Focus();else transport.Focus();Appearance.Apply(endpointHelp);}
  };
  WorkspaceUi.Help(this);GuiHelp.Set(name,"A local label for the remote computer.");GuiHelp.Set(id,"The remote computer's nine-digit Device ID, kept separate from its endpoint.");
  GuiHelp.Set(transport,"Relay uses a pinned TLS connection through a shared server; LAN connects directly over the local network without encryption.");GuiHelp.Set(custom,"Override the global relay for this device only. Enter its host, port, and SPKI SHA-256 pin. Uncheck to use current Settings > Network.");
  GuiHelp.Set(defaultEndpoint,"Current global TLS relay host and port. The built-in public key pin is used; this endpoint is resolved again on each connection.");GuiHelp.Set(pin,"SPKI SHA-256 public-key pin, as 64 hexadecimal characters. For LAN, copy and compare it from the receiving computer's Settings screen. Missing or changed pins refuse the connection before sending a password.");
  GuiHelp.Set(save,"Opt in to storing a permanent password in the current-user DPAPI vault. Do not save a one-time password.");GuiHelp.Set(password,"Optional permanent password for the remote computer, separate from this computer's receiving password.");
  GuiHelp.Set(note,"Optional local note. Do not include credentials.");GuiHelp.Set(reveal,"Reveal this field; leaving the control hides it.");GuiHelp.Set(ok,quick?"Validate this profile, then confirm the target before connection.":"Save this device profile without changing the global relay.");GuiHelp.Set(close,"Discard unsaved changes and close this dialog.");
  GuiHelp.Set(optionalToggle,"Expand optional saved permanent-password and note fields. Changing disclosure does not save or discard.");UpdateEndpoint();Appearance.Attach(this);
 }
 void UpdateEndpoint(){
  bool relay=transport.SelectedIndex==0,overrideRelay=relay&&custom.Checked;
  custom.Visible=relay;defaultEndpoint.Visible=relay&&!overrideRelay;
  host.Enabled=!relay||overrideRelay;port.Enabled=host.Enabled;hostField.Visible=portField.Visible=host.Enabled;pinField.Visible=overrideRelay||!relay;
  endpointLabel.Text=relay?"Relay server address":"Target LAN address (IPv4 / hostname)";host.AccessibleName=endpointLabel.Text;
  string help;
  if(relay&&!overrideRelay){try{var configured=loadRelay().Validated();defaultEndpoint.Text="Default: "+configured.Display;}catch(Exception){defaultEndpoint.Text="Not configured — open Settings > Network.";}
   help="Default uses the current global TLS relay address and port with the built-in public-key pin. Configure it in Settings > Network.";
  }else help=relay?"Custom relay applies only to this device. Enter its host, TLS port (typically 443), and exact SPKI SHA-256 pin. Device ID is a separate field.":"Use the remote computer's LAN address, e.g. 192.168.1.25, and its configured port (typically 8444). LAN uses pinned TLS. Copy the receiver SPKI SHA256 fingerprint from its screen before connecting.";
  endpointHelp.Text=relay?(overrideRelay?"Custom endpoint requires its public-key pin.":"Uses the pinned TLS relay in Network settings."):"Address of the other computer; verify its SPKI pin from the receiving screen.";endpointHelp.AccessibleDescription=help;GuiHelp.Set(host,help);GuiHelp.Set(port,"Port 1–65535: match the configured endpoint. Typical defaults: 443 TLS relay, 8444 LAN.");
 }
 protected override void Dispose(bool disposing){if(disposing&&!IsDisposed){password.Clear();if(optionalFields.Parent==null)optionalFields.Dispose();if(name.Parent==null)name.Dispose();if(optionalToggle.Parent==null)optionalToggle.Dispose();}base.Dispose(disposing);}
}
internal sealed class PasswordForm : Form {
 readonly TextBox value=new TextBox();
 public string Password{get{return value.Text;}}
 public PasswordForm(){
  Text="Current OTP / password";Width=480;Height=245;MinimumSize=new Size(400,220);StartPosition=FormStartPosition.CenterParent;AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Segoe UI",10);
  var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=3};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
  var help=new Label{Text="Use the authorized target's current OTP or permanent password. This prompt uses it for the session only; it is not saved.",AutoSize=true,Dock=DockStyle.Fill,MaximumSize=new Size(420,0),Tag="muted"};
  layout.Controls.Add(help,0,0);value.UseSystemPasswordChar=true;value.AccessibleName="Current OTP or password";value.Dock=DockStyle.Fill;layout.Controls.Add(value,0,1);
  var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};var ok=new Button{Text="Connect",AutoSize=true,DialogResult=DialogResult.OK};var cancel=new Button{Text="Cancel",AutoSize=true,DialogResult=DialogResult.Cancel};buttons.Controls.Add(ok);buttons.Controls.Add(cancel);layout.Controls.Add(buttons,0,2);Controls.Add(layout);AcceptButton=ok;CancelButton=cancel;
  GuiHelp.Attach(this);GuiHelp.Set(value,help.Text);GuiHelp.Set(ok,"Continue to target confirmation. No password is saved by this prompt.");GuiHelp.Set(cancel,"Cancel this connection.");Appearance.Attach(this);
 }
}
